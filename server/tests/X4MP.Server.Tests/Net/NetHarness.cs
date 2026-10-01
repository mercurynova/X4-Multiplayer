using System.Net;
using System.Net.Sockets;
using Google.FlatBuffers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Net;
using X4MP.Transport;

namespace X4MP.Server.Tests.Net;

/// <summary>One client connection as a stream, over either transport.</summary>
public sealed class ClientHandle(Stream stream, IAsyncDisposable owner) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public async Task SendAsync(MsgType type, FlatBufferBuilder finished) =>
        await Stream.WriteAsync(FrameCodec.Encode(type, finished)).ConfigureAwait(false);

    public async Task SendRawAsync(byte[] bytes) => await Stream.WriteAsync(bytes).ConfigureAwait(false);

    /// <summary>Reads one frame (5 s timeout); null on end of stream.</summary>
    public async Task<Frame?> ReadAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return await FrameCodec.ReadFrameAsync(Stream, cancellationToken: cts.Token).ConfigureAwait(false);
    }

    /// <summary>Reads and decodes one frame, asserting its type.</summary>
    public async Task<T> ReadAsync<T>(MsgType expected, int timeoutMs = 5000) where T : struct, IFlatbufferObject
    {
        var frame = await ReadAsync(timeoutMs).ConfigureAwait(false) ?? throw new EndOfStreamException("connection closed");
        Assert.Equal(expected, frame.Type);
        return MessageRegistry.Default.Decode<T>(frame);
    }

    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

/// <summary>A transport under test: a listener plus a way to open client connections.</summary>
public abstract class NetHarness : IAsyncDisposable
{
    public abstract string Kind { get; }

    public abstract NodeListenerBase Listener { get; }

    public abstract Task<ClientHandle> ConnectAsync(IPAddress? remoteIp = null);

    public abstract ValueTask DisposeAsync();

    public static Task<NetHarness> CreateAsync(string kind, NetOptions? options = null, TimeProvider? time = null) =>
        kind == "tcp" ? TcpHarness.StartAsync(options ?? new NetOptions(), time) : Task.FromResult<NetHarness>(new InProcHarness(options ?? new NetOptions(), time));

    /// <summary>Accepts connections and runs <paramref name="handler"/> for each until disposed.</summary>
    public Task Serve(Func<INodeConnection, Task> handler, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            try
            {
                await foreach (var connection in Listener.AcceptAsync(ct))
                {
                    _ = Task.Run(() => handler(connection), CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }
}

public sealed class InProcHarness(NetOptions options, TimeProvider? time) : NetHarness
{
    private readonly InProcListener _listener = new(options, time);

    public override string Kind => "inproc";

    public override NodeListenerBase Listener => _listener;

    public override Task<ClientHandle> ConnectAsync(IPAddress? remoteIp = null)
    {
        var client = _listener.Connect(remoteIp);
        return Task.FromResult(new ClientHandle(client.GetStream(), client));
    }

    public override ValueTask DisposeAsync() => _listener.DisposeAsync();
}

public sealed class TcpHarness : NetHarness
{
    private readonly WebApplication _app;
    private readonly int _port;

    private TcpHarness(WebApplication app, int port)
    {
        _app = app;
        _port = port;
        Listener = app.Services.GetRequiredService<TcpNodeListener>();
    }

    public override string Kind => "tcp";

    public override NodeListenerBase Listener { get; }

    public static async Task<NetHarness> StartAsync(NetOptions options, TimeProvider? time)
    {
        int port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["X4MP:Net:NodeTcpEndpoint"] = $"127.0.0.1:{port}",
        });
        builder.Services.AddNodeNetworking(builder.Configuration);
        // Tests tune the options the extension bound (queue caps, frame size...).
        var bound = builder.Services.Single(d => d.ServiceType == typeof(NetOptions)).ImplementationInstance as NetOptions;
        CopyInto(options, bound!);
        if (time is not null)
        {
            builder.Services.AddSingleton(time);
        }

        var app = builder.Build();
        await app.StartAsync().ConfigureAwait(false);
        return new TcpHarness(app, port);
    }

    private static void CopyInto(NetOptions from, NetOptions to)
    {
        foreach (var p in typeof(NetOptions).GetProperties())
        {
            if (p.Name is nameof(NetOptions.NodeTcpEndpoint))
            {
                continue;
            }

            p.SetValue(to, p.GetValue(from));
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public override async Task<ClientHandle> ConnectAsync(IPAddress? remoteIp = null)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _port).ConfigureAwait(false);
        return new ClientHandle(client.GetStream(), new TcpOwner(client));
    }

    public override async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class TcpOwner(TcpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Small builders for test frames.</summary>
public static class TestFrames
{
    public static FlatBufferBuilder Ping(uint seq = 1)
    {
        var fbb = new FlatBufferBuilder(32);
        fbb.Finish(X4MP.Proto.Ping.CreatePing(fbb, seq, 12345).Value);
        return fbb;
    }
}
