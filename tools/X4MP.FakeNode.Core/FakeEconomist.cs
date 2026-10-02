using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>How one sending of a request key relates to the original request (the attack bookkeeping).</summary>
internal enum RequestRole
{
    /// <summary>A normal request with a fresh key.</summary>
    Original,

    /// <summary>The same key and payload again; the server must answer with the stored result and change nothing.</summary>
    Replay,

    /// <summary>The same key with another payload; the server must answer <c>RequestIdReuse</c>.</summary>
    Reuse,

    /// <summary>One request sent several times back to back before any answer; it must take effect once.</summary>
    Race,

    /// <summary>One of two different requests that together spend more than the wallet holds.</summary>
    Overdraw,
}

/// <summary>
/// The credit behaviour of a fake client (<c>--economy</c>, M1-F4): a deterministic, realistic mix of teammate transfers, donations, pool deposits and
/// withdrawals, loan offers, answers, repayments, forgiving and withdrawing, driven by the clock the node loop passes in. It answers loan offers made
/// to it and repays the loans it took (unless <c>--loan-default</c>: then it never repays and the offers it makes fall due in seconds).
/// With <c>--dupe-attack</c> it also replays keys, reuses them with other payloads and races identical requests, and checks that every answer is
/// idempotent (<see cref="DuplicateEffects"/> counts the answers that were not).
/// <para>
/// No I/O: the reader thread feeds <see cref="Handle"/>, the node loop calls <see cref="Tick"/> and sends what comes back. The wallet it spends from
/// is the one the <see cref="EconomyReconciler"/> follows. The server answers one connection's requests in order, so the answers to one key are
/// matched to the sendings of that key in order.
/// </para>
/// </summary>
public sealed partial class FakeEconomist
{
    /// <summary>The high half of every request key of the economist (the trader uses the player id alone), so the two never collide.</summary>
    internal const ulong KeyDomain = 0xEC0UL << 32;
    private const int NoteLimit = 40;

    private enum Kind
    {
        Transfer,
        Donate,
        PoolDeposit,
        PoolWithdraw,
        LoanOffer,
        LoanRespond,
        LoanRepay,
        LoanForgive,
        LoanWithdraw,
    }

    private sealed class Sent
    {
        public Kind Kind;
        public OutMessage Message = null!;
        public Func<OutMessage>? Altered;
        public ulong LoanId;
        public Queue<RequestRole> Attempts = new();
        public int Results;
        public bool? FirstOk;
        public string? OkSignature;
        public bool Replayed;
        public bool Reused;
        public int Group;
        public (ulong Hi, ulong Lo) Key;
    }

    private sealed class Loan
    {
        public ulong Id;
        public int Lender;
        public int Borrower;
        public LoanState State;
        public long RepayTotal;
        public long Repaid;
        public long Forgiven;
        public double ActionAt = double.NaN;
        public int Attempts;
        public bool Dead;
        public bool Seen;

        public long Outstanding => Math.Max(0, RepayTotal - Repaid - Forgiven);

        public bool Open => State is LoanState.Offered or LoanState.Active or LoanState.Overdue;
    }

    private readonly object _gate = new();
    private readonly int _playerId;
    private readonly EconomyMode _mode;
    private readonly bool _dupeAttack;
    private readonly bool _loanDefault;
    private readonly DetRandom _rng;
    private readonly Dictionary<int, PlayerInfoT> _peers = [];
    private readonly Dictionary<(ulong Hi, ulong Lo), Sent> _keys = [];
    private readonly List<Sent> _okOriginals = [];
    private readonly Dictionary<ulong, Loan> _loans = [];
    private readonly Dictionary<Kind, long> _sentByKind = [];
    private readonly Dictionary<Kind, long> _okByKind = [];
    private readonly Dictionary<EconomyReject, long> _rejects = [];
    private readonly Dictionary<int, (int Pending, int Oks)> _raceGroups = [];
    private readonly List<string> _notes = [];
    private ulong _counter;
    private int _group;
    private double _nextAction = double.NaN;
    private double _nextDupe = double.NaN;
    private double _nextOffer = double.NaN;
    private int _frozenNotes;

    public FakeEconomist(int playerId, EconomyMode mode, ulong seed, bool dupeAttack, bool loanDefault, EconomyReconciler model)
    {
        _playerId = playerId;
        _mode = mode;
        _dupeAttack = dupeAttack;
        _loanDefault = loanDefault;
        Model = model;
        _rng = new DetRandom(DetHash.Hash(seed, (ulong)playerId, 0xEC0));
    }

    public EconomyReconciler Model { get; }

    public EconomyMode Mode => _mode;

    // ---- counters

    public long RequestsSent => Locked(() => _sentByKind.Values.Sum());

