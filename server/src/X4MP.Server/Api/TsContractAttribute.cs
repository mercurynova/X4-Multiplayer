namespace X4MP.Server.Api;

/// <summary>
/// Marks an admin API DTO (record/class) or enum for emission into <c>server/web/src/generated/generated.ts</c> by
/// <c>tools/X4MP.TsContractGenerator</c>. A drift-check test fails if the committed file is stale.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class TsContractAttribute : Attribute;
