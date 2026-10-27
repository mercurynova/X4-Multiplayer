using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Transport.Recording;

/// <summary>How a <see cref="MessageRecorder"/> writes its parts.</summary>
public sealed class MessageRecorderOptions
{
    /// <summary>Directory the parts are written to. Created if missing.</summary>
    public string Directory { get; set; } = "recordings";

    /// <summary>Base name of a part, before the five-digit number and the extension.</summary>
    public string Prefix { get; set; } = RecordingFormat.DefaultPrefix;

    /// <summary>A part rotates into the next one once it would exceed this many bytes.</summary>
    public long MaxPartBytes { get; set; } = RecordingFormat.DefaultMaxPartBytes;

    /// <summary>Seed of the recorded session, copied into every part header (0 = unknown).</summary>
    public ulong SessionSeed { get; set; }

    /// <summary>Start a new part at the beginning instead of continuing the existing numbering.</summary>
    public bool StartNewSeries { get; set; }

    /// <summary>Optional logger for rotation and write failures (the recorder never throws into the send path).</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// Writes the authority's outbound stream to rotating <c>.x4rec</c> parts (M4-26; docs/m4-plan.md W6).
///
/// <para>
/// The recorder sits on the one choke point every outbound frame passes - <see cref="PipeNodeConnection.TrySend"/> -
/// so it sees Control, Realtime and Bulk alike, whether the frame went over TCP or the UDP datagram path. It records
/// what the server <em>decided to send</em>, before any transport-level drop, because that is the stream a replay or a
/// mirror has to reproduce.
/// </para>
///
/// <para>
/// Recording must never take the server down, so every I/O failure is swallowed, counted in
/// <see cref="DroppedRecords"/> and logged once per rotation instead of being thrown into the sender's thread.
/// </para>
/// </summary>
public sealed class MessageRecorder : IDisposable
{
    private static readonly Action<ILogger, string, int, Exception?> s_logPart =
        LoggerMessage.Define<string, int>(
            Microsoft.Extensions.Logging.LogLevel.Information,
            new EventId(1, "RecordingPart"),
            "recording: writing {Path} (part {Part})");

    private static readonly Action<ILogger, string, Exception?> s_logWriteFailed =
        LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(2, "RecordingWriteFailed"),
            "recording: write to {Path} failed; records are being dropped");

    private readonly MessageRecorderOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly long _startTimestamp;

    private FileStream? _part;
    private string? _partPath;
    private int _partNumber;
    private long _records;
    private long _bytes;
    private long _dropped;
    private bool _disposed;

