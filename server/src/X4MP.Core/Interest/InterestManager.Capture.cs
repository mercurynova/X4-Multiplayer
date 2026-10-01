using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Interest;

/// <summary>What the manager knows about one sector it asked the authority to capture.</summary>
public sealed class CaptureSectorState
{
    /// <summary>The <c>CaptureSet</c> epoch that first included the sector (a <c>SectorComplete</c> must carry at least this epoch).</summary>
    public uint AddedEpoch { get; internal set; }

    /// <summary>The authority completed the sector: its spawns are all in the mirror.</summary>
    public bool Complete { get; internal set; }

    public uint CompleteEpoch { get; internal set; }

    public long LastNeeded { get; internal set; }

    public int RateHz { get; internal set; }
}

/// <summary>A sector in the capture set and the rate asked for.</summary>
public readonly record struct CaptureRate(ushort Sector, int RateHz);

/// <summary>A focus sphere (centre in whole metres, quantised so a moving player does not resend the set every sample).</summary>
public readonly record struct CaptureFocusSphere(ushort Sector, int X, int Y, int Z, int RadiusM, int RateHz);

public sealed partial class InterestManager
{
    private readonly Dictionary<ushort, CaptureSectorState> _capture = [];
    private readonly List<CaptureRate> _lastSectors = [];
    private readonly List<CaptureFocusSphere> _lastFocus = [];
    private readonly Dictionary<ushort, int> _neededScratch = [];
    private readonly List<CaptureRate> _sectorsScratch = [];
    private readonly List<CaptureFocusSphere> _focusScratch = [];
    private uint _captureEpoch;
    private long _lastCaptureSent;
    private bool _everSent;
    private bool _forceCapture;

    /// <summary>Epoch of the last <c>CaptureSet</c> sent (0 = none yet).</summary>
    public uint CaptureEpoch => _captureEpoch;

    /// <summary>The sectors of the last <c>CaptureSet</c> sent.</summary>
    public IReadOnlyList<CaptureRate> LastCaptureSectors => _lastSectors;

    public IReadOnlyList<CaptureFocusSphere> LastCaptureFocus => _lastFocus;

    /// <summary>Capture bookkeeping of a sector, or null when it is not in the capture set.</summary>
    public CaptureSectorState? GetCaptureState(ushort sector) => _capture.GetValueOrDefault(sector);

    private bool IsCaptureComplete(ushort sector) => _capture.TryGetValue(sector, out var state) && state.Complete;

    /// <summary>A new authority (not a resume) knows nothing of what was captured: every sector must be completed again.</summary>
    private void ResetCaptureCompletion()
    {
        foreach (var state in _capture.Values)
        {
            state.Complete = false;
            state.AddedEpoch = _captureEpoch + 1;
        }
    }

    /// <summary>
    /// Sends the authority its <c>CaptureSet</c> when it changed: the union of every client's sectors (at the highest tier rate asked
    /// for), the admin map views, sectors nobody needs yet that are inside their <c>CaptureEvictSeconds</c> grace, and a focus sphere
    /// around each player ship. At most once per <c>CaptureSetMinIntervalMs</c>; a sector that stays unneeded past the grace is dropped
    /// from the set and its transient entities are evicted from the mirror.
    /// </summary>
    private void MaybeSendCaptureSet(long now)
    {
        var opt = Opt;
        BuildCapture(now, opt);

        if (!_transport.AuthorityReady)
        {
            return;
        }

        if (_everSent && now - _lastCaptureSent < MillisecondsToTicks(opt.CaptureSetMinIntervalMs))
        {
            return;
        }

        if (!_forceCapture
            && (_everSent
                ? _sectorsScratch.SequenceEqual(_lastSectors) && _focusScratch.SequenceEqual(_lastFocus)
                : _sectorsScratch.Count == 0 && _focusScratch.Count == 0))
        {
            return; // nothing changed (or nothing to ask for yet)
        }

        uint epoch = _captureEpoch + 1;
        var message = new CaptureSetT
        {
            Epoch = epoch,
            Sectors = [.. _sectorsScratch.Select(s => new CaptureSectorT { Sector = s.Sector, RateHz = (byte)Math.Min(s.RateHz, byte.MaxValue) })],
            Focus =
            [
                .. _focusScratch.Select(f => new CaptureFocusT
                {
                    Sector = f.Sector,
                    Center = new Vec3fT { X = f.X, Y = f.Y, Z = f.Z },
                    RadiusM = f.RadiusM,
                    RateHz = (byte)Math.Min(f.RateHz, byte.MaxValue),
                }),
            ],
        };
        var payload = MessageEncoder.EncodePayload(b => CaptureSet.Pack(b, message), 128 + (_sectorsScratch.Count * 8) + (_focusScratch.Count * 24));
        var frame = OutboundFrame.Create(MsgType.CaptureSet, payload);
        SendResult result;
        try
        {
            result = _transport.SendToAuthority(frame);
        }
        finally
        {
            frame.Release();
        }

        if (!Accepted(result))
        {
            return; // try again on the next tick
        }

        _captureEpoch = epoch;
        _lastCaptureSent = now;
        _everSent = true;
        _forceCapture = false;
        Stats.CaptureSetsSent++;

        // Bookkeeping: new sectors start incomplete at this epoch; sectors that left the set are evicted from the mirror.
        _sectorScratchSet.Clear();
        foreach (var s in _sectorsScratch)
        {
            _sectorScratchSet.Add(s.Sector);
            if (_capture.TryGetValue(s.Sector, out var state))
            {
                state.RateHz = s.RateHz;
            }
            else
            {
                _capture[s.Sector] = new CaptureSectorState { AddedEpoch = epoch, LastNeeded = now, RateHz = s.RateHz };
            }
        }

        _sectorScratch2.Clear();
        foreach (var sector in _capture.Keys)
        {
            if (!_sectorScratchSet.Contains(sector))
            {
                _sectorScratch2.Add(sector);
            }
        }

        foreach (ushort sector in _sectorScratch2)
        {
            _capture.Remove(sector);
            _mirror.EvictTransient(sector);
        }

        _lastSectors.Clear();
        _lastSectors.AddRange(_sectorsScratch);
        _lastFocus.Clear();
        _lastFocus.AddRange(_focusScratch);
    }

