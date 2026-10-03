using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// <c>--selftest</c> (M2-13): what the mod's in-game self-test sends, as <c>LogForward</c> lines in the format the mod writes (docs/mod-design.md 8.5.1):
/// <c>SELFTEST &lt;PASS|FAIL|WARN|SKIP&gt; &lt;check&gt; &lt;detail&gt;</c> (result padded to 5, check to 16), a <c>SELFTEST summary:</c> line, preceded by two
/// ordinary log lines. Written without the server's parser on purpose: an independent reading of the convention.
/// </summary>
public static class FakeSelfTest
{
    public static readonly IReadOnlyList<(string Result, string Name, string Detail)> Rows =
    [
        ("PASS", "x4native.api", "game 9.00 x4native 9.0.0 exports 15/15"),
        ("PASS", "x4native.hooks", "md hooks installed"),
        ("PASS", "build.supported", "611726"),
        ("FAIL", "game.adapter", "options menu adapter probe failed (simulated)"),
        ("WARN", "saves.block", "no save-block ever written yet"),
        ("SKIP", "main_thread", "no jobs queued"),
    ];

    public static int Passed => Rows.Count(r => r.Result == "PASS");

    public static int Failed => Rows.Count(r => r.Result == "FAIL");

    public static int Warned => Rows.Count(r => r.Result == "WARN");

    public static int Skipped => Rows.Count(r => r.Result == "SKIP");

    /// <summary>The <c>LogForward</c> payload of one self-test run, with <paramref name="timeUs"/> as the line time.</summary>
    public static byte[] BuildPayload(ulong timeUs)
    {
        var lines = new List<LogLineT>
        {
            new() { Level = LogLevel.Info, TimeUs = timeUs, Text = "fakenode: self-test starting" },
            new() { Level = LogLevel.Warn, TimeUs = timeUs, Text = "fakenode: game adapter check will fail (simulated)" },
        };
        foreach (var (result, name, detail) in Rows)
        {
            lines.Add(new LogLineT
            {
                Level = result == "FAIL" ? LogLevel.Error : result == "WARN" ? LogLevel.Warn : LogLevel.Info,
                TimeUs = timeUs,
                Text = $"SELFTEST {result,-5} {name,-16} {detail}",
            });
        }

        lines.Add(new LogLineT { Level = LogLevel.Error, TimeUs = timeUs, Text = $"SELFTEST summary: {Passed} PASS, {Failed} FAIL, {Warned} WARN, {Skipped} SKIP" });
        var forward = new LogForwardT { Lines = lines };
        return MessageEncoder.EncodePayload(b => LogForward.Pack(b, forward));
    }

    /// <summary>Sends the table over a joined node's control connection.</summary>
    internal static async Task SendAsync(NodeLink link, CancellationToken ct)
    {
        ulong timeUs = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        await link.Client.SendPayloadAsync(MsgType.LogForward, BuildPayload(timeUs), ct).ConfigureAwait(false);
    }
}
