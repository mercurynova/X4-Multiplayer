using System.Net;
using System.Net.Sockets;
using Google.FlatBuffers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Core.Session;
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

    /// <summary>Set when created with <c>withGateway</c>: the gateway under test and its collaborators.</summary>
    public NodeGateway? Gateway { get; protected set; }

    public GatewayState? State { get; protected set; }

    public InMemoryNodeStore? Store { get; protected set; }

    public NetOptions Options { get; protected set; } = new();

    public abstract NodeListenerBase Listener { get; }

    public abstract Task<ClientHandle> ConnectAsync(IPAddress? remoteIp = null);

    public abstract ValueTask DisposeAsync();

    /// <summary>Set when the harness runs the SessionActor for the test (<c>useActor: true</c>); disposed with the harness.</summary>
    protected ActorFixture? OwnedActor { get; set; }

    protected async ValueTask DisposeOwnedActorAsync()
    {
        if (OwnedActor is { } owned)
        {
            OwnedActor = null;
            await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <param name="withGateway">Run a real <see cref="NodeGateway"/> over the listener (in-memory identity stores).</param>
    /// <param name="store">Identity/ban store for the gateway (default: a fresh <see cref="InMemoryNodeStore"/>).</param>
    /// <param name="handler">Admission handler factory (default: <see cref="DefaultAdmissionHandler"/>).</param>
    /// <param name="useActor">With no <paramref name="handler"/>, run a <see cref="X4MP.Core.Session.SessionActor"/> as the admission handler instead.</param>
    public static async Task<NetHarness> CreateAsync(
        string kind, NetOptions? options = null, TimeProvider? time = null, bool withGateway = false,
        InMemoryNodeStore? store = null, Func<GatewayState, IAdmissionHandler>? handler = null, bool useActor = false,
        X4MP.Core.Mods.IModPolicyProvider? modPolicy = null, X4MP.Core.Mods.IModStore? modStore = null)
    {
        options ??= new NetOptions();
        ActorFixture? ownedActor = null;
        if (withGateway && handler is null && useActor)
        {
            ownedActor = new ActorFixture(options);
            handler = ownedActor.Handler;
        }

        NetHarness harness;
        if (kind == "tcp")
        {
            harness = await TcpHarness.StartAsync(options, time, withGateway, store, handler, modPolicy, modStore).ConfigureAwait(false);
        }
        else
        {
            var inproc = new InProcHarness(options, time);
            if (withGateway)
            {
                inproc.StartGateway(store ?? new InMemoryNodeStore(), handler, modPolicy, modStore);
            }

            harness = inproc;
        }

        harness.Options = options;
        harness.OwnedActor = ownedActor;
        return harness;
    }

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
    private readonly CancellationTokenSource _stop = new();
    private Task? _gatewayLoop;

    public override string Kind => "inproc";

    public void StartGateway(InMemoryNodeStore store, Func<GatewayState, IAdmissionHandler>? handler, X4MP.Core.Mods.IModPolicyProvider? modPolicy = null, X4MP.Core.Mods.IModStore? modStore = null)
    {
        Store = store;
        State = GatewayState.FromOptions(options);
        Gateway = new NodeGateway(options, State, store, store, handler?.Invoke(State), time, modPolicy: modPolicy, modStore: modStore);
        _gatewayLoop = Gateway.RunAsync(_listener, _stop.Token);
    }

    public override NodeListenerBase Listener => _listener;

    public override Task<ClientHandle> ConnectAsync(IPAddress? remoteIp = null)
    {
        var client = _listener.Connect(remoteIp);
        return Task.FromResult(new ClientHandle(client.GetStream(), client));
    }

    public override async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_gatewayLoop is not null)
        {
            await _gatewayLoop;
        }

        await _listener.DisposeAsync();
        _stop.Dispose();
        await DisposeOwnedActorAsync();
    }
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

    /// <summary>The ephemeral port the node listener is bound to.</summary>
    public int Port => _port;

    public override NodeListenerBase Listener { get; }

    public static Task<NetHarness> StartAsync(
        NetOptions options, TimeProvider? time, bool withGateway, InMemoryNodeStore? store, Func<GatewayState, IAdmissionHandler>? handler,
        X4MP.Core.Mods.IModPolicyProvider? modPolicy = null, X4MP.Core.Mods.IModStore? modStore = null) =>
        TestPorts.StartWithRetryAsync(() => StartOnceAsync(options, time, withGateway, store, handler, modPolicy, modStore));

    private static async Task<NetHarness> StartOnceAsync(
        NetOptions options, TimeProvider? time, bool withGateway, InMemoryNodeStore? store, Func<GatewayState, IAdmissionHandler>? handler,
        X4MP.Core.Mods.IModPolicyProvider? modPolicy, X4MP.Core.Mods.IModStore? modStore)
    {
        int port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["X4MP:Net:NodeTcpEndpoint"] = $"127.0.0.1:{port}",
        });
        InMemoryNodeStore? memory = null;
        if (withGateway)
        {
            memory = store ?? new InMemoryNodeStore();
            builder.Services.AddSingleton<IPlayerStore>(memory);
            builder.Services.AddSingleton<IBanStore>(memory);
            if (handler is not null)
            {
                builder.Services.AddSingleton(sp => handler(sp.GetRequiredService<GatewayState>()));
            }
        }

        if (modStore is not null)
        {
            builder.Services.AddSingleton(modStore);
        }

        if (modPolicy is not null)
        {
            builder.Services.AddSingleton(modPolicy);
        }

        builder.Services.AddNodeNetworking(builder.Configuration);
        // Tests tune the options the extension bound (queue caps, frame size...).
        var bound = builder.Services.Single(d => d.ServiceType == typeof(NetOptions)).ImplementationInstance as NetOptions;
        CopyInto(options, bound!);
        if (time is not null)
        {
            builder.Services.AddSingleton(time);
        }

        if (!withGateway)
        {
            // Transport-only tests consume the listener themselves; keep the gateway out of the way.
            foreach (var d in builder.Services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "NodeGatewayService").ToList())
            {
                builder.Services.Remove(d);
            }
        }

        var app = builder.Build();
        var harness = new TcpHarness(app, port);
        if (withGateway)
        {
            harness.Store = memory;
            harness.State = app.Services.GetRequiredService<GatewayState>();
            harness.Gateway = app.Services.GetRequiredService<NodeGateway>();
        }

        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            // Lost the port race: release what was built so the retry starts clean.
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return harness;
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

    private static int FreePort() => TestPorts.FreeTcp();

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
        await DisposeOwnedActorAsync().ConfigureAwait(false);
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
