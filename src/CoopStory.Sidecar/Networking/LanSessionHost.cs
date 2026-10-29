using System.Net;
using System.Net.Sockets;
using CoopStory.Protocol;
using CoopStory.Sidecar.Configuration;
using CoopStory.Sidecar.Diagnostics;

namespace CoopStory.Sidecar.Networking;

// Listens for up to network.maxGuests authenticated guests, one per seat.
// Each seat pairs reliable TCP control with authenticated UDP snapshots and replaces its own old connection at a clear generation boundary when the same guest reconnects.
// The ILanSession members address the primary guest (the seat the single-peer runtime drives); IMultiPeerLanSession addresses every seat.
public sealed class LanSessionHost : IMultiPeerLanSession
{
    private readonly object _peerSync = new();
    // Serializes connection replacement so connection-change events cannot be emitted out of order while old loops are still unwinding.
    private readonly SemaphoreSlim _peerActivationGate = new(1, 1);
    private readonly SidecarConfig _config;
    private readonly SessionCredentials _credentials;
    private readonly JsonLineLogger _logger;
    private readonly IPAddress _listenAddress;
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly CancellationTokenSource _stop = new();
    private readonly GuestSeat?[] _seats;
    // A guest that drops and returns gets its previous seat number back when that seat is still free.
    private readonly Dictionary<Guid, byte> _lastSlotByGuest = new();
    private TcpListener? _listener;
    private UdpClient? _udp;
    private byte? _primarySlot;
    // One sequence for every seat: a datagram sent to one guest stays below the next guest's handshake floor and cannot be replayed to it.
    private int _sequence;
    private ulong _controlPeerGeneration;
    private int _udpPolicyRejections;
    private bool _disposed;

    public LanSessionHost(
        SidecarConfig config,
        SessionCredentials credentials,
        JsonLineLogger logger,
        IPAddress? listenAddress = null)
    {
        _config = (config ?? throw new ArgumentNullException(nameof(config))).Validate();
        if (_config.Role != SessionRole.Host)
        {
            throw new ArgumentException("Host service requires role Host.", nameof(config));
        }

        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _listenAddress = listenAddress ?? IPAddress.Any;
        _seats = new GuestSeat?[_config.Network.MaxGuests];
    }

    public bool IsConnected
    {
        get
        {
            lock (_peerSync)
            {
                return GetPrimarySeat() is not null;
            }
        }
    }

    public IPAddress? RemoteAddress
    {
        get
        {
            lock (_peerSync)
            {
                return GetPrimarySeat()?.Peer?.RemoteEndPoint.Address;
            }
        }
    }

    public IPAddress ListenAddress => _listenAddress;

    public int MaxGuests => _seats.Length;

    public IReadOnlyList<ControlPeerToken> ConnectedPeers
    {
        get
        {
            lock (_peerSync)
            {
                var peers = new List<ControlPeerToken>(_seats.Length);
                foreach (var seat in _seats)
                {
                    if (seat?.Peer is not null)
                    {
                        peers.Add(TokenFor(seat));
                    }
                }

                return peers;
            }
        }
    }

    public event EnvelopeReceivedHandler? EnvelopeReceived;

    public event EnvelopeReceivedHandler? AnyPeerEnvelopeReceived;

    public event AuthenticationRejectedHandler? AuthenticationRejected;

    public event PeerConnectionChangedHandler? ConnectionChanged;

    public event GuestSlotChangedHandler? GuestSlotChanged;

    public bool TryCaptureControlPeer(out ControlPeerToken peer)
    {
        lock (_peerSync)
        {
            var seat = GetPrimarySeat();
            if (seat is null)
            {
                peer = default;
                return false;
            }

            peer = TokenFor(seat);
            return true;
        }
    }

    public bool IsControlPeerCurrent(ControlPeerToken peer)
    {
        lock (_peerSync)
        {
            return GetCurrentSeat(peer) is not null;
        }
    }

    public bool TryRunForControlPeer(
        ControlPeerToken peer,
        Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_peerSync)
        {
            if (GetCurrentSeat(peer) is null)
            {
                return false;
            }

            operation();
            return true;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stop.Token);
        // Bind both transports before announcing readiness: the TCP handshake establishes identity, then UDP may begin carrying snapshots.
        _listener = new TcpListener(_listenAddress, _config.TcpPort);
        _udp = new UdpClient(new IPEndPoint(_listenAddress, _config.UdpPort));
        _listener.Start(backlog: 1 + _seats.Length);

