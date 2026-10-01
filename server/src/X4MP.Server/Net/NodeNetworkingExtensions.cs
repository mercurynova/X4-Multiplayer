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

        // Gateway and session hand-off. TryAdd so tests (and later the SessionActor) can pre-register replacements.
        services.TryAddSingleton(sp => GatewayState.FromOptions(sp.GetRequiredService<NetOptions>()));
        services.TryAddSingleton<SqliteNodeStore>();
        services.TryAddSingleton<IPlayerStore>(sp => sp.GetRequiredService<SqliteNodeStore>());
        services.TryAddSingleton<IBanStore>(sp => sp.GetRequiredService<SqliteNodeStore>());
        services.TryAddSingleton<IAdmissionHandler, DefaultAdmissionHandler>();
        services.TryAddSingleton(sp => new NodeGateway(
            sp.GetRequiredService<NetOptions>(),
            sp.GetRequiredService<GatewayState>(),
            sp.GetRequiredService<IPlayerStore>(),
            sp.GetRequiredService<IBanStore>(),
            sp.GetRequiredService<IAdmissionHandler>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<NodeGateway>>()));
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

/// <summary>Runs the <see cref="NodeGateway"/> accept loop for the lifetime of the host.</summary>
internal sealed class NodeGatewayService(NodeGateway gateway, INodeListener listener) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => gateway.RunAsync(listener, stoppingToken);
}
