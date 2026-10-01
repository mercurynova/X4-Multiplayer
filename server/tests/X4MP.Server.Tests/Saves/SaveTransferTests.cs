using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using X4MP.Core.Saves;
using X4MP.FakeNode;
using X4MP.Proto;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Saves;

/// <summary>
/// The save pipeline end to end over real sockets (M1-12 acceptance): authority uploads save and manifest in-band, the server stores them
/// content-addressed and makes the checkpoint current, three clients download, verify, match and reach InGame. The CI size is small;
/// <c>X4MP_LONG_TESTS=1</c> runs the 200 MB acceptance.
/// </summary>
public sealed class SaveTransferTests(ITestOutputHelper output)
{
    private static string Sha(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static FakeAuthoritySaveOptions AuthorityOptions(SaveServer server, int megabytes) =>
        new() { SaveBytes = megabytes * 1024L * 1024, Directory = Path.Combine(server.Dir, "fake-authority") };

    [Fact]
    public async Task AuthorityUploadsAndThreeClientsDownloadVerifyAndReachInGame() =>
        await RunTransferAsync(SaveTestSize.CiMegabytes, clientsFirst: false);

    [Fact]
    public async Task ClientsThatJoinBeforeTheFirstCheckpointWaitForItAndThenDownload() =>
        await RunTransferAsync(4, clientsFirst: true);

    [LongFact]
    public async Task TwoHundredMegabyteSaveGoesFromTheAuthorityToThreeClients() =>
        await RunTransferAsync(SaveTestSize.LongMegabytes, clientsFirst: false);

    private async Task RunTransferAsync(int megabytes, bool clientsFirst)
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var clients = new List<ClientRig>();
        try
        {
            if (clientsFirst)
            {
                for (int i = 0; i < 3; i++)
                {
                    clients.Add(await ClientRig.StartAsync(server, server.NextName("Bot")));
                }
            }

            var clock = Stopwatch.StartNew();
            await using var authority = await AuthorityRig.StartAsync(server, AuthorityOptions(server, megabytes));
            await server.WaitForAsync(s => s.Phase == SessionPhase.Running, timeoutMs: 60_000, what: "first checkpoint stored");
            double uploadSeconds = clock.Elapsed.TotalSeconds;

            var result = authority.Saves.LastResult!;
            Assert.Equal(SaveStoreResult.Stored, result.SaveResult);
            Assert.Equal(SaveStoreResult.Stored, result.ManifestResult);
            string sha = result.Save.ShaHex;
            string storedPath = Path.Combine(server.Dir, "saves", sha + ".xml.gz");
            Assert.True(File.Exists(storedPath), "the save is in the content-addressed store");
            Assert.Equal(sha, Sha(storedPath));
            Assert.True(File.Exists(Path.Combine(server.Dir, "saves", result.Manifest.ShaHex + ".x4mf")));
            Assert.Equal(sha, server.Saves.Status.CurrentSha256);
            Assert.Equal(0, authority.Saves.LastResult!.SaveResumeOffset);

            if (!clientsFirst)
            {
                for (int i = 0; i < 3; i++)
                {
                    clients.Add(await ClientRig.StartAsync(server, server.NextName("Bot")));
                }
            }

            var downloadClock = Stopwatch.StartNew();
            await Task.WhenAll(clients.Select(c => c.WaitReadyAsync(60_000)));
            double downloadSeconds = downloadClock.Elapsed.TotalSeconds;

            foreach (var client in clients)
            {
                Assert.Equal(sha, client.Saves.VerifiedSaveSha);
                Assert.Equal(sha, client.Saves.AnnouncedSaveSha); // SessionState.current_save_sha256
                Assert.Equal(result.Manifest.ShaHex, client.Saves.VerifiedManifestSha);
                Assert.Equal(sha, Sha(client.Saves.SavePath!));
                Assert.Equal(FakeJoinStage.InGame, client.Saves.Stage);
                Assert.Equal(result.Save.Size + result.Manifest.Size, client.Saves.BytesReceived);
                Assert.Equal(0, client.Saves.ChecksumFailures);
                Assert.True(client.Saves.StringEntries >= authority.Authority.Strings.Entries.Count, "the full string table is replayed");
            }

            var snapshot = await server.WaitForAsync(s => s.Nodes.Count(n => n.Phase == NodePhase.InGame) == 4, what: "all nodes in game");
            Assert.Equal(SessionPhase.Running, snapshot.Phase);

            double totalMb = result.Save.Size / 1048576.0;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"save {totalMb:F1} MB: authority -> server in {uploadSeconds:F1}s ({totalMb / uploadSeconds:F0} MB/s incl. generation and hashing); " +
                $"server -> 3 clients in {downloadSeconds:F1}s ({3 * totalMb / downloadSeconds:F0} MB/s aggregate, {totalMb / downloadSeconds:F0} MB/s per client)"));
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }
}
