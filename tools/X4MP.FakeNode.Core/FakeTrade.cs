using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// The fake authority's side of escrowed trades (M1-E5, failure injection of server-design 6.4), with no I/O. It executes every
/// <c>AssetTransferOrder</c> exactly once per trade id (a repeated order gets the same answer) and decides the outcome of each trade
/// deterministically from the seed and the trade id:
/// <list type="bullet">
/// <item><b>--trade-fail PCT</b>: the order fails (<c>ok=false</c>, compensated); nothing was changed.</item>
/// <item><b>--trade-timeout PCT</b>: the authority <i>withholds</i> the confirm. The transfer itself may have been applied (80%) or not.
/// A third of these trades are silent for good: even the <c>TradeQuery</c>s get no answer, so the server must leave them InDoubt.
/// The others answer the first <c>TradeQuery</c> with the real outcome.</item>
/// <item>Everything else is applied and confirmed at once; an applied <c>OwnerChange</c> also emits the journaled <c>EntityChange</c>.</item>
/// </list>
/// A <c>TradeQuery</c> for a trade it never received is answered <c>ok=false, failed_line=-1, error="Unknown"</c> (protocol.md 15.6).
/// </summary>
public sealed class FakeTradeAuthority(ulong seed, double failPercent, double timeoutPercent)
{
    /// <summary>What the fake did with one trade (the ground truth the swarm test compares the server's states with).</summary>
    public sealed record Outcome(bool Applied, bool Withheld, bool Silent);

    private readonly Dictionary<ulong, Outcome> _outcomes = [];
    private readonly Dictionary<ulong, List<AssetTransferLineT>> _lines = [];

    public IReadOnlyDictionary<ulong, Outcome> Outcomes => _outcomes;

    public long OrdersReceived { get; private set; }

    public long DuplicateOrders { get; private set; }

    public long Applied => _outcomes.Values.Count(o => o.Applied);

    public long Failed => _outcomes.Values.Count(o => !o.Applied);

    public long Withheld => _outcomes.Values.Count(o => o.Withheld);

    public long Silent => _outcomes.Values.Count(o => o.Silent);

    public long QueriesAnswered { get; private set; }

    public long QueriesIgnored { get; private set; }

    public long UnknownQueries { get; private set; }

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"trade-authority: orders={OrdersReceived} applied={Applied} failed={Failed} withheld={Withheld} silent={Silent} " +
        $"duplicate-orders={DuplicateOrders} queries-answered={QueriesAnswered} queries-ignored={QueriesIgnored} unknown-queries={UnknownQueries}");

    private Outcome Decide(ulong tradeId)
    {
        double u = DetHash.Unit(DetHash.Hash(seed, 0x7ADE, tradeId));
        if (u < failPercent / 100.0)
            return new Outcome(false, false, false);
        if (u < (failPercent + timeoutPercent) / 100.0)
        {
            bool applied = DetHash.Unit(DetHash.Hash(seed, 0x7ADF, tradeId)) >= 0.2;
            bool silent = DetHash.Unit(DetHash.Hash(seed, 0x7AE0, tradeId)) < 1.0 / 3.0;
            return new Outcome(applied, true, silent);
        }

        return new Outcome(true, false, false);
    }

    /// <summary>Executes an order (once per trade id) and returns what the authority sends back.</summary>
    public IReadOnlyList<OutMessage> OnOrder(AssetTransferOrderT order)
    {
        ArgumentNullException.ThrowIfNull(order);
        OrdersReceived++;
        ulong id = order.TradeId?.Lo ?? 0;
        if (_outcomes.TryGetValue(id, out var known))
        {
            DuplicateOrders++;
            return known.Withheld ? [] : [Confirm(id, known)];
        }

        var outcome = Decide(id);
        _outcomes[id] = outcome;
        _lines[id] = [.. order.Lines ?? []];
        var output = new List<OutMessage>();
        if (outcome.Applied)
        {
            foreach (var line in _lines[id].Where(l => l.Kind == AssetTransferKind.OwnerChange))
            {
                var change = new EntityChangeT
                {
                    NetId = line.Asset,
                    Fields = ChangeField.OwnerTeam | ChangeField.OwnerPlayer,
                    OwnerTeam = line.ToTeam,
                    OwnerPlayer = line.ToPlayer,
                    CauseTradeId = new Id128T { Lo = id },
                };
                output.Add(new OutMessage(MsgType.EntityChange, MessageEncoder.EncodePayload(b => EntityChange.Pack(b, change), 96)));
            }
        }

        if (!outcome.Withheld)
            output.Add(Confirm(id, outcome));
        return output;
    }

    /// <summary>Answers a <c>TradeQuery</c>: the real outcome, "Unknown", or nothing (a silent trade).</summary>
    public IReadOnlyList<OutMessage> OnQuery(TradeQueryT query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ulong id = query.TradeId?.Lo ?? 0;
        if (!_outcomes.TryGetValue(id, out var outcome))
        {
            UnknownQueries++;
            var unknown = new AssetTransferConfirmT { TradeId = new Id128T { Lo = id }, Ok = false, FailedLine = -1, Error = "Unknown" };
            return [new OutMessage(MsgType.AssetTransferConfirm, MessageEncoder.EncodePayload(b => AssetTransferConfirm.Pack(b, unknown), 64))];
        }

        if (outcome.Silent)
        {
            QueriesIgnored++;
            return [];
        }

        QueriesAnswered++;
        return [Confirm(id, outcome)];
    }

    private static OutMessage Confirm(ulong id, Outcome outcome)
    {
        var confirm = new AssetTransferConfirmT
        {
            TradeId = new Id128T { Lo = id },
            Ok = outcome.Applied,
            FailedLine = outcome.Applied ? (short)-1 : (short)0,
            Compensated = !outcome.Applied,
            Error = outcome.Applied ? string.Empty : "fake failure",
        };
        return new OutMessage(MsgType.AssetTransferConfirm, MessageEncoder.EncodePayload(b => AssetTransferConfirm.Pack(b, confirm), 64));
    }
}

