using System.IO.Pipelines;
using System.Net;
using X4MP.Core.Net;

namespace X4MP.Transport;

/// <summary>
/// The in-process transport (server-design 2.2): a pair of <see cref="Pipe"/>s per connection. Used by
/// tests and by FakeNode's inproc mode. Fast, deterministic, and proves the core is transport-agnostic.
/// </summary>
public sealed class InProcListener(NetOptions options, TimeProvider? time = null) : NodeListenerBase(options, time)
{
    private static readonly PipeOptions PipeConfig = new(
        pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19, useSynchronizationContext: false);

    public override string Name => "inproc";

    /// <summary>
    /// Connects a new in-process client. <paramref name="remoteIp"/> is what the server sees as the peer
    /// address (default 127.0.0.1), which lets tests exercise per-IP limits and CIDR bans.
    /// </summary>
    public InProcClient Connect(IPAddress? remoteIp = null, int port = 0)
    {
        var clientToServer = new Pipe(PipeConfig);
        var serverToClient = new Pipe(PipeConfig);
        var serverSide = new DuplexPipe(clientToServer.Reader, serverToClient.Writer);
        var remote = new IPEndPoint(remoteIp ?? IPAddress.Loopback, port);
        var connection = CreateConnection(serverSide, remote);
        if (!Offer(connection))
        {
            connection.Abort();
        }

        return new InProcClient(new DuplexPipe(serverToClient.Reader, clientToServer.Writer), connection.Completion);
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}

/// <summary>The client end of an in-process connection: raw pipes plus a duplex <see cref="Stream"/> view.</summary>
public sealed class InProcClient : IDuplexPipe, IAsyncDisposable
{
    private readonly IDuplexPipe _pipe;
    private Stream? _stream;

    internal InProcClient(IDuplexPipe pipe, Task serverClosed)
    {
        _pipe = pipe;
        ServerClosed = serverClosed;
    }

    public PipeReader Input => _pipe.Input;

    public PipeWriter Output => _pipe.Output;

    /// <summary>Completes when the server side of this connection is fully closed.</summary>
    public Task ServerClosed { get; }

    /// <summary>A read/write stream over the pipes (the same view a TCP client gets from <c>NetworkStream</c>).</summary>
    public Stream GetStream() => _stream ??= new DuplexStream(_pipe.Input.AsStream(), _pipe.Output.AsStream());

    /// <summary>Closes the client side (the server sees end-of-stream).</summary>
    public async ValueTask DisposeAsync()
    {
        await _pipe.Output.CompleteAsync().ConfigureAwait(false);
        await _pipe.Input.CompleteAsync().ConfigureAwait(false);
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            read.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            write.WriteAsync(buffer, cancellationToken);
    }
}
