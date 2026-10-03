using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using X4MP.Proto;

namespace X4MP.FakeNode;

/// <summary>What the generated fake save looks like (tests feed the server broken ones).</summary>
public enum FakeSaveFlavor
{
    /// <summary>gzip of an X4-style <c>&lt;savegame&gt;</c> document.</summary>
    Valid,

    /// <summary>Plain text: no gzip magic.</summary>
    NotGzip,

    /// <summary>A valid gzip of an XML document whose root is not <c>&lt;savegame&gt;</c>.</summary>
    GzipNotASave,
}

/// <summary>A generated fake save (or manifest) on disk.</summary>
public sealed record FakeSaveFile(string Path, byte[] Sha256, long Size)
{
    public string ShaHex => Convert.ToHexStringLower(Sha256);
}

/// <summary>
/// Builds the fake save the fake authority uploads (server-design 6.2, M1-12): a deterministic gzip of an XML document that starts like a
/// real X4 save (<c>&lt;savegame&gt;&lt;info&gt;...</c>, so the server's sniff and metadata read pass) followed by pseudo-random filler up to the
/// requested size, so a 200 MB transfer moves real bytes. The same (seed, counter, size) always gives the same bytes and hash. The
/// manifest is a real <c>X4MF</c> FlatBuffers file built from the fake galaxy's stations.
/// </summary>
public static class FakeSaveGenerator
{
    /// <summary>Money (whole credits, as in the save header) the fake save claims the player has.</summary>
    public const long DefaultMoneyCredits = 10_000_000;

    /// <summary>
    /// Writes <c>fake-&lt;seed&gt;-&lt;counter&gt;-&lt;size&gt;.xml.gz</c> into <paramref name="directory"/> (or reuses it) and returns its hash. The file is
    /// about <paramref name="targetBytes"/> long (gzip with no compression: random filler does not compress anyway).
    /// </summary>
    public static FakeSaveFile CreateSave(
        string directory, ulong seed, int counter, long targetBytes, long moneyCredits = DefaultMoneyCredits, FakeSaveFlavor flavor = FakeSaveFlavor.Valid)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetBytes, 1024);
        Directory.CreateDirectory(directory);
        string suffix = flavor == FakeSaveFlavor.Valid ? string.Empty : "-" + flavor.ToString().ToLowerInvariant();
        string path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"fake-{seed}-{counter}-{targetBytes}{suffix}.xml.gz"));
        if (!File.Exists(path))
        {
            string temp = path + ".tmp";
            switch (flavor)
            {
                case FakeSaveFlavor.NotGzip:
                    File.WriteAllBytes(temp, Encoding.UTF8.GetBytes($"this is plain text, not a gzip file ({seed}/{counter})"));
                    break;
                case FakeSaveFlavor.GzipNotASave:
                    using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
                    {
                        gzip.Write(Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><html><body>not a save {seed}/{counter}</body></html>"));
                    }

                    break;
                default:
                    Write(temp, seed, counter, targetBytes, moneyCredits);
                    break;
            }

            File.Move(temp, path, overwrite: true);
        }

        return Describe(path);
    }

    /// <summary>Hashes an existing file.</summary>
    public static FakeSaveFile Describe(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return new FakeSaveFile(path, SHA256.HashData(file), file.Length);
    }

    private static void Write(string path, ulong seed, int counter, long targetBytes, long moneyCredits)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using (var gzip = new GZipStream(file, CompressionLevel.NoCompression, leaveOpen: true))
        {
            string head = string.Create(
                CultureInfo.InvariantCulture,
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame><info><save name=\"X4MP fake save {counter}\" date=\"{1_760_000_000 + counter}\"/>" +
                $"<game id=\"x4mp-fake\" version=\"900\" build=\"611726\"/><player name=\"FakeNode\" location=\"{{galaxy}}\" money=\"{moneyCredits}\"/></info>" +
                $"<universe seed=\"{seed}\" checkpoint=\"{counter}\">");
            gzip.Write(Encoding.UTF8.GetBytes(head));

            var rng = new DetRandom(DetHash.Hash(seed, 0x5A5E, (ulong)counter));
            var block = new byte[1 << 20];
            long written = head.Length;
            long want = Math.Max(0, targetBytes - 64);
            while (written < want)
            {
                int n = (int)Math.Min(block.Length, want - written);
                for (int i = 0; i + 8 <= n; i += 8)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(i), rng.NextUInt64());
                }

                gzip.Write(block, 0, n & ~7);
                written += n & ~7;
                if ((n & 7) != 0)
                {
                    written = want; // the tail is shorter than a word: done
                }
            }

            gzip.Write(Encoding.UTF8.GetBytes("</universe></savegame>"));
        }
    }

    /// <summary>The <c>X4MF</c> manifest of the fake world's stations (what the authority uploads next to the save).</summary>
    public static byte[] BuildManifest(FakeAuthority authority, Id128T checkpoint, double gameTime, uint nextNetId, bool emptyStations = false)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var galaxy = authority.World.Galaxy;
        var entries = new List<ManifestEntryT>();
        foreach (var e in galaxy.Entities.Where(e => e.IsStation && !emptyStations))
        {
            entries.Add(new ManifestEntryT
            {
                NetId = FakeNetIds.ToNetId(e.EntityId),
                Kind = e.Kind,
                ComponentId = (ulong)e.EntityId,
                MacroRef = authority.Strings.Index(e.Macro),
                OwnerRef = authority.Strings.Index(galaxy.Factions[e.Faction]),
                Sector = e.HomeSector,
                Idcode = e.IdCode,
                Position = new Vec3fT { X = (float)e.StaticPos.X, Y = (float)e.StaticPos.Y, Z = (float)e.StaticPos.Z },
            });
        }

        var manifest = new ManifestT
        {
            CheckpointId = checkpoint,
            GameTime = gameTime,
            NextNetId = nextNetId,
            Strings = [.. authority.Strings.Entries],
            Sectors = authority.BuildSectorInfos(),
            Entries = entries,
        };
        return manifest.SerializeToBinary();
    }

    /// <summary>Writes manifest bytes to <paramref name="directory"/> and returns the file.</summary>
    public static FakeSaveFile WriteManifest(string directory, string name, byte[] manifest)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".x4mf");
        File.WriteAllBytes(path, manifest);
        return new FakeSaveFile(path, SHA256.HashData(manifest), manifest.Length);
    }
}