/// <summary>
/// The trading behaviour of a fake client (<c>--trade</c>): proposes to sell a ship of its team for credits to another player, and accepts the
/// proposals that arrive (the offer's price is fair by construction). It counts what the server answered, so a swarm shows how every trade ended.
/// No I/O: feed it the frames (<see cref="Handle"/>) and ask for the next proposal (<see cref="NextProposal"/>).
/// </summary>
public sealed class FakeTrader(int playerId, ulong seed)
{
    /// <summary>A trade that waits for this client to accept is asked again after this many seconds (the economy rate limit may have dropped it).</summary>
    private const double AcceptRetrySeconds = 4;

    private sealed class Seen
    {
        public TradeState State;
        public uint Version;
        public int Initiator;
        public int Counterparty;
        public uint InitiatorAccepted;
        public uint CounterpartyAccepted;
        public double LastAcceptAt = double.NegativeInfinity;
    }

    // The frames arrive on the connection's reader thread, the proposals and retries are driven from the node loop.
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Seen> _trades = [];
    private readonly Dictionary<int, PlayerInfoT> _peers = [];
    private readonly Dictionary<EconomyReject, long> _rejects = [];
    private readonly Dictionary<TradeState, long> _results = [];
    private ulong _key;
    private long _proposals;

    /// <summary>Seconds between two proposals of this client (<c>--economy</c> changes it).</summary>
    public double ProposalIntervalSeconds { get; init; } = 5;

    public long ProposalsSent => Interlocked.Read(ref _proposals);

    public long AcceptsSent { get; private set; }

    public long RequestsRejected
    {
        get
        {
            lock (_gate)
                return _rejects.Values.Sum();
        }
    }

    /// <summary>A copy of the rejection counts of this client's requests, by reason.</summary>
    public IReadOnlyDictionary<EconomyReject, long> RejectReasons
    {
        get
        {
            lock (_gate)
                return new Dictionary<EconomyReject, long>(_rejects);
        }
    }

    /// <summary>Final states this client was told about (<c>TradeResult</c>), by state (a copy).</summary>
    public IReadOnlyDictionary<TradeState, long> Results
    {
        get
        {
            lock (_gate)
                return new Dictionary<TradeState, long>(_results);
        }
    }

    public long Finished
    {
        get
        {
            lock (_gate)
                return _results.Values.Sum();
        }
    }

    /// <summary>Trades seen that are still open (not final) from this client's point of view.</summary>
    public int Open
    {
        get
        {
            lock (_gate)
                return _trades.Values.Count(t => t.State is not (TradeState.Completed or TradeState.RolledBack or TradeState.Cancelled or TradeState.Expired or TradeState.Rejected));
        }
    }