        await _logger.InfoAsync(
            "network.host.started",
            $"Listening on {_listenAddress} TCP {_config.TcpPort} and " +
            $"UDP {_config.UdpPort}.",
            new Dictionary<string, object?>
            {
                ["listenAddress"] = _listenAddress.ToString(),
                ["listenMode"] = IPAddress.IsLoopback(_listenAddress)
                    ? "loopback-only"
                    : "lan",
                ["tcpPort"] = _config.TcpPort,
                ["udpPort"] = _config.UdpPort
            },
            cancellationToken: linked.Token).ConfigureAwait(false);

        try
        {
            // TCP accepts/reliable receive and UDP reception run independently for the lifetime of the host session.
            await Task.WhenAll(
                AcceptLoopAsync(linked.Token),
                UdpReceiveLoopAsync(linked.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            _udp.Dispose();
        }
    }

    public async ValueTask<bool> SendControlAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default)
    {
        // Capture the peer generation before scheduling work.
        // A reconnect after this point must make the send fail instead of target the newcomer.
        if (!TryCaptureControlPeer(out var peer))
        {
            return false;
        }

        return await SendControlAsync(
                peer,
                type,
                payload,
                tick,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> SendControlAsync(
        ControlPeerToken peerToken,
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default)
    {
        ControlConnection? peer;
        lock (_peerSync)
        {
            peer = GetCurrentSeat(peerToken)?.Peer;
        }

        if (peer is null)
        {
            return false;
        }

        try
        {
            await peer.SendAsync(
                CreateEnvelope(type, payload, tick),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    public ValueTask<bool> SendSnapshotAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default)
    {
        if (!TryCaptureControlPeer(out var peer))
        {
            return ValueTask.FromResult(false);
        }

        return SendSnapshotAsync(peer, type, payload, tick, cancellationToken);
    }

    public async ValueTask<bool> SendSnapshotAsync(
        ControlPeerToken peerToken,
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default)
    {
        UdpClient? udp;
        IPEndPoint? endpoint;
        lock (_peerSync)
        {
            udp = _udp;
            endpoint = GetCurrentSeat(peerToken)?.Udp?.PinnedEndpoint;
        }

        if (udp is null || endpoint is null)
        {
            return false;
        }

        // UDP is still authenticated; it is only unreliable in delivery/order, not an unauthenticated shortcut around the session secret.
        var datagram = AuthenticatedDatagramCodec.Encode(
            CreateEnvelope(type, payload, tick),
            _credentials,
            _instanceId);
        try
        {
            await udp.SendAsync(datagram, endpoint, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async ValueTask<int> BroadcastControlAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        foreach (var peer in ConnectedPeers)
        {
            if (await SendControlAsync(peer, type, payload, tick, cancellationToken)
                    .ConfigureAwait(false))
            {
                delivered++;
            }
        }

        return delivered;
    }

    public async ValueTask<int> BroadcastSnapshotAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default)
    {
        var delivered = 0;
        foreach (var peer in ConnectedPeers)
        {
            if (await SendSnapshotAsync(peer, type, payload, tick, cancellationToken)
                    .ConfigureAwait(false))
            {
                delivered++;
            }
        }

        return delivered;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        _udp?.Dispose();
        var seats = new List<GuestSeat>();
        lock (_peerSync)
        {
            foreach (var seat in _seats)
            {
                if (seat is not null)
                {
                    seats.Add(seat);
                }
            }
        }

        // Seats stay in place so each connection's own cleanup still publishes its disconnect, as a single-guest host always did.

        foreach (var seat in seats)
        {
            CancelAndDispose(seat.UdpStop);
            if (seat.Peer is not null)
            {
                await seat.Peer.DisposeAsync().ConfigureAwait(false);
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener
            ?? throw new InvalidOperationException("TCP listener is not initialized.");
        while (!cancellationToken.IsCancellationRequested)
        {
            // Do not block accepting a reconnect while the current peers run; HandlePeerSafelyAsync performs serialized replacement later.
            var client = await listener.AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            _ = HandlePeerSafelyAsync(client, cancellationToken);
        }
    }

    private async Task HandlePeerSafelyAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        await using var peer = new ControlConnection(client);
        var authenticated = false;
        var admittedGuest = Guid.Empty;
        try
        {
            // No gameplay state reaches callbacks until this proof exchange has confirmed it is an invited guest with a seat.
            var handshake = await HandshakeProtocol.AcceptGuestAsync(
                peer,
                _credentials,
                _instanceId,
                NextSequence,
                cancellationToken,
                guestInstance =>
                {
                    var rejection = TryReserveSeat(guestInstance);
                    if (rejection is null)
                    {
                        admittedGuest = guestInstance;
                    }

                    return rejection;
                }).ConfigureAwait(false);
            var guestInstance = handshake.InstanceId;
            authenticated = true;

            var guestUdpStop = new CancellationTokenSource();
            var peerToken = default(ControlPeerToken);
            await _peerActivationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                ControlConnection? replaced;
                CancellationTokenSource? replacedUdpStop;
                ControlPeerToken replacedToken;
                bool isPrimary;
                lock (_peerSync)
                {
                    var seat = FindSeat(guestInstance) ?? AllocateSeat(guestInstance);
                    if (seat is null)
                    {
                        // The guest's previous seat was freed and taken while this reconnect was authenticating.
                        guestUdpStop.Dispose();
                        throw new ProtocolException("session-full");
                    }

                    replaced = seat.Peer;
                    replacedUdpStop = seat.UdpStop;
                    replacedToken = replaced is null ? default : TokenFor(seat);
                    seat.Peer = peer;
                    // Incrementing this invalidates all send/cache work which still holds the previous capability for this seat.
                    _controlPeerGeneration = NextPeerGeneration(
                        _controlPeerGeneration);
                    seat.Generation = _controlPeerGeneration;
                    // UDP inherits identity/IP and a sequence floor from the authenticated TCP Hello before its port is pinned.
                    seat.Udp = new UdpPeerBinding(
                        peer.RemoteEndPoint.Address,
                        controlSequenceFloor: handshake.ControlSequence,
                        expectedInstanceId: guestInstance);
                    seat.UdpStop = guestUdpStop;
                    _primarySlot ??= seat.Slot;
                    isPrimary = _primarySlot == seat.Slot;
                    peerToken = TokenFor(seat);
                }

                CancelAndDispose(replacedUdpStop);
                if (replaced is not null && !ReferenceEquals(replaced, peer))
                {
                    await replaced.DisposeAsync().ConfigureAwait(false);
                    // Replacement is a real authority boundary even though the old receive loop must not emit a second stale disconnect.
                    // Runtime releases restraint state before negotiating the freshly installed peer generation.
                    await RaiseSeatChangedAsync(
                        replacedToken,
                        connected: false,
                        isPrimary,
                        CancellationToken.None).ConfigureAwait(false);
                }

                await _logger.InfoAsync(
                    "network.guest.connected",
                    $"Authenticated guest {guestInstance:N} in seat {peerToken.Slot}.",
                    new Dictionary<string, object?>
                    {
                        ["guestInstanceId"] = guestInstance.ToString("N"),
                        ["slot"] = peerToken.Slot,
                        ["primary"] = isPrimary
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await RaiseSeatChangedAsync(
                    peerToken,
                    connected: true,
                    isPrimary,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _peerActivationGate.Release();
            }
            await RunConnectedPeerAsync(
                    peer,
                    peerToken,
                    guestUdpStop.Token,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or
                         SocketException or
                         ProtocolException or
                         TimeoutException or
                         ObjectDisposedException)
        {
            await _logger.WarningAsync(
                authenticated
                    ? "network.guest.connection-fault"
                    : "network.guest.rejected",
                exception.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (!authenticated && exception is ProtocolException)
            {
                await RaiseAuthenticationRejectedAsync(
                    exception.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await _peerActivationGate.WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                GuestSeat? vacated = null;
                ControlPeerToken vacatedToken = default;
                var wasPrimary = false;
                ControlConnection? handover = null;
                lock (_peerSync)
                {
                    var seat = FindSeat(peer);
                    if (seat is not null)
                    {
                        vacated = seat;
                        vacatedToken = TokenFor(seat);
                        _seats[seat.Slot - 1] = null;
                        wasPrimary = _primarySlot == seat.Slot;
                        if (wasPrimary)
                        {
                            _primarySlot = null;
                            handover = FindHandoverCandidate()?.Peer;
                        }
                    }
                    else if (admittedGuest != Guid.Empty &&
                             FindSeat(admittedGuest) is { Peer: null } reservation)
                    {
                        // The handshake reserved a seat but never activated it.
                        _seats[reservation.Slot - 1] = null;
                    }
                }

                if (vacated is not null)
                {
                    CancelAndDispose(vacated.UdpStop);
                    await RaiseSeatChangedAsync(
                        vacatedToken,
                        connected: false,
                        wasPrimary,
                        CancellationToken.None).ConfigureAwait(false);
                    await _logger.InfoAsync(
                        "network.guest.disconnected",
                        $"Authenticated guest in seat {vacatedToken.Slot} disconnected.",
                        new Dictionary<string, object?>
                        {
                            ["slot"] = vacatedToken.Slot,
                            ["primary"] = wasPrimary
                        },
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }

                if (handover is not null)
                {
                    // The runtime only drives the primary seat, so hand it over through a normal reconnect:
                    // the waiting guest re-handshakes, runs its local resync and arrives as a fresh primary.
                    await _logger.InfoAsync(
                        "network.guest.primary-handover",
                        "Primary guest left; asking the next seated guest to reconnect as primary.",
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    await handover.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _peerActivationGate.Release();
            }
        }
    }

    private async Task RunConnectedPeerAsync(
        ControlConnection peer,
        ControlPeerToken peerToken,
        CancellationToken peerCancellationToken,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            peerCancellationToken);
        // A failed reliable read, heartbeat send, or timeout tears down this peer together; otherwise half-dead connections linger indefinitely.
        var receive = ReceiveControlLoopAsync(
            peer,
            peerToken,
            linked.Token);
        var heartbeat = HeartbeatLoopAsync(peer, linked.Token);
        var timeout = TimeoutLoopAsync(peer, linked.Token);
        await Task.WhenAny(receive, heartbeat, timeout).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        await ObserveCompletionAsync(receive, heartbeat, timeout).ConfigureAwait(false);
    }

    private async Task ReceiveControlLoopAsync(
        ControlConnection peer,
        ControlPeerToken peerToken,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await peer.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                return;
            }

            // An old receive loop cannot deliver frames after the same guest has reconnected and taken over its seat.
            if (!IsControlPeerCurrent(peerToken))
            {
                return;
            }

            await RaiseEnvelopeAsync(
                    envelope,
                    peerToken,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(
        ControlConnection peer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(_config.Network.HeartbeatIntervalMs));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            // Heartbeats make an otherwise idle TCP connection observable to the timeout loop and carry a current monotonic tick.
            var heartbeat = new HeartbeatPayload(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                NetworkClock.Tick);
            await peer.SendAsync(
                CreateEnvelope(
                    MessageType.Heartbeat,
                    PayloadJson.Serialize(heartbeat),
                    NetworkClock.Tick),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TimeoutLoopAsync(
        ControlConnection peer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(_config.Network.HeartbeatIntervalMs));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            // Lack of any valid TCP frame for this window means reconnect/resync is safer than continuing to apply a stale remote world.
            if (Environment.TickCount64 - peer.LastReceivedTimestamp >
                _config.Network.HeartbeatTimeoutMs)
            {
                throw new TimeoutException("Guest heartbeat timed out.");
            }
        }
    }

    private async Task UdpReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var udp = _udp ?? throw new InvalidOperationException("UDP socket is not initialized.");
        while (!cancellationToken.IsCancellationRequested)
        {
            var received = await udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var candidates = new List<(GuestSeat Seat, UdpPeerBinding Binding)>(_seats.Length);
            var anySeatBound = false;
            lock (_peerSync)
            {
                foreach (var seat in _seats)
                {
                    if (seat?.Udp is not { } binding)
                    {
                        continue;
                    }

                    anySeatBound = true;
                    if (binding.IsSourceAllowed(received.RemoteEndPoint))
                    {
                        candidates.Add((seat, binding));
                    }
                }
            }

            // Reject UDP before TCP authentication, even if its HMAC would be valid, because there is no active peer to associate it with.
            if (!anySeatBound)
            {
                await LogUdpPolicyRejectionAsync(
                    "no-authenticated-tcp-peer",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (candidates.Count == 0)
            {
                await LogUdpPolicyRejectionAsync(
                    "tcp-source-or-pinned-endpoint",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Several guests can share one address (one PC, or a NAT); the sender's instance ID inside the HMAC decides the seat.
            ProtocolEnvelope? envelope = null;
            GuestSeat? sender = null;
            UdpPeerBinding? senderBinding = null;
            foreach (var (seat, binding) in candidates)
            {
                try
                {
                    var expectedGuestInstanceId = binding.ExpectedInstanceId
                        ?? throw new ProtocolException(
                            "UDP binding is not associated with an authenticated TCP instance.");
                    // Verify the HMAC and expected remote instance before parsing an untrusted datagram into a protocol envelope.
                    envelope = AuthenticatedDatagramCodec.Decode(
                        received.Buffer,
                        _credentials,
                        expectedGuestInstanceId);
                    sender = seat;
                    senderBinding = binding;
                    break;
                }
                catch (ProtocolException)
                {
                }
            }

            if (envelope is null || sender is null || senderBinding is null)
            {
                await LogUdpPolicyRejectionAsync(
                    "authentication-or-frame",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            CancellationToken peerCancellation;
            ControlPeerToken peerToken;
            string rejectionReason;
            var accepted = false;
            lock (_peerSync)
            {
                // Recheck every binding under the lock: TCP could reconnect after the asynchronous HMAC check but before dispatch.
                if (ReferenceEquals(_seats[sender.Slot - 1], sender) &&
                    ReferenceEquals(sender.Udp, senderBinding) &&
                    sender.Peer is not null &&
                    sender.UdpStop is not null &&
                    senderBinding.TryAccept(
                        received.RemoteEndPoint,
                        envelope,
                        out rejectionReason))
                {
                    peerCancellation = sender.UdpStop.Token;
                    peerToken = TokenFor(sender);
                    accepted = true;
                }
                else
                {
                    peerCancellation = new CancellationToken(canceled: true);
                    peerToken = default;
                    rejectionReason = ReferenceEquals(sender.Udp, senderBinding)
                        ? "sequence-or-message-type"
                        : "stale-tcp-peer";
                }
            }

            if (!accepted)
            {
                await LogUdpPolicyRejectionAsync(
                    rejectionReason,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RaiseEnvelopeAsync(
                        envelope,
                        peerToken,
                        peerCancellation)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                peerCancellation.IsCancellationRequested)
            {
                // The authenticated TCP peer was replaced while this datagram was being dispatched.
            }
        }
    }

    private async ValueTask LogUdpPolicyRejectionAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        // Log the first failures and then powers of two to expose a sustained bad network/policy condition without flooding the diagnostics file.
        var count = Interlocked.Increment(ref _udpPolicyRejections);
        if (count > 3 && (count & (count - 1)) != 0)
        {
            return;
        }

        await _logger.WarningAsync(
            "network.udp.policy-rejected",
            $"Dropped UDP datagram ({reason}); rejection count {count}.",
            new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["rejectionCount"] = count
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        source.Cancel();
        source.Dispose();
    }

    private async ValueTask RaiseEnvelopeAsync(
        ProtocolEnvelope envelope,
        ControlPeerToken peer,
        CancellationToken cancellationToken)
    {
        if (!IsControlPeerCurrent(peer))
        {
            return;
        }

        if (AnyPeerEnvelopeReceived is { } anyHandler)
        {
            await anyHandler(envelope, peer, cancellationToken)
                .ConfigureAwait(false);
        }

        bool isPrimary;
        lock (_peerSync)
        {
            isPrimary = _primarySlot == peer.Slot;
        }

        // The single-peer runtime sees only the primary seat so other guests cannot drive its one remote player.
        if (isPrimary &&
            EnvelopeReceived is { } handler &&
            IsControlPeerCurrent(peer))
        {
            await handler(envelope, peer, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask RaiseAuthenticationRejectedAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (AuthenticationRejected is not { } handlers)
        {
            return;
        }

        foreach (AuthenticationRejectedHandler handler in handlers.GetInvocationList())
        {
            await handler(reason, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RaiseSeatChangedAsync(
        ControlPeerToken peer,
        bool connected,
        bool primary,
        CancellationToken cancellationToken)
    {
        if (GuestSlotChanged is { } seatHandlers)
        {
            foreach (GuestSlotChangedHandler handler in seatHandlers.GetInvocationList())
            {
                await handler(peer, connected, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!primary || ConnectionChanged is not { } handlers)
        {
            return;
        }

        foreach (PeerConnectionChangedHandler handler in handlers.GetInvocationList())
        {
            await handler(connected, cancellationToken).ConfigureAwait(false);
        }
    }

    // Admission during the handshake: a returning guest keeps its seat, a new guest reserves a free one, otherwise the session is full.
    private string? TryReserveSeat(Guid guestInstance)
    {
        lock (_peerSync)
        {
            if (FindSeat(guestInstance) is not null)
            {
                return null;
            }

            return AllocateSeat(guestInstance) is null
                ? "session-full"
                : null;
        }
    }

    // Caller holds _peerSync.
    private GuestSeat? AllocateSeat(Guid guestInstance)
    {
        var index = -1;
        if (_lastSlotByGuest.TryGetValue(guestInstance, out var previous) &&
            previous <= _seats.Length &&
            _seats[previous - 1] is null)
        {
            index = previous - 1;
        }
        else
        {
            index = Array.IndexOf(_seats, null);
        }

        if (index < 0)
        {
            return null;
        }

        var seat = new GuestSeat((byte)(index + 1), guestInstance);
        _seats[index] = seat;
        // Every guest sidecar restart is a new instance ID; keep the remembered seats bounded.
        if (_lastSlotByGuest.Count >= 64)
        {
            _lastSlotByGuest.Clear();
        }

        _lastSlotByGuest[guestInstance] = seat.Slot;
        return seat;
    }

    // Caller holds _peerSync.
    private GuestSeat? FindSeat(Guid guestInstance)
    {
        foreach (var seat in _seats)
        {
            if (seat is not null && seat.GuestInstanceId == guestInstance)
            {
                return seat;
            }
        }

        return null;
    }

    // Caller holds _peerSync.
    private GuestSeat? FindSeat(ControlConnection peer)
    {
        foreach (var seat in _seats)
        {
            if (seat is not null && ReferenceEquals(seat.Peer, peer))
            {
                return seat;
            }
        }

        return null;
    }

    // Caller holds _peerSync.
    private GuestSeat? FindHandoverCandidate()
    {
        foreach (var seat in _seats)
        {
            if (seat?.Peer is not null)
            {
                return seat;
            }
        }

        return null;
    }

    // Caller holds _peerSync.
    private GuestSeat? GetPrimarySeat() =>
        _primarySlot is { } slot && _seats[slot - 1] is { Peer: not null } seat
            ? seat
            : null;

    // Caller holds _peerSync.
    private GuestSeat? GetCurrentSeat(ControlPeerToken peer)
    {
        if (!peer.IsValid ||
            peer.SessionInstanceId != _instanceId ||
            peer.Slot < 1 ||
            peer.Slot > _seats.Length)
        {
            return null;
        }

        var seat = _seats[peer.Slot - 1];
        return seat?.Peer is not null && seat.Generation == peer.Generation
            ? seat
            : null;
    }

    private ControlPeerToken TokenFor(GuestSeat seat) =>
        new(_instanceId, seat.Generation, seat.Slot);

    private ProtocolEnvelope CreateEnvelope(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick) =>
        new(type, NextSequence(), tick, payload);

    private uint NextSequence() => unchecked((uint)Interlocked.Increment(ref _sequence));

    private static ulong NextPeerGeneration(ulong generation) =>
        generation == ulong.MaxValue ? 1 : generation + 1;

    private static async Task ObserveCompletionAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

// One guest seat. Mutated only under LanSessionHost._peerSync.
// A seat with no Peer is reserved by a guest whose handshake is still finishing.
internal sealed class GuestSeat(byte slot, Guid guestInstanceId)
{
    public byte Slot { get; } = slot;

    public Guid GuestInstanceId { get; } = guestInstanceId;

    public ControlConnection? Peer { get; set; }

    public ulong Generation { get; set; }

    public UdpPeerBinding? Udp { get; set; }

    public CancellationTokenSource? UdpStop { get; set; }
}
