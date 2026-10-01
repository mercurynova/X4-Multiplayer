namespace X4MP.Protocol.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void ProtocolAssemblyLoads() =>
        Assert.Equal("X4MP.Protocol", typeof(AssemblyMarker).Assembly.GetName().Name);
}
