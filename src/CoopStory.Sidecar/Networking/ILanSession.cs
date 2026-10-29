using System.Net;
using CoopStory.Protocol;

namespace CoopStory.Sidecar.Networking;

// Delivers a validated control or snapshot frame together with the connection generation that was current when the sidecar accepted it.
public delegate ValueTask EnvelopeReceivedHandler(
    ProtocolEnvelope envelope,
    ControlPeerToken peer,
    CancellationToken cancellationToken);

public delegate ValueTask AuthenticationRejectedHandler(
    string reason,
    CancellationToken cancellationToken);

public delegate ValueTask PeerConnectionChangedHandler(
    bool connected,
    CancellationToken cancellationToken);

public delegate ValueTask GuestSlotChangedHandler(
    ControlPeerToken peer,
    bool connected,
    CancellationToken cancellationToken);

// Capability for the currently authenticated TCP peer.
// Queued work must carry it so a reconnect cannot accidentally mutate the replacement peer's state.
// Slot names the player seat: 0 is the host (as seen by a guest), 1-3 are host-side guest seats.
public readonly record struct ControlPeerToken(
    Guid SessionInstanceId,
    ulong Generation,
    byte Slot = 0)
{
    public const byte HostSlot = 0;

    public const byte MaxGuestSlots = 3;

    public bool IsValid =>
        SessionInstanceId != Guid.Empty && Generation != 0;
}

// Shared contract for host and guest transport.
// TCP is the reliable control lane; UDP is the lossy/latest-state snapshot lane.
public interface ILanSession : IAsyncDisposable
{
    bool IsConnected { get; }

    IPAddress? RemoteAddress { get; }

    event EnvelopeReceivedHandler? EnvelopeReceived;

    event AuthenticationRejectedHandler? AuthenticationRejected;

    event PeerConnectionChangedHandler? ConnectionChanged;

    Task RunAsync(CancellationToken cancellationToken = default);

    bool TryCaptureControlPeer(out ControlPeerToken peer);

    bool IsControlPeerCurrent(ControlPeerToken peer);

    // Runs a synchronous cache change only if this is still the same peer.
    bool TryRunForControlPeer(ControlPeerToken peer, Action operation);

    ValueTask<bool> SendControlAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SendControlAsync(
        ControlPeerToken peer,
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SendSnapshotAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default);
}

// Host transport with several guest seats.
// The ILanSession members keep addressing the primary guest so the single-peer runtime is unchanged;
// these members address every seat for multi-player replication.
public interface IMultiPeerLanSession : ILanSession
{
    int MaxGuests { get; }

    IReadOnlyList<ControlPeerToken> ConnectedPeers { get; }

    // Raised for every seat, unlike ConnectionChanged which follows only the primary guest.
    event GuestSlotChangedHandler? GuestSlotChanged;

    // Raised for frames from every seat, unlike EnvelopeReceived which delivers only the primary guest's frames.
    event EnvelopeReceivedHandler? AnyPeerEnvelopeReceived;

    ValueTask<bool> SendSnapshotAsync(
        ControlPeerToken peer,
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default);

    // Returns how many seats accepted the frame.
    ValueTask<int> BroadcastControlAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default);

    ValueTask<int> BroadcastSnapshotAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        ulong tick,
        CancellationToken cancellationToken = default);
}

// Monotonic local time carried in frames for interpolation and age checks, not wall-clock time (which can jump when a computer adjusts its clock).
internal static class NetworkClock
{
    public static ulong Tick => unchecked((ulong)Environment.TickCount64);
}
