namespace X4MP.Persistence;

/// <summary>Marker used by dependent projects so the assembly reference is retained.</summary>
public static class AssemblyMarker
{
    /// <summary>Marker types of the assemblies this project depends on.</summary>
    public static readonly Type[] Dependencies = [typeof(Core.AssemblyMarker)];
}
