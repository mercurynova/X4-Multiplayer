using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using X4MP.Core.Net;

namespace X4MP.Transport;

/// <summary>
/// The TCP transport: Kestrel owns the socket and the pipes, <see cref="NodeConnectionHandler"/> hands each
/// accepted <see cref="ConnectionContext"/> to this listener, and the gateway consumes
/// <see cref="NodeListenerBase.AcceptAsync"/>.
/// </summary>
public sealed class TcpNodeListener(NetOptions options, TimeProvider? time = null) : NodeListenerBase(options, time)
{
    public override string Name => "tcp";

    /// <summary>
    /// Adopts a Kestrel connection and returns the connection whose <see cref="PipeNodeConnection.Completion"/>
    /// the handler must await (Kestrel closes the socket when the handler returns).
    /// </summary>
    internal PipeNodeConnection Adopt(ConnectionContext context)
    {
        var remote = context.RemoteEndPoint ?? new IPEndPoint(IPAddress.IPv6None, 0);
        var connection = CreateConnection(context.Transport, remote);
        connection.DrainOnClose = true;
        if (!Offer(connection))
        {
            connection.Abort(); // listener disposed or backlog full
        }

        return connection;
    }
}

/// <summary>Kestrel entry point for the node port (<c>UseConnectionHandler</c>).</summary>
public sealed class NodeConnectionHandler(TcpNodeListener listener) : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var node = listener.Adopt(connection);
        await using (node.ConfigureAwait(false))
        {
            using var registration = connection.ConnectionClosed.Register(static state => ((PipeNodeConnection)state!).Abort(), node);
            // Kestrel stops before the hosted services (the gateway): without this its graceful shutdown would wait for every open node
            // connection to end by itself, which for an idle peer is the 10 s heartbeat timeout plus the drain. Say goodbye right away instead.
            using var shutdownRegistration = connection.Features.Get<IConnectionLifetimeNotificationFeature>()?.ConnectionClosedRequested
                .Register(static state => ((PipeNodeConnection)state!).Close(X4MP.Proto.DisconnectCode.ServerShutdown, "server stopping"), node);
            await node.Completion.ConfigureAwait(false);
            await node.DrainInputAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); // let the peer read the Disconnect before the socket closes
            if (node.MustAbortSocket)
            {
                connection.Abort(); // a slow consumer never reads what is queued in the socket: without this Kestrel keeps the socket open for it
            }
        }
    }
}
