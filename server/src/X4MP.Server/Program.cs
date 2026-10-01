using Serilog;
using X4MP.Server.Logging;

var loggingOptions = new LoggingOptions();
Log.Logger = ServerLogging.ConfigureBootstrap(new LoggerConfiguration(), loggingOptions).CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.GetSection(LoggingOptions.SectionName).Bind(loggingOptions);

    var ringBuffer = new RingBufferSink(loggingOptions.RingBufferCapacity);
    builder.Services.AddSingleton(loggingOptions);
    builder.Services.AddSingleton(ringBuffer);
    builder.Host.UseSerilog((_, _, configuration) =>
        ServerLogging.Configure(configuration, loggingOptions, ringBuffer));

    var app = builder.Build();
    app.UseSerilogRequestLogging();
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Server terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