    public long RequestsOk => Locked(() => _okByKind.Values.Sum());

    public long RequestsRejected => Locked(() => _rejects.Values.Sum());

    /// <summary>Times the mix had nothing to do (no peer, not enough credits, no loan to act on).</summary>
    public long Skipped { get; private set; }

    /// <summary>Exact replays sent, and how they were answered.</summary>
    public long ReplaysSent { get; private set; }

    public long ReplaysIdempotent { get; private set; }

    public long ReplaysRateLimited { get; private set; }

    public long ReplaysOther { get; private set; }

    public long ReusesSent { get; private set; }

    public long ReusesRejected { get; private set; }

    public long ReusesRateLimited { get; private set; }

    public long ReusesOther { get; private set; }

    /// <summary>Races: one request sent three times back to back.</summary>
    public long RacesSent { get; private set; }

    public long RaceCopiesSent { get; private set; }

    public long OverdrawPairs { get; private set; }

    /// <summary>Overdraw pairs where both requests went through (incoming credits in between can explain it; the ledger check decides).</summary>
    public long OverdrawBothOk { get; private set; }

    /// <summary>Answers that show a second effect: a reuse that succeeded, or two successes of one key with different balances.</summary>
    public long DuplicateEffects { get; private set; }

    public long LoansOffered => Locked(() => _sentByKind.GetValueOrDefault(Kind.LoanOffer));

    public long LoanAccepts { get; private set; }

    public long LoanDeclines { get; private set; }

    public long LoanRepayments { get; private set; }

    /// <summary>Loans this client borrowed that are still open (active or overdue).</summary>
    public int OpenBorrowed => Locked(() => _loans.Values.Count(l => l.Borrower == _playerId && l.State is LoanState.Active or LoanState.Overdue));

    public int OverdueBorrowed => Locked(() => _loans.Values.Count(l => l.Borrower == _playerId && l.State == LoanState.Overdue));

    public IReadOnlyDictionary<EconomyReject, long> RejectReasons => Locked(() => new Dictionary<EconomyReject, long>(_rejects));

    public IReadOnlyDictionary<string, long> SentByKind => Locked(() => _sentByKind.ToDictionary(k => k.Key.ToString(), k => k.Value));

    /// <summary>Lines the node should print (a rejection worth seeing, a duplicate effect); each is returned once.</summary>
    public IReadOnlyList<string> DrainNotes()
    {
        lock (_gate)
        {
            var copy = _notes.ToList();
            _notes.Clear();
            return copy;
        }
    }

    private T Locked<T>(Func<T> read)
    {
        lock (_gate)
            return read();
    }

    // ------------------------------------------------------------------ incoming

    /// <summary>Processes a frame from the server (roster, wallet updates, results, loan status).</summary>
    public void Handle(Frame frame, double now)
    {
        switch (frame.Type)
        {
            case MsgType.WalletUpdate:
                Model.OnWalletUpdate(MessageRegistry.Default.Decode<WalletUpdate>(frame).UnPack());
                break;
            case MsgType.EconomyResult:
                var result = MessageRegistry.Default.Decode<EconomyResult>(frame).UnPack();
                Model.OnResult(result);
                lock (_gate)
                    OnResult(result, now);
                break;
            case MsgType.RosterUpdate:
                var roster = MessageRegistry.Default.Decode<RosterUpdate>(frame).UnPack();
                lock (_gate)
                {
                    if (roster.Full)
                        _peers.Clear();
                    foreach (var p in roster.Players ?? [])
                        _peers[p.PlayerId] = p;
                    foreach (var removed in roster.Removed ?? [])
                        _peers.Remove(removed);
                }

                break;
            case MsgType.LoanStatus:
                var status = MessageRegistry.Default.Decode<LoanStatus>(frame).UnPack();
                lock (_gate)
                    OnLoanStatus(status, now);
                break;
        }
    }

    private void OnLoanStatus(LoanStatusT status, double now)
    {
        ulong id = status.LoanId?.Lo ?? 0;
        if (id == 0 || (status.Lender != _playerId && status.Borrower != _playerId))
            return;
        if (!_loans.TryGetValue(id, out var loan))
            _loans[id] = loan = new Loan { Id = id };
        var before = loan.State;
        bool first = !loan.Seen;
        loan.Seen = true;
        loan.Lender = status.Lender;
        loan.Borrower = status.Borrower;
        loan.State = status.State;
        loan.RepayTotal = status.RepayTotal;
        loan.Repaid = status.Repaid;
        loan.Forgiven = status.Forgiven;
        if (first || before != status.State)
        {
            loan.Attempts = 0;
            loan.ActionAt = double.NaN;
            loan.Dead = false;
        }

        if (!loan.Open || loan.Borrower != _playerId || !double.IsNaN(loan.ActionAt))
            return;
        if (loan.State == LoanState.Offered)
            loan.ActionAt = now + _rng.NextDouble(0.4, 3.0); // the borrower thinks about it
        else if (!_loanDefault)
            loan.ActionAt = now + (_mode == EconomyMode.Heavy ? _rng.NextDouble(3, 8) : _rng.NextDouble(5, 14));
    }

