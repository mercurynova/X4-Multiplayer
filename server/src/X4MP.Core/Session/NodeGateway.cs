using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

/// <summary>
/// Owns every connection from accept until it is admitted to the session (server-design 2.4, protocol.md 4):
/// per-IP and handshake-rate limits, <c>ServerHello</c> with a fresh nonce, <c>ClientHello</c> within the
/// handshake timeout, the compatibility and identity checks in protocol.md 4.2 order, HMAC auth, bans,
/// name binding, capacity and role rules, then <c>Welcome</c> and the hand-off to the
/// <see cref="IAdmissionHandler"/>. Every refusal is a <c>Disconnect{code}</c> followed by a close.
/// </summary>
public sealed partial class NodeGateway
{
    private const int NonceLength = 32;
    private const int PlayerKeyLength = 32;

    [GeneratedRegex(@"^[\p{L}\p{N} _\-.]{3,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private sealed record Rejection(DisconnectCode Code, string Message, string? Expected = null, uint RetryAfterMs = 0);

    private readonly NetOptions _options;
    private readonly GatewayState _state;
    private readonly IPlayerStore _players;
    private readonly IBanStore _bans;
    private readonly IAdmissionHandler _handler;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly long _startTimestamp;

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, int> _perIp = [];
    private readonly Dictionary<ConnectionId, INodeConnection> _live = [];
    private readonly Dictionary<string, AdmittedNode> _admitted = [];
    private readonly Dictionary<IPAddress, Queue<long>> _authFailures = [];
    private double _tokens;
    private long _tokensStamp;

    public NodeGateway(
        NetOptions options,
        GatewayState state,
        IPlayerStore players,
        IBanStore bans,
        IAdmissionHandler? handler = null,
        TimeProvider? time = null,
        ILogger<NodeGateway>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(bans);
        _options = options;
        _state = state;
        _players = players;
        _bans = bans;
        _handler = handler ?? new DefaultAdmissionHandler();
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _startTimestamp = _time.GetTimestamp();
        _tokens = options.HandshakesPerSecond;
        _tokensStamp = _startTimestamp;
    }

    /// <summary>Connections currently owned by the gateway (handshaking or admitted, not yet closed).</summary>
    public int LiveConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _live.Count;
            }
        }
    }

    /// <summary>Admitted nodes by <c>players.id</c> order is not guaranteed; snapshot for diagnostics and tests.</summary>
    public IReadOnlyList<AdmittedNode> AdmittedNodes
    {
        get
        {
            lock (_gate)
            {
                return [.. _admitted.Values];
            }
        }
    }

    /// <summary>Accept loop. Returns when <paramref name="ct"/> is cancelled or the listener ends.</summary>
    public async Task RunAsync(INodeListener listener, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listener);
        try
        {
            await foreach (var connection in listener.AcceptAsync(ct).ConfigureAwait(false))
            {
                _ = HandleSafeAsync(connection, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            CloseAll(DisconnectCode.ServerShutdown, "server stopping");
        }
    }

    /// <summary>Closes every connection the gateway owns.</summary>
    public void CloseAll(DisconnectCode code, string? detail = null)
    {
        INodeConnection[] all;
        lock (_gate)
        {
            all = [.. _live.Values];
        }

        foreach (var connection in all)
        {
            connection.Close(code, detail);
        }
    }

    private async Task HandleSafeAsync(INodeConnection connection, CancellationToken ct)
    {
        try
        {
            await HandleAsync(connection, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            connection.Close(DisconnectCode.ServerShutdown, "server stopping");
        }
        catch (Exception ex)
        {
            LogHandshakeFailed(connection.Id.Value, ex);
            connection.Close(DisconnectCode.InternalError, "internal error");
        }
    }

    /// <summary>Runs the handshake for one connection. Completes when the node was handed over or refused.</summary>
    public async Task HandleAsync(INodeConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var address = NetAddress.Normalize(connection.RemoteEndPoint);

        if (!TryRegister(connection, address))
        {
            ServerMetrics.RecordHandshakeRefused(DisconnectCode.SessionFull);
            connection.Close(DisconnectCode.SessionFull, "too many connections from this address", retryAfterMs: 5000);
            return;
        }

        if (!TryTakeHandshakeToken())
        {
            ServerMetrics.RecordHandshakeRefused(DisconnectCode.RateLimited);
            connection.Close(DisconnectCode.RateLimited, "handshake rate limit", retryAfterMs: 1000);
            return;
        }

        var ipBan = address is null ? null : await _bans.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, address, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (ipBan is not null)
        {
            ServerMetrics.RecordHandshakeRefused(DisconnectCode.Banned);
            LogRefused(connection.Id.Value, DisconnectCode.Banned, "address banned");
            connection.Close(DisconnectCode.Banned, ipBan.Reason);
            return;
        }

        var reader = new NodeFrameReader(connection, _options, _time, _bans, _logger);
        connection.MaxInboundFrameBytes = Math.Min(_options.HandshakeMaxFrameBytes, _options.MaxFrameBytes);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        SendServerHello(connection, nonce);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HandshakeTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        try
        {
            var first = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
            if (first is not { } inbound)
            {
                return; // peer left or the frame was malformed (reader already closed it)
            }

            if (inbound.Type != MsgType.ClientHello)
            {
                if (inbound.Type == MsgType.Disconnect)
                {
                    connection.Close(DisconnectCode.ClientQuit);
                    return;
                }

                Refuse(connection, new Rejection(DisconnectCode.UnexpectedMessage, $"expected ClientHello, got {inbound.Type}"));
                return;
            }

            ClientHelloT hello;
            try
            {
                hello = MessageRegistry.Default.Decode<ClientHello>(inbound.Frame).UnPack();
            }
            catch (ProtocolViolation violation)
            {
                connection.Stats.AddViolation();
                Refuse(connection, new Rejection(DisconnectCode.MalformedMessage, violation.Code.ToString()));
                return;
            }

            var outcome = await AdmitAsync(connection, reader, address, nonce, hello, linked.Token).ConfigureAwait(false);
            if (outcome.Rejection is { } rejection)
            {
                Refuse(connection, rejection);
                return;
            }

            connection.MaxInboundFrameBytes = _options.MaxFrameBytes;
            var node = outcome.Node!;
            linked.Dispose();
            await _handler.OnAdmittedAsync(node, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Refuse(connection, new Rejection(DisconnectCode.HandshakeTimeout, "handshake timed out", RetryAfterMs: 1000));
        }
    }

    private readonly record struct AdmitOutcome(AdmittedNode? Node, Rejection? Rejection);

    private async Task<AdmitOutcome> AdmitAsync(
        INodeConnection connection, NodeFrameReader reader, IPAddress? address, byte[] nonce, ClientHelloT hello, CancellationToken ct)
    {
        var playerKey = hello.PlayerKey?.ToArray() ?? [];
        if (playerKey.Length != PlayerKeyLength)
        {
            return Fail(DisconnectCode.MalformedMessage, "player_key must be 32 bytes");
        }

        // 1. Protocol
        if (hello.ProtocolMajor != ProtocolConstants.ProtocolMajor)
        {
            return Fail(DisconnectCode.ProtocolMismatch, $"protocol major {hello.ProtocolMajor} not supported",
                $"{ProtocolConstants.ProtocolMajor}.{ProtocolConstants.ProtocolMinor}");
        }

        ushort negotiatedMinor = Math.Min(hello.ProtocolMinor, ProtocolConstants.ProtocolMinor);
        reader.PeerMinor = hello.ProtocolMinor;
        var authority = _state.Authority;

        // 2. Mod version (pinned, else the authority's) and build (strict)
        string modVersion = hello.ModVersion ?? string.Empty;
        string modBuild = hello.ModBuild ?? string.Empty;
        if (!string.IsNullOrEmpty(_options.RequiredModVersion))
        {
            if (!string.Equals(modVersion, _options.RequiredModVersion, StringComparison.Ordinal))
            {
                return Fail(DisconnectCode.ModVersionMismatch, $"mod version {modVersion} is not accepted", _options.RequiredModVersion);
            }
        }
        else if (authority is not null && !string.Equals(modVersion, authority.ModVersion, StringComparison.Ordinal))
        {
            return Fail(DisconnectCode.ModVersionMismatch, $"mod version {modVersion} differs from the authority's", authority.ModVersion);
        }

        if (_options.ModBuildStrict && authority is not null && !string.Equals(modBuild, authority.ModBuild, StringComparison.Ordinal))
        {
            return Fail(DisconnectCode.ModVersionMismatch, $"mod build {modBuild} differs from the authority's", authority.ModBuild);
        }

        // 3. Game build: pinned list AND equal to the authority's (ADR-004, always on)
        string gameBuild = hello.GameBuild ?? string.Empty;
        if (!_options.SupportedGameBuilds.Contains(gameBuild, StringComparer.Ordinal))
        {
            return Fail(DisconnectCode.GameVersionMismatch, $"game build {gameBuild} is not supported", string.Join(", ", _options.SupportedGameBuilds));
        }

        if (authority is not null && !string.Equals(gameBuild, authority.GameBuild, StringComparison.Ordinal))
        {
            return Fail(DisconnectCode.GameVersionMismatch, $"game build {gameBuild} differs from the authority's", authority.GameBuild);
        }

        // 4. Extensions (admin may downgrade to a warning)
        var extensionsHash = hello.ExtensionsHash?.ToArray() ?? [];
        if (authority is not null && !authority.ExtensionsHash.AsSpan().SequenceEqual(extensionsHash))
        {
            string diff = DescribeExtensionDiff(authority.Extensions, hello.Extensions);
            if (_options.ExtensionsMismatchIsWarning)
            {
                LogExtensionsWarning(connection.Id.Value, diff);
            }
            else
            {
                return Fail(DisconnectCode.ExtensionsMismatch, "enabled extensions differ from the authority's", diff);
            }
        }

        // 5. Auth: session password and optional admin proof
        var ip = address ?? IPAddress.None;
        bool wantsAdmin = (hello.RequestedRoles & Role.Admin) != 0 || (hello.AdminProof?.Count ?? 0) > 0;
        bool adminOk = false;
        var joinHash = _state.JoinPasswordHash;
        if ((wantsAdmin || joinHash is not null) && AuthIsRateLimited(ip))
        {
            return Fail(DisconnectCode.RateLimited, "too many failed authentication attempts", retryAfterMs: 60_000);
        }

        if (wantsAdmin)
        {
            var adminHash = _state.AdminPasswordHash;
            adminOk = adminHash is not null && ProofMatches(adminHash, nonce, playerKey, hello.AdminProof);
            if (!adminOk)
            {
                return await AuthFailedAsync(ip, ct).ConfigureAwait(false);
            }
        }

        if (joinHash is not null && !adminOk && !ProofMatches(joinHash, nonce, playerKey, hello.AuthProof))
        {
            return await AuthFailedAsync(ip, ct).ConfigureAwait(false);
        }

        // 6. Ban by key hash / address
        var keyHash = SHA256.HashData(playerKey);
        var ban = await _bans.FindActiveBanAsync(keyHash, address, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (ban is not null)
        {
            return Fail(DisconnectCode.Banned, ban.Reason);
        }

        // 7. Name: shape, then binding to the first key seen with it
        string name = hello.PlayerName ?? string.Empty;
        if (!NamePattern().IsMatch(name) || name != name.Trim())
        {
            return Fail(DisconnectCode.NameTaken, "player name must be 3-24 characters: letters, digits, space, _ - .");
        }

        var bind = await _players.BindAsync(name, keyHash, address, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (bind.Status != PlayerBindStatus.Ok)
        {
            return Fail(DisconnectCode.NameTaken, "name is bound to another player", name);
        }

        if (bind.PlayerId is <= 0 or > ushort.MaxValue)
        {
            return Fail(DisconnectCode.InternalError, "player id out of range");
        }

        // 8/9. Roles and capacity, evaluated and registered atomically
        var requested = hello.RequestedRoles & (Role.Authority | Role.Client | Role.Observer);
        if (requested == 0 || (hello.RequestedRoles & ~(Role.Authority | Role.Client | Role.Observer | Role.Admin)) != 0)
        {
            return Fail(DisconnectCode.MalformedMessage, "requested_roles must name Authority, Client or Observer");
        }

        var granted = requested | (adminOk ? Role.Admin : 0);
        var welcome = BuildWelcome(connection, hello, granted, bind.PlayerId);
        var node = new AdmittedNode
        {
            Connection = connection,
            Reader = reader,
            PlayerId = bind.PlayerId,
            Name = name,
            KeyHash = keyHash,
            Roles = granted,
            Hello = hello,
            NegotiatedMinor = negotiatedMinor,
            NegotiatedCaps = hello.ClientCaps & _state.ServerCaps,
            RemoteAddress = ip,
            Welcome = welcome,
        };

        AdmittedNode? superseded;
        string registryKey = Convert.ToHexString(keyHash);
        lock (_gate)
        {
            _admitted.TryGetValue(registryKey, out superseded);

            bool needsSlot = (requested & (Role.Authority | Role.Client)) != 0;
            if (needsSlot)
            {
                int others = _admitted.Values.Count(n => n.KeyHash != superseded?.KeyHash && (n.Roles & (Role.Authority | Role.Client)) != 0);
                if (others >= _state.MaxPlayers)
                {
                    return Fail(DisconnectCode.SessionFull, "the session is full", retryAfterMs: 10_000);
                }
            }

            if ((requested & Role.Authority) != 0)
            {
                bool otherAuthority = _admitted.Values.Any(n => n.IsAuthority && n.KeyHash != superseded?.KeyHash);
                bool designated = _state.DesignatedAuthorityPlayerId == bind.PlayerId;
                if ((otherAuthority || _state.AuthorityLive) && !designated && !adminOk && superseded is not { IsAuthority: true })
                {
                    return Fail(DisconnectCode.RoleUnavailable, "the session already has an authority");
                }
            }

            _admitted[registryKey] = node;
        }

        superseded?.Connection.Close(DisconnectCode.SupersededByNewConnection, "same player key connected again");

        // Hand over: the session layer may adjust the Welcome (resume, team) or refuse.
        var verdict = await _handler.BeforeWelcomeAsync(node, ct).ConfigureAwait(false);
        if (!verdict.Accepted)
        {
            Unregister(node);
            return Fail(verdict.Code, verdict.Message ?? "not joinable", verdict.Expected);
        }

        reader.Roles = granted;
        node.Phase = NodePhase.Admitted;
        var frame = ControlFrames.Encode(MsgType.Welcome, fbb => X4MP.Proto.Welcome.Pack(fbb, node.Welcome).Value, 1024);
        connection.TrySend(frame);
        frame.Release();
        _ = connection.Completion.ContinueWith(_ => Unregister(node), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        ServerMetrics.RecordHandshakeOk();
        LogAdmitted(connection.Id.Value, name, bind.PlayerId, granted);
        return new AdmitOutcome(node, null);

        static AdmitOutcome Fail(DisconnectCode code, string message, string? expected = null, uint retryAfterMs = 0) =>
            new(null, new Rejection(code, message, expected, retryAfterMs));
    }

    private void Unregister(AdmittedNode node)
    {
        string key = Convert.ToHexString(node.KeyHash);
        lock (_gate)
        {
            if (_admitted.TryGetValue(key, out var current) && ReferenceEquals(current, node))
            {
                _admitted.Remove(key);
            }
        }
    }

    private void Refuse(INodeConnection connection, Rejection rejection)
    {
        ServerMetrics.RecordHandshakeRefused(rejection.Code);
        LogRefused(connection.Id.Value, rejection.Code, rejection.Message);
        connection.Close(rejection.Code, rejection.Message, rejection.Expected, rejection.RetryAfterMs);
    }

    private async Task<AdmitOutcome> AuthFailedAsync(IPAddress ip, CancellationToken ct)
    {
        RecordAuthFailure(ip);
        await Task.Delay(TimeSpan.FromMilliseconds(_options.AuthFailureDelayMs), _time, ct).ConfigureAwait(false);
        return new AdmitOutcome(null, new Rejection(DisconnectCode.AuthFailed, "authentication failed"));
    }

    private static bool ProofMatches(byte[] passwordHash, byte[] nonce, byte[] playerKey, List<byte>? proof)
    {
        if (proof is null || proof.Count != 32)
        {
            return false;
        }

        var expected = GatewayState.ComputeProof(passwordHash, nonce, playerKey);
        return CryptographicOperations.FixedTimeEquals(expected, proof.ToArray());
    }

    private bool AuthIsRateLimited(IPAddress ip)
    {
        lock (_gate)
        {
            if (!_authFailures.TryGetValue(ip, out var failures))
            {
                return false;
            }

            Prune(failures);
            return failures.Count >= _options.AuthFailuresPerMinute;
        }
    }

    private void RecordAuthFailure(IPAddress ip)
    {
        lock (_gate)
        {
            if (!_authFailures.TryGetValue(ip, out var failures))
            {
                _authFailures[ip] = failures = new Queue<long>();
            }

            Prune(failures);
            failures.Enqueue(_time.GetTimestamp());
        }
    }

    private void Prune(Queue<long> stamps)
    {
        while (stamps.Count > 0 && _time.GetElapsedTime(stamps.Peek()) > TimeSpan.FromMinutes(1))
        {
            stamps.Dequeue();
        }
    }

    private static string DescribeExtensionDiff(IReadOnlyList<string> authority, List<string>? node)
    {
        var mine = new HashSet<string>(node ?? [], StringComparer.Ordinal);
        var theirs = new HashSet<string>(authority, StringComparer.Ordinal);
        var missing = theirs.Except(mine).Order(StringComparer.Ordinal);
        var extra = mine.Except(theirs).Order(StringComparer.Ordinal);
        return $"missing: [{string.Join(", ", missing)}] extra: [{string.Join(", ", extra)}]";
    }

    private WelcomeT BuildWelcome(INodeConnection connection, ClientHelloT hello, Role granted, int playerId)
    {
        Span<byte> random = stackalloc byte[24];
        RandomNumberGenerator.Fill(random);
        return new WelcomeT
        {
            PlayerId = (ushort)playerId,
            GrantedRoles = granted,
            NegotiatedCaps = hello.ClientCaps & _state.ServerCaps,
            ResumeToken = new Id128T
            {
                Lo = BitConverter.ToUInt64(random[..8]),
                Hi = BitConverter.ToUInt64(random.Slice(8, 8)),
            },
            Resumed = false,
            ConnId = (uint)connection.Id.Value,
            UdpPort = 0,
            UdpToken = BitConverter.ToUInt64(random[16..]),
            ServerTimeUs = (ulong)_time.GetElapsedTime(_startTimestamp).TotalMicroseconds,
            ResumeGraceS = (ushort)Math.Clamp(_options.ResumeGraceSeconds, 0, ushort.MaxValue),
        };
    }

    private void SendServerHello(INodeConnection connection, byte[] nonce)
    {
        var authority = _state.Authority;
        var session = _state.SessionId.ToByteArray();
        var hello = new ServerHelloT
        {
            ProtocolMajor = ProtocolConstants.ProtocolMajor,
            ProtocolMinor = ProtocolConstants.ProtocolMinor,
            ServerVersion = _state.ServerVersion,
            ServerName = _state.ServerName,
            SessionId = new Id128T { Lo = BitConverter.ToUInt64(session, 0), Hi = BitConverter.ToUInt64(session, 8) },
            Nonce = [.. nonce],
            Auth = _state.JoinPasswordHash is null ? AuthMethod.None : AuthMethod.SessionPassword,
            ServerCaps = _state.ServerCaps,
            Phase = _state.Phase,
            RequiredGameBuild = authority?.GameBuild ?? string.Empty,
            SupportedGameBuilds = [.. _options.SupportedGameBuilds],
            RequiredModVersion = !string.IsNullOrEmpty(_options.RequiredModVersion) ? _options.RequiredModVersion : authority?.ModVersion ?? string.Empty,
            ExtensionsHash = authority is null ? [] : [.. authority.ExtensionsHash],
        };
        var frame = ControlFrames.Encode(MsgType.ServerHello, fbb => X4MP.Proto.ServerHello.Pack(fbb, hello).Value, 512);
        connection.TrySend(frame);
        frame.Release();
    }

    private bool TryRegister(INodeConnection connection, IPAddress? address)
    {
        lock (_gate)
        {
            if (address is not null)
            {
                _perIp.TryGetValue(address, out int count);
                if (count >= _options.MaxConnectionsPerIp)
                {
                    return false;
                }

                _perIp[address] = count + 1;
            }

            _live[connection.Id] = connection;
        }

        _ = connection.Completion.ContinueWith(
            _ => Release(connection, address), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    private void Release(INodeConnection connection, IPAddress? address)
    {
        lock (_gate)
        {
            _live.Remove(connection.Id);
            if (address is not null && _perIp.TryGetValue(address, out int count))
            {
                if (count <= 1)
                {
                    _perIp.Remove(address);
                }
                else
                {
                    _perIp[address] = count - 1;
                }
            }
        }
    }

    /// <summary>Global handshake rate limit: a token bucket refilled at HandshakesPerSecond with the same burst.</summary>
    private bool TryTakeHandshakeToken()
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            double rate = _options.HandshakesPerSecond;
            _tokens = Math.Min(rate, _tokens + _time.GetElapsedTime(_tokensStamp, now).TotalSeconds * rate);
            _tokensStamp = now;
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "conn {ConnectionId}: admitted {Name} (player {PlayerId}) as {Roles}")]
    private partial void LogAdmitted(long connectionId, string name, int playerId, Role roles);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "conn {ConnectionId}: refused with {Code}: {Reason}")]
    private partial void LogRefused(long connectionId, DisconnectCode code, string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "conn {ConnectionId}: extensions differ from the authority's (admitted anyway): {Diff}")]
    private partial void LogExtensionsWarning(long connectionId, string diff);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "conn {ConnectionId}: handshake failed")]
    private partial void LogHandshakeFailed(long connectionId, Exception ex);
}

/// <summary>
/// Admission handler used when no session layer is attached yet: accepts everyone, answers <c>Ping</c> with
/// <c>Pong</c>, ignores everything else, and ends when the connection does.
/// </summary>
public sealed class DefaultAdmissionHandler : IAdmissionHandler
{
    public ValueTask<AdmissionVerdict> BeforeWelcomeAsync(AdmittedNode node, CancellationToken ct) =>
        ValueTask.FromResult(AdmissionVerdict.Accept);

    public async Task OnAdmittedAsync(AdmittedNode node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        try
        {
            while (await node.Reader.ReadAsync(ct).ConfigureAwait(false) is { } inbound)
            {
                if (inbound.Type == MsgType.Ping)
                {
                    var ping = MessageRegistry.Default.Decode<Ping>(inbound.Frame);
                    var pong = ControlFrames.Pong(ping.Seq, ping.SendTimeUs, 0, 0);
                    node.Connection.TrySend(pong);
                    pong.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
