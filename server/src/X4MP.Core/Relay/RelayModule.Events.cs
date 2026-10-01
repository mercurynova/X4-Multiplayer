using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Relay;

public sealed partial class RelayModule
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private void OnGameEvent(SessionNode node, InboundFrame frame)
    {
        if (!node.IsAuthority)
        {
            return; // policy lets only the authority send it; a stale role after a hand-over is ignored
        }

        Stats.GameEventsReceived++;
        var gameEvent = MessageRegistry.Default.Decode<GameEvent>(frame.Frame).UnPack();
        if (gameEvent.Body is null || gameEvent.Body.Type == GameEventBody.NONE || gameEvent.Body.Value is null)
        {
            LogDropped("GameEvent", "no body");
            return;
        }

        gameEvent.EventSeq = ++_eventSeq;
        gameEvent.ServerTimeUs = (ulong)Math.Max(0, _driver?.ServerTimeUs ?? 0);
        var outbound = Encode(MsgType.GameEvent, fbb => GameEvent.Pack(fbb, gameEvent), 256);
        bool everybody = gameEvent.Sector == 0 || IsPlayerRelated(gameEvent.Body.Type) || _interest is null;
        foreach (var target in _nodes.Values)
        {
            if (ReferenceEquals(target, node) || !IsLive(target) || (target.Roles & (Role.Client | Role.Observer)) == 0)
            {
                continue;
            }

            // Observers (tools, the GUI feed) see everything; players only what is relevant to where they are.
            bool wanted = everybody || (target.Roles & Role.Client) == 0 || _interest!.IsInterested(target.PlayerId, gameEvent.Sector);
            if (wanted && TrySend(target, outbound))
            {
                Stats.GameEventsDelivered++;
            }
        }

        outbound.Release();
        Publish(ToDomainEvent(gameEvent));
    }

    /// <summary>Player-related events matter to everybody (the feed, the player list); world events only to nodes that follow the sector.</summary>
    private static bool IsPlayerRelated(GameEventBody type) =>
        type is GameEventBody.PlayerDiedEvent or GameEventBody.PlayerSpawnedEvent or GameEventBody.PlayerConnectionEvent
            or GameEventBody.TeamEvent or GameEventBody.EconomyEvent;

    private GameEventOccurred ToDomainEvent(GameEventT e)
    {
        long? player = e.Body.Type switch
        {
            GameEventBody.KillEvent => e.Body.AsKillEvent().KillerPlayer,
            GameEventBody.PlayerDiedEvent => e.Body.AsPlayerDiedEvent().PlayerId,
            GameEventBody.PlayerSpawnedEvent => e.Body.AsPlayerSpawnedEvent().PlayerId,
            GameEventBody.StationBuiltEvent => e.Body.AsStationBuiltEvent().BuilderPlayer,
            GameEventBody.TradeEvent => e.Body.AsTradeEvent().PlayerId,
            GameEventBody.CaptureEvent => e.Body.AsCaptureEvent().PlayerId,
            GameEventBody.PlayerConnectionEvent => e.Body.AsPlayerConnectionEvent().PlayerId,
            GameEventBody.TeamEvent => e.Body.AsTeamEvent().PlayerId,
            GameEventBody.EconomyEvent => e.Body.AsEconomyEvent().FromPlayer,
            _ => 0,
        };
        string kind = e.Body.Type.ToString();
        if (kind.EndsWith("Event", StringComparison.Ordinal))
        {
            kind = kind[..^"Event".Length];
        }

        string json = JsonSerializer.Serialize(
            new { seq = e.EventSeq, gameTime = e.GameTime, sector = e.Sector, body = e.Body.Value },
            EventJson);
        return new GameEventOccurred(
            _time.GetUtcNow(), SessionId, kind, player is null or 0 ? null : player, e.Sector == 0 ? null : e.Sector, json);
    }
}
