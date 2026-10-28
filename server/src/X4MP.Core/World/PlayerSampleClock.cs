namespace X4MP.Core.World;

/// <summary>
/// Maps a player's <c>PlayerState.sample_time_us</c> (the sender's estimate of the server clock) onto the authority's game-time axis, so
/// consecutive samples of one ship are evenly spaced and strictly increasing (F9b). Stamping every sample with the latest WorldUpdate's
/// game time instead put 20 Hz samples 4-400 ms apart (bursts and duplicates), which the ghost driver and the client's delay controller read as jitter.
/// <para>
/// The mapping is <c>stamp = sample_time + offset</c>. The offset comes from the latest WorldUpdate pair (<c>game_time</c> sampled at
/// <c>capture_time_us</c>, both on the authority's side), is slewed per sample so a jumpy pair estimate cannot jitter the spacing, and re-snaps
/// when it moves by more than <see cref="ResnapSeconds"/> (a game pause, a clock-estimate jump). When the two clocks disagree by more than
/// <see cref="MaxDomainGapSeconds"/> (not the same clock, or garbage) the arrival-based offset (<c>game_time - sample_time</c>) is used instead.
/// </para>
/// </summary>
internal static class PlayerSampleClock
{
    /// <summary>Most the offset follows the target per sample (20 Hz: 40 ms per second).</summary>
    internal const double SlewSecondsPerSample = 0.002;

    /// <summary>An offset target further than this from the current offset replaces it at once.</summary>
    internal const double ResnapSeconds = 0.25;

    /// <summary>A sample further than this from the WorldUpdate capture time is not trusted to share its clock.</summary>
    internal const double MaxDomainGapSeconds = 1.0;

    /// <summary>The stamp is kept within [game time - Behind, game time + Ahead].</summary>
    internal const double MaxBehindSeconds = 1.0;

    internal const double MaxAheadSeconds = 0.5;

    /// <summary>Minimum step when a stamp would not increase (duplicate or reordered sample time).</summary>
    internal const double NudgeSeconds = 0.001;

    /// <summary>
    /// Returns the game time to stamp the sample with and updates the player's clock state. A zero <c>sampleTimeUs</c> or no WorldUpdate yet
    /// returns <paramref name="authorityGameTime"/> (the pre-F9b behaviour) and drops the mapping.
    /// </summary>
    internal static double Stamp(PlayerShipState ship, ulong sampleTimeUs, bool hasWorldUpdate, double authorityGameTime, ulong captureTimeUs)
    {
        if (sampleTimeUs == 0 || !hasWorldUpdate)
        {
            ship.ClockValid = false;
            return authorityGameTime;
        }

        double sample = sampleTimeUs * 1e-6;
        double target = authorityGameTime - sample;
        if (captureTimeUs != 0)
        {
            double capture = captureTimeUs * 1e-6;
            if (Math.Abs(sample - capture) <= MaxDomainGapSeconds)
            {
                target = authorityGameTime - capture;
            }
        }

        // First sample, or the sender's clock went back by more than a second (its estimate restarted): start over.
        if (!ship.ClockValid || (ship.ClockSampleUs > sampleTimeUs && ship.ClockSampleUs - sampleTimeUs > 1_000_000UL))
        {
            ship.ClockOffset = target;
            ship.ClockValid = true;
            ship.LastStamp = double.NegativeInfinity;
        }
        else
        {
            double diff = target - ship.ClockOffset;
            if (Math.Abs(diff) > ResnapSeconds)
            {
                ship.ClockOffset = target;
            }
            else
            {
                ship.ClockOffset += Math.Clamp(diff, -SlewSecondsPerSample, SlewSecondsPerSample);
            }
        }

        ship.ClockSampleUs = sampleTimeUs;
        double ceiling = authorityGameTime + MaxAheadSeconds;
        double stamp = Math.Clamp(sample + ship.ClockOffset, authorityGameTime - MaxBehindSeconds, ceiling);
        if (stamp <= ship.LastStamp)
        {
            stamp = Math.Min(ship.LastStamp + NudgeSeconds, Math.Max(ceiling, ship.LastStamp));
        }

        ship.LastStamp = stamp;
        return stamp;
    }
}
