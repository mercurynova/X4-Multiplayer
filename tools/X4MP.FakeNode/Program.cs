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
        var galaxy = FakeGalaxy.Generate(options.Seed, new GalaxyOptions { SectorCount = options.Sectors, ShipCount = options.Ships });
        Console.WriteLine($"seed={options.Seed} {GalaxyStats.Of(galaxy)}");
        return 0;

    default:
        // Part 2 wires these to TcpNodeClient + FakeAuthority/FakePlayer once the M1-03 server exists.
        Console.Error.WriteLine($"fakenode {options.Command.ToString().ToLowerInvariant()}: requires M1-03 server (TCP listener + handshake); not available yet.");
        return 3;
}