    private readonly HashSet<ushort> _sectorScratchSet = [];
    private readonly List<ushort> _sectorScratch2 = [];

    private long MillisecondsToTicks(int ms) => (long)(ms / 1000.0 * _time.TimestampFrequency);

    /// <summary>Builds the wanted capture set into the scratch lists (sorted, so equality means "nothing to say").</summary>
    private void BuildCapture(long now, InterestOptions opt)
    {
        _neededScratch.Clear();
        foreach (var c in _clients.Values)
        {
            foreach (var sub in c.Subs.Values)
            {
                Need(sub.Sector, opt.RateHz(sub.Tier));
            }
        }

        foreach (var sectors in _adminViews.Values)
        {
            foreach (ushort sector in sectors)
            {
                Need(sector, opt.AdjacentRateHz);
            }
        }

        _sectorsScratch.Clear();
        long grace = Seconds(opt.CaptureEvictSeconds);
        foreach (var (sector, rate) in _neededScratch)
        {
            if (_capture.TryGetValue(sector, out var state))
            {
                state.LastNeeded = now;
            }

            _sectorsScratch.Add(new CaptureRate(sector, rate));
        }

        foreach (var (sector, state) in _capture)
        {
            if (!_neededScratch.ContainsKey(sector) && now - state.LastNeeded < grace)
            {
                _sectorsScratch.Add(new CaptureRate(sector, opt.AdjacentRateHz)); // nobody needs it now: kept warm until the grace ends
            }
        }

        _sectorsScratch.Sort(static (a, b) => a.Sector.CompareTo(b.Sector));

        _focusScratch.Clear();
        int quantum = Math.Max(1, opt.NearRadiusM / 4);
        int radius = opt.NearRadiusM + quantum;
        foreach (var c in _clients.Values)
        {
            if (c.Sector == 0)
            {
                continue;
            }

            var sphere = new CaptureFocusSphere(
                c.Sector,
                Quantise(c.Px, quantum),
                Quantise(c.Py, quantum),
                Quantise(c.Pz, quantum),
                radius,
                opt.NearRateHz);
            if (!_focusScratch.Contains(sphere))
            {
                _focusScratch.Add(sphere);
            }
        }

        _focusScratch.Sort(static (a, b) =>
        {
            int cmp = a.Sector.CompareTo(b.Sector);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.X.CompareTo(b.X);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.Z.CompareTo(b.Z);
        });
    }

    private void Need(ushort sector, int rateHz)
    {
        if (rateHz <= 0)
        {
            return;
        }

        _neededScratch[sector] = Math.Max(_neededScratch.GetValueOrDefault(sector), rateHz);
    }

    /// <summary>Wire units (1/64 m) to whole metres on a grid of <paramref name="quantum"/> metres, so the focus only moves in steps.</summary>
    private static int Quantise(int wireUnits, int quantum) =>
        (int)(Math.Round(wireUnits / (double)NearGrid.UnitsPerMetre / quantum) * quantum);
}
