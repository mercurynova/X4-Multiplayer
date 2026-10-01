using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Session;

public partial class MessagePolicyTests
{
    private static readonly MsgType[] AllTypes = [.. Enum.GetValues<MsgType>()];

    [Fact]
    public void EveryMsgTypeHasAPolicyEntry()
    {
        foreach (var type in AllTypes)
        {
            Assert.True(MessagePolicy.TryGetRule(type, out _), $"{type} has no MessagePolicy entry");
        }

        Assert.Equal(AllTypes.Length, MessagePolicy.Rules.Count);
    }

    [Fact]
    public void RuleLanesComeFromTheCatalog()
    {
        foreach (var rule in MessagePolicy.Rules)
        {
            if (MessageRegistry.Default.TryGetDescriptor(rule.Type, out var d))
            {
                Assert.Equal(d.Lane, rule.Lane);
            }
        }

        foreach (var d in MessageRegistry.Default.Descriptors)
        {
            Assert.True(MessagePolicy.TryGetRule(d.Type, out var rule));
            Assert.Equal(d.Lane, rule.Lane);
        }
    }

    [Fact]
    public void ServerOnlyRowsHaveNoSendersAndNoPhases()
    {
        foreach (var rule in MessagePolicy.Rules.Where(r => r.ServerOnly))
        {
            Assert.Equal(PolicyPhase.None, rule.Phases);
        }

        foreach (var rule in MessagePolicy.Rules.Where(r => !r.ServerOnly))
        {
            Assert.NotEqual(PolicyPhase.None, rule.Phases);
        }
    }

    // ---- directions agree with protocol.md section 20 ----

    [GeneratedRegex(@"^\|\s*0x([0-9A-Fa-f]{4})\s*\|\s*([A-Za-z0-9]+)\s*\|\s*([^|]+?)\s*\|")]
    private static partial Regex CatalogRow();

    private static string? FindProtocolDoc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "protocol.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public void SenderRolesAgreeWithTheCatalogDirectionsInProtocolMd()
    {
        var doc = FindProtocolDoc();
        Assert.True(doc is not null, "docs/protocol.md not found above the test binaries");

        int checkedRows = 0;
        foreach (var line in File.ReadLines(doc))
        {
            var m = CatalogRow().Match(line);
            if (!m.Success)
            {
                continue;
            }

            var type = (MsgType)Convert.ToUInt16(m.Groups[1].Value, 16);
            if (!Enum.IsDefined(type) || type == MsgType.DamageReport)
            {
                continue; // reserved
            }

            Assert.True(MessagePolicy.TryGetRule(type, out var rule), $"{type} missing");
            string dir = Regex.Replace(m.Groups[3].Value, @"\([^)]*\)", string.Empty);
            bool fromAuthority = dir.Contains("A→S", StringComparison.Ordinal);
            bool fromClient = dir.Contains("C→S", StringComparison.Ordinal);
            bool fromAnyNode = dir.Contains("N→S", StringComparison.Ordinal) || dir.Contains("S↔N", StringComparison.Ordinal);
            bool nodeSends = fromAuthority || fromClient || fromAnyNode;

            if (type == MsgType.AdminCommand)
            {
                Assert.Equal(Role.Admin, rule.Senders); // N→S in the catalog, restricted to the Admin flag (architecture 4.1)
            }
            else if (!nodeSends)
            {
                Assert.True(rule.ServerOnly, $"{type} ({dir}) is server-originated but the policy lets a node send it");
            }
            else
            {
                Assert.False(rule.ServerOnly, $"{type} ({dir}) is node-originated but the policy has no sender");
                if (fromAuthority)
                {
                    Assert.True((rule.Senders & Role.Authority) != 0, $"{type}: catalog says the authority sends it");
                }

                if (fromClient || fromAnyNode)
                {
                    Assert.True((rule.Senders & Role.Client) != 0, $"{type}: catalog says clients send it");
                }

                if (!fromClient && !fromAnyNode)
                {
                    Assert.True((rule.Senders & Role.Client) == 0, $"{type}: only the authority sends it, but Client is allowed");
                }
            }

            checkedRows++;
        }

        Assert.Equal(AllTypes.Length - 2, checkedRows); // every type except Invalid and the reserved DamageReport
    }