    /// <summary>The players this client could trade with: in game, not itself, not the authority-only node.</summary>
    public IReadOnlyList<int> Peers
    {
        get
        {
            lock (_gate)
                return [.. _peers.Values.Where(p => p.PlayerId != playerId && p.Phase == NodePhase.InGame && (p.Roles & Role.Client) != 0).Select(p => (int)p.PlayerId).Order()];
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"proposals={ProposalsSent} accepts={AcceptsSent} finished={_results.Values.Sum()} " +
                $"completed={Count(TradeState.Completed)} rolled-back={Count(TradeState.RolledBack)} cancelled={Count(TradeState.Cancelled)} " +
                $"rejected={Count(TradeState.Rejected)} expired={Count(TradeState.Expired)} open={Open} " +
                $"in-doubt-seen={_trades.Values.Count(t => t.State == TradeState.InDoubt)} request-rejects=[{string.Join(",", _rejects.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}"))}]");
        }
    }

    private long Count(TradeState state) => _results.GetValueOrDefault(state);

    /// <summary>Processes a frame from the server; returns what the client sends back (<c>TradeAccept</c>s).</summary>
    public IReadOnlyList<OutMessage> Handle(Frame frame, double now)
    {
        lock (_gate)
            return HandleLocked(frame, now);
    }

    private IReadOnlyList<OutMessage> HandleLocked(Frame frame, double now)
    {
        switch (frame.Type)
        {
            case MsgType.RosterUpdate:
                var roster = MessageRegistry.Default.Decode<RosterUpdate>(frame).UnPack();
                if (roster.Full)
                    _peers.Clear();
                foreach (var p in roster.Players ?? [])
                    _peers[p.PlayerId] = p;
                foreach (var removed in roster.Removed ?? [])
                    _peers.Remove(removed);
                return [];
            case MsgType.EconomyResult:
                var result = MessageRegistry.Default.Decode<EconomyResult>(frame);
                if (((result.RequestKey?.Hi ?? 0) >> 32) == (FakeEconomist.KeyDomain >> 32))
                    return []; // an answer to a request of the economy behaviour, not a trade request
                if (result.Status == EconomyStatus.Rejected)
                    _rejects[result.Reason] = _rejects.GetValueOrDefault(result.Reason) + 1;
                return [];
            case MsgType.TradeResult:
                var final = MessageRegistry.Default.Decode<TradeResult>(frame);
                _results[final.State] = _results.GetValueOrDefault(final.State) + 1;
                return [];
            case MsgType.TradeStatus:
                return OnStatus(MessageRegistry.Default.Decode<TradeStatus>(frame).UnPack(), now);
            default:
                return [];
        }
    }

    private IReadOnlyList<OutMessage> OnStatus(TradeStatusT status, double now)
    {
        ulong id = status.TradeId?.Lo ?? 0;
        if (!_trades.TryGetValue(id, out var seen))
            _trades[id] = seen = new Seen();
        seen.State = status.State;
        seen.Version = status.Version;
        seen.Initiator = status.Initiator?.PlayerId ?? 0;
        seen.Counterparty = status.Counterparty?.PlayerId ?? 0;
        seen.InitiatorAccepted = status.Initiator?.AcceptedVersion ?? 0;
        seen.CounterpartyAccepted = status.Counterparty?.AcceptedVersion ?? 0;
        return WantsAccept(seen) && now - seen.LastAcceptAt >= AcceptRetrySeconds ? [Accept(id, seen, now)] : [];
    }

    private bool WantsAccept(Seen t) =>
        t.State is TradeState.Proposed or TradeState.Countered && t.Counterparty == playerId && t.InitiatorAccepted == t.Version && t.CounterpartyAccepted != t.Version;

    /// <summary>Accepts that are due again (the first one may have been dropped by the rate limit). Call about once a second.</summary>
    public IReadOnlyList<OutMessage> Retries(double now)
    {
        lock (_gate)
        {
            var list = new List<OutMessage>();
            foreach (var (id, seen) in _trades)
            {
                if (WantsAccept(seen) && now - seen.LastAcceptAt >= AcceptRetrySeconds)
                    list.Add(Accept(id, seen, now));
            }

            return list;
        }
    }

    private OutMessage Accept(ulong id, Seen seen, double now)
    {
        seen.LastAcceptAt = now;
        AcceptsSent++;
        var accept = new TradeAcceptT { RequestKey = NextKey(), TradeId = new Id128T { Lo = id }, Version = seen.Version };
        return new OutMessage(MsgType.TradeAccept, MessageEncoder.EncodePayload(b => TradeAccept.Pack(b, accept), 96));
    }

    private Id128T NextKey() => new() { Hi = (ulong)playerId, Lo = ++_key };

    /// <summary>A proposal to sell <paramref name="ship"/> to a peer for a seeded price; null when there is nobody to trade with.</summary>
    public OutMessage? NextProposal(uint ship)
    {
        var peers = Peers;
        if (peers.Count == 0)
            return null;
        ulong n = (ulong)Interlocked.Increment(ref _proposals);
        lock (_gate)
            return Propose(ship, peers, n);
    }

    private OutMessage Propose(uint ship, IReadOnlyList<int> peers, ulong n)
    {
        var rng = new DetRandom(DetHash.Hash(seed, (ulong)playerId, n));
        int to = peers[rng.NextInt(0, peers.Count)];
        var proposal = new TradeProposalT
        {
            RequestKey = NextKey(),
            Counterparty = (ushort)to,
            Give = [new TradeItemT { Kind = TradeItemKind.Ship, Asset = ship }],
            Want = [new TradeItemT { Kind = TradeItemKind.Credits, Amount = rng.NextInt(500, 5000) }],
            TtlS = 60,
            Memo = "fakenode",
        };
        return new OutMessage(MsgType.TradeProposal, MessageEncoder.EncodePayload(b => TradeProposal.Pack(b, proposal), 160));
    }
}
