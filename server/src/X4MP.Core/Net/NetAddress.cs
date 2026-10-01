using System.Net;

namespace X4MP.Core.Net;

/// <summary>Helpers for the peer address used by per-IP limits and bans.</summary>
public static class NetAddress
{
    /// <summary>The peer IP with IPv4-mapped IPv6 addresses folded to IPv4; null for non-IP endpoints.</summary>
    public static IPAddress? Normalize(EndPoint? endPoint)
    {
        if (endPoint is not IPEndPoint ip)
        {
            return null;
        }

        return ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
    }
}