    // ---- evaluation matrix ----

    private static PolicyVerdict Eval(MsgType type, Role roles, PolicyPhase phase, Lane? lane = null, ushort peerMinor = 1) =>
        MessagePolicy.Evaluate(type, lane ?? MessagePolicy.Rules.First(r => r.Type == type).Lane, roles, phase, peerMinor);

    [Fact]
    public void ClientCannotSendAuthorityMessages()
    {
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.WorldUpdate, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.EntitySpawn, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.GameEvent, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.SaveUploadBegin, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.GalaxyMetadata, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.WorldUpdate, Role.Authority | Role.Client, PolicyPhase.InGame));
    }

    [Fact]
    public void AuthorityOnlyNodeCannotSendClientMessages()
    {
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.PlayerState, Role.Authority, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.Intent, Role.Authority, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.PlayerState, Role.Authority | Role.Client, PolicyPhase.InGame));
    }

    [Fact]
    public void ObserversMayOnlyResyncAndChat()
    {
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.ResyncRequest, Role.Observer, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.ChatSend, Role.Observer, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.PlayerState, Role.Observer, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.Intent, Role.Observer, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.DonateRequest, Role.Observer, PolicyPhase.InGame));
    }

    [Fact]
    public void AdminCommandNeedsTheAdminFlagNotJustAnyRole()
    {
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.AdminCommand, Role.Authority | Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.AdminCommand, Role.Client | Role.Admin, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.RoleDenied, Eval(MsgType.PlayerState, Role.Admin, PolicyPhase.InGame)); // Admin alone is not a Client
    }

    [Fact]
    public void ServerOriginatedMessagesAreViolationsFromNodes()
    {
        foreach (var type in new[] { MsgType.Welcome, MsgType.Replication, MsgType.ServerHello, MsgType.RosterUpdate, MsgType.CaptureSet, MsgType.TradeResult, MsgType.DamageReport })
        {
            Assert.Equal(PolicyVerdict.ServerOnly, Eval(type, Role.Authority | Role.Client | Role.Admin, PolicyPhase.InGame));
        }
    }

    [Fact]
    public void PhaseRestrictsWhenAMessageIsAccepted()
    {
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.ClientHello, 0, PolicyPhase.Handshaking));
        Assert.Equal(PolicyVerdict.PhaseDenied, Eval(MsgType.ClientHello, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.PhaseDenied, Eval(MsgType.PlayerState, Role.Client, PolicyPhase.SyncingSave));
        Assert.Equal(PolicyVerdict.PhaseDenied, Eval(MsgType.Intent, Role.Client, PolicyPhase.CatchingUp));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.Intent, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.TeamChoice, Role.Client, PolicyPhase.AwaitingTeam));
        Assert.Equal(PolicyVerdict.PhaseDenied, Eval(MsgType.TeamChoice, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.SaveReady, Role.Client, PolicyPhase.SyncingSave));
        Assert.Equal(PolicyVerdict.PhaseDenied, Eval(MsgType.SaveReady, Role.Client, PolicyPhase.InGame));
        Assert.Equal(PolicyVerdict.PhaseDenied, Eval(MsgType.ChatSend, Role.Client, PolicyPhase.Handshaking));
    }

    [Fact]
    public void PingPongAndDisconnectAreAcceptedInEveryPhase()
    {
        foreach (var phase in new[] { PolicyPhase.Handshaking, PolicyPhase.Admitted, PolicyPhase.SyncingSave, PolicyPhase.InGame, PolicyPhase.Detached })
        {
            foreach (var type in new[] { MsgType.Ping, MsgType.Pong, MsgType.Disconnect })
            {
                Assert.Equal(PolicyVerdict.Allowed, Eval(type, phase == PolicyPhase.Handshaking ? 0 : Role.Client, phase));
            }
        }
    }

    [Fact]
    public void FrameLaneMustMatchTheCatalog()
    {
        Assert.Equal(PolicyVerdict.LaneMismatch, Eval(MsgType.ChatSend, Role.Client, PolicyPhase.InGame, Lane.Realtime));
        Assert.Equal(PolicyVerdict.LaneMismatch, Eval(MsgType.PlayerState, Role.Client, PolicyPhase.InGame, Lane.Control));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.PlayerState, Role.Client, PolicyPhase.InGame, Lane.Realtime));
        Assert.Equal(PolicyVerdict.Allowed, Eval(MsgType.SaveChunk, Role.Authority, PolicyPhase.InGame, Lane.Bulk));
    }

    [Fact]
    public void UnknownTypesAreSkippedOnlyForNewerPeers()
    {
        var unknown = (MsgType)0x7F01;
        Assert.Equal(PolicyVerdict.UnknownType, MessagePolicy.Evaluate(unknown, Lane.Control, Role.Client, PolicyPhase.InGame, ProtocolConstants.ProtocolMinor));
        Assert.Equal(PolicyVerdict.SkipUnknown, MessagePolicy.Evaluate(unknown, Lane.Control, Role.Client, PolicyPhase.InGame, ProtocolConstants.ProtocolMinor + 1));
        // a known-but-reserved id is never "unknown from the future"
        Assert.Equal(PolicyVerdict.ServerOnly, MessagePolicy.Evaluate(MsgType.DamageReport, Lane.Control, Role.Client, PolicyPhase.InGame, 9));
        Assert.Equal(PolicyVerdict.UnknownType, MessagePolicy.Evaluate(MsgType.Invalid, Lane.Control, Role.Client, PolicyPhase.InGame, ProtocolConstants.ProtocolMinor));
    }

    [Fact]
    public void ViolationVerdictsMapToDisconnectCodes()
    {
        Assert.Equal(DisconnectCode.UnexpectedMessage, MessagePolicy.CodeFor(PolicyVerdict.RoleDenied));
        Assert.Equal(DisconnectCode.UnexpectedMessage, MessagePolicy.CodeFor(PolicyVerdict.PhaseDenied));
        Assert.Equal(DisconnectCode.UnexpectedMessage, MessagePolicy.CodeFor(PolicyVerdict.ServerOnly));
        Assert.Equal(DisconnectCode.MalformedMessage, MessagePolicy.CodeFor(PolicyVerdict.UnknownType));
        Assert.Equal(DisconnectCode.MalformedMessage, MessagePolicy.CodeFor(PolicyVerdict.LaneMismatch));
    }

    [Fact]
    public void NodePhasesMapOntoDistinctPolicyFlags()
    {
        var seen = new HashSet<PolicyPhase>();
        foreach (var phase in Enum.GetValues<NodePhase>())
        {
            Assert.True(seen.Add(MessagePolicy.ToPolicyPhase(phase)));
        }

        Assert.Equal(PolicyPhase.InGame, MessagePolicy.ToPolicyPhase(NodePhase.InGame));
        Assert.Equal(PolicyPhase.AwaitingTeam, MessagePolicy.ToPolicyPhase(NodePhase.AwaitingTeam));
    }

    // ---- violation counting ----

    [Fact]
    public void MoreThanTwentyViolationsInAMinuteExceedTheLimit()
    {
        var time = new FakeTimeProvider();
        var tracker = new ViolationTracker(20, time);
        for (int i = 0; i < 20; i++)
        {
            tracker.Record();
        }

        Assert.False(tracker.Exceeded);
        Assert.Equal(21, tracker.Record());
        Assert.True(tracker.Exceeded);
    }

    [Fact]
    public void ViolationsAgeOutOfTheSlidingWindow()
    {
        var time = new FakeTimeProvider();
        var tracker = new ViolationTracker(20, time);
        for (int i = 0; i < 15; i++)
        {
            tracker.Record();
        }

        time.Advance(TimeSpan.FromSeconds(40));
        for (int i = 0; i < 5; i++)
        {
            tracker.Record();
        }

        Assert.Equal(20, tracker.Count);
        time.Advance(TimeSpan.FromSeconds(30)); // the first 15 are now 70 s old
        Assert.Equal(5, tracker.Count);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(0, tracker.Count);
    }
}
