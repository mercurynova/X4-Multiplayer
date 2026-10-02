using System.Net;
using System.Net.Sockets;

namespace X4MP.Server.Tests;

/// <summary>
/// Port allocation for tests that start a real Kestrel/UDP listener. Probing a free port and releasing it before the server binds is
/// inherently racy (another fixture or process can take it in between), so this hands out ports no other caller in this process has
/// received, and <see cref="StartWithRetryAsync{T}"/> restarts on fresh ports when the bind loses the race anyway.
/// </summary>
public static class TestPorts
{
    private const int MaxAttempts = 10;

    private static readonly object Gate = new();
    private static readonly HashSet<int> IssuedTcp = [];
    private static readonly HashSet<int> IssuedUdp = [];

    /// <summary>A loopback TCP port not yet handed out by this process.</summary>
    public static int FreeTcp()
    {
        while (true)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            lock (Gate)
            {
                if (IssuedTcp.Add(port))
                {
                    return port;
                }
            }
        }
    }

    /// <summary>A loopback UDP port not yet handed out by this process.</summary>
    public static int FreeUdp()
    {
        while (true)
        {
            using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)u.Client.LocalEndPoint!).Port;
            lock (Gate)
            {
                if (IssuedUdp.Add(port))
                {
                    return port;
                }
            }
        }
    }

    /// <summary>True when the exception (or anything inside it) is a "port already in use" bind failure.</summary>
    public static bool IsAddressInUse(Exception? ex)
    {
        switch (ex)
        {
            case null:
                return false;
            case SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }:
                return true;
            case AggregateException agg:
                return agg.InnerExceptions.Any(IsAddressInUse);
        }

        return ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase)
               || IsAddressInUse(ex.InnerException);
    }

    /// <summary>
    /// Runs <paramref name="attempt"/>; if it fails because a port was taken, runs it again (the attempt must allocate its own ports and
    /// release what it built when it throws). Any other failure propagates at once.
    /// </summary>
    public static async Task<T> StartWithRetryAsync<T>(Func<Task<T>> attempt)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                return await attempt().ConfigureAwait(false);
            }
            catch (Exception ex) when (i < MaxAttempts && IsAddressInUse(ex))
            {
            }
        }
    }

    /// <summary>The same for an attempt with no result.</summary>
    public static Task StartWithRetryAsync(Func<Task> attempt) =>
        StartWithRetryAsync(async () =>
        {
            await attempt().ConfigureAwait(false);
            return true;
        });

    /// <summary>Retry form for hosts built in a constructor: <paramref name="rebuild"/> disposes the failed host and builds a new one on new ports.</summary>
    public static async Task StartWithRetryAsync(Func<Task> start, Func<Task> rebuild)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                await start().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (i < MaxAttempts && IsAddressInUse(ex))
            {
                await rebuild().ConfigureAwait(false);
            }
        }
    }
}
