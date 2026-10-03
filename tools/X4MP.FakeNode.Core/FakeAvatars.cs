using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>Tunables of the fake authority's avatar provisioning (<c>--avatar-macro</c>, <c>--host-name</c>, <c>--avatar-offset</c>).</summary>
public sealed record FakeAvatarOptions
{
    /// <summary>The Argon Elite, the M3 starter ship (m3-plan Q5; the server setting <c>Avatars.StarterShipMacro</c> picks it per session once M3-01 lands).</summary>
    public const string DefaultStarterMacro = "ship_arg_s_fighter_01_a_macro";

    /// <summary>The ship every avatar is (a real macro id, not a FakeNode macro: a real client binds a real object to it).</summary>
    public string StarterShipMacro { get; init; } = DefaultStarterMacro;

    /// <summary>Distance of a new avatar from the host ship, metres; the per-slot spread is +-150 m around it (default 450: 300 to 600 m, m3-plan Q5).</summary>
    public double SpawnOffsetMeters { get; init; } = 450;

    /// <summary>The host ship shows up as <c>[MP] &lt;HostName&gt;</c> (m3-plan 6.2 row M3-05: <c>[MP] Host</c>).</summary>
    public string HostName { get; init; } = "Host";

    /// <summary>How long a request waits for the roster to name its player before the avatar is made with a placeholder name.</summary>
    public TimeSpan RosterWait { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>One avatar (or the host ship) the fake authority holds.</summary>
public sealed record FakeAvatar(ushort PlayerId, uint NetId, ushort Team, string Name, string Macro, bool IsHost, bool Online, ushort Sector, Vec3 Position);

/// <summary>
/// Avatar provisioning of the fake authority (m3-plan 4.3, ADR-015), with no I/O. <c>PlayerShip</c> (the server forwards the request of a client that
/// stands in the host's ship) is answered with an <c>EntitySpawn{origin=PlayerShip, controller_player}</c> for the player's avatar: a real ship macro
/// (<see cref="FakeAvatarOptions.StarterShipMacro"/>), owner <c>x4mp_team_k</c>, placed next to the host ship. The **host ship** itself is self-spawned
/// where the first request says the client stands (same macro, sector and position: the save's player ship), so a real client sees <c>[MP] Host</c>
/// where its local copy was. A request for a player that already has an avatar is answered with the existing one (the server re-forwards held requests
/// after an authority resume), at the position the relayed <c>PlayerState</c>s last put it. A player that leaves (<c>RosterUpdate.removed</c>) gets
/// <c>EntityChange{Controller=0}</c>: the avatar stays, parked.
/// <para>Threading: the frame handlers call <see cref="OnPlayerShip"/>, <see cref="OnPlayerState"/> and <see cref="NoteRoster"/> from the reader thread;
/// <see cref="Drain"/> runs on the authority loop and is the only place that builds messages.</para>
/// </summary>
public sealed partial class FakeAvatars(FakeAuthority authority, FakeAvatarOptions? options = null)
{
    private sealed record Leave(int PlayerId);

    private sealed record Request(PlayerShipT Ship, Stopwatch Age);

    private sealed class Entry
    {
        public required ushort PlayerId;
        public required uint NetId;
        public ushort Team;
        public required string Name;
        public required string Macro;
        public required EntityKind Kind;
        public required string IdCode;
        public bool IsHost;
        public bool Online;
        public required EntityStateT State;
    }

    private readonly FakeAvatarOptions _opt = options ?? new FakeAvatarOptions();
    private readonly ConcurrentQueue<object> _events = new();
    private readonly ConcurrentDictionary<uint, PlayerStateT> _latest = new();
    private readonly object _rosterGate = new();
    private readonly Dictionary<int, PlayerInfoT> _roster = [];
    private readonly List<Request> _waiting = [];
    private readonly Dictionary<int, Entry> _avatars = [];
    private Entry? _host;
    private long _provisioned;
    private long _reissued;
    private long _leaves;

    public FakeAvatarOptions Options => _opt;

    /// <summary>Avatars created (the host ship is not counted).</summary>
    public long Provisioned => Interlocked.Read(ref _provisioned);

    /// <summary>Requests answered with an avatar that already existed (a resume, a rejoin).</summary>
    public long Reissued => Interlocked.Read(ref _reissued);

    /// <summary>Avatars parked because their player left.</summary>
    public long Leaves => Interlocked.Read(ref _leaves);

    /// <summary>The self-spawned host ship (null until the first <c>PlayerShip</c> arrived).</summary>
    public FakeAvatar? Host
    {
        get
        {
            lock (_avatars)
                return _host is null ? null : Describe(_host);
        }
    }

    /// <summary>The avatars now (a snapshot, in player id order).</summary>
    public IReadOnlyList<FakeAvatar> Snapshot()
    {
        lock (_avatars)
            return [.. _avatars.Values.OrderBy(a => a.PlayerId).Select(Describe)];
    }

    /// <summary>The avatar of <paramref name="playerId"/>, if any.</summary>
    public FakeAvatar? AvatarOf(int playerId)
    {
        lock (_avatars)
            return _avatars.TryGetValue(playerId, out var a) ? Describe(a) : null;
    }

    private static FakeAvatar Describe(Entry e) => new(
        e.PlayerId, e.NetId, e.Team, e.Name, e.Macro, e.IsHost, e.Online, e.State.Sector,
        new Vec3(Quantize.PositionToMetres(e.State.Px), Quantize.PositionToMetres(e.State.Py), Quantize.PositionToMetres(e.State.Pz)));

    // ------------------------------------------------------------------ reader thread

    /// <summary>A forwarded <c>PlayerShip</c> request (the server stamped <c>player_id</c>).</summary>
    public void OnPlayerShip(PlayerShipT ship) => _events.Enqueue(new Request(ship, Stopwatch.StartNew()));

    /// <summary>A relayed <c>PlayerState</c>: remembers the newest pose of avatar ships (the server stamps the avatar's net_id on the relayed copy).</summary>
    public void OnPlayerState(PlayerStateT state)
    {
        if (state.NetId != 0)
            _latest[state.NetId] = state;
    }

    /// <summary>Applies a roster update: names and teams for the avatars, and a removed player leaves (its avatar is parked by the next <see cref="Drain"/>).</summary>
    public void NoteRoster(RosterUpdateT roster)
    {
        lock (_rosterGate)
        {
            if (roster.Full)
            {
                var gone = _roster.Keys.Where(id => roster.Players is null || roster.Players.All(p => p.PlayerId != id)).ToList();
                _roster.Clear();
                foreach (int id in gone)
                    _events.Enqueue(new Leave(id));
            }

            foreach (var p in roster.Players ?? [])
                _roster[p.PlayerId] = p;
            foreach (ushort removed in roster.Removed ?? [])
            {
                _roster.Remove(removed);
                _events.Enqueue(new Leave(removed));
            }
        }
    }

    private bool TryRoster(int playerId, out PlayerInfoT info)
    {
        lock (_rosterGate)
            return _roster.TryGetValue(playerId, out info!);
    }

    // ------------------------------------------------------------------ authority loop

    /// <summary>Handles everything that arrived since the last call and returns what to send (in order). Cheap when idle.</summary>
    public IReadOnlyList<OutMessage> Drain(double gameTime)
    {
        var output = new List<OutMessage>();
        while (_events.TryDequeue(out var evt))
        {
            if (evt is Request request)
                _waiting.Add(request);
            else if (evt is Leave leave)
                Park(leave.PlayerId, gameTime, output);
        }

        for (int i = 0; i < _waiting.Count;)
        {
            var request = _waiting[i];
            bool known = TryRoster(request.Ship.PlayerId, out _);
            if (!known && request.Age.Elapsed < _opt.RosterWait)
            {
                i++; // the roster has not named this player yet: a moment, so the avatar gets its real name
                continue;
            }

            _waiting.RemoveAt(i);
            Provision(request.Ship, gameTime, output);
        }

        return output;
    }

    private void Park(int playerId, double gameTime, List<OutMessage> output)
    {
        lock (_avatars)
        {
            if (!_avatars.TryGetValue(playerId, out var avatar) || !avatar.Online)
                return;
            avatar.Online = false;
            Interlocked.Increment(ref _leaves);
            var change = new EntityChangeT { NetId = avatar.NetId, Fields = ChangeField.Controller, ControllerPlayer = 0 };
            output.Add(new OutMessage(MsgType.EntityChange, MessageEncoder.EncodePayload(b => EntityChange.Pack(b, change), 64)));
        }
    }

    private void Provision(PlayerShipT request, double gameTime, List<OutMessage> output)
    {
        int player = request.PlayerId;
        lock (_avatars)
        {
            if (_host is null)
                _host = SpawnHost(request, gameTime, output);

            if (_avatars.TryGetValue(player, out var existing))
            {
                // the same request again (an authority resume re-forwards held ones) or a rejoin: the existing avatar, controlled again, where it was
                if (_latest.TryGetValue(existing.NetId, out var pose))
                    existing.State = StateOf(existing.NetId, pose);
                existing.Online = true;
                Interlocked.Increment(ref _reissued);
                AddSpawn(existing, gameTime, output);
                return;
            }

            var host = _host;
            int slot = _avatars.Count;
            double angle = slot * 2.399963229728653; // golden angle: neighbours never line up
            double distance = _opt.SpawnOffsetMeters + (50.0 * (((slot * 3) % 7) - 3));
            var hostPos = new Vec3(
                Quantize.PositionToMetres(host.State.Px), Quantize.PositionToMetres(host.State.Py), Quantize.PositionToMetres(host.State.Pz));
            var pos = hostPos + new Vec3(Math.Cos(angle) * distance, 40.0 * ((slot % 3) - 1), Math.Sin(angle) * distance);
            string name = TryRoster(player, out var info) && !string.IsNullOrEmpty(info.Name) ? info.Name : string.Create(CultureInfo.InvariantCulture, $"Player{player}");
            ushort team = TryRoster(player, out info) ? info.TeamId : (ushort)0;
            var avatar = new Entry
            {
                PlayerId = (ushort)player,
                NetId = authority.NetIds.Allocate(),
                Team = team,
                Name = "[MP] " + name,
                Macro = _opt.StarterShipMacro,
                Kind = KindOf(_opt.StarterShipMacro),
                IdCode = string.Create(CultureInfo.InvariantCulture, $"AVA-{player % 1000:D3}"),
                Online = true,
                State = new EntityStateT
                {
                    Sector = host.State.Sector,
                    Px = Quantize.Position(pos.X),
                    Py = Quantize.Position(pos.Y),
                    Pz = Quantize.Position(pos.Z),
                    Yaw = host.State.Yaw,
                    Pitch = host.State.Pitch,
                    Roll = host.State.Roll,
                },
            };
            avatar.State.NetId = avatar.NetId;
            _avatars[player] = avatar;
            Interlocked.Increment(ref _provisioned);
            AddSpawn(avatar, gameTime, output);
        }
    }

    private Entry SpawnHost(PlayerShipT request, double gameTime, List<OutMessage> output)
    {
        string macro = string.IsNullOrEmpty(request.ShipMacro) ? _opt.StarterShipMacro : request.ShipMacro;
        int hostPlayer = authority.Teams.PlayerId;
        ushort team = TryRoster(hostPlayer, out var info) ? info.TeamId : (ushort)0;
        var host = new Entry
        {
            PlayerId = (ushort)hostPlayer,
            NetId = authority.NetIds.Allocate(),
            Team = team,
            Name = "[MP] " + _opt.HostName,
            Macro = macro,
            Kind = KindOf(macro),
            IdCode = string.IsNullOrEmpty(request.Idcode) ? "HST-001" : request.Idcode,
            IsHost = true,
            Online = true,
            State = new EntityStateT
            {
                Sector = request.Sector,
                Px = request.Px,
                Py = request.Py,
                Pz = request.Pz,
                Yaw = request.Yaw,
                Pitch = request.Pitch,
                Roll = request.Roll,
            },
        };
        host.State.NetId = host.NetId;
        AddSpawn(host, gameTime, output);
        return host;
    }

    private void AddSpawn(Entry e, double gameTime, List<OutMessage> output)
    {
        uint macroRef = authority.Strings.Ensure(e.Macro, StringKind.Macro, out var newMacro);
        string faction = e.Team == 0 ? "player" : string.Create(CultureInfo.InvariantCulture, $"x4mp_team_{SlotOf(e.Team)}");
        uint ownerRef = authority.Strings.Ensure(faction, StringKind.Faction, out var newFaction);
        var added = new List<StringEntryT>(2);
        if (newMacro is not null)
            added.Add(newMacro);
        if (newFaction is not null)
            added.Add(newFaction);
        if (added.Count > 0)
        {
            var table = new StringTableAddT { Entries = added };
            output.Add(new OutMessage(MsgType.StringTableAdd, MessageEncoder.EncodePayload(b => StringTableAdd.Pack(b, table), 256)));
        }

        var record = new EntityRecordT
        {
            NetId = e.NetId,
            Kind = e.Kind,
            Origin = EntityOrigin.PlayerShip,
            MacroRef = macroRef,
            OwnerRef = ownerRef,
            OwnerTeam = e.Team,
            OwnerPlayer = e.PlayerId,
            ControllerPlayer = e.Online ? e.PlayerId : (ushort)0,
            Name = e.Name,
            Idcode = e.IdCode,
            Hull = 255,
            Shield = 255,
            State = e.State,
        };
        var spawn = new EntitySpawnT { Entities = [record], GameTime = gameTime };
        output.Add(new OutMessage(MsgType.EntitySpawn, MessageEncoder.EncodePayload(b => EntitySpawn.Pack(b, spawn), 512)));
    }

    /// <summary>The faction slot (1..8) of a team: from the team table the authority holds, else the team id itself.</summary>
    private int SlotOf(ushort team) => authority.Teams.Team(team) is { FactionSlot: > 0 } t ? t.FactionSlot : Math.Clamp((int)team, 1, 8);

    private static EntityStateT StateOf(uint netId, PlayerStateT s) => new()
    {
        NetId = netId, Sector = s.Sector, Px = s.Px, Py = s.Py, Pz = s.Pz, Yaw = s.Yaw, Pitch = s.Pitch, Roll = s.Roll,
    };

    /// <summary>The ship class from a macro id (<c>ship_arg_s_fighter_01_a_macro</c> -> S); S when it cannot tell.</summary>
    public static EntityKind KindOf(string macro)
    {
        var m = SizeInMacro().Match(macro);
        return !m.Success ? EntityKind.ShipS : m.Groups["s"].Value switch
        {
            "xs" => EntityKind.ShipXS,
            "m" => EntityKind.ShipM,
            "l" => EntityKind.ShipL,
            "xl" => EntityKind.ShipXL,
            _ => EntityKind.ShipS,
        };
    }

    [GeneratedRegex(@"^ship_[a-z]+_(?<s>xs|s|m|l|xl)_", RegexOptions.CultureInvariant)]
    private static partial Regex SizeInMacro();
}
