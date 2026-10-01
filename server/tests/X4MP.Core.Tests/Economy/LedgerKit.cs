using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Tests.Session;

namespace X4MP.Core.Tests.Economy;

/// <summary>A ledger and auditor on the in-memory store with a fake clock and a recording event publisher.</summary>
public sealed class LedgerKit
{
    public const long Session = 7;

    public LedgerKit()
    {
        Store = new InMemoryEconomyStore();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Events = new RecordingEvents();
        Ledger = new EconomyLedger(Session, Store, Time, Events);
        Auditor = new EconomyAuditor(Ledger, Store, Time);
    }

    public InMemoryEconomyStore Store { get; }

    public FakeTimeProvider Time { get; }

    public RecordingEvents Events { get; }

    public EconomyLedger Ledger { get; }

    public EconomyAuditor Auditor { get; }

    public long Balance(WalletId id) => Ledger.BalanceOf(id);

    /// <summary>Credits a player wallet out of the World wallet.</summary>
    public void Fund(int player, long amount) =>
        Assert.True(Ledger.Post(new PostRequest
        {
            Kind = TxKind.GameIncome,
            Actor = "authority",
            Entries = [new(WalletId.World, -amount), new(WalletId.Player(player), amount)],
        }).Ok);
}
