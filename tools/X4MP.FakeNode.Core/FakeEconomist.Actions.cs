using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

public sealed partial class FakeEconomist
{
    /// <summary>
    /// What the client sends now: loan answers and repayments that are due, the next action of the mix and, with <c>--dupe-attack</c>, an attack.
    /// <paramref name="quiet"/> (the last seconds of a timed run) stops everything that starts something new, so the books can settle.
    /// </summary>
    public IReadOnlyList<OutMessage> Tick(double now, bool quiet)
    {
        lock (_gate)
        {
            var output = new List<OutMessage>();
            if (_mode is EconomyMode.Off or EconomyMode.Idle)
                return output;
            if (double.IsNaN(_nextAction))
            {
                _nextAction = now + 2 + _rng.NextDouble(0, 4);
                _nextDupe = now + 5 + _rng.NextDouble(0, 4);
                _nextOffer = now + 3 + _rng.NextDouble(0, 5);
            }

            LoanReactions(now, quiet, output);
            if (quiet)
                return output;

            bool heavy = _mode == EconomyMode.Heavy;
            if (_loanDefault && now >= _nextOffer)
            {
                _nextOffer = now + ((heavy ? 3 : 6) * _rng.NextDouble(0.7, 1.3));
                AddMessage(output, OfferLoan(now));
            }

            if (now >= _nextAction)
            {
                _nextAction = now + ((heavy ? 3.0 : 8.0) * _rng.NextDouble(0.5, 1.5));
                AddMessage(output, NextAction(now));
            }

            if (_dupeAttack && now >= _nextDupe)
            {
                _nextDupe = now + ((heavy ? 7.0 : 14.0) * _rng.NextDouble(0.6, 1.4));
                Attack(now, output);
            }

            return output;
        }
    }

    private static void AddMessage(List<OutMessage> output, OutMessage? message)
    {
        if (message is not null)
            output.Add(message);
    }

    private void LoanReactions(double now, bool quiet, List<OutMessage> output)
    {
        foreach (var loan in _loans.Values.OrderBy(l => l.Id).ToList())
        {
            if (loan.Dead || !loan.Open || loan.Borrower != _playerId || double.IsNaN(loan.ActionAt) || now < loan.ActionAt)
                continue;
            ulong id = loan.Id;
            if (loan.State == LoanState.Offered)
            {
                bool accept = _loanDefault || _rng.Chance(0.85);
                if (accept)
                    LoanAccepts++;
                else
                    LoanDeclines++;
                output.Add(Create(Kind.LoanRespond, RequestRole.Original, id, key => EncodeRespond(key, id, accept), key => EncodeRespond(key, id, !accept)).Message);
                loan.ActionAt = now + 6; // asked again if the answer was dropped; the status that follows clears it
            }
            else if (!_loanDefault && !quiet && loan.Outstanding > 0)
            {
                long balance = Model.EffectiveWallet()?.Balance ?? 0;
                long amount = Math.Min(_rng.Chance(0.4) ? Math.Max(1, loan.Outstanding / 2) : loan.Outstanding, balance);
                if (amount <= 0)
                {
                    loan.ActionAt = now + 8;
                    continue;
                }

                LoanRepayments++;
                output.Add(Create(Kind.LoanRepay, RequestRole.Original, id, key => EncodeRepay(key, id, amount), key => EncodeRepay(key, id, amount + 1)).Message);
                loan.ActionAt = now + _rng.NextDouble(3, 7); // a partial repayment is followed by another
            }
        }
    }

    // ------------------------------------------------------------------ the mix

    private OutMessage? NextAction(double now)
    {
        var wallet = Model.EffectiveWallet();
        if (wallet is null)
            return Skip();
        long balance = wallet.Value.Balance;
        bool shared = Model.Mode == EffectiveCreditMode.Shared; // one wallet per team: no transfers inside it, no pool
        int roll = _rng.NextInt(0, 100);
        OutMessage? message;
        if (roll < 26)
            message = shared ? Donation(now, balance) : Transfer(now, balance);
        else if (roll < 42)
            message = Donation(now, balance);
        else if (roll < 54)
            message = shared ? Donation(now, balance) : PoolDeposit(now, balance);
        else if (roll < 64)
            message = shared ? Donation(now, balance) : PoolWithdraw(now);
        else if (roll < 74)
            message = OfferLoan(now);
        else if (roll < 82)
            message = ForgiveLoan(now);
        else if (roll < 87)
            message = WithdrawLoan(now);
        else
            message = shared ? Donation(now, balance) : Transfer(now, balance);
        return message ?? Skip();
    }

