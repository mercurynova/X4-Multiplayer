using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Saves;

/// <summary>A fake client (join pipeline of <see cref="FakeSaveClient"/>) connected to a <see cref="SaveServer"/>.</summary>
public sealed class ClientRig : IAsyncDisposable
{
    private readonly string _name;
    private readonly SaveServer _server;
    private CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    private ClientRig(SaveServer server, string name, TcpNodeClient client, FakeSaveClient saves)
    {
        _server = server;
        _name = name;
        Client = client;
        Saves = saves;
    }

    public TcpNodeClient Client { get; private set; }

    public FakeSaveClient Saves { get; }


    /// <summary>The <c>Disconnect</c> code the server sent (None when it did not).</summary>
    public DisconnectCode DisconnectCode { get; private set; }

    public static async Task<ClientRig> StartAsync(
        SaveServer server, string name, FakeSaveClientOptions? options = null, ulong caps = 0, Action<string>? log = null)
    {
        var client = await server.ConnectAsync(name, Role.Client, caps: caps);
        var saves = new FakeSaveClient(client, options ?? new FakeSaveClientOptions { Directory = server.ClientDir(name) }, log);
        var rig = new ClientRig(server, name, client, saves);
        rig.StartLoop();
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

                    if (frame.Value.Type == MsgType.Disconnect)
                    {
                        DisconnectCode = MessageRegistry.Default.Decode<Disconnect>(frame.Value).Code;
                        break;
                    }

                    await Saves.HandleAsync(frame.Value, ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // closed
            }
        });
    }

    /// <summary>The socket dies (no Disconnect); the server keeps the slot for the resume grace.</summary>
    public async Task DropAsync()
    {
        await _cts.CancelAsync();
        Client.Abort();
        await _loop;
    }

    /// <summary>Reconnects with the resume token; the server resends <c>SessionSaveInfo</c> and the download continues from the part file.</summary>
    public async Task ResumeAsync(ulong lastJournalSeq = 0)
    {
        var token = Client.ResumeToken;
        Client = await _server.ConnectAsync(_name, Role.Client, token, lastJournalSeq);
        Saves.Attach(Client);
        StartLoop();
    }

    /// <summary>
    /// An orderly quit: sends <c>Disconnect(ClientQuit)</c> and waits for the server to close, so the frame is not lost to a reset (closing a
    /// socket with unread data resets it).
    /// </summary>
    public async Task LeaveAsync()
    {
        var quit = new DisconnectT { Code = DisconnectCode.ClientQuit, Message = string.Empty, Expected = string.Empty };
        await Client.SendAsync(MsgType.Disconnect, b => Disconnect.Pack(b, quit));
        await _loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async Task WaitReadyAsync(int timeoutMs = 60000) => await Saves.Ready.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

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
        Saves.Dispose();
        _cts.Dispose();
    }
}
