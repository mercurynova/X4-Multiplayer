using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests.Net;

[Collection("net")]
public class NodeGatewayTests
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private static NetOptions Fast() => new() { AuthFailureDelayMs = 0 };

    // ---- success ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task SuccessfulJoinGetsServerHelloThenWelcome(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, new NetOptions { ServerName = "Test Server" }, withGateway: true);
        await using var client = await net.ConnectAsync();
        var alice = new TestNode("Alice");

        var (server, reply) = await alice.JoinAsync(client);
        var welcome = TestNode.AsWelcome(reply);

        Assert.Equal(ProtocolConstants.ProtocolMajor, server.ProtocolMajor);
        Assert.Equal(ProtocolConstants.ProtocolMinor, server.ProtocolMinor);
        Assert.Equal("Test Server", server.ServerName);
        Assert.Equal(32, server.Nonce.Count);
        Assert.Equal(AuthMethod.None, server.Auth);
        Assert.Equal(["900-611726"], server.SupportedGameBuilds);
        Assert.Equal(SessionPhase.Idle, server.Phase);
        Assert.Empty(server.RequiredGameBuild);

        Assert.Equal(1, welcome.PlayerId);
        Assert.Equal(Role.Client, welcome.GrantedRoles);
        Assert.Equal((ulong)(Capability.GhostRender | Capability.Economy), welcome.NegotiatedCaps); // client & server, UDP not offered
        Assert.NotEqual(0ul, welcome.ResumeToken!.Value.Lo | welcome.ResumeToken.Value.Hi);
        Assert.False(welcome.Resumed);
        Assert.Equal((uint)net.Gateway!.AdmittedNodes[0].Connection.Id.Value, welcome.ConnId);
        Assert.Equal(60, welcome.ResumeGraceS);
        Assert.Equal(0, welcome.UdpPort);
        Assert.Single(net.Gateway.AdmittedNodes);
    }

    [Fact]
    public async Task NoncesAreFreshPerConnection()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        var nonces = new HashSet<string>();
        for (int i = 0; i < 5; i++)
        {
            await using var client = await net.ConnectAsync(IPAddress.Parse("10.0.0." + (i + 1)));
            var server = await client.ReadAsync<ServerHello>(MsgType.ServerHello);
            nonces.Add(Convert.ToHexString(server.UnPack().Nonce.ToArray()));
        }

        Assert.Equal(5, nonces.Count);
    }

    [Fact]
    public async Task AdmittedNodeReadsPingAndAnswersPongThroughTheGuardedReader()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice").JoinAsync(client);
        TestNode.AsWelcome(reply);
        await client.SendAsync(MsgType.Ping, TestFrames.Ping(41));
        var pong = await client.ReadAsync<Pong>(MsgType.Pong);
        Assert.Equal(41u, pong.Seq);
    }

    // ---- auth ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task CorrectJoinPasswordIsAcceptedAndNeverSentOnTheWire(string kind)
    {
        var options = new NetOptions { JoinPassword = "s3cret" };
        await using var net = await NetHarness.CreateAsync(kind, options, withGateway: true);
        await using var client = await net.ConnectAsync();
        var node = new TestNode("Alice") { JoinPassword = "s3cret" };
        var (server, reply) = await node.JoinAsync(client);
        Assert.Equal(AuthMethod.SessionPassword, server.Auth);
        TestNode.AsWelcome(reply);

        var hello = TestNode.Pack(node.BuildHello(server.Nonce.ToArray())).DataBuffer.ToSizedArray();
        Assert.False(hello.AsSpan().IndexOf("s3cret"u8) >= 0);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task WrongPasswordIsRejectedAfterOneSecondDelay(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, new NetOptions { JoinPassword = "s3cret" }, withGateway: true);
        await using var client = await net.ConnectAsync();
        var node = new TestNode("Alice") { JoinPassword = "wrong" };

        var sw = Stopwatch.StartNew();
        var (_, reply) = await node.JoinAsync(client);
        sw.Stop();

        Assert.Equal(DisconnectCode.AuthFailed, TestNode.AsDisconnect(reply).Code);
        Assert.True(sw.ElapsedMilliseconds >= 950, $"rejected after only {sw.ElapsedMilliseconds} ms");
        Assert.Null(await client.ReadAsync());
        Assert.Empty(net.Gateway!.AdmittedNodes);
    }

    [Fact]
    public async Task MissingProofIsRejectedWhenPasswordIsRequired()
    {
        await using var net = await NetHarness.CreateAsync("inproc", Fast() with { JoinPassword = "pw" }, withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice").JoinAsync(client); // no password supplied
        Assert.Equal(DisconnectCode.AuthFailed, TestNode.AsDisconnect(reply).Code);
    }

    [Fact]
    public async Task ProofFromAnotherConnectionsNonceIsRejectedReplayIsUseless()
    {
        await using var net = await NetHarness.CreateAsync("inproc", Fast() with { JoinPassword = "pw" }, withGateway: true);
        var node = new TestNode("Alice") { JoinPassword = "pw" };
        await using var first = await net.ConnectAsync(IPAddress.Parse("10.0.0.1"));
        var s1 = (await first.ReadAsync<ServerHello>(MsgType.ServerHello)).UnPack();
        var captured = node.BuildHello(s1.Nonce.ToArray());

        await using var second = await net.ConnectAsync(IPAddress.Parse("10.0.0.2"));
        await second.ReadAsync<ServerHello>(MsgType.ServerHello);
        await second.SendAsync(MsgType.ClientHello, TestNode.Pack(captured)); // proof bound to the first nonce
        Assert.Equal(DisconnectCode.AuthFailed, TestNode.AsDisconnect(await second.ReadAsync()).Code);
    }

    [Fact]
    public async Task AdminProofGrantsAdminAndWrongAdminProofFails()
    {
        await using var net = await NetHarness.CreateAsync("inproc", Fast() with { AdminPassword = "root" }, withGateway: true);
        await using var good = await net.ConnectAsync(IPAddress.Parse("10.0.0.1"));
        var (_, reply) = await new TestNode("Admin1") { AdminPassword = "root" }.JoinAsync(good);
        Assert.Equal(Role.Client | Role.Admin, TestNode.AsWelcome(reply).GrantedRoles);
        Assert.True(net.Gateway!.AdmittedNodes[0].IsAdmin);

        await using var bad = await net.ConnectAsync(IPAddress.Parse("10.0.0.2"));
        var (_, reply2) = await new TestNode("Admin2") { AdminPassword = "nope" }.JoinAsync(bad);
        Assert.Equal(DisconnectCode.AuthFailed, TestNode.AsDisconnect(reply2).Code);

        // no admin password configured at all: nobody can claim Admin
        await using var net2 = await NetHarness.CreateAsync("inproc", Fast(), withGateway: true);
        await using var c3 = await net2.ConnectAsync();
        var (_, reply3) = await new TestNode("Admin3") { AdminPassword = "root" }.JoinAsync(c3);
        Assert.Equal(DisconnectCode.AuthFailed, TestNode.AsDisconnect(reply3).Code);
    }

    [Fact]
    public async Task SixthFailedAuthWithinAMinuteIsRateLimited()
    {
        await using var net = await NetHarness.CreateAsync("inproc", Fast() with { JoinPassword = "pw", MaxConnectionsPerIp = 50 }, withGateway: true);
        var codes = new List<DisconnectCode>();
        for (int i = 0; i < 6; i++)
        {
            await using var client = await net.ConnectAsync(IPAddress.Parse("10.1.1.1"));
            var (_, reply) = await new TestNode("Bob" + i) { JoinPassword = "bad" }.JoinAsync(client);
            codes.Add(TestNode.AsDisconnect(reply).Code);
        }

        Assert.Equal([.. Enumerable.Repeat(DisconnectCode.AuthFailed, 5), DisconnectCode.RateLimited], codes);

        // another address is unaffected
        await using var other = await net.ConnectAsync(IPAddress.Parse("10.2.2.2"));
        var (_, ok) = await new TestNode("Carol") { JoinPassword = "pw" }.JoinAsync(other);
        TestNode.AsWelcome(ok);
    }

    // ---- compatibility ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ProtocolMajorMismatchIsRejected(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice") { ProtocolMajor = 7 }.JoinAsync(client);
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.ProtocolMismatch, d.Code);
        Assert.Equal("0.1", d.Expected);
    }

    [Fact]
    public async Task HigherClientMinorIsAcceptedAndNegotiatedDown()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice") { ProtocolMinor = 9 }.JoinAsync(client);
        TestNode.AsWelcome(reply);
        Assert.Equal(ProtocolConstants.ProtocolMinor, net.Gateway!.AdmittedNodes[0].NegotiatedMinor);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task UnsupportedGameBuildIsRejectedEvenWithoutAnAuthority(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice") { GameBuild = "900-123456" }.JoinAsync(client);
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.GameVersionMismatch, d.Code);
        Assert.Equal("900-611726", d.Expected);
    }

    /// <summary>A gateway whose first admitted node is the session authority (as the SessionActor will arrange).</summary>
    private static async Task<(NetHarness Net, ClientHandle Authority)> WithAuthorityAsync(
        NetOptions? options = null, Action<TestNode>? configure = null, string kind = "inproc")
    {
        var net = await NetHarness.CreateAsync(kind, options, withGateway: true, handler: state => new AuthorityRecordingHandler(state));
        var client = await net.ConnectAsync(IPAddress.Parse("10.9.9.9"));
        var node = new TestNode("HostPlayer") { Roles = Role.Authority | Role.Client };
        configure?.Invoke(node);
        var (_, reply) = await node.JoinAsync(client);
        TestNode.AsWelcome(reply);
        await WaitForAsync(() => net.State!.Authority is not null);
        return (net, client);
    }

    [Fact]
    public async Task GameBuildMustEqualTheAuthoritysEvenWhenSupported()
    {
        var options = new NetOptions { SupportedGameBuilds = ["900-611726", "900-700000"] };
        var (net, authority) = await WithAuthorityAsync(options);
        await using var _ = net;
        await using var __ = authority;

        await using var client = await net.ConnectAsync(IPAddress.Parse("10.0.0.5"));
        var (server, reply) = await new TestNode("Alice") { GameBuild = "900-700000" }.JoinAsync(client);
        Assert.Equal("900-611726", server.RequiredGameBuild); // ServerHello announces the authority's build
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.GameVersionMismatch, d.Code);
        Assert.Equal("900-611726", d.Expected);
    }

    [Fact]
    public async Task ModBuildAndVersionMustEqualTheAuthoritys()
    {
        var (net, authority) = await WithAuthorityAsync();
        await using var _ = net;
        await using var __ = authority;

        await using var c1 = await net.ConnectAsync(IPAddress.Parse("10.0.0.5"));
        var (s1, r1) = await new TestNode("Alice") { ModBuild = "deadbee" }.JoinAsync(c1);
        Assert.Equal("0.1.0", s1.RequiredModVersion);
        var d1 = TestNode.AsDisconnect(r1);
        Assert.Equal(DisconnectCode.ModVersionMismatch, d1.Code);
        Assert.Equal("abc1234", d1.Expected);

        await using var c2 = await net.ConnectAsync(IPAddress.Parse("10.0.0.6"));
        var (_, r2) = await new TestNode("Bob") { ModVersion = "0.2.0" }.JoinAsync(c2);
        var d2 = TestNode.AsDisconnect(r2);
        Assert.Equal(DisconnectCode.ModVersionMismatch, d2.Code);
        Assert.Equal("0.1.0", d2.Expected);

        await using var c3 = await net.ConnectAsync(IPAddress.Parse("10.0.0.7"));
        var (_, r3) = await new TestNode("Carol").JoinAsync(c3); // identical build joins
        TestNode.AsWelcome(r3);
    }

    [Fact]
    public async Task PinnedRequiredModVersionAppliesBeforeAnAuthorityExists()
    {
        await using var net = await NetHarness.CreateAsync("inproc", new NetOptions { RequiredModVersion = "1.2.3" }, withGateway: true);
        await using var client = await net.ConnectAsync();
        var (server, reply) = await new TestNode("Alice").JoinAsync(client);
        Assert.Equal("1.2.3", server.RequiredModVersion);
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.ModVersionMismatch, d.Code);
        Assert.Equal("1.2.3", d.Expected);
    }

    [Fact]
    public async Task ExtensionsMustMatchTheAuthoritysAndTheDiffIsReported()
    {
        var (net, authority) = await WithAuthorityAsync();
        await using var _ = net;
        await using var __ = authority;

        await using var client = await net.ConnectAsync(IPAddress.Parse("10.0.0.5"));
        var (server, reply) = await new TestNode("Alice") { Extensions = ["ego_dlc_boron@1.0", "other_mod@3"] }.JoinAsync(client);
        Assert.Equal(32, server.ExtensionsHash.Count);
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.ExtensionsMismatch, d.Code);
        Assert.Contains("disable: other_mod@3", d.Expected);
        Assert.DoesNotContain("x4mp", d.Expected, StringComparison.Ordinal); // never the mod itself; no raw missing/extra diff
        Assert.DoesNotContain("missing:", d.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionsMismatchCanBeDowngradedToAWarning()
    {
        var (net, authority) = await WithAuthorityAsync(new NetOptions { ExtensionsMismatchIsWarning = true });
        await using var _ = net;
        await using var __ = authority;
        await using var client = await net.ConnectAsync(IPAddress.Parse("10.0.0.5"));
        var (_, reply) = await new TestNode("Alice") { Extensions = ["ego_dlc_boron@1.0", "something_else@1"] }.JoinAsync(client);
        TestNode.AsWelcome(reply);
    }

    [Fact]
    public async Task ADlcMismatchIsRefusedEvenWhenDowngradedToAWarning()
    {
        var (net, authority) = await WithAuthorityAsync(new NetOptions { ExtensionsMismatchIsWarning = true });
        await using var _ = net;
        await using var __ = authority;
        await using var client = await net.ConnectAsync(IPAddress.Parse("10.0.0.5"));
        var (_, reply) = await new TestNode("Alice") { Extensions = ["something_else@1"] }.JoinAsync(client);
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.ExtensionsMismatch, d.Code);
        Assert.Equal("ego_dlc_boron", d.ModViolation!.Value.Install(0)!.Value.Id);
    }

    // ---- identity ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task NameStaysBoundToTheFirstKey(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, withGateway: true);
        await using var a = await net.ConnectAsync();
        var (_, r1) = await new TestNode("Alice").JoinAsync(a);
        TestNode.AsWelcome(r1);

        await using var b = await net.ConnectAsync();
        var (_, r2) = await new TestNode("alice").JoinAsync(b); // different key, same name (case-insensitive)
        Assert.Equal(DisconnectCode.NameTaken, TestNode.AsDisconnect(r2).Code);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("this name is far too long to be valid")]
    [InlineData("bad<name>")]
    [InlineData(" padded ")]
    public async Task InvalidNamesAreRejected(string name)
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode(name).JoinAsync(client);
        Assert.Equal(DisconnectCode.NameTaken, TestNode.AsDisconnect(reply).Code);
    }

    [Fact]
    public async Task SameKeyConnectingAgainSupersedesTheFirstConnectionAndKeepsThePlayerId()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        var key = RandomNumberGenerator.GetBytes(32);
        await using var first = await net.ConnectAsync();
        var (_, r1) = await new TestNode("Alice", key).JoinAsync(first);
        ushort id = TestNode.AsWelcome(r1).PlayerId;

        await using var second = await net.ConnectAsync();
        var (_, r2) = await new TestNode("Alice", key).JoinAsync(second);
        Assert.Equal(id, TestNode.AsWelcome(r2).PlayerId);

        var d = await first.ReadAsync<Disconnect>(MsgType.Disconnect);
        Assert.Equal(DisconnectCode.SupersededByNewConnection, d.Code);
        Assert.Single(net.Gateway!.AdmittedNodes);
    }

    [Fact]
    public async Task KeyMayChangeItsNameWhenTheNewNameIsFree()
    {
        var store = new InMemoryNodeStore();
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true, store: store);
        var key = RandomNumberGenerator.GetBytes(32);
        await using var a = await net.ConnectAsync();
        var (_, r1) = await new TestNode("Alice", key).JoinAsync(a);
        ushort id = TestNode.AsWelcome(r1).PlayerId;
        await using var b = await net.ConnectAsync();
        var (_, r2) = await new TestNode("Alicia", key).JoinAsync(b);
        Assert.Equal(id, TestNode.AsWelcome(r2).PlayerId);
    }

    // ---- bans ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task BannedKeyIsRefused(string kind)
    {
        var store = new InMemoryNodeStore();
        var node = new TestNode("Mallory");
        store.BanKey(node.KeyHash, "griefing");
        await using var net = await NetHarness.CreateAsync(kind, withGateway: true, store: store);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await node.JoinAsync(client);
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.Banned, d.Code);
        Assert.Equal("griefing", d.Message);
        Assert.Empty(net.Gateway!.AdmittedNodes);
    }

    [Fact]
    public async Task BannedCidrIsRefusedBeforeTheHandshakeStarts()
    {
        var store = new InMemoryNodeStore();
        store.BanNetwork("10.9.0.0/16", "bad network");
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true, store: store);

        await using var banned = await net.ConnectAsync(IPAddress.Parse("10.9.8.7"));
        var d = TestNode.AsDisconnect(await banned.ReadAsync()); // no ServerHello first
        Assert.Equal(DisconnectCode.Banned, d.Code);

        await using var fine = await net.ConnectAsync(IPAddress.Parse("10.10.8.7"));
        var (_, reply) = await new TestNode("Alice").JoinAsync(fine);
        TestNode.AsWelcome(reply);
    }

    [Fact]
    public async Task Ipv4MappedAddressesMatchIpv4Bans()
    {
        var store = new InMemoryNodeStore();
        store.BanNetwork("192.168.5.0/24");
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true, store: store);
        await using var client = await net.ConnectAsync(IPAddress.Parse("192.168.5.20").MapToIPv6());
        Assert.Equal(DisconnectCode.Banned, TestNode.AsDisconnect(await client.ReadAsync()).Code);
    }

    // ---- timing and limits ----

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task SilentConnectionIsClosedAfterTheHandshakeTimeout(string kind)
    {
        var time = new FakeTimeProvider();
        await using var net = await NetHarness.CreateAsync(kind, withGateway: true, time: time);
        await using var client = await net.ConnectAsync();
        await client.ReadAsync<ServerHello>(MsgType.ServerHello);

        time.Advance(TimeSpan.FromSeconds(9.5));
        await Task.Delay(100);
        var pending = client.ReadAsync(500);
        Assert.False((await Task.WhenAny(pending, Task.Delay(300))) == pending, "connection must stay open before 10 s");

        time.Advance(TimeSpan.FromSeconds(1.5)); // 11 s of silence
        var d = TestNode.AsDisconnect(await pending);
        Assert.Equal(DisconnectCode.HandshakeTimeout, d.Code);
        Assert.Null(await client.ReadAsync());
        await WaitForAsync(() => net.Gateway!.LiveConnectionCount == 0);
        Assert.Equal(0, net.Gateway!.LiveConnectionCount);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task FifthConnectionFromOneAddressIsRefused(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, new NetOptions { HandshakesPerSecond = 1000 }, withGateway: true);
        var clients = new List<ClientHandle>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var c = await net.ConnectAsync();
                clients.Add(c);
                await c.ReadAsync<ServerHello>(MsgType.ServerHello); // gateway registered it
            }

            await using var fifth = await net.ConnectAsync();
            var d = TestNode.AsDisconnect(await fifth.ReadAsync());
            Assert.Equal(DisconnectCode.SessionFull, d.Code);
            Assert.Contains("connections", d.Message);

            // freeing a slot lets the next one in
            await clients[0].DisposeAsync();
            await WaitForAsync(() => net.Gateway!.LiveConnectionCount == 3);
            await using var sixth = await net.ConnectAsync();
            await sixth.ReadAsync<ServerHello>(MsgType.ServerHello);
        }
        finally
        {
            foreach (var c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ConnectionLimitIsPerAddress()
    {
        await using var net = await NetHarness.CreateAsync("inproc", new NetOptions { MaxConnectionsPerIp = 1 }, withGateway: true);
        await using var a = await net.ConnectAsync(IPAddress.Parse("10.0.0.1"));
        await a.ReadAsync<ServerHello>(MsgType.ServerHello);
        await using var b = await net.ConnectAsync(IPAddress.Parse("10.0.0.2"));
        await b.ReadAsync<ServerHello>(MsgType.ServerHello);
        await using var c = await net.ConnectAsync(IPAddress.Parse("10.0.0.1"));
        Assert.Equal(DisconnectCode.SessionFull, TestNode.AsDisconnect(await c.ReadAsync()).Code);
    }

    [Fact]
    public async Task HandshakeRateIsLimitedGlobally()
    {
        var options = new NetOptions { HandshakesPerSecond = 3, MaxConnectionsPerIp = 100 };
        await using var net = await NetHarness.CreateAsync("inproc", options, withGateway: true, time: new FakeTimeProvider());
        int accepted = 0, limited = 0;
        for (int i = 0; i < 6; i++)
        {
            await using var c = await net.ConnectAsync(IPAddress.Parse("10.0.1." + (i + 1)));
            var first = await c.ReadAsync();
            if (first!.Value.Type == MsgType.ServerHello)
            {
                accepted++;
            }
            else if (TestNode.AsDisconnect(first).Code == DisconnectCode.RateLimited)
            {
                limited++;
            }
        }

        Assert.Equal(3, accepted);
        Assert.Equal(3, limited);
    }

    // ---- roles and capacity ----

    [Fact]
    public async Task OnlyOneLiveAuthorityAndCapacityIsEnforced()
    {
        var options = new NetOptions { MaxPlayers = 2 };
        var (net, authority) = await WithAuthorityAsync(options);
        await using var _ = net;
        await using var __ = authority;

        await using var second = await net.ConnectAsync(IPAddress.Parse("10.0.0.2"));
        var (_, r2) = await new TestNode("Rival") { Roles = Role.Authority | Role.Client }.JoinAsync(second);
        Assert.Equal(DisconnectCode.RoleUnavailable, TestNode.AsDisconnect(r2).Code);

        await using var third = await net.ConnectAsync(IPAddress.Parse("10.0.0.3"));
        var (_, r3) = await new TestNode("Player2").JoinAsync(third);
        TestNode.AsWelcome(r3); // 2 of 2

        await using var fourth = await net.ConnectAsync(IPAddress.Parse("10.0.0.4"));
        var (_, r4) = await new TestNode("Player3").JoinAsync(fourth);
        Assert.Equal(DisconnectCode.SessionFull, TestNode.AsDisconnect(r4).Code);

        // observers do not use a player slot
        await using var observer = await net.ConnectAsync(IPAddress.Parse("10.0.0.5"));
        var (_, r5) = await new TestNode("Watcher") { Roles = Role.Observer }.JoinAsync(observer);
        Assert.Equal(Role.Observer, TestNode.AsWelcome(r5).GrantedRoles);
    }

    [Fact]
    public async Task AuthorityRoleIsFreeOnceTheLiveAuthorityDisconnects()
    {
        var (net, authority) = await WithAuthorityAsync();
        await using var _ = net;
        var host = net.State!;
        Assert.True(host.AuthorityLive);
        await authority.DisposeAsync();
        await WaitForAsync(() => !host.AuthorityLive);

        await using var stranger = await net.ConnectAsync(IPAddress.Parse("10.0.0.2"));
        var (_, rs) = await new TestNode("Stranger") { Roles = Role.Authority | Role.Client }.JoinAsync(stranger);
        TestNode.AsWelcome(rs); // nobody holds the role any more
    }

    [Fact]
    public async Task ObserverOrNoRolesRequestIsMalformed()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Nobody") { Roles = 0 }.JoinAsync(client);
        Assert.Equal(DisconnectCode.MalformedMessage, TestNode.AsDisconnect(reply).Code);
    }

    // ---- malformed handshakes ----

    [Fact]
    public async Task FirstFrameMustBeClientHello()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        await client.ReadAsync<ServerHello>(MsgType.ServerHello);
        await client.SendAsync(MsgType.Ping, TestFrames.Ping());
        Assert.Equal(DisconnectCode.UnexpectedMessage, TestNode.AsDisconnect(await client.ReadAsync()).Code);
    }

    [Fact]
    public async Task GarbageClientHelloIsMalformed()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        await client.ReadAsync<ServerHello>(MsgType.ServerHello);
        await client.SendRawAsync(FrameCodec.Encode(MsgType.ClientHello, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
        Assert.Equal(DisconnectCode.MalformedMessage, TestNode.AsDisconnect(await client.ReadAsync()).Code);
    }

    [Fact]
    public async Task ShortPlayerKeyIsMalformed()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice").JoinAsync(client, h => h.PlayerKey = [1, 2, 3]);
        Assert.Equal(DisconnectCode.MalformedMessage, TestNode.AsDisconnect(reply).Code);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task OversizedPreHandshakeFrameClosesTheConnection(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, withGateway: true);
        await using var client = await net.ConnectAsync();
        await client.ReadAsync<ServerHello>(MsgType.ServerHello);
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 200_000, MsgType.ClientHello, FrameOptions.None, Lane.Control); // < 1 MiB but > handshake cap
        await client.SendRawAsync(header);
        Assert.Equal(DisconnectCode.MalformedMessage, TestNode.AsDisconnect(await client.ReadAsync()).Code);
    }

    [Fact]
    public async Task ClientQuittingDuringHandshakeReleasesTheSlot()
    {
        await using var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        var client = await net.ConnectAsync();
        await client.ReadAsync<ServerHello>(MsgType.ServerHello);
        await client.DisposeAsync();
        await WaitForAsync(() => net.Gateway!.LiveConnectionCount == 0);
        Assert.Equal(0, net.Gateway!.LiveConnectionCount);
    }

    [Fact]
    public async Task GatewayShutdownClosesEveryConnectionWithServerShutdown()
    {
        var net = await NetHarness.CreateAsync("inproc", withGateway: true);
        await using var client = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice").JoinAsync(client);
        TestNode.AsWelcome(reply);
        await net.DisposeAsync();
        var d = TestNode.AsDisconnect(await client.ReadAsync());
        Assert.Equal(DisconnectCode.ServerShutdown, d.Code);
    }

    [Fact]
    public void ComputedProofMatchesTheSpecConstruction()
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var key = RandomNumberGenerator.GetBytes(32);
        var expected = HMACSHA256.HashData(SHA256.HashData("pw"u8.ToArray()), nonce.Concat(key).ToArray());
        Assert.Equal(expected, GatewayState.ComputeProof(GatewayState.HashPassword("pw")!, nonce, key));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }
}
