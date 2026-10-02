using System.Security.Cryptography;
using System.Text;
using Google.FlatBuffers;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests.Net;

/// <summary>A scripted mod: builds <c>ClientHello</c>s and runs the join exchange.</summary>
public sealed class TestNode(string name, byte[]? key = null)
{
    public string Name { get; set; } = name;

    public byte[] Key { get; } = key ?? RandomNumberGenerator.GetBytes(32);

    public byte[] KeyHash => SHA256.HashData(Key);

    public ushort ProtocolMajor { get; set; } = ProtocolConstants.ProtocolMajor;

    public ushort ProtocolMinor { get; set; } = ProtocolConstants.ProtocolMinor;

    public string ModVersion { get; set; } = "0.1.0";

    public string ModBuild { get; set; } = "abc1234";

    public string GameBuild { get; set; } = "900-611726";

    public string[] Extensions { get; set; } = ["ego_dlc_boron@1.0", "x4mp@0.1.0"];

    public Role Roles { get; set; } = Role.Client;

    public ulong Caps { get; set; } = (ulong)(Capability.GhostRender | Capability.Economy | Capability.UdpRealtime);

    public string? JoinPassword { get; set; }

    public string? AdminPassword { get; set; }

    /// <summary>SHA-256 over the sorted extension lines, as the mod computes it.</summary>
    public byte[] ExtensionsHash => ExtensionsHashOf(Extensions);

    public static byte[] ExtensionsHashOf(IEnumerable<string> extensions) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(extensions.Order(StringComparer.Ordinal).Select(e => e + "\n"))));

    public ClientHelloT BuildHello(ReadOnlySpan<byte> nonce)
    {
        var hello = new ClientHelloT
        {
            ProtocolMajor = ProtocolMajor,
            ProtocolMinor = ProtocolMinor,
            ModVersion = ModVersion,
            ModBuild = ModBuild,
            GameVersion = "9.00",
            GameBuild = GameBuild,
            X4nativeVersion = "v9.0.0-611726",
            Platform = "win64",
            ExtensionsHash = [.. ExtensionsHash],
            Extensions = [.. Extensions],
            PlayerKey = [.. Key],
            PlayerName = Name,
            RequestedRoles = Roles | (AdminPassword is null ? 0 : Role.Admin),
            ClientCaps = Caps,
            AuthProof = JoinPassword is null ? [] : [.. GatewayState.ComputeProof(GatewayState.HashPassword(JoinPassword)!, nonce, Key)],
            AdminProof = AdminPassword is null ? [] : [.. GatewayState.ComputeProof(GatewayState.HashPassword(AdminPassword)!, nonce, Key)],
        };
        return hello;
    }

    public static FlatBufferBuilder Pack(ClientHelloT hello)
    {
        var fbb = new FlatBufferBuilder(512);
        fbb.Finish(ClientHello.Pack(fbb, hello).Value);
        return fbb;
    }

    /// <summary>Reads <c>ServerHello</c>, answers with this node's <c>ClientHello</c>, returns both replies.</summary>
    public async Task<(ServerHelloT Server, Frame? Reply)> JoinAsync(ClientHandle client, Action<ClientHelloT>? tweak = null)
    {
        var server = (await client.ReadAsync<ServerHello>(MsgType.ServerHello)).UnPack();
        var hello = BuildHello(server.Nonce.ToArray());
        tweak?.Invoke(hello);
        await client.SendAsync(MsgType.ClientHello, Pack(hello));
        return (server, await client.ReadAsync());
    }

    public static Disconnect AsDisconnect(Frame? frame)
    {
        Assert.NotNull(frame);
        Assert.Equal(MsgType.Disconnect, frame.Value.Type);
        return MessageRegistry.Default.Decode<Disconnect>(frame.Value);
    }

    public static Welcome AsWelcome(Frame? frame)
    {
        Assert.NotNull(frame);
        if (frame.Value.Type == MsgType.Disconnect)
        {
            var d = MessageRegistry.Default.Decode<Disconnect>(frame.Value);
            Assert.Fail($"expected Welcome, got Disconnect {d.Code}: {d.Message}");
        }

        Assert.Equal(MsgType.Welcome, frame.Value.Type);
        return MessageRegistry.Default.Decode<Welcome>(frame.Value);
    }
}

/// <summary>
/// What the SessionActor will do at admission (M1-05): the first authority defines the identity later nodes
/// must match.
/// </summary>
public sealed class AuthorityRecordingHandler(GatewayState state) : IAdmissionHandler
{
    public List<AdmittedNode> Admitted { get; } = [];

    public ValueTask<AdmissionVerdict> BeforeWelcomeAsync(AdmittedNode node, CancellationToken ct) =>
        ValueTask.FromResult(AdmissionVerdict.Accept);

    public async Task OnAdmittedAsync(AdmittedNode node, CancellationToken ct)
    {
        lock (Admitted)
        {
            Admitted.Add(node);
        }

        if (node.IsAuthority)
        {
            var h = node.Hello;
            state.Authority = new AuthorityIdentity(h.GameBuild, h.ModVersion, h.ModBuild, h.ExtensionsHash.ToArray(), h.Extensions, h.ExtensionList);
            state.AuthorityLive = true;
            state.DesignatedAuthorityPlayerId = node.PlayerId;
            _ = node.Connection.Completion.ContinueWith(_ => state.AuthorityLive = false, TaskScheduler.Default);
        }

        await new DefaultAdmissionHandler().OnAdmittedAsync(node, ct);
    }
}
