using X4MP.Server.Hosting;

namespace X4MP.Server.Tests;

public class ServiceCommandTests
{
    private const string Exe = @"C:\Program Files\X4MP\x4mp-server.exe";
    private const string Data = @"C:\ProgramData\X4MP";

    [Fact]
    public void InstallCreatesAutoStartServiceUnderVirtualAccount()
    {
        var commands = ServiceCommands.BuildInstall("X4MP", Exe, Data);

        var create = commands[0];
        Assert.Equal("sc.exe", create.FileName);
        Assert.Equal(["create", "X4MP"], create.Arguments.Take(2));
        Assert.Equal(
            "\"C:\\Program Files\\X4MP\\x4mp-server.exe\" run --service --name X4MP --data-dir \"C:\\ProgramData\\X4MP\"",
            ValueAfter(create, "binPath="));
        Assert.Equal("auto", ValueAfter(create, "start="));
        Assert.Equal(@"NT SERVICE\X4MP", ValueAfter(create, "obj="));
    }

    [Fact]
    public void InstallSetsRestartAfterTenSecondsRecoveryAndGrantsDataDirAcl()
    {
        var commands = ServiceCommands.BuildInstall("X4MP", Exe, Data);

        var failure = commands.Single(c => c.Arguments[0] == "failure");
        Assert.Equal("restart/10000/restart/10000/restart/10000", ValueAfter(failure, "actions="));

        var acl = commands[^1];
        Assert.Equal("icacls.exe", acl.FileName);
        Assert.Equal([Data, "/grant", @"NT SERVICE\X4MP:(OI)(CI)M"], acl.Arguments);
        Assert.True(commands.ToList().IndexOf(acl) > commands.ToList().FindIndex(c => c.Arguments[0] == "create"));
    }

    [Fact]
    public void CustomNameFlowsIntoAccountAndBinPath()
    {
        var create = ServiceCommands.BuildInstall("X4TEST", Exe, Data)[0];
        Assert.Equal(@"NT SERVICE\X4TEST", ValueAfter(create, "obj="));
        Assert.Contains("--name X4TEST", ValueAfter(create, "binPath="), StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallStopsThenDeletesAndToleratesNotRunning()
    {
        var commands = ServiceCommands.BuildUninstall("X4MP");
        Assert.Equal(["stop", "X4MP"], commands[0].Arguments);
        Assert.True(commands[0].IgnoreFailure);
        Assert.Equal(["delete", "X4MP"], commands[1].Arguments);
        Assert.False(commands[1].IgnoreFailure);
    }

    [Fact]
    public void RenderQuotesArgumentsWithSpacesAndEscapesInnerQuotes()
    {
        var create = ServiceCommands.BuildInstall("X4MP", Exe, Data)[0];
        var text = create.Render();
        Assert.StartsWith("sc.exe create X4MP binPath= \"\\\"C:\\Program Files\\X4MP\\x4mp-server.exe\\\" run", text, StringComparison.Ordinal);
        Assert.Contains("obj= \"NT SERVICE\\X4MP\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NotElevatedPrintsCommandsAndRunsNothing()
    {
        var ran = new List<ScCommand>();
        var output = new StringWriter();
        var cli = CliArguments.Parse(["service", "install", "--data-dir", Data]);

        var code = ServiceCommands.Install(cli, Exe, output, c => { ran.Add(c); return 0; }, elevated: false);

        Assert.Equal(1, code);
        Assert.Empty(ran);
        Assert.Contains("sc.exe create X4MP", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ElevatedUninstallRunsAllCommandsAndStopsOnFirstRealFailure()
    {
        var ran = new List<ScCommand>();
        var cli = CliArguments.Parse(["service", "uninstall"]);

        // sc stop fails (not running) but is ignored; delete succeeds.
        var code = ServiceCommands.Uninstall(cli, new StringWriter(), c => { ran.Add(c); return c.Arguments[0] == "stop" ? 1062 : 0; }, elevated: true);
        Assert.Equal(0, code);
        Assert.Equal(2, ran.Count);

        ran.Clear();
        code = ServiceCommands.Uninstall(cli, new StringWriter(), c => { ran.Add(c); return 5; }, elevated: true);
        Assert.Equal(5, code);
        Assert.Equal(2, ran.Count); // stop (ignored), delete (fails)
    }

    [Fact]
    public void ServiceVerbsParse()
    {
        Assert.Equal(CliVerb.ServiceInstall, CliArguments.Parse(["service", "install", "--name", "X"]).Verb);
        Assert.Equal("X", CliArguments.Parse(["service", "install", "--name", "X"]).ServiceName);
        Assert.Equal(CliVerb.ServiceUninstall, CliArguments.Parse(["service", "uninstall"]).Verb);
    }

    private static string ValueAfter(ScCommand command, string key)
    {
        var index = command.Arguments.ToList().IndexOf(key);
        Assert.True(index >= 0, $"missing {key}");
        return command.Arguments[index + 1];
    }
}
