namespace X4MP.Protocol;

/// <summary>
/// The hash behind <c>InterestChecksum.xor_hash</c> (protocol.md 10.3): the XOR of <see cref="Mix"/> over the net_ids of the ghosts the
/// server believes the client holds, together with their count. Both sides compute the same value, so a mismatch means the
/// client's ghost set differs from the server's. The mixer is the splitmix64 finaliser (public domain), trivial to port to C++.
/// <para>
/// The schema comment says <c>hash64(net_id ^ version)</c>; the version is server-internal (a client never sees it), so the net_id alone is hashed.
/// </para>
/// </summary>
public static class InterestHash
{
    /// <summary>splitmix64 of <paramref name="netId"/>.</summary>
    public static ulong Mix(uint netId)
    {
        unchecked
        {
            ulong z = netId + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>The checksum value of a set of net_ids (order does not matter; ids must be distinct).</summary>
    public static ulong Of(IEnumerable<uint> netIds)
    {
        ArgumentNullException.ThrowIfNull(netIds);
        ulong hash = 0;
        foreach (uint id in netIds)
        {
            hash ^= Mix(id);
        }

        return hash;
    }
}
