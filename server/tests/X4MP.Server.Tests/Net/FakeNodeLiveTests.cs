using X4MP.Core.Net;
using X4MP.FakeNode;

namespace X4MP.Server.Tests.Net;

/// <summary>The FakeNode live runner (swarm, authority, client) against the real server over TCP.</summary>
[Collection("net")]
public class FakeNodeLiveTests
{
    /// <summary>True in <see cref="FakeNodeLiveActorTests"/>: the SessionActor is the admission handler.</summary>
    protected virtual bool UseActor => false;

    private static readonly LiveRunOptions Quick = new()
    {
        PingInterval = TimeSpan.FromMilliseconds(100),
        ReportInterval = TimeSpan.FromMilliseconds(500),
        ConnectStagger = TimeSpan.FromMilliseconds(10),
        SessionDetect = TimeSpan.FromMilliseconds(300),
    };

    private static NetOptions Roomy() => new() { MaxConnectionsPerIp = 64, MaxPlayers = 32, HandshakeTimeoutSeconds = 5 };

    [Fact]
    public async Task SwarmWithAuthorityConnectsEveryNodeWithoutErrors()
    {
        await using var harness = (TcpHarness)await NetHarness.CreateAsync("tcp", Roomy(), withGateway: true, useActor: UseActor);
        var options = CliParser.Parse(["swarm", "--clients", "8", "--with-authority", "--duration", "2"]).Options! with { Port = harness.Port };
        var output = new StringWriter();

        int exit = await LiveRunner.RunAsync(options, output, Quick, CancellationToken.None);

        string text = output.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("summary: nodes=9 joined=9 errors=0", text);
        Assert.Equal(9, text.Split('\n').Count(l => l.Contains("welcome:", StringComparison.Ordinal)));
        Assert.Contains("roles=Authority", text);
        Assert.Contains("connected=9/9", text);
        await Task.Delay(300);
        Assert.Empty(harness.Gateway!.AdmittedNodes);
    }

    [Fact]
    public async Task SingleClientAndAuthorityPrintWelcomeAndRtt()
    {
        await using var harness = (TcpHarness)await NetHarness.CreateAsync("tcp", Roomy(), withGateway: true, useActor: UseActor);
        foreach (var command in new[] { "client", "authority" })
        {
            var options = CliParser.Parse([command, "--duration", "1", "--name", "Solo" + command]).Options! with { Port = harness.Port };
            var output = new StringWriter();
            int exit = await LiveRunner.RunAsync(options, output, Quick, CancellationToken.None);
            Assert.Equal(0, exit);
            Assert.Contains("welcome:", output.ToString());
            Assert.Contains("rtt=", output.ToString());
            Assert.Contains("errors=0", output.ToString());
        }
    }

    [Fact]
    public async Task CancellationEndsCleanly()
    {
        await using var harness = (TcpHarness)await NetHarness.CreateAsync("tcp", Roomy(), withGateway: true, useActor: UseActor);
        var options = CliParser.Parse(["client"]).Options! with { Port = harness.Port };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        Assert.Equal(0, await LiveRunner.RunAsync(options, new StringWriter(), Quick, cts.Token));
    }

    [Fact]
    public async Task UnreachableServerReportsErrorsAndNonZeroExit()
    {
        var options = CliParser.Parse(["client", "--duration", "10", "--server", "127.0.0.1:1"]).Options!;
        var output = new StringWriter();
        Assert.Equal(1, await LiveRunner.RunAsync(options, output, Quick, CancellationToken.None));
        Assert.Contains("connect failed", output.ToString());
    }

    [Fact]
    public async Task InspectConnectsListensAndSummarisesTheFramesItSaw()
    {
        await using var harness = (TcpHarness)await NetHarness.CreateAsync("tcp", Roomy(), withGateway: true, useActor: UseActor);
        var options = CliParser.Parse(["inspect", "--no-join", "--duration", "1"]).Options! with { Port = harness.Port };
        var output = new StringWriter();
        Assert.Equal(0, await LiveRunner.RunAsync(options, output, Quick, CancellationToken.None));
        Assert.Contains("inspect: connected", output.ToString());
        Assert.Contains("inspect: frames-received=", output.ToString());
    }
}

/// <summary>The FakeNode live runs again with the <c>SessionActor</c> as the gateway's admission handler.</summary>
[Collection("net")]
public sealed class FakeNodeLiveActorTests : FakeNodeLiveTests
{
    protected override bool UseActor => true;
}