    private OutMessage? Skip()
    {
        Skipped++;
        return null;
    }

    private bool IsClientInGame(PlayerInfoT p) => p.PlayerId != _playerId && p.Phase == NodePhase.InGame && (p.Roles & Role.Client) != 0;

    private int OwnTeam() => _peers.TryGetValue(_playerId, out var me) ? me.TeamId : 0;

    /// <summary>A recipient: a teammate (most of the time) or any other client.</summary>
    private int? PickPeer(bool teammateOnly)
    {
        int team = OwnTeam();
        var mates = _peers.Values.Where(p => IsClientInGame(p) && p.TeamId != 0 && p.TeamId == team).Select(p => (int)p.PlayerId).Order().ToList();
        var others = _peers.Values.Where(p => IsClientInGame(p) && p.TeamId != team).Select(p => (int)p.PlayerId).Order().ToList();
        if (teammateOnly || others.Count == 0)
            return mates.Count == 0 ? null : _rng.Pick(mates);
        if (mates.Count == 0)
            return _rng.Pick(others);
        return _rng.Chance(0.7) ? _rng.Pick(mates) : _rng.Pick(others);
    }

    /// <summary>An amount between <paramref name="low"/> and the smaller of <paramref name="cap"/> and a twenty-fifth of the balance.</summary>
    private long Amount(long balance, int low, int cap)
    {
        int high = (int)Math.Min(cap, Math.Max(low + 1, balance / 25));
        return _rng.NextInt(low, Math.Max(low + 1, high));
    }

    private OutMessage? Transfer(double now, long balance, RequestRole role = RequestRole.Original)
    {
        if (balance < 1000 || PickPeer(teammateOnly: true) is not { } to)
            return null;
        long amount = Amount(balance, 10, 600);
        return Create(Kind.Transfer, role, 0, key => EncodeTransfer(key, to, amount), key => EncodeTransfer(key, to, amount + 1)).Message;
    }

    private OutMessage? Donation(double now, long balance, RequestRole role = RequestRole.Original)
    {
        if (balance < 1000 || PickPeer(teammateOnly: false) is not { } to)
            return null;
        long amount = Amount(balance, 10, 400);
        return Create(Kind.Donate, role, 0, key => EncodeDonate(key, to, amount), key => EncodeDonate(key, to, amount + 1)).Message;
    }

    private OutMessage? PoolDeposit(double now, long balance)
    {
        if (balance < 1000)
            return null;
        long amount = Amount(balance, 20, 800);
        return Create(Kind.PoolDeposit, RequestRole.Original, 0, key => EncodeDeposit(key, amount), key => EncodeDeposit(key, amount + 1)).Message;
    }

    private OutMessage? PoolWithdraw(double now)
    {
        long amount = _rng.NextInt(20, 500);
        return Create(Kind.PoolWithdraw, RequestRole.Original, 0, key => EncodeWithdraw(key, amount), key => EncodeWithdraw(key, amount + 1)).Message;
    }

    private OutMessage? OfferLoan(double now)
    {
        var wallet = Model.EffectiveWallet();
        if (wallet is null || wallet.Value.Balance < 5000 || PickPeer(teammateOnly: false) is not { } borrower)
            return Skip();
        long principal = Math.Min(_rng.NextInt(500, 3000), Math.Max(500, wallet.Value.Balance / 10));
        long repay = principal + (principal * _rng.NextInt(0, 16) / 100);
        uint due = _loanDefault ? (uint)_rng.NextInt(4, 7) : (uint)_rng.NextInt(40, 91);
        return Create(Kind.LoanOffer, RequestRole.Original, 0, key => EncodeOffer(key, borrower, principal, repay, due), key => EncodeOffer(key, borrower, principal + 1, repay + 1, due)).Message;
    }

