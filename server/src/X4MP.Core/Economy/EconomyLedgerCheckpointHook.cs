using X4MP.Core.Saves;

namespace X4MP.Core.Economy;

/// <summary>Connects the save service to the economy: checkpoints record the ledger mark, and loading an older checkpoint rolls the ledger back (ADR-053).</summary>
public sealed class EconomyLedgerCheckpointHook(Func<EconomyModule?> economy) : ILedgerCheckpointHook
{
    public LedgerMark? CaptureMark() => economy()?.CaptureMark();

    public LedgerRollbackResult? RollBack(LedgerMark? mark, string checkpointLabel, DateTimeOffset checkpointAt, string reason) =>
        economy()?.RollBackLedger(mark, checkpointLabel, checkpointAt, reason);
}
