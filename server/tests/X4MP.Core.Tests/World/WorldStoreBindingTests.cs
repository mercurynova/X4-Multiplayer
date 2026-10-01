using X4MP.Core.World;

namespace X4MP.Core.Tests.World;

/// <summary>M1-06/07 follow-up 1: the world store learns its session through <c>OnSessionBegun</c>, not through a GUID in <c>settings_json</c>.</summary>
public sealed class WorldStoreBindingTests
{
    private sealed class BindingStore : IWorldStore
    {
        public List<long> Bound { get; } = [];

        public void BindSession(long sessionId) => Bound.Add(sessionId);

        public byte[]? TryLoadGalaxy(string saveSha256Hex) => null;

        public void SaveGalaxy(string saveSha256Hex, byte[] payload, DateTimeOffset at)
        {
        }

        public void AppendJournal(JournalRecord record)
        {
        }

        public void TruncateJournal(ulong beforeSeq)
        {
        }

        public IReadOnlyList<JournalRecord> LoadJournal() => [];

        public void AppendStrings(IReadOnlyList<StringTableEntry> entries)
        {
        }

        public IReadOnlyList<StringTableEntry> LoadStrings() => [];
    }

    [Fact]
    public void TheMirrorBindsItsStoreToEverySessionRowTheActorAnnounces()
    {
        var store = new BindingStore();
        var mirror = new WorldMirror(store: store);
        mirror.OnSessionBegun(7);
        mirror.OnSessionBegun(9); // the next session after a reset
        Assert.Equal([7L, 9L], store.Bound);
    }

    [Fact]
    public void AStoreWithoutASessionScopeIgnoresTheBinding()
    {
        var mirror = new WorldMirror(store: new InMemoryWorldStore());
        mirror.OnSessionBegun(1); // must not throw: the interface has a default no-op
        Assert.Equal(0, mirror.Count);
    }
}
