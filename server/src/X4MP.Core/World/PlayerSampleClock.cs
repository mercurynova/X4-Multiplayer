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
/// <para>
/// Invariant (review 2026-10-28, 2): the stamps of one ship are strictly increasing AND never exceed <c>game time + MaxAheadSeconds + MaxOvershootSeconds</c>.
/// While game time is frozen (pause, a leave with a pausing menu) the natural stamp sits at the ceiling; the stamp may then creep past it by
/// <see cref="NudgeSeconds"/> per sample, but only up to the overshoot cap. Beyond that the sample is DROPPED (<see cref="TryStamp"/> returns false):
/// a duplicate stamp gives viewers zero dt, and an unbounded creep leaves stamps seconds ahead of game time after a long pause (the F1d symptom class).
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

    /// <summary>How far past the ceiling (game time + <see cref="MaxAheadSeconds"/>) the nudge may take a stamp while game time is frozen; further samples are dropped.</summary>
    internal const double MaxOvershootSeconds = 0.1;

    /// <summary>
    /// Computes the game time to stamp the sample with and updates the player's clock state. A zero <c>sampleTimeUs</c> or no WorldUpdate yet
    /// stamps <paramref name="authorityGameTime"/> (the pre-F9b behaviour) and drops the mapping. Returns false (no stamp) when a strictly
    /// increasing stamp would exceed the overshoot cap, i.e. game time is frozen: the caller drops the sample.
    /// </summary>
    internal static bool TryStamp(PlayerShipState ship, ulong sampleTimeUs, bool hasWorldUpdate, double authorityGameTime, ulong captureTimeUs, out double stamp)
    {
        if (sampleTimeUs == 0 || !hasWorldUpdate)
        {
            ship.ClockValid = false;
            stamp = authorityGameTime;
            return true;
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
        stamp = Math.Clamp(sample + ship.ClockOffset, authorityGameTime - MaxBehindSeconds, ceiling);
        if (stamp <= ship.LastStamp)
        {
            // Strictly increasing wins over the ceiling: while game time stalls (pause, a leave with a pausing menu) the natural stamp stays at the
            // ceiling, and clamping the nudge to it too gave every sample the SAME stamp (zero dt for viewers, review 2026-10-28 finding 2).
            // The stamp may therefore run ahead of game time by NudgeSeconds per sample (20 ms per second at 20 Hz); the clamp takes over again
            // once game time has caught up. A jump of the authority's time axis is handled by the mirror (it resets this state).
            stamp = ship.LastStamp + NudgeSeconds;
            if (stamp > ceiling + MaxOvershootSeconds)
            {
                return false; // game time is frozen: keep LastStamp, the sample is dropped
            }
        }

        ship.LastStamp = stamp;
        return true;
    }
}
