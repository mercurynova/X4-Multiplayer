using System.Diagnostics;
using System.Reflection;
using X4MP.Protocol;

namespace X4MP.Server.Hosting;

/// <summary>Static facts about this build plus the process uptime clock.</summary>
public sealed class ServerInfo
{
    /// <summary>
    /// Lowest wire version accepted, as <c>major * 1000 + minor</c>. Peers with another major are rejected and the minor is negotiated down
    /// per session, so any minor of the one supported major is accepted: min is <c>major.0</c>.
    /// </summary>
    public const int ProtocolMin = (ProtocolConstants.ProtocolMajor * 1000) + 0;

    /// <summary>Highest wire version spoken: <c>major * 1000 + minor</c> (0.1 = 1).</summary>
    public const int ProtocolMax = (ProtocolConstants.ProtocolMajor * 1000) + ProtocolConstants.ProtocolMinor;

    private readonly long _startedAt = Stopwatch.GetTimestamp();

    public ServerInfo()
    {
        var informational = typeof(ServerInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        Version = plus < 0 ? informational : informational[..plus];
        BuildHash = plus < 0 ? "unknown" : informational[(plus + 1)..];
    }

    public string Version { get; }

    /// <summary>Short git hash taken from <c>SourceRevisionId</c>, or "unknown".</summary>
    public string BuildHash { get; }

    public TimeSpan Uptime => Stopwatch.GetElapsedTime(_startedAt);

    public string DescribeVersion() =>
        $"x4mp-server {Version}{Environment.NewLine}" +
        $"protocol: {ProtocolConstants.ProtocolMajor}.{ProtocolConstants.ProtocolMinor}{Environment.NewLine}" +
        $"build: {BuildHash}";
}
