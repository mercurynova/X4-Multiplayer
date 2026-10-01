using System.Reflection;

namespace X4MP.Server.Tests;

/// <summary>Enforces the solution dependency direction: Protocol, Core, Transport/Persistence, Server.</summary>
public class DependencyDirectionTests
{
    private static readonly Assembly ProtocolAsm = typeof(X4MP.Protocol.AssemblyMarker).Assembly;
    private static readonly Assembly CoreAsm = typeof(X4MP.Core.AssemblyMarker).Assembly;
    private static readonly Assembly TransportAsm = typeof(X4MP.Transport.AssemblyMarker).Assembly;
    private static readonly Assembly PersistenceAsm = typeof(X4MP.Persistence.AssemblyMarker).Assembly;
    private static readonly Assembly ServerAsm = typeof(X4MP.Server.AssemblyMarker).Assembly;
    private static readonly Assembly FakeNodeAsm = Assembly.Load("X4MP.FakeNode");

    private static string[] X4Refs(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("X4MP.", StringComparison.Ordinal) || n == "x4mp-server")
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void ProtocolReferencesNoX4Assembly() =>
        Assert.Empty(X4Refs(ProtocolAsm));

    [Fact]
    public void CoreReferencesOnlyProtocol() =>
        Assert.Equal(["X4MP.Protocol"], X4Refs(CoreAsm));

    [Fact]
    public void TransportDoesNotReferenceServerOrPersistence()
    {
        var refs = X4Refs(TransportAsm);
        Assert.DoesNotContain("x4mp-server", refs);
        Assert.DoesNotContain("X4MP.Persistence", refs);
        Assert.Contains("X4MP.Core", refs);
    }

    [Fact]
    public void PersistenceDoesNotReferenceServerOrTransport()
    {
        var refs = X4Refs(PersistenceAsm);
        Assert.DoesNotContain("x4mp-server", refs);
        Assert.DoesNotContain("X4MP.Transport", refs);
        Assert.Contains("X4MP.Core", refs);
    }

    [Fact]
    public void ServerReferencesAllLibraries() =>
        Assert.Equal(
            ["X4MP.Core", "X4MP.Persistence", "X4MP.Protocol", "X4MP.Transport"],
            X4Refs(ServerAsm));

    [Fact]
    public void FakeNodeReferencesOnlyProtocol() =>
        Assert.Equal(["X4MP.Protocol"], X4Refs(FakeNodeAsm));
}
