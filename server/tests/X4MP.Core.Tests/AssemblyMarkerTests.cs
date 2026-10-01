namespace X4MP.Core.Tests;

public class AssemblyMarkerTests
{
    [Fact]
    public void CoreAssemblyLoads() =>
        Assert.Equal("X4MP.Core", typeof(AssemblyMarker).Assembly.GetName().Name);
}
