using System.Buffers.Binary;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Transport.Recording;

/// <summary>
/// Reads <c>.x4rec</c> parts back (M4-26/M4-27; docs/m4-plan.md W6). The reader is strict: a truncated header,
/// an unknown magic, a payload length above <see cref="MaxPayloadBytes"/> or a part whose header claims another
/// format version all raise <see cref="InvalidDataException"/> instead of silently producing a short stream, because
/// a replay that quietly loses messages is worse than a replay that refuses to start.
/// </summary>
public sealed class RecordingReader : IDisposable
{
    /// <summary>Default cap on a single recorded payload (the wire cap is 1 MiB).</summary>
    public const int DefaultMaxPayloadBytes = FrameCodec.DefaultMaxFrameBytes;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly int _maxPayloadBytes;
    private readonly byte[] _header = new byte[RecordingFormat.RecordHeaderSize];
    private long _count;
    private bool _done;

    /// <summary>Wraps <paramref name="stream"/> positioned at the start of a part and reads its file header.</summary>
    /// <param name="stream">The part to read.</param>
    /// <param name="ownsStream">Dispose the stream with the reader.</param>
    /// <param name="maxPayloadBytes">Largest payload accepted.</param>
    public RecordingReader(Stream stream, bool ownsStream = true, int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _ownsStream = ownsStream;
        _maxPayloadBytes = maxPayloadBytes;

        var fileHeader = new byte[RecordingFormat.FileHeaderSize];
        int got = ReadFully(fileHeader);
        if (got != fileHeader.Length || !RecordingFormat.TryReadFileHeader(fileHeader, out var header))
        {
            throw new InvalidDataException(
                $"not an x4rec recording: {got} header bytes and magic 0x{(got >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(fileHeader) : 0):X8}");
        }

        Header = header;
    }

    /// <summary>The part's file header.</summary>
    public RecordingFileHeader Header { get; }

    /// <summary>Records returned so far.</summary>
    public long Count => _count;

    /// <summary>Timestamp of the last record returned, or 0.</summary>
    public long LastTimeUs { get; private set; }

    /// <summary>Timestamp of the first record returned, or 0.</summary>
    public long FirstTimeUs { get; private set; }

    /// <summary>Span between the first and the last record returned.</summary>
    public TimeSpan RecordedSpan => TimeSpan.FromMicroseconds(LastTimeUs - FirstTimeUs);

    /// <summary>Opens a part from disk.</summary>
    public static RecordingReader Open(string path, int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1 << 16, FileOptions.SequentialScan);
        try
        {
            return new RecordingReader(stream, ownsStream: true, maxPayloadBytes);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Returns the next record, or false at the end of the part.</summary>
    public bool TryRead(out RecordingRecord record)
    {
        record = default;
        if (_done)
            return false;

        int got = ReadFully(_header);
        if (got == 0)
        {
            _done = true;
            return false;
        }

        if (got != _header.Length)
            throw new InvalidDataException($"truncated record header: {got} of {_header.Length} bytes at record {_count}");

        long timeUs = (long)BinaryPrimitives.ReadUInt64LittleEndian(_header);
        var type = (MsgType)BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(8));
        var lane = (Lane)_header[10];
        var flags = (RecordingMark)_header[11];
        uint stream = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(12));
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(16));

        if (lane > Lane.Bulk)
            throw new InvalidDataException($"record {_count}: invalid lane {(byte)lane}");
        if (length <= 0 || length > _maxPayloadBytes)
            throw new InvalidDataException($"record {_count}: payload length {length} outside 1..{_maxPayloadBytes}");

        var payload = new byte[length];
        int payloadGot = ReadFully(payload);
        if (payloadGot != length)
            throw new InvalidDataException($"record {_count}: truncated payload, {payloadGot} of {length} bytes");

        int pad = RecordingFormat.Align(length) - length;
        if (pad > 0 && ReadFully(_header.AsSpan(0, pad)) != pad)
            throw new InvalidDataException($"record {_count}: truncated padding");

        if (_count == 0)
            FirstTimeUs = timeUs;
        LastTimeUs = timeUs;
        _count++;
        record = new RecordingRecord(timeUs, type, lane, flags, stream, payload);
        return true;
    }

    /// <summary>Reads a whole part into memory.</summary>
    public static List<RecordingRecord> ReadAll(string path, int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        using var reader = Open(path, maxPayloadBytes);
        var records = new List<RecordingRecord>();
        while (reader.TryRead(out var record))
        {
            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Reads every part of a series in <paramref name="directory"/> in rotation order. Parts are ordered by their
    /// five-digit number, not by file name, so <c>-00010</c> follows <c>-00009</c>.
    /// </summary>
    public static List<RecordingRecord> ReadSeries(
        string directory,
        string prefix = RecordingFormat.DefaultPrefix,
        int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        var records = new List<RecordingRecord>();
        foreach (string path in EnumerateSeries(directory, prefix))
        {
            records.AddRange(ReadAll(path, maxPayloadBytes));
        }

        return records;
    }

    /// <summary>Part paths of a series, in rotation order.</summary>
    public static IReadOnlyList<string> EnumerateSeries(string directory, string prefix = RecordingFormat.DefaultPrefix)
    {
        if (!System.IO.Directory.Exists(directory))
            return [];

        var parts = new List<(int Number, string Path)>();
        foreach (string path in System.IO.Directory.EnumerateFiles(directory, prefix + "-*" + RecordingFormat.Extension))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int dash = name.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(name.AsSpan(dash + 1), out int number))
                parts.Add((number, path));
        }

        parts.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        var result = new string[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            result[i] = parts[i].Path;
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }

    private int ReadFully(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = _stream.Read(destination[total..]);
            if (n == 0)
                break;
            total += n;
        }

        return total;
    }
}
