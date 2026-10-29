# Multi-guest seats (experimental, transport only)

The host can accept up to three guests (four players in total) when
`network.maxGuests` is set to 2 or 3 in the host's sidecar configuration. The
default is 1, which keeps the two-player behaviour unchanged.

## What this stage does

- `LanSessionHost` keeps one seat per guest (seats 1-3). Each seat has its own
  TCP connection, connection generation, UDP binding, sequence floor and
  pinned endpoint.
- Seat admission runs inside the handshake, after the guest's proof is valid.
  A returning guest (same sidecar instance ID) replaces its own connection. A
  new guest reserves a free seat, preferring the seat it held before. A guest
  arriving at a full session receives `HelloAck` with reason `session-full`.
- Guests that share one address (one PC, a NAT) are told apart by the sender
  instance ID that the UDP HMAC authenticates, never by port alone.
- One host sequence counter is shared by all seats, so a datagram sent to one
  guest stays below the next guest's handshake floor and cannot be replayed to
  it. Guests therefore see sequence gaps that are not packet loss.
- `ControlPeerToken` carries the seat number (`Slot`).
- `IMultiPeerLanSession` adds `ConnectedPeers`, `GuestSlotChanged`,
  `AnyPeerEnvelopeReceived`, per-seat `SendSnapshotAsync`, and
  `BroadcastControlAsync` / `BroadcastSnapshotAsync`.

## What it does not do yet

The gameplay runtime (`SidecarRuntime`) and the native bridge still replicate a
single remote player. To keep them correct, the `ILanSession` members address
only the **primary** guest:

- `EnvelopeReceived`, `ConnectionChanged`, `IsConnected`,
  `TryCaptureControlPeer` and token-less sends see only the primary seat.
- Extra guests stay connected and authenticated but receive no gameplay
  traffic.
- When the primary guest leaves, the host closes the next seated guest's
  connection. That guest reconnects into its old seat and becomes the new
  primary through the normal reconnect and resync path.

Per-seat replication (each guest seeing the host and every other guest, with
mission, dialogue and world state for all seats) is the next stage.

## Tests

`tests/CoopStory.SelfTest`, test "host seats three guests, rejects a fourth and
hands over primary": three loopback guests on one address, a fourth rejected
with `session-full`, per-seat UDP/TCP routing, targeted and broadcast sends,
primary-only legacy events, primary handover, and reuse of a freed seat.