    private void OnResult(EconomyResultT result, double now)
    {
        var key = (Hi: result.RequestKey?.Hi ?? 0, Lo: result.RequestKey?.Lo ?? 0);
        if (!_keys.TryGetValue(key, out var sent))
            return; // not ours (a trade request, or an older run)
        sent.Results++;
        var role = sent.Attempts.Count > 0 ? sent.Attempts.Dequeue() : RequestRole.Original;
        bool ok = result.Status == EconomyStatus.Ok;
        string signature = Signature(result);
        string label = $"{sent.Kind}{(role == RequestRole.Original ? string.Empty : "/" + role)} key {key.Lo}";

        if (ok)
        {
            if (sent.OkSignature is null)
            {
                sent.OkSignature = signature;
            }
            else if (sent.OkSignature != signature)
            {
                DuplicateEffects++;
                Note($"DUPLICATE EFFECT: {label} answered Ok twice with different balances ({sent.OkSignature} then {signature})");
            }
        }

        switch (role)
        {
            case RequestRole.Replay:
                if (ok)
                    ReplaysIdempotent++;
                else if (result.Reason == EconomyReject.RateLimited)
                    ReplaysRateLimited++;
                else
                    ReplaysOther++;
                return;
            case RequestRole.Reuse:
                if (ok)
                {
                    ReusesOther++;
                    DuplicateEffects++;
                    Note($"DUPLICATE EFFECT: {label} was reused with another payload and the server said Ok");
                }
                else if (result.Reason == EconomyReject.RequestIdReuse)
                {
                    ReusesRejected++;
                }
                else if (result.Reason == EconomyReject.RateLimited)
                {
                    ReusesRateLimited++;
                }
                else
                {
                    ReusesOther++;
                }

                return;
            case RequestRole.Overdraw:
                CountOverdraw(sent, ok);
                break;
        }

        // The first answer of a request is its outcome; later answers (race copies) were checked above.
        if (sent.FirstOk is not null)
            return;
        sent.FirstOk = ok;
        if (ok)
        {
            _okByKind[sent.Kind] = _okByKind.GetValueOrDefault(sent.Kind) + 1;
            if (role == RequestRole.Original && _okOriginals.Count < 60)
                _okOriginals.Add(sent);
            if (sent.Kind == Kind.LoanRespond && _loans.TryGetValue(sent.LoanId, out var loan) && loan.State == LoanState.Offered)
                loan.ActionAt = double.PositiveInfinity; // the status that follows moves it on
        }
        else
        {
            var reason = result.Reason;
            _rejects[reason] = _rejects.GetValueOrDefault(reason) + 1;
            if (reason == EconomyReject.EconomyFrozen && _frozenNotes < 20)
            {
                _frozenNotes++;
                Note($"{sent.Kind} rejected: EconomyFrozen ({result.Detail})");
            }

            AfterReject(sent, reason, now);
        }
    }

    private void CountOverdraw(Sent sent, bool ok)
    {
        var (pending, oks) = _raceGroups.GetValueOrDefault(sent.Group);
        pending--;
        if (ok)
            oks++;
        _raceGroups[sent.Group] = (pending, oks);
        if (pending == 0 && oks == 2)
            OverdrawBothOk++;
    }

    private void AfterReject(Sent sent, EconomyReject reason, double now)
    {
        if (sent.Kind is not (Kind.LoanRespond or Kind.LoanRepay) || !_loans.TryGetValue(sent.LoanId, out var loan))
            return;
        // The borrower tries again later when the budget or the wallet was the problem; a loan the server no longer knows or has closed is left alone.
        bool again = reason is EconomyReject.RateLimited or EconomyReject.InsufficientFunds or EconomyReject.TooManyOpen or EconomyReject.EconomyFrozen;
        if (again && ++loan.Attempts <= 6)
            loan.ActionAt = now + (reason == EconomyReject.RateLimited ? 2.5 : 8);
        else
            loan.Dead = true;
    }

    private static string Signature(EconomyResultT result) => string.Join(
        ";", (result.Balances ?? []).OrderBy(b => b.Wallet.Kind).ThenBy(b => b.Wallet.OwnerId).Select(b => $"{b.Wallet.Kind}:{b.Wallet.OwnerId}@{b.Version}={b.Balance}"));

    private void Note(string text)
    {
        if (_notes.Count < NoteLimit)
            _notes.Add(text);
    }
}
