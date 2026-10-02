using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// The <c>CreditDelta{seq}</c> emitter of a fake node (<c>--income-rate</c>, M1-F4): income and some spend, deterministic from the seed. The authority
/// books it for the players it names (<see cref="Due"/> with the players); a client books its own local money change (<see cref="DueOwn"/>).
/// Every delta carries the next <c>seq</c> of this node. With <c>dupe</c> the emitter also resends a delta it already sent, now and then: the same
/// <c>seq</c> with the same payload, or with another amount. The ledger must book each <c>seq</c> once (<c>acked_delta_seq</c> says so).
/// No I/O: it returns the messages to send.
/// </summary>
public sealed class FakeIncomeSource(int nodeId, ulong seed, double ratePerSecond, bool dupe, EconomyReconciler? model = null)
{
    private static readonly CreditSource[] IncomeSources = [CreditSource.Trade, CreditSource.Build, CreditSource.Bounty, CreditSource.Mission, CreditSource.StationIncome];
    private static readonly CreditSource[] SpendSources = [CreditSource.Upkeep, CreditSource.Repair, CreditSource.Build];

    private readonly DetRandom _rng = new(DetHash.Hash(seed, (ulong)nodeId, 0x1C0DE));
    private readonly List<CreditDeltaT> _sent = [];
    private readonly List<long> _amounts = [];
    private double _carry;
    private double _last = double.NaN;
    private ulong _seq;

    public long DeltasSent { get; private set; }

    /// <summary>Resends of an already sent <c>seq</c> (the attack).</summary>
    public long Resends { get; private set; }

    public long IncomeTotal { get; private set; }

    public long SpendTotal { get; private set; }

    public ulong LastSeq => _seq;

    /// <summary>The sum of the amounts of every distinct <c>seq</c> sent so far (what the ledger should have booked once all are acknowledged).</summary>
    public long SentSum => IncomeTotal - SpendTotal;

    /// <summary>
    /// True when <paramref name="booked"/> equals the sum of the amounts of the deltas <c>seq 1..k</c> for some <c>k</c> between
    /// <paramref name="fromSeq"/> and <paramref name="toSeq"/>: the ledger books the deltas of a node in order, each once, so what it holds is a prefix sum.
    /// A delta booked twice (or skipped) breaks it.
    /// </summary>
    public bool IsBookedPrefix(long booked, ulong fromSeq, ulong toSeq)
    {
        long sum = 0;
        for (ulong seq = 0; seq <= Math.Min(toSeq, (ulong)_amounts.Count); seq++)
        {
            if (seq > 0)
                sum += _amounts[(int)seq - 1];
            if (seq >= fromSeq && sum == booked)
                return true;
        }

        return false;
    }

    /// <summary>Deltas for the given players, <paramref name="ratePerSecond"/> per player and second since the last call.</summary>
    public IReadOnlyList<OutMessage> Due(double now, IReadOnlyList<int> players) => Make(now, players, forSelf: false);

    /// <summary>One delta for the node itself (its own money change), <paramref name="ratePerSecond"/> per second.</summary>
    public IReadOnlyList<OutMessage> DueOwn(double now) => Make(now, [nodeId], forSelf: true);

    private List<OutMessage> Make(double now, IReadOnlyList<int> players, bool forSelf)
    {
        var list = new List<OutMessage>();
        if (double.IsNaN(_last))
        {
            _last = now;
            return list;
        }

        double dt = Math.Clamp(now - _last, 0, 2);
        _last = now;
        if (ratePerSecond <= 0 || players.Count == 0)
            return list;
        _carry += dt * ratePerSecond * players.Count;
        while (_carry >= 1)
        {
            _carry -= 1;
            int player = players[_rng.NextInt(0, players.Count)];
            list.Add(Next(player, forSelf, now));
            if (dupe && _sent.Count > 0 && _rng.Chance(0.12))
                list.Add(Resend());
        }

        return list;
    }

    private OutMessage Next(int player, bool forSelf, double now)
    {
        bool spend = _rng.Chance(0.25);
        long amount = spend ? -_rng.NextInt(40, 400) : _rng.NextInt(150, 1500);
        var source = spend ? SpendSources[_rng.NextInt(0, SpendSources.Length)] : IncomeSources[_rng.NextInt(0, IncomeSources.Length)];
        ulong seq = ++_seq;
        var delta = new CreditDeltaT
        {
            RequestKey = new Id128T { Hi = (uint)nodeId | (0xDE17AUL << 32), Lo = seq },
            PlayerId = forSelf ? (ushort)0 : (ushort)player,
            Amount = amount,
            Source = source,
            GameTime = now,
            Seq = seq,
        };
        DeltasSent++;
        if (spend)
            SpendTotal -= amount;
        else
            IncomeTotal += amount;
        model?.NoteDeltaSent(seq, amount);
        _amounts.Add(amount);
        _sent.Add(delta);
        if (_sent.Count > 200)
            _sent.RemoveAt(0);
        return Encode(delta);
    }

    /// <summary>A delta that was already sent, again: same <c>seq</c>, the same amount or (one in three) another one. The ledger must ignore it.</summary>
    private OutMessage Resend()
    {
        Resends++;
        var original = _sent[_rng.NextInt(0, _sent.Count)];
        var copy = new CreditDeltaT
        {
            RequestKey = original.RequestKey,
            PlayerId = original.PlayerId,
            Amount = _rng.Chance(0.33) ? original.Amount + 7 : original.Amount,
            Source = original.Source,
            GameTime = original.GameTime,
            Seq = original.Seq,
        };
        return Encode(copy);
    }

    private static OutMessage Encode(CreditDeltaT delta) => new(MsgType.CreditDelta, MessageEncoder.EncodePayload(b => CreditDelta.Pack(b, delta), 96));

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"deltas={DeltasSent} income={IncomeTotal} spend={SpendTotal} resends={Resends} last-seq={_seq}");
}
