using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using X4MP.Core.Events;
using X4MP.Server.Api;
using X4MP.Server.Hubs;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Hubs;


/// <summary>The hub's method and event names in <c>generated.ts</c> match the C# surface.</summary>
public sealed class HubContractTests
{
    private static HashSet<string> ConstantValues(Type type) =>
        [.. type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)];

    [Fact]
    public void AdminHubMethodsListsEveryPublicHubMethod()
    {
        var methods = typeof(AdminHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => n is not ("OnConnectedAsync" or "OnDisconnectedAsync"))
            .ToHashSet();
        var constants = ConstantValues(typeof(AdminHubMethods));
        constants.Remove(AdminHubMethods.Route);
        Assert.Equal([.. methods.Order()], [.. constants.Order()]);
    }

    [Fact]
    public void AdminHubEventsListsEveryMethodOfIAdminClient()
    {
        var calls = typeof(IAdminClient).GetMethods().Select(m => m.Name).ToHashSet();
        Assert.Equal([.. calls.Order()], [.. ConstantValues(typeof(AdminHubEvents)).Order()]);
    }

    [Fact]
    public void TheGeneratedTypeScriptCarriesTheHubNamesAndDtos()
    {
        string generated = X4MP.TsContract.TsContractGenerator.Generate();
        Assert.Contains("export const AdminHubMethods = {", generated, StringComparison.Ordinal);
        Assert.Contains("SubscribeSector: 'SubscribeSector',", generated, StringComparison.Ordinal);
        Assert.Contains("export const AdminHubEvents = {", generated, StringComparison.Ordinal);
        Assert.Contains("export interface SectorFrameDto {", generated, StringComparison.Ordinal);
        Assert.Contains("playerIds: (number | null)[];", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPublicHubMethodDeclaresItsRole()
    {
        var hub = typeof(AdminHub);
        Assert.NotNull(hub.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
        var sendChat = hub.GetMethod(nameof(AdminHub.SendChat))!;
        Assert.Equal(X4MP.Server.Auth.AdminPolicies.Admin, sendChat.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()!.Policy);
        _ = typeof(HubException);
    }
}