    private OutMessage? ForgiveLoan(double now)
    {
        if (_loanDefault)
            return null;
        var loan = _loans.Values.Where(l => l.Lender == _playerId && l.State is LoanState.Active or LoanState.Overdue && l.Outstanding > 0).OrderBy(l => l.Id).FirstOrDefault();
        if (loan is null)
            return null;
        ulong id = loan.Id;
        return Create(Kind.LoanForgive, RequestRole.Original, id, key => EncodeForgive(key, id, 0), key => EncodeForgive(key, id, 1)).Message;
    }

    private OutMessage? WithdrawLoan(double now)
    {
        var loan = _loans.Values.Where(l => l.Lender == _playerId && l.State == LoanState.Offered).OrderBy(l => l.Id).FirstOrDefault();
        if (loan is null)
            return null;
        ulong id = loan.Id;
        return Create(Kind.LoanWithdraw, RequestRole.Original, id, key => EncodeCancel(key, id), key => EncodeCancel(key, id + 1)).Message;
    }

    // ------------------------------------------------------------------ the attacks

    private void Attack(double now, List<OutMessage> output)
    {
        int roll = _rng.NextInt(0, 100);
        if (roll < 40)
            Replay(output);
        else if (roll < 75)
            Reuse(output);
        else if (roll < 95)
            Race(now, output);
        else
            Overdraw(now, output);
    }

    private Sent? PickOkOriginal(Func<Sent, bool> eligible)
    {
        var candidates = _okOriginals.Where(eligible).ToList();
        return candidates.Count == 0 ? null : _rng.Pick(candidates);
    }

    /// <summary>The same bytes again for a request that went through: the server must answer with the stored result and book nothing.</summary>
    private void Replay(List<OutMessage> output)
    {
        if (PickOkOriginal(s => !s.Replayed) is not { } original)
            return;
        original.Replayed = true;
        original.Attempts.Enqueue(RequestRole.Replay);
        ReplaysSent++;
        output.Add(original.Message);
    }

    /// <summary>The same key with another amount (or target): the server must refuse it as a reuse of the key.</summary>
    private void Reuse(List<OutMessage> output)
    {
        if (PickOkOriginal(s => !s.Reused && s.Altered is not null) is not { } original)
            return;
        original.Reused = true;
        original.Attempts.Enqueue(RequestRole.Reuse);
        ReusesSent++;
        output.Add(original.Altered!());
    }

    /// <summary>A fresh request sent three times before any answer: it must take effect once.</summary>
    private void Race(double now, List<OutMessage> output)
    {
        var wallet = Model.EffectiveWallet();
        if (wallet is null || wallet.Value.Balance < 1000)
            return;
        bool shared = Model.Mode == EffectiveCreditMode.Shared;
        var message = shared || _rng.Chance(0.4) ? Donation(now, wallet.Value.Balance, RequestRole.Race) : Transfer(now, wallet.Value.Balance, RequestRole.Race);
        if (message is null)
            return;
        var sent = _keys.Values.OrderByDescending(s => s.Key.Lo).First();
        sent.Attempts.Enqueue(RequestRole.Race);
        sent.Attempts.Enqueue(RequestRole.Race);
        RacesSent++;
        RaceCopiesSent += 3;
        output.Add(message);
        output.Add(message);
        output.Add(message);
    }

    /// <summary>Two different transfers, each most of the balance: the server must refuse the one that no longer fits.</summary>
    private void Overdraw(double now, List<OutMessage> output)
    {
        var wallet = Model.EffectiveWallet();
        if (wallet is null || wallet.Value.Balance < 2000 || Model.Mode == EffectiveCreditMode.Shared || PickPeer(teammateOnly: true) is not { } to)
            return;
        long amount = wallet.Value.Balance * 6 / 10;
        int group = ++_group;
        _raceGroups[group] = (2, 0);
        OverdrawPairs++;
        for (int i = 0; i < 2; i++)
        {
            var sent = Create(Kind.Transfer, RequestRole.Overdraw, 0, key => EncodeTransfer(key, to, amount), null);
            sent.Group = group;
            output.Add(sent.Message);
        }
    }

    // ------------------------------------------------------------------ requests

