using X4MP.FakeNode;

var parsed = CliParser.Parse(args);
if (!parsed.Ok)
{
    Console.Error.WriteLine($"fakenode: {parsed.Error}");
    Console.Error.WriteLine(CliParser.Usage);
    return 2;
}

var options = parsed.Options!;
switch (options.Command)
{
    case FakeNodeCommand.Help:
        Console.WriteLine(CliParser.Usage);
        return 0;

    case FakeNodeCommand.Galaxy:
        var galaxy = FakeGalaxy.Generate(options.Seed, new GalaxyOptions { SectorCount = options.Sectors, ShipCount = options.Ships, MaxShipsPerSector = options.MaxShipsPerSector });
        Console.WriteLine($"seed={options.Seed} {GalaxyStats.Of(galaxy)}");
        return 0;

    default:
        using (var cts = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true; // finish the summary instead of dying mid-write
                cts.Cancel();
            };
            return await LiveRunner.RunAsync(options, Console.Out, null, cts.Token);
        }
}
