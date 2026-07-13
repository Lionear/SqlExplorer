using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lionear.SqlExplorer.Tools.UniversalBackup;

/// <summary>One backed-up column's definition (schema half of a table).</summary>
public sealed record BackupColumn(string Name, string DeclaredType, bool Nullable, bool PrimaryKey);

/// <summary>One backed-up table: its identity, columns and materialised rows (cells index-aligned to columns).</summary>
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
    int FormatVersion);

/// <summary>
/// Reader/writer for the <c>.lbak</c> (Lionear Backup) container. Layout: a fixed plaintext header
/// (magic + flags + KDF/AEAD material + plaintext meta JSON) followed by the payload
/// <c>[encrypt[gzip[body]]]</c>. BCL only (GZipStream + AesGcm/PBKDF2). Compress-then-encrypt; a wrong
/// passphrase or any tampering makes <see cref="AesGcm.Decrypt"/> throw (authenticated), never silent garbage.
/// </summary>
public static class LbakFormat
{
    private static readonly byte[] Magic = "LBAK"u8.ToArray();
    private const byte FormatVersion = 1;
    private const byte FlagEncrypted = 0b01;
    private const int Pbkdf2Iterations = 600_000;
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static async Task WriteAsync(string path, LbakMeta meta, IReadOnlyList<BackupTable> tables, string? passphrase, CancellationToken ct)
    {
        var body = WriteBody(tables);
        var compressed = Compress(body);

        var salt = new byte[SaltBytes];
        var nonce = new byte[NonceBytes];
        var tag = new byte[TagBytes];
        byte flags = 0;
        byte[] payload;

        if (!string.IsNullOrEmpty(passphrase))
        {
            flags |= FlagEncrypted;
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);
            var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
            payload = new byte[compressed.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, compressed, payload, tag);
        }
        else
        {
            payload = compressed;
        }

        var metaJson = JsonSerializer.SerializeToUtf8Bytes(meta with { Encrypted = flags != 0 });

        await using var fs = File.Create(path);
        await using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(flags);
        w.Write(salt);
        w.Write(nonce);
        w.Write(tag);
        w.Write(metaJson.Length);
        w.Write(metaJson);
        w.Write((long)payload.Length);
        w.Write(payload);
        await fs.FlushAsync(ct);
    }

    /// <summary>Read just the plaintext header/meta — always succeeds regardless of passphrase.</summary>
    public static LbakMeta ReadMeta(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        ReadAndCheckMagic(r);
        r.ReadByte();                       // format version
        r.ReadByte();                       // flags
        r.ReadBytes(SaltBytes + NonceBytes + TagBytes);
        var metaLen = r.ReadInt32();
        var metaJson = r.ReadBytes(metaLen);
        return JsonSerializer.Deserialize<LbakMeta>(metaJson)
               ?? throw new InvalidDataException("Backup metadata is empty or unreadable.");
    }

    /// <summary>Read and decode the payload (decrypt if needed, decompress, parse the table stream).</summary>
    public static IReadOnlyList<BackupTable> ReadPayload(string path, string? passphrase)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        ReadAndCheckMagic(r);
        r.ReadByte();                       // format version
        var flags = r.ReadByte();
        var salt = r.ReadBytes(SaltBytes);
        var nonce = r.ReadBytes(NonceBytes);
        var tag = r.ReadBytes(TagBytes);
        var metaLen = r.ReadInt32();
        r.ReadBytes(metaLen);               // skip meta
        var payloadLen = r.ReadInt64();
        var payload = r.ReadBytes((int)payloadLen);

        var compressed = (flags & FlagEncrypted) != 0 ? Decrypt(payload, salt, nonce, tag, passphrase) : payload;
        var body = Decompress(compressed);
        return ReadBody(body);
    }

    private static byte[] Decrypt(byte[] payload, byte[] salt, byte[] nonce, byte[] tag, string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("This backup is encrypted — a passphrase is required.");
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
        var plain = new byte[payload.Length];
        using var aes = new AesGcm(key, TagBytes);
        // Throws AuthenticationTagMismatchException on a wrong passphrase or a tampered file.
        aes.Decrypt(nonce, payload, tag, plain);
        return plain;
    }

    private static void ReadAndCheckMagic(BinaryReader r)
    {
        var magic = r.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a Lionear backup (.lbak) file.");
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] WriteBody(IReadOnlyList<BackupTable> tables)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(tables.Count);
            foreach (var table in tables)
            {
                w.Write(table.SchemaName);
                w.Write(table.TableName);
                w.Write(table.Columns.Count);
                foreach (var column in table.Columns)
                {
                    w.Write(column.Name);
                    w.Write(column.DeclaredType);
                    w.Write((byte)((column.Nullable ? 1 : 0) | (column.PrimaryKey ? 2 : 0)));
                }

                w.Write((long)table.Rows.Count);
                foreach (var row in table.Rows)
                {
                    foreach (var cell in row)
                    {
                        WriteValue(w, cell);
                    }
                }
            }
        }

        return ms.ToArray();
    }

    private static IReadOnlyList<BackupTable> ReadBody(byte[] body)
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
                    row[c] = ReadValue(r);
                }

                rows.Add(row);
            }

            tables.Add(new BackupTable(schema, table, columns, rows));
        }

        return tables;
    }

    // TypeTag: 0=Null,1=Int64,2=Double,3=String,4=Bool,5=DateTime(ticks),6=Bytes,7=Decimal(4×int32).
    private static void WriteValue(BinaryWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.Write((byte)0);
                break;
            case bool b:
                w.Write((byte)4);
                w.Write(b);
                break;
            case byte[] bytes:
                w.Write((byte)6);
                w.Write(bytes.Length);
                w.Write(bytes);
                break;
            case decimal d:
                w.Write((byte)7);
                foreach (var bits in decimal.GetBits(d))
                {
                    w.Write(bits);
                }

                break;
            case DateTime dt:
                w.Write((byte)5);
                w.Write(dt.Ticks);
                break;
            case float or double:
                w.Write((byte)2);
                w.Write(Convert.ToDouble(value));
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                w.Write((byte)1);
                w.Write(Convert.ToInt64(value));
                break;
            case string s:
                w.Write((byte)3);
                w.Write(s);
                break;
            default:
                // Provider-specific CLR types (jsonb/uuid/arrays) fall back to their string form.
                w.Write((byte)3);
                w.Write(value.ToString() ?? string.Empty);
                break;
        }
    }

    private static object? ReadValue(BinaryReader r) => r.ReadByte() switch
    {
        0 => null,
        1 => r.ReadInt64(),
        2 => r.ReadDouble(),
        3 => r.ReadString(),
        4 => r.ReadBoolean(),
        5 => new DateTime(r.ReadInt64()),
        6 => r.ReadBytes(r.ReadInt32()),
        7 => new decimal([r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()]),
        var tag => throw new InvalidDataException($"Unknown value type tag {tag}.")
    };
}
