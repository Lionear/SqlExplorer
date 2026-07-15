using System.Text;

namespace SqlExplorer.Tools.UniversalBackup;

/// <summary>
/// Streaming writer for a <c>.lbak</c> v2 payload (created by <see cref="LbakFormat.CreateWriter"/>).
/// Tables and rows are written incrementally over a <c>GZip[→ChunkedGcm]→File</c> pipeline; nothing is held
/// in memory beyond one buffer. LOB cells up to <see cref="LbakFormat.InlineThreshold"/> are inlined as
/// ordinary scalars; larger cells are chunked straight to disk so even a multi-gigabyte value fits.
/// Dispose to finish (flush gzip, seal the crypto stream).
/// </summary>
public sealed class LbakWriter : IDisposable
{
    private const int CopyBufferBytes = 64 * 1024;

    private readonly Stream _file;
    private readonly Stream _crypto;
    private readonly Stream _gzip;
    private readonly BinaryWriter _w;
    private readonly List<BackupObject> _objects = [];
    private bool _tableOpen;
    private bool _finished;

    internal LbakWriter(Stream file, Stream crypto, Stream gzip)
    {
        _file = file;
        _crypto = crypto;
        _gzip = gzip;
        _w = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true);
    }

    public void BeginTable(string schema, string tableName, IReadOnlyList<BackupColumn> columns)
    {
        EndTable();
        _w.Write((byte)1); // a table follows
        _w.Write(schema);
        _w.Write(tableName);
        _w.Write(columns.Count);
        foreach (var c in columns)
        {
            _w.Write(c.Name);
            _w.Write(c.DeclaredType);
            _w.Write((byte)((c.Nullable ? 1 : 0) | (c.PrimaryKey ? 2 : 0)));
        }

        _tableOpen = true;
    }

    private void EndTable()
    {
        if (_tableOpen)
        {
            _w.Write((byte)0); // no more rows
            _tableOpen = false;
        }
    }

    /// <summary>Queue a non-table object (view/procedure/function/trigger) for the v3 object section. Their
    /// DDL is small text, so they're buffered and written after all tables on <see cref="Dispose"/>.</summary>
    public void AddObject(BackupObject obj) => _objects.Add(obj);

    public void BeginRow() => _w.Write((byte)1); // a row follows

    public void WriteScalarCell(object? value) => LbakFormat.WriteScalar(_w, value);

    /// <summary>Write a binary LOB cell, inlining small values and streaming large ones.</summary>
    public async Task WriteLobBytesCellAsync(Stream source, CancellationToken ct)
    {
        var head = new byte[LbakFormat.InlineThreshold];
        var filled = await ReadFullAsync(source, head, ct);
        if (filled < head.Length)
        {
            WriteScalarCell(head[..filled]); // fits → inline as tag-6 bytes
            return;
        }

        _w.Write((byte)8);
        _w.Write((byte)0); // subtype: bytes
        WriteChunk(head, filled);

        var block = new byte[CopyBufferBytes];
        int n;
        while ((n = await source.ReadAsync(block, ct)) > 0)
        {
            WriteChunk(block, n);
        }

        _w.Write(0); // 0-length chunk terminates the cell
    }

    /// <summary>Write a text LOB cell (UTF-8 on disk), inlining small values and streaming large ones.</summary>
    public async Task WriteLobTextCellAsync(TextReader source, CancellationToken ct)
    {
        var head = new char[LbakFormat.InlineThreshold];
        var filled = await ReadFullAsync(source, head, ct);
        if (filled < head.Length)
        {
            WriteScalarCell(new string(head, 0, filled)); // fits → inline as tag-3 string
            return;
        }

        _w.Write((byte)8);
        _w.Write((byte)1); // subtype: text

        var encoder = Encoding.UTF8.GetEncoder();
        WriteEncodedChunk(encoder, head, filled, flush: false);

        var block = new char[CopyBufferBytes];
        int n;
        while ((n = await source.ReadBlockAsync(block, ct)) > 0)
        {
            WriteEncodedChunk(encoder, block, n, flush: false);
        }

        WriteEncodedChunk(encoder, [], 0, flush: true); // flush any trailing surrogate state
        _w.Write(0); // terminator
    }

    private void WriteChunk(byte[] buffer, int count)
    {
        _w.Write(count);
        _w.Write(buffer, 0, count);
    }

    private void WriteEncodedChunk(Encoder encoder, char[] chars, int count, bool flush)
    {
        var byteCount = encoder.GetByteCount(chars, 0, count, flush);
        if (byteCount == 0 && !flush)
        {
            return;
        }

        var bytes = new byte[byteCount];
        var written = encoder.GetBytes(chars, 0, count, bytes, 0, flush);
        if (written > 0)
        {
            WriteChunk(bytes, written);
        }
    }

    private static async Task<int> ReadFullAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var r = await source.ReadAsync(buffer.AsMemory(total), ct);
            if (r == 0)
            {
                break;
            }

            total += r;
        }

        return total;
    }

    private static async Task<int> ReadFullAsync(TextReader source, char[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var r = await source.ReadBlockAsync(buffer.AsMemory(total), ct);
            if (r == 0)
            {
                break;
            }

            total += r;
        }

        return total;
    }

    public void Dispose()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        EndTable();
        _w.Write((byte)0); // no more tables

        // v3 object section: count + each object (kind, schema, name, parent, definition text).
        _w.Write(_objects.Count);
        foreach (var o in _objects)
        {
            _w.Write((byte)o.Kind);
            _w.Write(o.SchemaName);
            _w.Write(o.Name);
            _w.Write(o.ParentTable);
            _w.Write(o.Definition);
        }

        _w.Flush();
        _w.Dispose();
        _gzip.Dispose();   // flushes gzip → disposes crypto (seals final chunk) → disposes file
    }
}
