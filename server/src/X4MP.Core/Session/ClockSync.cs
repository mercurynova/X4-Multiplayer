namespace X4MP.Core.Session;

/// <summary>
/// RTT and clock-offset estimator for one node (protocol.md 7). The server pings the node; every <c>Pong</c>
/// yields one sample. RTT is smoothed with an EWMA (alpha 1/8); the offset comes from the sample with the
/// lowest RTT among the last <see cref="Window"/> samples, because a low RTT means the least queueing delay
/// and so the most symmetric path.
/// <para>Offsets are <c>node clock minus server clock</c> in microseconds.</para>
/// </summary>
public sealed class ClockSync(int window = ClockSync.DefaultWindow, double alpha = 0.125)
{
    public const int DefaultWindow = 16;

    /// <summary>Samples above this are discarded as garbage (a stuck node, a wrapped clock).</summary>
    public const long MaxPlausibleRttUs = 60_000_000;

    private readonly (long RttUs, long OffsetUs)[] _ring = new (long, long)[Math.Max(1, window)];
    private int _next;
    private int _count;
    private double _smoothedUs;

    public int Window => _ring.Length;

    /// <summary>Samples accepted so far (capped at nothing; the filter only looks at the last <see cref="Window"/>).</summary>
    public long TotalSamples { get; private set; }

    /// <summary>Samples refused as implausible.</summary>
    public long Rejected { get; private set; }

    /// <summary>EWMA of the RTT in microseconds; 0 before the first sample.</summary>
    public double SmoothedRttUs => _smoothedUs;

    public double SmoothedRttMs => _smoothedUs / 1000.0;

    /// <summary>RTT of the newest accepted sample.</summary>
    public long LatestRttUs { get; private set; }

    /// <summary>Lowest RTT inside the window; 0 before the first sample.</summary>
    public long MinRttUs { get; private set; }

    /// <summary>Offset (node minus server, microseconds) of the lowest-RTT sample inside the window.</summary>
    public long OffsetUs { get; private set; }

    public bool HasSample => _count > 0;

    /// <summary>
    /// Adds one Ping/Pong exchange. All times are microseconds on the clock named in the parameter.
    /// </summary>
    /// <param name="serverSendUs">Server clock when the Ping left (<c>echo_send_time_us</c>).</param>
    /// <param name="nodeRecvUs">Node clock when the Ping arrived (<c>recv_time_us</c>).</param>
    /// <param name="nodeReplyUs">Node clock when the Pong left (<c>reply_time_us</c>).</param>
    /// <param name="serverRecvUs">Server clock when the Pong arrived.</param>
    /// <returns>False if the sample is implausible (negative or absurd RTT) and was ignored.</returns>
    public bool AddSample(long serverSendUs, long nodeRecvUs, long nodeReplyUs, long serverRecvUs)
    {
        // RTT without the time the node spent holding the message.
        long rtt = Rtt(serverSendUs, nodeRecvUs, nodeReplyUs, serverRecvUs);
        if (rtt < 0 || rtt > MaxPlausibleRttUs)
        {
            Rejected++;
            return false;
        }

        long offset = Offset(serverSendUs, nodeRecvUs, nodeReplyUs, serverRecvUs);
        _ring[_next] = (rtt, offset);
        _next = (_next + 1) % _ring.Length;
        _count = Math.Min(_count + 1, _ring.Length);
        TotalSamples++;
        LatestRttUs = rtt;
        _smoothedUs = TotalSamples == 1 ? rtt : _smoothedUs + (rtt - _smoothedUs) * alpha;

        // Lowest RTT wins; among equals the newest (walk from the newest backwards, strict less-than).
        long bestRtt = long.MaxValue;
        long bestOffset = 0;
        for (int i = 0; i < _count; i++)
        {
            var sample = _ring[((_next - 1 - i) % _ring.Length + _ring.Length) % _ring.Length];
            if (sample.RttUs < bestRtt)
            {
                bestRtt = sample.RttUs;
                bestOffset = sample.OffsetUs;
            }
        }

        MinRttUs = bestRtt;
        OffsetUs = bestOffset;
        return true;
    }

    /// <summary><c>(now - sent) - (reply - recv)</c>.</summary>
    public static long Rtt(long serverSendUs, long nodeRecvUs, long nodeReplyUs, long serverRecvUs) =>
        serverRecvUs - serverSendUs - (nodeReplyUs - nodeRecvUs);

    /// <summary>
    /// NTP offset <c>((recv - send) + (reply - now)) / 2</c>: node clock minus server clock, assuming the two
    /// directions take equally long.
    /// </summary>
    public static long Offset(long serverSendUs, long nodeRecvUs, long nodeReplyUs, long serverRecvUs) =>
        ((nodeRecvUs - serverSendUs) + (nodeReplyUs - serverRecvUs)) / 2;

    public void Reset()
    {
        Array.Clear(_ring);
        _next = 0;
        _count = 0;
        _smoothedUs = 0;
        TotalSamples = 0;
        Rejected = 0;
        LatestRttUs = 0;
        MinRttUs = 0;
        OffsetUs = 0;
    }
}
