namespace X4MP.Server;

/// <summary>Marker types of the assemblies the server depends on, so the references are retained.</summary>
public static class AssemblyMarker
{
    /// <summary>Marker types of the referenced assemblies.</summary>
    public static readonly Type[] Dependencies =
    [
        typeof(Core.AssemblyMarker),
        typeof(Transport.AssemblyMarker),
        typeof(Persistence.AssemblyMarker),
        typeof(Protocol.AssemblyMarker),
    ];
}
