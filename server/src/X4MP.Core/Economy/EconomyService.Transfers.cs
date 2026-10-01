using System.Globalization;
using X4MP.Core.Events;
using X4MP.Core.Teams;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>A teammate transfer, donation or pool movement that was booked (audit trail and GUI feed).</summary>
public sealed record EconomyActionCompleted(
    DateTimeOffset At, long? Session, string Kind, long FromPlayer, long? ToPlayer, long Amount, string TxId, string RequestKey)
    : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => FromPlayer;
}

public sealed partial class EconomyService
{
    private const int MaxMemoLength = 200;

    /// <summary>
    /// Takes one slot of the per-player request budget (5 per 10 s). Call once per incoming economy request, before the
    /// action itself; a refused request has <see cref="EconomyReject.RateLimited"/>.
    /// </summary>
    public EconomyActionResult? CheckRate(int playerId) =>
        _rate.TryAcquire(playerId) ? null : EconomyActionResult.Rejected(EconomyReject.RateLimited, "5 economy requests per 10 s");

    /// <summary>Drops the request budget of a player who left.</summary>
    public void ForgetRate(int playerId) => _rate.Forget(playerId);

    /// <summary>
    /// Teammate transfer (<c>CreditTransferRequest</c>): own wallet to a teammate's wallet (or, with
    /// <see cref="EconomyOptions.AllowAlliedTransfers"/>, to a player of an allied team). Posted as one <see cref="TxKind.Transfer"/>.
    /// In Shared mode two members of one team have the same wallet: <see cref="EconomyReject.NotApplicableInSharedMode"/>
    /// (server-design 2.14, "same-team actions in Shared mode are rejected with SameWallet"; the wire has no SameWallet).
    /// </summary>
    public EconomyActionResult Transfer(int playerId, string requestKey, int toPlayer, long amount, string? memo = null) =>
        MovePlayerToPlayer(TxKind.Transfer, LedgerReason.Transfer, "Transfer", playerId, requestKey, toPlayer, amount, memo);

    /// <summary>
    /// Donation (<c>DonateRequest</c>) to any player the <see cref="EconomyOptions.DonateScope"/> allows, judged against the
    /// live relation matrix. Works in both credit modes (in Shared mode from and to the team wallets).
    /// </summary>
    public EconomyActionResult Donate(int playerId, string requestKey, int toPlayer, long amount, string? memo = null) =>
        MovePlayerToPlayer(TxKind.Donate, LedgerReason.Donation, "Donate", playerId, requestKey, toPlayer, amount, memo);

    private EconomyActionResult MovePlayerToPlayer(
        TxKind kind, LedgerReason reason, string type, int playerId, string requestKey, int toPlayer, long amount, string? memo)
    {
        var note = memo is { Length: > MaxMemoLength } ? memo[..MaxMemoLength] : memo;
        var hash = PayloadHasher.Hash(type, toPlayer, amount, memo ?? string.Empty);

        // Replays first: the stored result is returned even if funds, scope or the freeze changed since.
        if (IsStoredRequest(playerId, requestKey))
        {
            return Finish(_ledger.Post(Build(kind, type, playerId, requestKey, hash, [], note)), reason, requestKey, null);
        }

        var rejected = ValidatePlayerToPlayer(kind, playerId, toPlayer, amount, out var from, out var to);
        if (rejected is not null)
        {
            return rejected;
        }

        var outcome = _ledger.Post(Build(kind, type, playerId, requestKey, hash, [new(from, -amount), new(to, amount)], note));
        var result = Finish(outcome, reason, requestKey, null);
        if (outcome.Ok && !outcome.Replayed)
        {
            PublishCompleted(type, playerId, toPlayer, amount, outcome, requestKey);
        }

        return result;
    }

    private static PostRequest Build(TxKind kind, string type, int playerId, string requestKey, byte[] hash, IReadOnlyList<PostEntry> entries, string? note) =>
        new()
        {
            Kind = kind,
            Actor = Actor(playerId),
            PlayerId = playerId,
            RequestId = requestKey,
            RequestType = type,
            PayloadHash = hash,
            Entries = entries,
            Note = note,
        };

