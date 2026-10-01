using System.Security.Cryptography;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// The real <see cref="TcpNodeClient"/> against the real server stack (NodeGateway + transports), over the
/// in-process transport and over real TCP (Kestrel on an ephemeral port).
/// </summary>
[Collection("net")]
public class ClientInteropTests
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private static NetOptions Fast() => new() { AuthFailureDelayMs = 0, HandshakeTimeoutSeconds = 2 };

    private static async Task<TcpNodeClient> JoinAsync(NetHarness net, NodeClientOptions options)
    {
        var handle = await net.ConnectAsync();
        try
        {
            return await TcpNodeClient.ConnectAsync(handle.Stream, options);
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }
    }

    private static NodeClientOptions Client(string name = "Alice") => new() { PlayerName = name };

    private static async Task<HandshakeRejectedException> RejectedAsync(NetHarness net, NodeClientOptions options) =>
        await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await JoinAsync(net, options)).DisposeAsync());

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, "condition not met in time");
            await Task.Delay(10);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ClientJoinsAndSeesWelcome(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast() with { ServerName = "Interop" }, withGateway: true);
        await using var client = await JoinAsync(net, Client());

        Assert.Equal("Interop", client.ServerHello.ServerName);
        Assert.Equal(1, client.Welcome.PlayerId);
        Assert.Equal(Role.Client, client.Welcome.GrantedRoles);
        Assert.False(client.Welcome.Resumed);
        Assert.NotNull(client.ResumeToken);
        Assert.Single(net.Gateway!.AdmittedNodes);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AuthorityJoinsThenClientMatchingItJoins(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast(), withGateway: true, handler: s => new AuthorityRecordingHandler(s));
        await using var authority = await JoinAsync(net, new NodeClientOptions { PlayerName = "Boss", RequestedRoles = Role.Authority });
        Assert.Equal(Role.Authority, authority.Welcome.GrantedRoles);
        await WaitUntilAsync(() => net.State!.AuthorityLive);

        await using var client = await JoinAsync(net, Client("Bob"));
        Assert.Equal(Role.Client, client.Welcome.GrantedRoles);
        Assert.NotEqual(authority.Welcome.PlayerId, client.Welcome.PlayerId);

        // A second authority is refused while one is live.
        var second = await RejectedAsync(net, new NodeClientOptions { PlayerName = "Usurper", RequestedRoles = Role.Authority });
        Assert.Equal(DisconnectCode.RoleUnavailable, second.Code);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task WrongPasswordIsAuthFailedAndCorrectOneJoins(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast() with { JoinPassword = "s3cret" }, withGateway: true);

        Assert.Equal(DisconnectCode.AuthFailed, (await RejectedAsync(net, new NodeClientOptions { PlayerName = "Alice", Password = "wrong" })).Code);
        Assert.Equal(DisconnectCode.AuthFailed, (await RejectedAsync(net, new NodeClientOptions { PlayerName = "Alice" })).Code);

        await using var ok = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", Password = "s3cret" });
        Assert.Equal(AuthMethod.SessionPassword, ok.ServerHello.Auth);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AdminProofGrantsAdminRole(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast() with { AdminPassword = "root" }, withGateway: true);
        await using var admin = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", AdminPassword = "root", RequestedRoles = Role.Client | Role.Admin });
        Assert.True((admin.Welcome.GrantedRoles & Role.Admin) != 0);
        Assert.Equal(DisconnectCode.AuthFailed,
            (await RejectedAsync(net, new NodeClientOptions { PlayerName = "Mallory", AdminPassword = "nope", RequestedRoles = Role.Client | Role.Admin })).Code);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task UnsupportedGameBuildIsRejectedWithTheSupportedList(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast(), withGateway: true);
        var ex = await RejectedAsync(net, new NodeClientOptions { PlayerName = "Alice", GameBuild = "800-1" });
        Assert.Equal(DisconnectCode.GameVersionMismatch, ex.Code);
        Assert.Equal("900-611726", ex.Expected);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ModAndGameBuildMustMatchTheAuthority(string kind)
    {
        var options = Fast() with { SupportedGameBuilds = ["900-611726", "900-1"] };
        await using var net = await NetHarness.CreateAsync(kind, options, withGateway: true, handler: s => new AuthorityRecordingHandler(s));
        await using var authority = await JoinAsync(net, new NodeClientOptions { PlayerName = "Boss", RequestedRoles = Role.Authority, ModBuild = "build-a" });
        await WaitUntilAsync(() => net.State!.Authority is not null);

        var modBuild = await RejectedAsync(net, new NodeClientOptions { PlayerName = "Alice", ModBuild = "build-b" });
        Assert.Equal(DisconnectCode.ModVersionMismatch, modBuild.Code);
        Assert.Equal("build-a", modBuild.Expected);

        var modVersion = await RejectedAsync(net, new NodeClientOptions { PlayerName = "Alice", ModBuild = "build-a", ModVersion = "9.9.9" });
        Assert.Equal(DisconnectCode.ModVersionMismatch, modVersion.Code);
        Assert.Equal("0.1.0", modVersion.Expected);

        var game = await RejectedAsync(net, new NodeClientOptions { PlayerName = "Alice", ModBuild = "build-a", GameBuild = "900-1" });
        Assert.Equal(DisconnectCode.GameVersionMismatch, game.Code);
        Assert.Equal("900-611726", game.Expected);

        await using var good = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", ModBuild = "build-a" });
        Assert.Equal(Role.Client, good.Welcome.GrantedRoles);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task NameBoundToAnotherKeyIsRejectedButSameKeyRejoins(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast(), withGateway: true);
        var key = RandomNumberGenerator.GetBytes(32);
        await using (var first = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key }))
        {
            Assert.Equal(DisconnectCode.NameTaken, (await RejectedAsync(net, Client("Alice"))).Code);
        }

        await WaitUntilAsync(() => net.Gateway!.AdmittedNodes.Count == 0);
        await using var again = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key });
        Assert.Equal(1, again.Welcome.PlayerId);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task BannedKeyIsRejected(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast(), withGateway: true);
        var key = RandomNumberGenerator.GetBytes(32);
        net.Store!.BanKey(SHA256.HashData(key), "griefing");
        var ex = await RejectedAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key });
        Assert.Equal(DisconnectCode.Banned, ex.Code);
        Assert.Equal("griefing", ex.ServerMessage);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ServerTimesOutAHandshakeThatNeverAnswers(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast() with { HandshakeTimeoutSeconds = 1 }, withGateway: true);
        await using var handle = await net.ConnectAsync();
        var swallowing = new WriteSwallowingStream(handle.Stream);
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(() =>
            TcpNodeClient.ConnectAsync(swallowing, new NodeClientOptions { PlayerName = "Alice", HandshakeTimeout = TimeSpan.FromSeconds(8) }));
        Assert.Equal(DisconnectCode.HandshakeTimeout, ex.Code);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task PingGetsPongWithAnRtt(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast(), withGateway: true);
        await using var client = await JoinAsync(net, Client());
        for (int i = 0; i < 3; i++)
        {
            var rtt = await client.PingAsync(new CancellationTokenSource(5000).Token);
            Assert.InRange(rtt, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task GracefulDisconnectReleasesTheNode(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast(), withGateway: true);
        var client = await JoinAsync(net, Client());
        Assert.Single(net.Gateway!.AdmittedNodes);
        await client.DisposeAsync();
        await WaitUntilAsync(() => net.Gateway.AdmittedNodes.Count == 0 && net.Gateway.LiveConnectionCount == 0);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task SameKeyReconnectSupersedesTheOldConnection(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, Fast(), withGateway: true);
        var key = RandomNumberGenerator.GetBytes(32);
        await using var first = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key });
        await using var second = await JoinAsync(net, first.ForResume());
        Assert.Equal(first.Welcome.PlayerId, second.Welcome.PlayerId);

        using var cts = new CancellationTokenSource(5000);
        var frame = await first.ReceiveAsync(cts.Token);
        Assert.Equal(MsgType.Disconnect, frame!.Value.Type);
        Assert.Equal(DisconnectCode.SupersededByNewConnection, MessageRegistry.Default.Decode<Disconnect>(frame.Value).Code);
    }

    [Fact(Skip = "Resume tokens are issued but the gateway never sets Welcome.resumed; resume lands with the SessionActor (M1-05).")]
    public void ResumeTokenReconnectRestoresTheSession()
    {
    }

    [Fact]
    public async Task TcpConnectAsyncByHostAndPortWorks()
    {
        await using var net = (TcpHarness)await NetHarness.CreateAsync("tcp", Fast(), withGateway: true);
        await using var client = await TcpNodeClient.ConnectAsync("127.0.0.1", net.Port, Client());
        Assert.Equal(1, client.Welcome.PlayerId);
        Assert.True(await client.PingAsync(new CancellationTokenSource(5000).Token) < TimeSpan.FromSeconds(2));
    }

    /// <summary>Reads pass through, writes vanish: a peer that never sends its ClientHello.</summary>
    private sealed class WriteSwallowingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
