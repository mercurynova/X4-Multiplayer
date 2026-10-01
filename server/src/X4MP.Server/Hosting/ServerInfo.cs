using System.Diagnostics;
using System.Reflection;
using X4MP.Protocol;

namespace X4MP.Server.Hosting;

/// <summary>Static facts about this build plus the process uptime clock.</summary>
public sealed class ServerInfo
{
    /// <summary>Lowest wire major accepted (peers with another major are rejected, so min == max).</summary>
    public const int ProtocolMin = ProtocolConstants.ProtocolMajor;

    /// <summary>Highest wire major accepted; the minor (<see cref="ProtocolConstants.ProtocolMinor"/>) is negotiated down per session.</summary>
    public const int ProtocolMax = ProtocolConstants.ProtocolMajor;

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
