using System.Diagnostics;
using System.Reflection;

namespace X4MP.Server.Hosting;

/// <summary>Static facts about this build plus the process uptime clock.</summary>
public sealed class ServerInfo
{
    /// <summary>Placeholder range until the protocol project exposes real version constants (M1).</summary>
    public const int ProtocolMin = 1;

    public const int ProtocolMax = 1;

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
        $"protocol: {ProtocolMin}..{ProtocolMax}{Environment.NewLine}" +
        $"build: {BuildHash}";
}
