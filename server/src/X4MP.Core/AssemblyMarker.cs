namespace X4MP.Core;

/// <summary>Marker used by dependent projects so the assembly reference is retained.</summary>
public static class AssemblyMarker
{
    /// <summary>Marker for the Protocol assembly this project depends on.</summary>
    public static readonly Type ProtocolMarker = typeof(Protocol.AssemblyMarker);
}
