using System.Net;

namespace X4MP.Server.Auth;

/// <summary>
/// Source-IP allow-list (server-design 4.3): loopback always, private ranges unless disabled, plus any configured
/// CIDRs. An unknown remote address (in-process TestServer) counts as loopback.
/// </summary>
public sealed class NetworkAllowList
{
    private static readonly string[] PrivateRanges =
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "fc00::/7", "fe80::/10",
    ];

    private readonly List<IPNetwork> _networks = [];

    public NetworkAllowList(AdminAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var cidrs = options.AllowPrivateNetworks ? PrivateRanges.Concat(options.AllowedNetworks) : options.AllowedNetworks;
        foreach (var cidr in cidrs)
        {
            if (!IPNetwork.TryParse(cidr.Trim(), out var network))
            {
                throw new InvalidOperationException($"X4MP:Admin:AllowedNetworks contains an invalid CIDR range: '{cidr}'.");
            }

            _networks.Add(network);
        }
    }

    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) || _networks.Exists(n => n.Contains(address));
    }
}
