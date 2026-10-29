using CoopStory.Protocol;
using CoopStory.Sidecar.Diagnostics;
using CoopStory.Sidecar.Networking;

namespace CoopStory.Sidecar.Session;

// Host-only companion stream for guest seats other than the primary one.
// The single-peer runtime keeps driving the primary guest; every other seated guest negotiates its motion mode here,
// receives the host's coherent replay on its ResyncRequest, and then receives a copy of the host-authored broadcast state.
// Seat-specific flows (mission barriers and completions, campaign grants, interactions, damage) are never copied,
// so these guests stay companion-only, the same fallback a non-matching primary guest already uses.
// Gameplay sent by these guests is dropped until per-seat player replication exists.
internal sealed class SecondarySeatMirror
{
    private readonly object _sync = new();
    private readonly IMultiPeerLanSession _network;
    private readonly MotionReplicationWireMode _localMode;
    private readonly Func<ControlPeerToken, CancellationToken, ValueTask<bool>> _replayToSeat;
    private readonly JsonLineLogger _logger;
    // Seats that are connected and not primary; true once their motion mode matched.
    private readonly Dictionary<byte, (ControlPeerToken Peer, bool Streaming)> _seats = new();
    private uint _announcementRevision;
    private int _droppedGuestFrames;

    public SecondarySeatMirror(
        IMultiPeerLanSession network,
        MotionReplicationWireMode localMode,
        Func<ControlPeerToken, CancellationToken, ValueTask<bool>> replayToSeat,
        JsonLineLogger logger)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _localMode = localMode;
        _replayToSeat = replayToSeat ?? throw new ArgumentNullException(nameof(replayToSeat));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<byte> StreamingSlots
    {
        get
        {
            lock (_sync)
            {
                return _seats
                    .Where(static seat => seat.Value.Streaming)
                    .Select(static seat => seat.Key)
                    .Order()
                    .ToArray();
            }
        }
    }

    // Host-authored state that means the same thing to every guest.
    // Anything addressed to one guest's save, inventory, mission instance or body stays primary-only.
    public static bool IsMirroredType(MessageType type) =>
        type is MessageType.PlayerState or
            MessageType.PlayerAnimationState or
            MessageType.PlayerMountState or
            MessageType.PlayerAppearanceState or
            MessageType.PlayerTraversal or
            MessageType.PlayerIdentity or
            MessageType.EntitySpawn or
            MessageType.EntityUpdate or
            MessageType.EntityDespawn or
            MessageType.NpcAnimation or
            MessageType.MissionState or
            MessageType.MissionObjective or
            MessageType.MissionCameraState or
            MessageType.MissionCinematicState or
            MessageType.AnimSceneDefinition or
            MessageType.AnimSceneControl or
            MessageType.AnimSceneReplicaState or
            MessageType.Goodbye;

