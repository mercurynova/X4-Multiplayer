using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using X4MP.Server.Hosting;
using X4MP.Server.Logging;

var cli = CliArguments.Parse(args);

switch (cli.Verb)
{
    case CliVerb.Version:
        Console.WriteLine(new ServerInfo().DescribeVersion());
        return 0;
    case CliVerb.Help:
        if (cli.Error is not null)
        {
            Console.Error.WriteLine(cli.Error);
            Console.Error.WriteLine();
        }
        Console.WriteLine(CliArguments.Usage);
        return cli.Error is null ? 0 : 2;
    case CliVerb.ServiceInstall when cli.Error is null:
        return ServiceCommands.Install(cli, Environment.ProcessPath ?? "x4mp-server.exe", Console.Out, ServiceCommands.RunProcess, ServiceCommands.IsElevated());
    case CliVerb.ServiceUninstall when cli.Error is null:
        return ServiceCommands.Uninstall(cli, Console.Out, ServiceCommands.RunProcess, ServiceCommands.IsElevated());
}

if (cli.Error is not null)
{
    Console.Error.WriteLine(cli.Error);
    return 2;
}

Log.Logger = ServerLogging.ConfigureBootstrap(new LoggerConfiguration(), new LoggingOptions()).CreateBootstrapLogger();

try
{
    var isService = cli.ServiceFlag || WindowsServiceHelpers.IsWindowsService();
    var app = ServerHost.Build(cli.Remaining, cli, isService);
    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Server terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry-point type, exposed for WebApplicationFactory in tests.</summary>
public partial class Program;