    private Sent Create(Kind kind, RequestRole role, ulong loanId, Func<Id128T, OutMessage> build, Func<Id128T, OutMessage>? alter)
    {
        ulong n = ++_counter;
        var key = new Id128T { Hi = KeyDomain | (uint)_playerId, Lo = n };
        var sent = new Sent { Kind = kind, LoanId = loanId, Key = (key.Hi, key.Lo), Message = build(key) };
        if (alter is not null)
            sent.Altered = () => alter(key);
        sent.Attempts.Enqueue(role);
        _keys[sent.Key] = sent;
        _sentByKind[kind] = _sentByKind.GetValueOrDefault(kind) + 1;
        return sent;
    }

    private static Id128T LoanKey(ulong id) => new() { Lo = id };

    private static OutMessage EncodeTransfer(Id128T key, int to, long amount) =>
        new(MsgType.CreditTransferRequest, MessageEncoder.EncodePayload(b => CreditTransferRequest.Pack(b, new CreditTransferRequestT { RequestKey = key, ToPlayer = (ushort)to, Amount = amount, Memo = "fakenode" }), 96));

    private static OutMessage EncodeDonate(Id128T key, int to, long amount) =>
        new(MsgType.DonateRequest, MessageEncoder.EncodePayload(b => DonateRequest.Pack(b, new DonateRequestT { RequestKey = key, ToPlayer = (ushort)to, Amount = amount, Memo = "fakenode" }), 96));

    private static OutMessage EncodeDeposit(Id128T key, long amount) =>
        new(MsgType.PoolDepositRequest, MessageEncoder.EncodePayload(b => PoolDepositRequest.Pack(b, new PoolDepositRequestT { RequestKey = key, Amount = amount }), 64));

    private static OutMessage EncodeWithdraw(Id128T key, long amount) =>
        new(MsgType.PoolWithdrawRequest, MessageEncoder.EncodePayload(b => PoolWithdrawRequest.Pack(b, new PoolWithdrawRequestT { RequestKey = key, Amount = amount }), 64));

    private static OutMessage EncodeOffer(Id128T key, int borrower, long principal, long repay, uint due) =>
        new(MsgType.LoanOffer, MessageEncoder.EncodePayload(b => LoanOffer.Pack(b, new LoanOfferT
        {
            RequestKey = key,
            Borrower = (ushort)borrower,
            Principal = principal,
            RepayTotal = repay,
            DueInS = due,
            OfferTtlS = 25,
            AutoRepayPct = 0,
            Memo = "fakenode loan",
        }), 128));

    private static OutMessage EncodeRespond(Id128T key, ulong loan, bool accept) =>
        new(MsgType.LoanRespond, MessageEncoder.EncodePayload(b => LoanRespond.Pack(b, new LoanRespondT { RequestKey = key, LoanId = LoanKey(loan), Accept = accept }), 64));

    private static OutMessage EncodeRepay(Id128T key, ulong loan, long amount) =>
        new(MsgType.LoanRepay, MessageEncoder.EncodePayload(b => LoanRepay.Pack(b, new LoanRepayT { RequestKey = key, LoanId = LoanKey(loan), Amount = amount }), 64));

    private static OutMessage EncodeForgive(Id128T key, ulong loan, long amount) =>
        new(MsgType.LoanForgive, MessageEncoder.EncodePayload(b => LoanForgive.Pack(b, new LoanForgiveT { RequestKey = key, LoanId = LoanKey(loan), Amount = amount }), 64));

    private static OutMessage EncodeCancel(Id128T key, ulong loan) =>
        new(MsgType.LoanCancel, MessageEncoder.EncodePayload(b => LoanCancel.Pack(b, new LoanCancelT { RequestKey = key, LoanId = LoanKey(loan) }), 64));

    // ------------------------------------------------------------------ summary

    public string Summary() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"requests={RequestsSent} ok={RequestsOk} rejected={RequestsRejected} skipped={Skipped} " +
        $"[{string.Join(",", SentByKind.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}] " +
        $"rejects=[{string.Join(",", RejectReasons.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}"))}] " +
        $"replays={ReplaysSent}(idempotent={ReplaysIdempotent} rate-limited={ReplaysRateLimited} other={ReplaysOther}) " +
        $"reuses={ReusesSent}(rejected={ReusesRejected} rate-limited={ReusesRateLimited} other={ReusesOther}) " +
        $"races={RacesSent} overdraw-pairs={OverdrawPairs} both-ok={OverdrawBothOk} duplicate-effects={DuplicateEffects}");
}
