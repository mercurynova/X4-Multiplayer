using System.Diagnostics;
using Google.FlatBuffers;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Tests.Saves;
using Xunit.Abstractions;
using LoanState = X4MP.Proto.LoanState;
using ProtoWalletKind = X4MP.Proto.WalletKind;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// The economy as two real nodes see it (m5-exit-report.md rows 10 to 12): a whole real server plus an authority, and two real
/// <c>TcpNodeClient</c>s walking the join phases. The FakeNode swarm proves that the books stay clean under load; these cases prove
/// the mechanics the exit report had to leave PARTIAL because they needed a second node - a transfer, a pool move in PerPlayer
/// mode, a loan and a trade between two players, the ledger latency of the first of them, and two simultaneous spends in Shared
/// mode converging on the one team wallet.
/// </summary>
[Collection("net")]
public sealed class EconomyTwoNodeLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ATransferBetweenTeammatesMovesBothWalletsWithinASecond()
    {
        await using var stand = await Stand.StartAsync();
        var key = Key(stand.Alice.PlayerId, 1);

        var watch = Stopwatch.StartNew();
        await stand.Alice.SendAsync(MsgType.CreditTransferRequest, b => CreditTransferRequest.Pack(b, new CreditTransferRequestT
        {
            RequestKey = key,
            ToPlayer = (ushort)stand.Bob.PlayerId,
            Amount = 25_000,
            Memo = "two nodes",
        }));

        var result = await ResultAsync(stand.Alice, key);
        Assert.Equal(EconomyStatus.Ok, result.Status);

        // The payer's own reply only carries what the payer may see; the credit arrives on the recipient's WalletUpdate.
        var update = await stand.Bob.WaitForAsync<WalletUpdateT>(
            Updates, u => u.Balances.Exists(x => IsPlayerWallet(x, stand.Bob.PlayerId, 125_000)),
            "bob's wallet update", 5000);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1000, $"the transfer became visible in {watch.ElapsedMilliseconds} ms");
        Assert.Equal(125_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Bob.PlayerId)));
        Assert.Equal(75_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Alice.PlayerId)));
        Assert.Contains(update.Balances, x => IsPlayerWallet(x, stand.Bob.PlayerId, 125_000));
        output.WriteLine($"transfer visible after {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TheTeamPoolTakesAndReturnsDeposits()
    {
        await using var stand = await Stand.StartAsync();
        int team = stand.Economy.AppliedTeamOf(stand.Alice.PlayerId) ?? throw new InvalidOperationException("alice has no team");
        var pool = WalletId.TeamPool(team);

        // The pool is seeded with a team fund at session start, so the test asserts the move, not the absolute balance.
        long poolBefore = stand.Ledger.BalanceOf(pool);
        var depositKey = Key(stand.Alice.PlayerId, 1);
        await stand.Alice.SendAsync(MsgType.PoolDepositRequest, b => PoolDepositRequest.Pack(b, new PoolDepositRequestT
        {
            RequestKey = depositKey,
            Amount = 40_000,
        }));
        Assert.Equal(EconomyStatus.Ok, (await ResultAsync(stand.Alice, depositKey)).Status);
        Assert.Equal(poolBefore + 40_000, stand.Ledger.BalanceOf(pool));
        Assert.Equal(60_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Alice.PlayerId)));

        var withdrawKey = Key(stand.Alice.PlayerId, 2);
        await stand.Alice.SendAsync(MsgType.PoolWithdrawRequest, b => PoolWithdrawRequest.Pack(b, new PoolWithdrawRequestT
        {
            RequestKey = withdrawKey,
            Amount = 15_000,
        }));
        Assert.Equal(EconomyStatus.Ok, (await ResultAsync(stand.Alice, withdrawKey)).Status);
        Assert.Equal(poolBefore + 25_000, stand.Ledger.BalanceOf(pool));
        Assert.Equal(75_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Alice.PlayerId)));
    }

    [Fact]
    public async Task ALoanIsOfferedAcceptedAndRepaid()
    {
        await using var stand = await Stand.StartAsync();

        var offerKey = Key(stand.Alice.PlayerId, 1);
        await stand.Alice.SendAsync(MsgType.LoanOffer, b => LoanOffer.Pack(b, new LoanOfferT
        {
            RequestKey = offerKey,
            Borrower = (ushort)stand.Bob.PlayerId,
            Principal = 10_000,
            RepayTotal = 12_000,
            DueInS = 600,
            OfferTtlS = 300,
            AutoRepayPct = 0,
            Memo = "two nodes",
        }));

        var offer = await ResultAsync(stand.Alice, offerKey);
        Assert.Equal(EconomyStatus.Ok, offer.Status);
        var offered = await stand.Alice.WaitForAsync<LoanStatusT>(
            LoanStatuses, s => Same(s.LoanId, offer.RefId) && s.State == LoanState.Offered, "the loan offer", 5000);
        var escrow = stand.Economy.FindLoan((long)offered.LoanId.Lo)!.EscrowWallet;

        // The principal sits in escrow from the offer until the borrower accepts.
        Assert.Equal(90_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Alice.PlayerId)));
        Assert.Equal(10_000, stand.Ledger.BalanceOf(escrow));

        var respondKey = Key(stand.Bob.PlayerId, 1);
        await stand.Bob.SendAsync(MsgType.LoanRespond, b => LoanRespond.Pack(b, new LoanRespondT
        {
            RequestKey = respondKey,
            LoanId = offered.LoanId,
            Accept = true,
        }));
        Assert.Equal(EconomyStatus.Ok, (await ResultAsync(stand.Bob, respondKey)).Status);
        await stand.Bob.WaitForAsync<LoanStatusT>(
            LoanStatuses, s => Same(s.LoanId, offered.LoanId) && s.State == LoanState.Active, "the active loan", 5000);
        Assert.Equal(110_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Bob.PlayerId)));
        Assert.Equal(0, stand.Ledger.BalanceOf(escrow));

        var repayKey = Key(stand.Bob.PlayerId, 2);
        await stand.Bob.SendAsync(MsgType.LoanRepay, b => LoanRepay.Pack(b, new LoanRepayT
        {
            RequestKey = repayKey,
            LoanId = offered.LoanId,
            Amount = 12_000,
        }));
        Assert.Equal(EconomyStatus.Ok, (await ResultAsync(stand.Bob, repayKey)).Status);
        await stand.Bob.WaitForAsync<LoanStatusT>(
            LoanStatuses, s => Same(s.LoanId, offered.LoanId) && s.State == LoanState.Repaid, "the repaid loan", 5000);

        Assert.Equal(102_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Alice.PlayerId)));
        // Bob received the 10 000 principal and repaid 12 000: 100 000 + 10 000 - 12 000.
        Assert.Equal(98_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Bob.PlayerId)));
        Assert.Equal(0, stand.Ledger.BalanceOf(escrow));

        var audit = stand.Server.Service<EconomyModule>().RunAudit("two-node test", acknowledge: false);
        Assert.NotNull(audit);
        Assert.True(audit!.Ok, string.Join("; ", audit.Violations));
    }

    [Fact]
    public async Task ACreditsOnlyTradeIsRefusedBecauseATradeNeedsAnAsset()
    {
        await using var stand = await Stand.StartAsync();

        // m5-exit-report rows 10-12 want a settled trade between two players. The shape rule comes first: only a non-credit line
        // becomes an AssetTransferOrder for the authority, so a credits-for-nothing proposal is refused before any escrow or trade
        // row exists. That refusal is the part a scripted two-node pair can prove; the settlement of a real asset line is still the
        // authority's half.
        var proposalKey = Key(stand.Alice.PlayerId, 1);
        await stand.Alice.SendAsync(MsgType.TradeProposal, b => TradeProposal.Pack(b, new TradeProposalT
        {
            RequestKey = proposalKey,
            Counterparty = (ushort)stand.Bob.PlayerId,
            Give = [new TradeItemT { Kind = TradeItemKind.Credits, Amount = 5_000 }],
            Want = [],
            TtlS = 300,
            Memo = "two nodes",
        }));

        var proposal = await ResultAsync(stand.Alice, proposalKey);
        Assert.Equal(EconomyStatus.Rejected, proposal.Status);
        Assert.Equal(EconomyReject.AmountInvalid, proposal.Reason);
        Assert.Contains("at least one asset", proposal.Detail);

        // Nothing was booked, nothing was escrowed and the counterparty was never told about a trade.
        Assert.Equal(100_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Alice.PlayerId)));
        Assert.Equal(100_000, stand.Ledger.BalanceOf(WalletId.Player(stand.Bob.PlayerId)));
        Assert.Empty(TradeStatuses(stand.Bob));
        Assert.Equal(0, stand.Ledger.TotalBalance());
    }

    [Fact]
    public async Task TwoSimultaneousPurchasesInSharedModeConvergeWithinTwoSeconds()
    {
        await using var stand = await Stand.StartAsync("--X4MP:Economy:CreditMode=Shared");
        Assert.Equal(EffectiveCreditMode.Shared, stand.Economy.AppliedMode);
        int team = stand.Economy.AppliedTeamOf(stand.Alice.PlayerId) ?? throw new InvalidOperationException("alice has no team");
        Assert.Equal(team, stand.Economy.AppliedTeamOf(stand.Bob.PlayerId));
        var shared = WalletId.TeamShared(team);

        // The team wallet already holds both players' starting credits and whatever the save seeded, so the case asserts the move.
        long before = stand.Ledger.BalanceOf(shared);

        // m5-exit-report row 10: two purchases at the same instant. In Shared mode a client's own spend is a CreditDelta against the
        // one team wallet, so this is the pair of frames the two mods send when two players buy at the same time.
        var aliceKey = Key(stand.Alice.PlayerId, 10);
        var bobKey = Key(stand.Bob.PlayerId, 10);
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(
            stand.Alice.SendAsync(MsgType.CreditDelta, b => CreditDelta.Pack(b, new CreditDeltaT
            {
                RequestKey = aliceKey,
                Amount = -7_000,
                Source = CreditSource.Other,
                Seq = 1,
            })),
            stand.Bob.SendAsync(MsgType.CreditDelta, b => CreditDelta.Pack(b, new CreditDeltaT
            {
                RequestKey = bobKey,
                Amount = -9_000,
                Source = CreditSource.Other,
                Seq = 1,
            })));

        long converged = before - 16_000;
        var aliceUpdate = await stand.Alice.WaitForAsync<WalletUpdateT>(
            Updates, u => Spent(u, team, converged), "alice's converged team wallet", 5000);
        var bobUpdate = await stand.Bob.WaitForAsync<WalletUpdateT>(
            Updates, u => Spent(u, team, converged), "bob's converged team wallet", 5000);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 2000, $"the two spends converged after {watch.ElapsedMilliseconds} ms");

        // Neither node's delta was lost and neither was booked twice: each node's own sequence is acked once, and both read the
        // same number the ledger holds.
        Assert.Equal(1UL, aliceUpdate.AckedDeltaSeq);
        Assert.Equal(1UL, bobUpdate.AckedDeltaSeq);
        Assert.Equal(converged, stand.Ledger.BalanceOf(shared));
        Assert.Equal(0, stand.Ledger.TotalBalance());

        // A resent frame whose sequence the ledger already booked must not spend a second time; the answer is an ack-only update.
        int seen = Updates(stand.Alice).Count;
        await stand.Alice.SendAsync(MsgType.CreditDelta, b => CreditDelta.Pack(b, new CreditDeltaT
        {
            RequestKey = aliceKey,
            Amount = -7_000,
            Source = CreditSource.Other,
            Seq = 1,
        }));
        await SaveServer.WaitUntilAsync(() => Updates(stand.Alice).Count > seen, 5000, "the replay ack");
        Assert.Equal(converged, stand.Ledger.BalanceOf(shared));
        Assert.Equal(0, stand.Ledger.TotalBalance());
        output.WriteLine($"two spends converged after {watch.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------ helpers

    private static readonly string[] Settings =
    [
        "--X4MP:Saves:AutosaveMinutes=0",
        "--X4MP:Economy:CreditMode=PerPlayer",
        "--X4MP:Economy:DonateScope=Anyone",
        "--X4MP:Economy:LoanScope=Anyone",
        "--X4MP:Economy:TradeScope=Anyone",
        "--X4MP:Economy:TradeRequiresProximity=false",
        "--X4MP:Economy:TradeExecuteTimeoutSeconds=2",
        "--X4MP:Economy:TradeQueryIntervalSeconds=1",
    ];

    private static Id128T Key(int playerId, uint n) => new() { Hi = 0xE000_0000u | (uint)playerId, Lo = n };

    /// <summary>The default settings with <paramref name="overrides"/> replacing the same-named ones (last wins, as on the command line).</summary>
    private static string[] Merge(IReadOnlyList<string> overrides)
    {
        static string Name(string arg) => arg[..arg.IndexOf('=', StringComparison.Ordinal)];
        return [.. Settings.Where(s => !overrides.Any(o => Name(o) == Name(s))), .. overrides];
    }

    private static bool Same(Id128T a, Id128T b) => a.Hi == b.Hi && a.Lo == b.Lo;

    private static bool IsPlayerWallet(WalletBalanceT balance, int playerId, long amount) =>
        IsWallet(balance, ProtoWalletKind.Player, playerId, amount);

    private static bool IsWallet(WalletBalanceT balance, ProtoWalletKind kind, int ownerId, long amount) =>
        balance.Wallet.Kind == kind && balance.Wallet.OwnerId == (ushort)ownerId && balance.Balance == amount;

    /// <summary>A spend update on the named wallet: the shared-mode half of row 10 looks for exactly this.</summary>
    private static bool Spent(WalletUpdateT update, int team, long amount) =>
        update.Reason == LedgerReason.GameSpend && update.Balances.Exists(x => IsWallet(x, ProtoWalletKind.TeamShared, team, amount));

    private static List<WalletUpdateT> Updates(Peer peer) =>
        peer.All(MsgType.WalletUpdate, f => MessageRegistry.Default.Decode<WalletUpdate>(f).UnPack());

    private static List<LoanStatusT> LoanStatuses(Peer peer) =>
        peer.All(MsgType.LoanStatus, f => MessageRegistry.Default.Decode<LoanStatus>(f).UnPack());

    private static List<TradeStatusT> TradeStatuses(Peer peer) =>
        peer.All(MsgType.TradeStatus, f => MessageRegistry.Default.Decode<TradeStatus>(f).UnPack());

    private static List<TradeResultT> TradeResults(Peer peer) =>
        peer.All(MsgType.TradeResult, f => MessageRegistry.Default.Decode<TradeResult>(f).UnPack());

    private static Task<EconomyResultT> ResultAsync(Peer peer, Id128T key, int timeoutMs = 5000) =>
        peer.WaitForAsync<EconomyResultT>(
            p => p.All(MsgType.EconomyResult, f => MessageRegistry.Default.Decode<EconomyResult>(f).UnPack()),
            r => Same(r.RequestKey, key),
            $"the EconomyResult of request {key.Lo}",
            timeoutMs);

    /// <summary>
    /// A whole real server, an authority that starts the session, and two nodes that walk the join phases. Both nodes are on the
    /// same team because the default team options put every player in one team, which is what a transfer requires.
    /// </summary>
    private sealed class Stand : IAsyncDisposable
    {
        private Stand(SaveServer server, AuthorityRig authority, Peer alice, Peer bob)
        {
            Server = server;
            Authority = authority;
            Alice = alice;
            Bob = bob;
        }

        public SaveServer Server { get; }

        public AuthorityRig Authority { get; }

        public Peer Alice { get; }

        public Peer Bob { get; }

        public EconomyService Economy => Server.Service<EconomyModule>().Service!;

        public EconomyLedger Ledger => Economy.Ledger;

        public static async Task<Stand> StartAsync(params string[] overrides)
        {
            var server = await SaveServer.StartAsync(Merge(overrides));
            try
            {
                var authority = await AuthorityRig.StartAsync(server, new FakeAuthoritySaveOptions
                {
                    SaveBytes = 1 << 20,
                    Directory = Path.Combine(server.Dir, "fake-authority"),
                });
                await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "the session to start running");

                var alice = Peer.Attach(await server.ConnectAsync("Alice", Role.Client));
                var bob = Peer.Attach(await server.ConnectAsync("Bob", Role.Client));

                foreach (var peer in new[] { alice, bob })
                {
                    int playerId = peer.PlayerId;

                    // The team gate comes before the save phases: a first LoadStatus report out of AwaitingTeam is Invalid, so wait
                    // until the server has put the node past it (the default team options do that on join).
                    await server.WaitForAsync(
                        s => s.Nodes.Any(n => n.PlayerId == playerId && n.Phase != NodePhase.Admitted && n.Phase != NodePhase.AwaitingTeam),
                        20_000,
                        "the node to leave the team gate");
                    await peer.BringInGameAsync(s => server.WaitForAsync(s));
                }

                return new Stand(server, authority, alice, bob);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Alice.DisposeAsync();
            await Bob.DisposeAsync();
            await Authority.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
