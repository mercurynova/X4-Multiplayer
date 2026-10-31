using Google.FlatBuffers;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.Core.Tests.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Economy;

/// <summary>
/// M5-29: the ledger list a node asks for on the economy screen. The point of these tests is the visibility rule - a page
/// holds what the requester could already see in a WalletUpdate, and nothing of anybody else's.
/// </summary>
public sealed class EconomyLedgerModuleTests
{
    private static FlatBufferBuilder LedgerFrame(ushort limit = 0, string before = "")
    {
        var fbb = new FlatBufferBuilder(128);
        var request = new LedgerRequestT { RequestKey = new Id128T { Lo = 7, Hi = 8 }, Limit = limit, Before = before };
        fbb.Finish(LedgerRequest.Pack(fbb, request).Value);
        return fbb;
    }

    private static async Task<(ActorRig Rig, JoinedNode Boss, JoinedNode Pilot, JoinedNode Other)> RunningAsync(
        EconomyModule module)
    {
        var rig = new ActorRig(modules: module);
        var boss = await rig.JoinAuthorityAsync();
        var pilot = await rig.JoinAsync("Pilot");
        var other = await rig.JoinAsync("Other");
        await rig.BringInGameAsync(boss);
        await rig.BringInGameAsync(pilot);
        await rig.BringInGameAsync(other);
        module.OnSessionPhaseChanged(SessionPhase.AuthorityLoading, SessionPhase.Running); // the rig has no world checkpoint, so tell the module
        return (rig, boss, pilot, other);
    }

    private static LedgerPage LastPage(JoinedNode node) => node.Connection.SentOf(MsgType.LedgerPage)[^1].Decode<LedgerPage>();

    [Fact]
    public async Task APageHoldsTheTransactionsThePlayerMaySeeAndNoOneElses()
    {
        var store = new InMemoryEconomyStore();
        var options = new EconomyOptions { StartingCredits = 1000, CreditMode = CreditMode.PerPlayer };
        var module = new EconomyModule(() => options, store);
        var (rig, _, pilot, other) = await RunningAsync(module);
        await using (rig)
        {
            var service = module.Service!;
            var moved = service.Transfer(pilot.PlayerId, "key-1", other.PlayerId, 200);
            Assert.True(moved.Ok, moved.Reason.ToString());

            await rig.SendAsync(pilot, MsgType.LedgerRequest, LedgerFrame());

            var page = LastPage(pilot);
            Assert.Equal(EconomyStatus.Ok, page.Status);
            Assert.False(page.More);
            Assert.True(page.RowsLength >= 2, $"a starting-credits row and the transfer: {page.RowsLength}");

            var transfer = page.Rows(0)!.Value;
            Assert.Equal(LedgerKind.Transfer, transfer.Kind);
            Assert.Equal(-200, transfer.Delta);                       // only the sender's line is visible to the sender
            Assert.Equal(X4MP.Proto.WalletKind.Player, transfer.Wallet!.Value.Kind);
            Assert.Equal((ushort)pilot.PlayerId, transfer.Wallet!.Value.OwnerId);
            Assert.Equal((ushort)pilot.PlayerId, transfer.Actor);
            Assert.Equal(string.Empty, transfer.RefType);             // a transfer carries no domain reference
            Assert.False(transfer.Reversed);

            // Every row of the page belongs to the requester: nothing of the other player's wallet leaks through.
            for (var i = 0; i < page.RowsLength; i++)
            {
                Assert.Equal((ushort)pilot.PlayerId, page.Rows(i)!.Value.Wallet!.Value.OwnerId);
            }

            await rig.SendAsync(other, MsgType.LedgerRequest, LedgerFrame());
            var theirs = LastPage(other);
            Assert.Equal(200, theirs.Rows(0)!.Value.Delta);           // the receiving side of the same transaction
            for (var i = 0; i < theirs.RowsLength; i++)
            {
                Assert.Equal((ushort)other.PlayerId, theirs.Rows(i)!.Value.Wallet!.Value.OwnerId);
            }
        }
    }

    [Fact]
    public async Task APagePagesBackwardsByCursorWithoutRepeatingARow()
    {
        var store = new InMemoryEconomyStore();
        var options = new EconomyOptions { StartingCredits = 1000, CreditMode = CreditMode.PerPlayer };
        var module = new EconomyModule(() => options, store);
        var (rig, _, pilot, other) = await RunningAsync(module);
        await using (rig)
        {
            var service = module.Service!;
            for (var i = 0; i < 4; i++)
            {
                Assert.True(service.Transfer(pilot.PlayerId, $"key-{i}", other.PlayerId, 10).Ok);
            }

            await rig.SendAsync(pilot, MsgType.LedgerRequest, LedgerFrame(limit: 2));
            var first = LastPage(pilot);
            Assert.Equal(2, first.RowsLength);
            Assert.True(first.More, "five visible rows and a page of two: there is another page");

            await rig.SendAsync(pilot, MsgType.LedgerRequest, LedgerFrame(limit: 2, before: first.Rows(1)!.Value.Id));
            var second = LastPage(pilot);
            Assert.True(second.RowsLength > 0);
            for (var i = 0; i < second.RowsLength; i++)
            {
                var id = second.Rows(i)!.Value.Id;
                Assert.NotEqual(first.Rows(0)!.Value.Id, id);
                Assert.NotEqual(first.Rows(1)!.Value.Id, id);
                Assert.True(string.CompareOrdinal(id, first.Rows(1)!.Value.Id) < 0, "a cursor page is strictly older");
            }
        }
    }
}