    private EconomyActionResult? ValidatePlayerToPlayer(TxKind kind, int playerId, int toPlayer, long amount, out WalletId from, out WalletId to)
    {
        from = default;
        to = default;
        var options = _options();
        if (_ledger.IsFrozen)
        {
            return EconomyActionResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason);
        }

        if (amount <= 0)
        {
            return EconomyActionResult.Rejected(EconomyReject.AmountInvalid);
        }

        if (amount > options.MaxSingleTransfer)
        {
            return EconomyActionResult.Rejected(EconomyReject.OverMaxAmount, options.MaxSingleTransfer.ToString(CultureInfo.InvariantCulture));
        }

        if (!IsKnown(playerId) || !IsKnown(toPlayer))
        {
            return EconomyActionResult.Rejected(EconomyReject.UnknownPlayer, IsKnown(playerId) ? "unknown recipient" : null);
        }

        if (playerId == toPlayer)
        {
            return EconomyActionResult.Rejected(EconomyReject.NotParty, "sender and recipient are the same player");
        }

        var scope = ScopeVerdict(kind, options, playerId, toPlayer);
        if (scope is not null)
        {
            return scope;
        }

        from = EffectiveWallet(playerId);
        to = EffectiveWallet(toPlayer);
        if (from == to)
        {
            return EconomyActionResult.Rejected(EconomyReject.NotApplicableInSharedMode, "both players use the same wallet");
        }

        if (from.Kind == WalletKind.TeamShared && options.SharedWalletSpend == SharedWalletSpendPolicy.LeaderOnly
            && AppliedTeamOf(playerId) is { } team && _leaderOf(team) != playerId)
        {
            return EconomyActionResult.Rejected(EconomyReject.NotParty, "only the team leader may spend the shared wallet");
        }

        var outgoing = CheckOutgoing(playerId);
        if (outgoing is not null)
        {
            return outgoing;
        }

        if (_ledger.Find(to) is { Frozen: true })
        {
            return EconomyActionResult.Rejected(EconomyReject.EconomyFrozen, "recipient wallet frozen");
        }

        return _ledger.BalanceOf(from) < amount ? EconomyActionResult.Rejected(EconomyReject.InsufficientFunds) : null;
    }

    private EconomyActionResult? ScopeVerdict(TxKind kind, EconomyOptions options, int playerId, int toPlayer)
    {
        var teamA = LiveTeamOf(playerId);
        var teamB = LiveTeamOf(toPlayer);
        var same = teamA is not null && teamA == teamB;
        var allied = same || (teamA is { } a && teamB is { } b && _teams?.RelationBetween(a, b) == X4MP.Core.Teams.TeamRelation.Allied);

        if (kind == TxKind.Transfer)
        {
            if (same || (options.AllowAlliedTransfers && allied))
            {
                return null;
            }

            return EconomyActionResult.Rejected(EconomyReject.NotTeammate, options.AllowAlliedTransfers ? "recipient is neither a teammate nor allied" : null);
        }

        return options.DonateScope switch
        {
            EconomyScope.Off => EconomyActionResult.Rejected(EconomyReject.ScopeDisabled),
            EconomyScope.Teammates => same ? null : EconomyActionResult.Rejected(EconomyReject.ScopeDenied, "recipient is not a teammate"),
            EconomyScope.Allied => allied ? null : EconomyActionResult.Rejected(EconomyReject.ScopeDenied, "recipient's team is not allied"),
            _ => null,
        };
    }

    /// <summary>The team of a player in the live directory (the economy layout may lag a pending migration).</summary>
    private int? LiveTeamOf(int playerId) => _teams is null ? 1 : _teams.TeamOf(playerId);

    private void PublishCompleted(string type, int from, int? to, long amount, PostOutcome outcome, string requestKey) =>
        _events?.Publish(new EconomyActionCompleted(_time.GetUtcNow(), _ledger.SessionId, type, from, to, amount, outcome.TxId ?? string.Empty, requestKey));
}
