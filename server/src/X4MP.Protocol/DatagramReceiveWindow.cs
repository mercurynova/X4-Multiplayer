namespace X4MP.Protocol;

/// <summary>
/// The receive side of the datagram sequence numbers (protocol.md 3.3): remembers which of the last 64 sequence numbers arrived, so duplicates and
/// datagrams older than the highest seen minus 64 are refused, and produces the <c>ack</c> / <c>ack_bits</c> a header carries. Not thread-safe:
/// the owner serialises access.
/// </summary>
public sealed class DatagramReceiveWindow
{
    /// <summary>A datagram this far behind the newest one (or more) is too old to be accepted.</summary>
    public const int WindowSize = 64;

    private bool _any;
    private uint _first;
    private uint _highest;
    private ulong _bits; // bit i = (_highest - i) arrived

    /// <summary>True once a datagram was accepted.</summary>
    public bool HasReceived => _any;

    /// <summary>Highest sequence number accepted (the header's <c>ack</c>); 0 before the first.</summary>
    public uint Ack => _any ? _highest : 0;

    /// <summary>The header's <c>ack_bits</c>: bit i set = <c>Ack - 1 - i</c> also arrived (32-datagram window).</summary>
    public uint AckBits => _any ? (uint)(_bits >> 1) : 0;

    /// <summary>Datagrams accepted.</summary>
    public long Accepted { get; private set; }

    /// <summary>Datagrams refused as duplicates.</summary>
    public long Duplicates { get; private set; }

    /// <summary>Datagrams refused as older than the window.</summary>
    public long TooOld { get; private set; }

    /// <summary>Sequence numbers between the first and the highest accepted that never arrived (so far): a loss estimate that ignores reordering.</summary>
    public long Missing => _any ? Math.Max(0, (long)unchecked(_highest - _first) + 1 - Accepted) : 0;

    /// <summary>Loss estimate in percent over everything accepted since the start (0 before the first datagram).</summary>
    public float LossPercent => _any && Accepted + Missing > 0 ? 100f * Missing / (Accepted + Missing) : 0f;

    /// <summary>Starts over (a peer that re-bound from a new endpoint may restart its numbering).</summary>
    public void Reset()
    {
        _any = false;
        _bits = 0;
        Accepted = 0;
    }

    /// <summary>Records <paramref name="seq"/>. False when it is a duplicate or too old (the datagram must be dropped).</summary>
    public bool Accept(uint seq)
    {
        if (!_any)
        {
            _any = true;
            _first = seq;
            _highest = seq;
            _bits = 1;
            Accepted++;
            return true;
        }

        int diff = unchecked((int)(seq - _highest));
        if (diff > 0)
        {
            _bits = diff >= WindowSize ? 0 : _bits << diff;
            _bits |= 1;
            _highest = seq;
            Accepted++;
            return true;
        }

        int back = -diff;
        if (back >= WindowSize)
        {
            TooOld++;
            return false;
        }

        ulong bit = 1UL << back;
        if ((_bits & bit) != 0)
        {
            Duplicates++;
            return false;
        }

        _bits |= bit;
        Accepted++;
        return true;
    }
}
