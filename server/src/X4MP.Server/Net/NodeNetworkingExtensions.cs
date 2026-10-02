using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Persistence;
using X4MP.Transport;

namespace X4MP.Server.Net;

/// <summary>Registers the node-facing network stack (the single hook <c>ServerHost</c> calls).</summary>
public static class NodeNetworkingExtensions
{
    /// <summary>
    /// Binds <see cref="NetOptions"/> from <c>X4MP:Net</c> and, unless disabled, adds the Kestrel TCP endpoint
    /// for nodes (default 0.0.0.0:47780) served by <see cref="NodeConnectionHandler"/>, plus the listener.
    /// </summary>
    public static IServiceCollection AddNodeNetworking(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new NetOptions();
        configuration.GetSection(NetOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        if (!options.Enabled)
        {
            return services;
        }

        if (!IPEndPoint.TryParse(options.NodeTcpEndpoint, out var endpoint))
        {
            throw new InvalidOperationException($"X4MP:Net:NodeTcpEndpoint '{options.NodeTcpEndpoint}' is not a valid address:port.");
        }

        services.AddSingleton<TcpNodeListener>(sp => new TcpNodeListener(sp.GetRequiredService<NetOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<INodeListener>(sp => sp.GetRequiredService<TcpNodeListener>());
        services.AddSingleton<NodeConnectionHandler>();
        services.Configure<KestrelServerOptions>(kestrel =>
            kestrel.Listen(endpoint, listen => listen.UseConnectionHandler<NodeConnectionHandler>()));

        // The UDP Realtime lane (M1-09). Off with UdpPort 0; if the port cannot be bound the lane switches itself off (Welcome.udp_port = 0).
        if (options.UdpPort > 0)
        {
            options.ServerCaps |= (ulong)X4MP.Proto.Capability.UdpRealtime;
            services.AddSingleton(sp => new UdpRealtimeServer(endpoint.Address, options.UdpPort, sp.GetRequiredService<TimeProvider>(), sp.GetService<ILogger<UdpRealtimeServer>>()));
            services.AddSingleton<IUdpRealtimeHost>(sp => sp.GetRequiredService<UdpRealtimeServer>());
            services.AddHostedService<UdpRealtimeService>();
        }

        // Gateway and session hand-off. TryAdd so tests (and later the SessionActor) can pre-register replacements.
        services.TryAddSingleton(sp => GatewayState.FromOptions(sp.GetRequiredService<NetOptions>()));
        services.TryAddSingleton<SqliteNodeStore>();
        services.TryAddSingleton<IPlayerStore>(sp => sp.GetRequiredService<SqliteNodeStore>());
        services.TryAddSingleton<IBanStore>(sp => sp.GetRequiredService<SqliteNodeStore>());
        services.TryAddSingleton<IAdmissionHandler, DefaultAdmissionHandler>();
        services.TryAddSingleton<X4MP.Core.Mods.IModPolicyProvider>(sp =>
        {
            var monitor = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<X4MP.Core.Settings.ModManagementOptions>>();
            var net = sp.GetRequiredService<NetOptions>();
            return new X4MP.Core.Mods.InMemoryModPolicyProvider(() =>
            {
                var o = monitor.CurrentValue;
                // The legacy NetOptions switch still downgrades enforcement to Warn.
                return net.ExtensionsMismatchIsWarning && o.Enforcement == X4MP.Proto.ModEnforcement.Strict
                    ? new X4MP.Core.Settings.ModManagementOptions { SourceMode = o.SourceMode, UnknownDefault = o.UnknownDefault, Enforcement = X4MP.Proto.ModEnforcement.Warn }
                    : o;
            });
        });
        services.TryAddSingleton(sp => new NodeGateway(
            sp.GetRequiredService<NetOptions>(),
            sp.GetRequiredService<GatewayState>(),
            sp.GetRequiredService<IPlayerStore>(),
            sp.GetRequiredService<IBanStore>(),
            sp.GetRequiredService<IAdmissionHandler>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<NodeGateway>>(),
            sp.GetService<IUdpRealtimeHost>(),
            sp.GetRequiredService<X4MP.Core.Mods.IModPolicyProvider>(),
            sp.GetService<X4MP.Core.Mods.IModStore>()));
        services.AddHostedService<NodeGatewayService>();

        // Pipe thresholds from server-design 2.2: pause the writer at 1 MiB, resume at 512 KiB.
        services.Configure<SocketTransportOptions>(socket =>
        {
            socket.MaxWriteBufferSize = 1 << 20;
            socket.MaxReadBufferSize = 1 << 20;
        });
        return services;
    }
}

/// <summary>Opens the UDP Realtime socket before the gateway accepts nodes and closes it at shutdown.</summary>
internal sealed class UdpRealtimeService(UdpRealtimeServer server) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        server.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await server.DisposeAsync().ConfigureAwait(false);
}

/// <summary>Runs the <see cref="NodeGateway"/> accept loop for the lifetime of the host.</summary>
internal sealed class NodeGatewayService(NodeGateway gateway, INodeListener listener) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => gateway.RunAsync(listener, stoppingToken);
}
