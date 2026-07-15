using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>One backed-up column's definition (schema half of a table).</summary>
public sealed record BackupColumn(string Name, string DeclaredType, bool Nullable, bool PrimaryKey);

/// <summary>Non-table object kinds carried in the v3 object section (schema-only; their DDL text).</summary>
public enum LbakObjectKind : byte
{
    View = 0,
    Procedure = 1,
    Function = 2,
    Trigger = 3
}

/// <summary>One non-table object in a v3 backup: its kind, name and the raw CREATE text from
/// <see cref="SqlExplorer.Sdk.IDbProvider.GetObjectDefinitionAsync"/>. <see cref="ParentTable"/> is set for
/// a trigger (the table/view it hangs on), empty otherwise.</summary>
public sealed record BackupObject(LbakObjectKind Kind, string SchemaName, string Name, string ParentTable, string Definition);

/// <summary>One materialised table (v1 read path only). v2 restore streams rows through
/// <see cref="ILbakVisitor"/> instead of building this in memory.</summary>
public sealed record BackupTable(string SchemaName, string TableName, IReadOnlyList<BackupColumn> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>Always-plaintext header metadata — lets the Restore dialog show context before asking for a passphrase.</summary>
public sealed record LbakMeta(
    string ProviderId,
    string EngineDisplayName,
    string DatabaseName,
    string CreatedUtc,
    string AppVersion,
    int TableCount,
    bool Encrypted,
    int FormatVersion,
    int ViewCount = 0,
    int RoutineCount = 0,
    int TriggerCount = 0);

/// <summary>Restore-side sink for a streamed v2 payload: one <see cref="OnTableAsync"/> per table, then
/// one <see cref="OnRowAsync"/> per row. A row's cells must be read in order and any LOB stream fully
/// consumed before the next cell (the payload is forward-only).</summary>
public interface ILbakVisitor
{
    Task OnTableAsync(BackupTable tableHeader, CancellationToken ct);   // tableHeader.Rows is always empty
    Task OnRowAsync(ILbakRow row, CancellationToken ct);

    /// <summary>One non-table object from the v3 object section, replayed after all tables. Default no-op so
    /// a visitor that only cares about table data need not implement it.</summary>
    Task OnObjectAsync(BackupObject obj, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>One row handed to an <see cref="ILbakVisitor"/>. Cells align with the table header's columns.</summary>
public interface ILbakRow
{
    int FieldCount { get; }
    bool IsNull(int i);
    bool IsStreamed(int i);          // a large LOB cell → read via OpenStream, don't GetValue
    object? GetValue(int i);         // scalar / small inlined LOB
    Stream OpenStream(int i);        // forward-only stream over a large binary/text LOB cell
    bool IsTextStream(int i);        // true → the streamed bytes are UTF-8 text (restore as nvarchar)
}

/// <summary>
/// Reader/writer for the <c>.lbak</c> (Lionear Backup) container.
///
/// <para><b>v2 (streaming).</b> Header: magic + version + flags + salt(16) + baseNonce(4) + plaintext
/// meta JSON. Payload pipeline: <c>File ← [ChunkedGcm] ← GZip ← body</c>, written and read without ever
/// holding a whole table (or a whole LOB cell) in memory — so a multi-gigabyte value round-trips. Body is
/// self-delimiting: a <c>1</c> byte precedes each table and each row, a <c>0</c> byte ends the list.
/// Small LOB cells are inlined as ordinary scalars; only cells past <see cref="InlineThreshold"/> use the
/// streamed-LOB tag (8) and are chunked to disk.</para>
///
/// <para><b>v1 (legacy, read-only).</b> Whole payload buffered: one-shot AES-GCM over one-shot GZip over a
/// length-prefixed body. Still restorable via <see cref="ReadPayloadV1"/>.</para>
/// </summary>
public static class LbakFormat
{
    private static readonly byte[] Magic = "LBAK"u8.ToArray();
    private const byte FlagEncrypted = 0b01;
    private const int Pbkdf2Iterations = 600_000;
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    // v1-only header fields.
    private const int V1NonceBytes = 12;
    private const int V1TagBytes = 16;

    /// <summary>LOB cells at or below this size are inlined as scalars (fast common path); larger cells
    /// stream. Keeps tables of many small <c>max</c>-typed values from spooling a temp file per cell.</summary>
    public const int InlineThreshold = 1 << 20; // 1 MiB

    // ---------------------------------------------------------------- write (v2, streaming)

    /// <summary>Open a streaming v2 writer. Write the header immediately; the caller then streams tables
    /// and rows and disposes the writer to finish (flush gzip + seal the final crypto chunk).</summary>
    public static LbakWriter CreateWriter(string path, LbakMeta meta, string? passphrase)
    {
        var encrypted = !string.IsNullOrEmpty(passphrase);
        var salt = new byte[SaltBytes];
        var baseNonce = new byte[ChunkedGcm.BaseNonceBytes];
        byte[]? key = null;
        if (encrypted)
        {
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(baseNonce);
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase!, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
        }

        var file = File.Create(path);
        WriteHeader(file, meta with { Encrypted = encrypted, FormatVersion = 3 }, salt, baseNonce);

        Stream crypto = encrypted ? ChunkedGcm.CreateWriter(file, key!, baseNonce) : file;
        var gzip = new GZipStream(crypto, CompressionLevel.Optimal);
        return new LbakWriter(file, crypto, gzip);
    }

    private static void WriteHeader(Stream s, LbakMeta meta, byte[] salt, byte[] baseNonce)
    {
        var metaJson = JsonSerializer.SerializeToUtf8Bytes(meta);
        using var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write((byte)meta.FormatVersion);
        w.Write((byte)(meta.Encrypted ? FlagEncrypted : 0));
        w.Write(salt);
        w.Write(baseNonce);
        w.Write(metaJson.Length);
        w.Write(metaJson);
    }

    // ---------------------------------------------------------------- read: meta + dispatch

    /// <summary>Read just the plaintext header/meta — always succeeds regardless of passphrase.</summary>
    public static LbakMeta ReadMeta(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        ReadAndCheckMagic(r);
        var version = r.ReadByte();
        r.ReadByte(); // flags
        var headerBytes = version >= 2 ? SaltBytes + ChunkedGcm.BaseNonceBytes : SaltBytes + V1NonceBytes + V1TagBytes;
        r.ReadBytes(headerBytes);
        var metaLen = r.ReadInt32();
        var metaJson = r.ReadBytes(metaLen);
        return JsonSerializer.Deserialize<LbakMeta>(metaJson)
               ?? throw new InvalidDataException("Backup metadata is empty or unreadable.");
    }

    /// <summary>Stream a v2 payload table-by-table, row-by-row into <paramref name="visitor"/>.</summary>
    public static async Task ReadStreamingAsync(string path, string? passphrase, ILbakVisitor visitor, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var (version, flags, salt, baseNonce) = ReadHeaderForPayload(fs);
        if (version < 2)
        {
            throw new InvalidOperationException("This is a v1 backup — use ReadPayloadV1.");
        }

        Stream crypto = (flags & FlagEncrypted) != 0
            ? ChunkedGcm.CreateReader(fs, DeriveKey(passphrase, salt), baseNonce)
            : fs;
        await using var gzip = new GZipStream(crypto, CompressionMode.Decompress);
        using var r = new BinaryReader(gzip, Encoding.UTF8);

        while (r.ReadByte() == 1) // next table?
        {
            ct.ThrowIfCancellationRequested();
            var header = ReadTableHeader(r);
            await visitor.OnTableAsync(header, ct);

            while (r.ReadByte() == 1) // next row?
            {
                ct.ThrowIfCancellationRequested();
                var row = new StreamingRow(r, header.Columns.Count);
                await visitor.OnRowAsync(row, ct);
                row.DrainRemaining(); // ensure the reader sits at the row boundary even if a cell was skipped
            }
        }

        // v3 object section (views/procedures/functions/triggers), replayed after all table data.
        if (version >= 3)
        {
            var objectCount = r.ReadInt32();
            for (var i = 0; i < objectCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var obj = new BackupObject(
                    (LbakObjectKind)r.ReadByte(), r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
                await visitor.OnObjectAsync(obj, ct);
            }
        }
    }

    // ---------------------------------------------------------------- read: v1 (materialised, legacy)

    /// <summary>Read and decode a v1 payload the old way (whole-buffer). Only for FormatVersion 1 files.</summary>
    public static IReadOnlyList<BackupTable> ReadPayloadV1(string path, string? passphrase)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        ReadAndCheckMagic(r);
        r.ReadByte();                       // version (1)
        var flags = r.ReadByte();
        var salt = r.ReadBytes(SaltBytes);
        var nonce = r.ReadBytes(V1NonceBytes);
        var tag = r.ReadBytes(V1TagBytes);
        var metaLen = r.ReadInt32();
        r.ReadBytes(metaLen);               // skip meta
        var payloadLen = r.ReadInt64();
        var payload = r.ReadBytes((int)payloadLen);

        var compressed = (flags & FlagEncrypted) != 0 ? DecryptV1(payload, salt, nonce, tag, passphrase) : payload;
        var body = DecompressV1(compressed);
        return ReadBodyV1(body);
    }

    private static byte[] DecryptV1(byte[] payload, byte[] salt, byte[] nonce, byte[] tag, string? passphrase)
    {
        var key = DeriveKey(passphrase, salt);
        var plain = new byte[payload.Length];
        using var aes = new AesGcm(key, V1TagBytes);
        aes.Decrypt(nonce, payload, tag, plain);
        return plain;
    }

    private static byte[] DecompressV1(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    private static IReadOnlyList<BackupTable> ReadBodyV1(byte[] body)
    {
        using var ms = new MemoryStream(body);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var tableCount = r.ReadInt32();
        var tables = new List<BackupTable>(tableCount);
        for (var t = 0; t < tableCount; t++)
        {
            var schema = r.ReadString();
            var table = r.ReadString();
            var columnCount = r.ReadInt32();
            var columns = new List<BackupColumn>(columnCount);
            for (var c = 0; c < columnCount; c++)
            {
                var name = r.ReadString();
                var type = r.ReadString();
                var flags = r.ReadByte();
                columns.Add(new BackupColumn(name, type, (flags & 1) != 0, (flags & 2) != 0));
            }

            var rowCount = r.ReadInt64();
            var rows = new List<object?[]>((int)Math.Min(rowCount, 1024));
            for (long i = 0; i < rowCount; i++)
            {
                var row = new object?[columnCount];
                for (var c = 0; c < columnCount; c++)
                {
                    row[c] = ReadScalar(r);
                }

                rows.Add(row);
            }

            tables.Add(new BackupTable(schema, table, columns, rows));
        }

        return tables;
    }

    // ---------------------------------------------------------------- shared helpers

    private static (byte version, byte flags, byte[] salt, byte[] baseNonce) ReadHeaderForPayload(Stream fs)
    {
        using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        ReadAndCheckMagic(r);
        var version = r.ReadByte();
        var flags = r.ReadByte();
        var salt = r.ReadBytes(SaltBytes);
        var baseNonce = r.ReadBytes(ChunkedGcm.BaseNonceBytes);
        var metaLen = r.ReadInt32();
        r.ReadBytes(metaLen); // skip meta
        return (version, flags, salt, baseNonce);
    }

    private static BackupTable ReadTableHeader(BinaryReader r)
    {
        var schema = r.ReadString();
        var table = r.ReadString();
        var columnCount = r.ReadInt32();
        var columns = new List<BackupColumn>(columnCount);
        for (var c = 0; c < columnCount; c++)
        {
            var name = r.ReadString();
            var type = r.ReadString();
            var flags = r.ReadByte();
            columns.Add(new BackupColumn(name, type, (flags & 1) != 0, (flags & 2) != 0));
        }

        return new BackupTable(schema, table, columns, []);
    }

    private static byte[] DeriveKey(string? passphrase, byte[] salt)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("This backup is encrypted — a passphrase is required.");
        }

        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
    }

    private static void ReadAndCheckMagic(BinaryReader r)
    {
        var magic = r.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Lionear backup (.lbak) file.");
        }
    }

    // TypeTag: 0=Null,1=Int64,2=Double,3=String,4=Bool,5=DateTime(ticks),6=Bytes,7=Decimal(4×int32),
    //          8=StreamedLob,9=DateTimeOffset(ticks+offsetTicks),10=TimeSpan(ticks),11=Guid(16 bytes).
    // Tags 9-11 keep date/time/uuid values as native CLR types instead of a culture-dependent ToString()
    // (which round-trips as a string and then fails "Conversion failed …" against a time/datetimeoffset column).
    internal static void WriteScalar(BinaryWriter w, object? value)
    {
        switch (value)
        {
            case null: w.Write((byte)0); break;
            case bool b: w.Write((byte)4); w.Write(b); break;
            case byte[] bytes: w.Write((byte)6); w.Write(bytes.Length); w.Write(bytes); break;
            case decimal d:
                w.Write((byte)7);
                foreach (var bits in decimal.GetBits(d)) { w.Write(bits); }
                break;
            case DateTimeOffset dto: w.Write((byte)9); w.Write(dto.Ticks); w.Write(dto.Offset.Ticks); break;
            case DateTime dt: w.Write((byte)5); w.Write(dt.Ticks); break;
            case TimeSpan ts: w.Write((byte)10); w.Write(ts.Ticks); break;
            case Guid g: w.Write((byte)11); w.Write(g.ToByteArray()); break;
            case float or double: w.Write((byte)2); w.Write(Convert.ToDouble(value)); break;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                w.Write((byte)1); w.Write(Convert.ToInt64(value)); break;
            case string s: w.Write((byte)3); w.Write(s); break;
            default: w.Write((byte)3); w.Write(value.ToString() ?? string.Empty); break;
        }
    }

    internal static object? ReadScalarByTag(BinaryReader r, byte tag) => tag switch
    {
        0 => null,
        1 => r.ReadInt64(),
        2 => r.ReadDouble(),
        3 => r.ReadString(),
        4 => r.ReadBoolean(),
        5 => new DateTime(r.ReadInt64()),
        6 => r.ReadBytes(r.ReadInt32()),
        7 => new decimal([r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()]),
        9 => new DateTimeOffset(r.ReadInt64(), TimeSpan.FromTicks(r.ReadInt64())),
        10 => TimeSpan.FromTicks(r.ReadInt64()),
        11 => new Guid(r.ReadBytes(16)),
        var t => throw new InvalidDataException($"Unknown value type tag {t}.")
    };

    private static object? ReadScalar(BinaryReader r) => ReadScalarByTag(r, r.ReadByte());

    // ---- v2 streamed-LOB reader used by StreamingRow ----

    /// <summary>A forward-only stream over a tag-8 LOB cell: reads <c>[len][bytes]…[0]</c> chunks off the
    /// body reader on demand, so the caller never buffers the whole value.</summary>
    internal sealed class LobCellStream(BinaryReader reader) : Stream
    {
        private byte[] _chunk = [];
        private int _chunkPos;
        private bool _done;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunkPos >= _chunk.Length)
            {
                if (_done)
                {
                    return 0;
                }

                var len = reader.ReadInt32();
                if (len == 0)
                {
                    _done = true;
                    return 0;
                }

                _chunk = reader.ReadBytes(len);
                _chunkPos = 0;
            }

            var n = Math.Min(count, _chunk.Length - _chunkPos);
            Array.Copy(_chunk, _chunkPos, buffer, offset, n);
            _chunkPos += n;
            return n;
        }

        // Consume any remaining chunks so the reader lands on the next cell boundary.
        public void Drain()
        {
            var buf = new byte[64 * 1024];
            while (Read(buf, 0, buf.Length) > 0) { }
        }

        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Pull-model row over the body reader. Cells are read strictly in ascending index; a streamed
    /// cell hands out a <see cref="LobCellStream"/> the caller must drain before the next cell.</summary>
    private sealed class StreamingRow(BinaryReader reader, int fieldCount) : ILbakRow
    {
        private object? _scalar;                 // decoded scalar for the current cell
        private bool _streamed;                  // current cell is a tag-8 LOB
        private bool _textStream;
        private LobCellStream? _open;
        private int _current = -1;               // index of the currently-loaded cell (-1 = none yet)

        public int FieldCount => fieldCount;

        // Advance the reader until cell i is the loaded cell. Cells are strictly forward-only.
        private void Advance(int i)
        {
            if (i < _current)
            {
                throw new InvalidOperationException("Backup cells must be read in order.");
            }

            while (_current < i)
            {
                _open?.Drain();  // skip any LOB the caller didn't fully consume
                _open = null;
                ReadCellHeader();
                _current++;
            }
        }

        private void ReadCellHeader()
        {
            var tag = reader.ReadByte();
            if (tag == 8)
            {
                _streamed = true;
                _textStream = reader.ReadByte() == 1;
                _scalar = null;
                _open = new LobCellStream(reader);
            }
            else
            {
                _streamed = false;
                _open = null;
                _scalar = LbakFormat.ReadScalarByTag(reader, tag);
            }
        }

        public bool IsNull(int i) { Advance(i); return !_streamed && _scalar is null; }
        public bool IsStreamed(int i) { Advance(i); return _streamed; }
        public bool IsTextStream(int i) { Advance(i); return _textStream; }
        public object? GetValue(int i) { Advance(i); return _scalar; }

        public Stream OpenStream(int i)
        {
            Advance(i);
            return _open ?? throw new InvalidOperationException("Cell is not a streamed LOB.");
        }

        public void DrainRemaining()
        {
            if (fieldCount == 0)
            {
                return;
            }

            Advance(fieldCount - 1);
            _open?.Drain();
            _open = null;
        }
    }
}
