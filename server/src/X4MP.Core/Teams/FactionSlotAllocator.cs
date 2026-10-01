namespace X4MP.Core.Teams;

/// <summary>
/// Hands out the X4 faction slots <c>1..<see cref="TeamOptions.MaxFactionSlots"/></c> (faction <c>x4mp_team_N</c>, ADR-014).
/// The lowest free slot goes first. Not thread-safe: owned by the team module on the actor thread.
/// </summary>
public sealed class FactionSlotAllocator
{
    private readonly bool[] _used = new bool[TeamOptions.MaxFactionSlots + 1]; // index 0 unused

    public static int Capacity => TeamOptions.MaxFactionSlots;

    public int FreeCount => _used.Skip(1).Count(u => !u);

    public bool IsUsed(int slot) => slot is >= 1 and <= TeamOptions.MaxFactionSlots && _used[slot];

    /// <summary>The lowest free slot, or false when all eight are in use (<c>NoFactionSlot</c>).</summary>
    public bool TryAllocate(out int slot)
    {
        for (int i = 1; i <= TeamOptions.MaxFactionSlots; i++)
        {
            if (!_used[i])
            {
                _used[i] = true;
                slot = i;
                return true;
            }
        }

        slot = 0;
        return false;
    }

    /// <summary>Takes a specific slot; false when it is out of range or taken.</summary>
    public bool TryReserve(int slot)
    {
        if (slot is < 1 or > TeamOptions.MaxFactionSlots || _used[slot])
        {
            return false;
        }

        _used[slot] = true;
        return true;
    }

    public void Release(int slot)
    {
        if (slot is >= 1 and <= TeamOptions.MaxFactionSlots)
        {
            _used[slot] = false;
        }
    }

    public void Clear() => Array.Clear(_used);
}