    /// <summary>Creates a recorder and opens its first part immediately.</summary>
    public MessageRecorder(MessageRecorderOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Directory))
            throw new ArgumentException("A recording directory is required.", nameof(options));
        if (options.MaxPartBytes <= RecordingFormat.FileHeaderSize)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxPartBytes must exceed the file header size.");

        _options = options;
        _time = time ?? TimeProvider.System;
        _startTimestamp = _time.GetTimestamp();
        System.IO.Directory.CreateDirectory(_options.Directory);
        _partNumber = _options.StartNewSeries ? 0 : NextFreePartNumber(_options.Directory, _options.Prefix) - 1;
        OpenPart();
    }

    /// <summary>Records accepted since the recorder started.</summary>
    public long Records => Interlocked.Read(ref _records);

    /// <summary>Bytes written to all parts, headers included.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Records that could not be written because of an I/O failure.</summary>
    public long DroppedRecords => Interlocked.Read(ref _dropped);

    /// <summary>Number of parts opened so far (1 once the first part exists).</summary>
    public int Parts
    {
        get
        {
            lock (_gate)
            {
                return _partNumber;
            }
        }
    }

    /// <summary>Path of the part currently being written, or null after <see cref="Dispose"/>.</summary>
    public string? CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _partPath;
            }
        }
    }

    /// <summary>Directory the parts are written to.</summary>
    public string Directory => _options.Directory;

    /// <summary>Observes one outbound frame as it is accepted for sending.</summary>
    /// <param name="frame">The frame, still referenced by the caller so its bytes are valid.</param>
    /// <param name="datagram">True when the frame was handed to the UDP Realtime path.</param>
    public void Record(OutboundFrame frame, bool datagram)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ReadOnlySpan<byte> bytes = frame.Bytes.Span[..frame.Length];
        if (bytes.Length < FrameCodec.HeaderSize)
            return;

        Record(
            frame.MessageType,
            frame.Lane,
            datagram ? RecordingMark.Datagram : RecordingMark.None,
            0,
            bytes[FrameCodec.HeaderSize..]);
    }

    /// <summary>Records one message with an explicit timestamp and stream.</summary>
    public void Record(MsgType type, Lane lane, RecordingMark flags, uint stream, ReadOnlySpan<byte> payload)
    {
        long timeUs = (long)_time.GetElapsedTime(_startTimestamp).TotalMicroseconds;
        Record(timeUs, type, lane, flags, stream, payload);
    }

    /// <summary>Records one message at an explicit monotonic timestamp (used by replay and mirror drivers).</summary>
    public void Record(long timeUs, MsgType type, Lane lane, RecordingMark flags, uint stream, ReadOnlySpan<byte> payload)
    {
        int size = RecordingFormat.RecordSize(payload.Length);
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                if (_part is null)
                    return;
                if (_part.Length + size > _options.MaxPartBytes && _part.Length > RecordingFormat.FileHeaderSize)
                    RotateLocked();

                Span<byte> header = stackalloc byte[RecordingFormat.RecordHeaderSize];
                RecordingFormat.WriteRecordHeader(header, timeUs, type, lane, flags, stream, payload.Length);
                _part.Write(header);
                _part.Write(payload);

                int pad = RecordingFormat.Align(payload.Length) - payload.Length;
                if (pad > 0)
                {
                    Span<byte> zeros = stackalloc byte[RecordingFormat.Alignment];
                    _part.Write(zeros[..pad]);
                }

                Interlocked.Add(ref _records, 1);
                Interlocked.Add(ref _bytes, size);
            }
            catch (IOException ex)
            {
                Interlocked.Increment(ref _dropped);
                FailLocked(ex);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    /// <summary>Flushes the current part to disk.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _part?.Flush();
            }
            catch (IOException ex)
            {
                FailLocked(ex);
            }
        }
    }

    /// <summary>Closes the current part. The recorder accepts no more records afterwards.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _part?.Dispose();
            }
            catch (IOException)
            {
                // Closing a file that is already gone must not fail a shutdown.
            }

            _part = null;
            _partPath = null;
        }
    }

    private void RotateLocked()
    {
        try
        {
            _part?.Dispose();
        }
        catch (IOException)
        {
            // A part that cannot be closed is still a finished part; the next one starts regardless.
        }

        _part = null;
        _partPath = null;
        OpenPart();
    }

    private void OpenPart()
    {
        _partNumber++;
        string path = RecordingFormat.PartPath(_options.Directory, _options.Prefix, _partNumber);
        try
        {
            System.IO.Directory.CreateDirectory(_options.Directory);
            var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1 << 16,
                FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[RecordingFormat.FileHeaderSize];
            var fileHeader = new RecordingFileHeader(
                RecordingFormat.Version,
                0,
                RecordingFormat.ProtocolStamp,
                (ulong)_time.GetUtcNow().ToUnixTimeMilliseconds(),
                _options.SessionSeed);
            RecordingFormat.WriteFileHeader(header, fileHeader);
            stream.Write(header);
            _part = stream;
            _partPath = path;
            Interlocked.Add(ref _bytes, RecordingFormat.FileHeaderSize);
            if (_options.Logger is { } log)
            {
                s_logPart(log, path, _partNumber, null);
            }
        }
        catch (IOException ex)
        {
            _part = null;
            _partPath = null;
            FailLocked(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _part = null;
            _partPath = null;
            FailLocked(ex);
        }
    }

    private void FailLocked(Exception ex)
    {
        string path = _partPath ?? "(none)";
        if (_options.Logger is { } log)
        {
            s_logWriteFailed(log, path, ex);
        }
        try
        {
            _part?.Dispose();
        }
        catch (IOException)
        {
            // Already failing; nothing else to do.
        }

        _part = null;
        _partPath = null;
    }

    /// <summary>Highest existing part number in <paramref name="directory"/> + 1, so a restart never overwrites a recording.</summary>
    public static int NextFreePartNumber(string directory, string prefix)
    {
        if (!System.IO.Directory.Exists(directory))
            return 0;

        int highest = 0;
        foreach (string path in System.IO.Directory.EnumerateFiles(directory, prefix + "-*" + RecordingFormat.Extension))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int dash = name.LastIndexOf('-');
            if (dash < 0 || !int.TryParse(name.AsSpan(dash + 1), out int number))
                continue;
            if (number > highest)
                highest = number;
        }

        return highest + 1;
    }

    /// <summary>Number of bytes the recorder has written to a part header, for readers that skip it.</summary>
    internal static int FileHeaderBytes => RecordingFormat.FileHeaderSize;

    /// <summary>Little-endian helper used by the replay driver to read a record header back.</summary>
    internal static uint ReadPayloadLength(ReadOnlySpan<byte> header) => BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
}
