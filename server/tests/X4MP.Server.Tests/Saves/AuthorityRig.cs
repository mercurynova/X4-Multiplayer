using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Saves;

/// <summary>A fake authority connected to a <see cref="SaveServer"/> with its receive loop running.</summary>
public sealed class AuthorityRig : IAsyncDisposable
{
    private readonly string _name;
    private CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    private AuthorityRig(string name, TcpNodeClient client, FakeAuthority authority, FakeAuthoritySaves saves)
    {
        _name = name;
        Client = client;
        Authority = authority;
        Saves = saves;
    }

    public TcpNodeClient Client { get; private set; }

    public FakeAuthority Authority { get; }

    public FakeAuthoritySaves Saves { get; }

    /// <summary>Every frame the server sent that the save job did not consume.</summary>
    public List<Frame> Other { get; } = [];

    public static async Task<AuthorityRig> StartAsync(
        SaveServer server, FakeAuthoritySaveOptions saveOptions, string name = "Auth", int sectors = 20, int ships = 200, bool ready = true)
    {
        var client = await server.ConnectAsync(name, Role.Authority);
        var galaxy = FakeGalaxy.Generate(42, new GalaxyOptions { SectorCount = sectors, ShipCount = ships });
        var authority = new FakeAuthority(new FakeWorld(galaxy));
        var saves = new FakeAuthoritySaves(client, authority, saveOptions);
        var rig = new AuthorityRig(name, client, authority, saves);
        rig.StartLoop();
        if (ready)
        {
            await rig.ReportReadyAsync();
        }

        return rig;
    }

    private void StartLoop()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var client = Client;
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = await client.ReceiveAsync(ct);
                    if (frame is null)
                    {
                        break;
                    }

                    if (!Saves.Handle(frame.Value))
                    {
                        lock (Other)
                        {
                            Other.Add(frame.Value);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // closed
            }
        });
    }

    /// <summary>
    /// Waits until this authority has read the server's final answer for its checkpoint. The session is <c>Running</c> as soon as the server stored
    /// the manifest, which can be before the authority's receive loop has handled that <c>SaveStored</c> (<c>LastResult</c>, <c>CheckpointsStored</c>).
    /// </summary>
    public Task WaitForCheckpointStoredAsync(int count = 1, int timeoutMs = 30_000) =>
        SaveServer.WaitUntilAsync(() => Saves.CheckpointsStored >= count && Saves.LastResult is not null, timeoutMs, "the authority has seen the upload result");

    /// <summary>"Loads" and reports <c>NodeReady</c> so the server asks for the first checkpoint.</summary>
    public Task ReportReadyAsync() => Saves.ReportReadyAsync(CancellationToken.None);

    /// <summary>Reports <paramref name="count"/> new stations (persistent entities, so the server journals them).</summary>
    public async Task SpawnStationsAsync(int count)
    {
        var records = new List<EntityRecordT>();
        for (int i = 0; i < count; i++)
        {
            var template = Authority.World.Galaxy.Entities.First(e => e.IsStation);
            records.Add(new EntityRecordT
            {
                NetId = Authority.NetIds.Allocate(),
                Kind = EntityKind.Station,
                Origin = EntityOrigin.AuthorityRuntime,
                MacroRef = Authority.Strings.Index(template.Macro),
                OwnerRef = Authority.Strings.Index(Authority.World.Galaxy.Factions[template.Faction]),
                Name = "New station " + i,
                Idcode = "NEW-" + i,
                Hull = 255,
                Shield = 255,
            });
        }

        var spawn = new EntitySpawnT { Entities = records };
        await Client.SendPayloadAsync(MsgType.EntitySpawn, MessageEncoder.EncodePayload(b => EntitySpawn.Pack(b, spawn), 1024));
    }

    /// <summary>The socket dies (no Disconnect): the server keeps the slot for the resume grace.</summary>
    public async Task DropAsync()
    {
        await _cts.CancelAsync();
        Client.Abort();
        await _loop;
    }

    /// <summary>Reconnects with the resume token and restarts the receive loop (after <see cref="DropAsync"/>).</summary>
    public async Task ResumeAsync(SaveServer server)
    {
        var token = Client.ResumeToken;
        Client = await server.ConnectAsync(_name, Role.Authority, token);
        Saves.Attach(Client);
        StartLoop();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Client.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // already gone
        }

        await _loop;
        _cts.Dispose();
    }
}