    public async ValueTask OnSeatChangedAsync(
        ControlPeerToken peer,
        bool connected,
        CancellationToken cancellationToken)
    {
        if (!connected)
        {
            lock (_sync)
            {
                if (_seats.TryGetValue(peer.Slot, out var known) && known.Peer == peer)
                {
                    _seats.Remove(peer.Slot);
                }
            }

            return;
        }

        if (IsPrimary(peer))
        {
            return;
        }

        uint revision;
        lock (_sync)
        {
            _seats[peer.Slot] = (peer, false);
            revision = ++_announcementRevision;
        }

        // Same announcement the runtime sends its primary guest; the guest streams nothing until both modes match.
        var announcement = new MotionReplicationConfigPayload(
            AnimationReplicationPayloadCodec.MotionReplicationConfigSchemaVersion,
            _localMode,
            MotionReplicationConfigFlags.None,
            revision);
        var delivered = await _network.SendControlAsync(
                peer,
                MessageType.MotionReplicationConfig,
                AnimationReplicationPayloadCodec.EncodeMotionReplicationConfig(announcement),
                NetworkClock.Tick,
                cancellationToken)
            .ConfigureAwait(false);
        await _logger.InfoAsync(
            "network.seat.companion-joined",
            $"Guest seat {peer.Slot} joined as a companion; announced the host motion mode.",
            new Dictionary<string, object?>
            {
                ["slot"] = peer.Slot,
                ["announced"] = delivered
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OnEnvelopeAsync(
        ProtocolEnvelope envelope,
        ControlPeerToken peer,
        CancellationToken cancellationToken)
    {
        bool known;
        bool streaming;
        lock (_sync)
        {
            known = _seats.TryGetValue(peer.Slot, out var seat) && seat.Peer == peer;
            streaming = known && seat.Streaming;
        }

        if (!known || envelope.Type == MessageType.Heartbeat)
        {
            return;
        }

        if (envelope.Type == MessageType.MotionReplicationConfig)
        {
            await HandleAnnouncementAsync(envelope, peer, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (envelope.Type == MessageType.Goodbye)
        {
            return;
        }

        if (envelope.Type == MessageType.ResyncRequest && streaming)
        {
            var completed = await _replayToSeat(peer, cancellationToken).ConfigureAwait(false);
            await _logger.InfoAsync(
                "network.seat.resync-replay",
                $"Replayed the host cache to guest seat {peer.Slot}.",
                new Dictionary<string, object?>
                {
                    ["slot"] = peer.Slot,
                    ["complete"] = completed
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Log the first drops and then powers of two, like the transport's UDP policy log.
        var count = Interlocked.Increment(ref _droppedGuestFrames);
        if (count <= 3 || (count & (count - 1)) == 0)
        {
            await _logger.InfoAsync(
                "network.seat.companion-frame-dropped",
                $"Dropped {envelope.Type} from companion seat {peer.Slot}; per-seat player replication is not implemented yet.",
                new Dictionary<string, object?>
                {
                    ["slot"] = peer.Slot,
                    ["messageType"] = envelope.Type.ToString(),
                    ["dropCount"] = count
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    // Callers send the primary copy first and hold the runtime's control gate, so a seat replay cannot interleave with these frames.
    public async ValueTask<int> MirrorControlAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken)
    {
        if (!IsMirroredType(type))
        {
            return 0;
        }

        var delivered = 0;
        foreach (var peer in CaptureStreamingPeers())
        {
            if (await _network.SendControlAsync(peer, type, payload, tick, cancellationToken)
                    .ConfigureAwait(false))
            {
                delivered++;
            }
        }

        return delivered;
    }

    public async ValueTask<int> MirrorSnapshotAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken)
    {
        if (!IsMirroredType(type))
        {
            return 0;
        }

        var delivered = 0;
        foreach (var peer in CaptureStreamingPeers())
        {
            if (await _network.SendSnapshotAsync(peer, type, payload, tick, cancellationToken)
                    .ConfigureAwait(false))
            {
                delivered++;
            }
        }

        return delivered;
    }

    private async ValueTask HandleAnnouncementAsync(
        ProtocolEnvelope envelope,
        ControlPeerToken peer,
        CancellationToken cancellationToken)
    {
        string? failure = null;
        try
        {
            var announcement = AnimationReplicationPayloadCodec.DecodeMotionReplicationConfig(
                envelope.Payload.Span);
            if (announcement.Mode != _localMode)
            {
                failure = $"Motion-mode mismatch: local {_localMode}, peer {announcement.Mode}.";
            }
            else if (announcement.Flags != MotionReplicationConfigFlags.None)
            {
                failure = "Peer requested a motion-mode fallback, but this build keeps both engines separate.";
            }
        }
        catch (ProtocolException exception)
        {
            failure = $"Invalid motion-mode announcement: {exception.Message}";
        }

        if (failure is not null)
        {
            lock (_sync)
            {
                _seats.Remove(peer.Slot);
            }

            await _logger.WarningAsync(
                "network.seat.motion-mode-failed",
                $"Guest seat {peer.Slot}: {failure}",
                new Dictionary<string, object?> { ["slot"] = peer.Slot },
                cancellationToken).ConfigureAwait(false);
            // Stop only that guest's session; the primary guest and other seats continue.
            _ = await _network.SendControlAsync(
                    peer,
                    MessageType.Goodbye,
                    System.Text.Encoding.UTF8.GetBytes(failure),
                    NetworkClock.Tick,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            if (!_seats.TryGetValue(peer.Slot, out var seat) || seat.Peer != peer)
            {
                return;
            }

            _seats[peer.Slot] = (peer, true);
        }

        await _logger.InfoAsync(
            "network.seat.motion-mode-negotiated",
            $"Guest seat {peer.Slot} matched the host motion mode; companion streaming enabled.",
            new Dictionary<string, object?> { ["slot"] = peer.Slot },
            cancellationToken).ConfigureAwait(false);
    }

    private ControlPeerToken[] CaptureStreamingPeers()
    {
        lock (_sync)
        {
            return _seats.Values
                .Where(static seat => seat.Streaming)
                .Select(static seat => seat.Peer)
                .ToArray();
        }
    }

    private bool IsPrimary(ControlPeerToken peer) =>
        _network.TryCaptureControlPeer(out var primary) && primary == peer;
}
