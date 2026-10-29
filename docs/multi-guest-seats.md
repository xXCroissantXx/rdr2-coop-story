# Multi-guest seats (experimental)

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

## Companion seats (stage 2a)

The gameplay runtime (`SidecarRuntime`) and the native bridge still replicate a
single remote player, so the `ILanSession` members address only the
**primary** guest:

- `EnvelopeReceived`, `ConnectionChanged`, `IsConnected`,
  `TryCaptureControlPeer` and token-less sends see only the primary seat.
- When the primary guest leaves, the host closes the next seated guest's
  connection. That guest reconnects into its old seat and becomes the new
  primary through the normal reconnect and resync path.

Every other seat is a **companion seat**, handled by
`Session/SecondarySeatMirror.cs` on the host:

1. On join, the host sends the seat its motion-mode announcement. A seat whose
   mode differs is sent `Goodbye`; the other seats continue.
2. Once the modes match, the seat's `ResyncRequest` gets the host's coherent
   replay (mission, cinematic, parent-first world spawns, optional AnimScene
   definition). The replay goes to that seat only and leaves the primary
   guest's entity cache and mapping alone.
3. After that, the seat gets a copy of host-authored broadcast state: the
   host player's state, animation, mount, appearance, traversal and identity;
   world entities and NPC animation; mission, objective, camera and cinematic
   state; AnimScene frames; and `Goodbye`. Reliable copies are sent under the
   runtime's control gate, right after the primary copy.

A companion therefore sees the host and the host's world and missions. It
never receives seat-specific flows: mission barriers and completions, campaign
grants, interactions, damage, dialogue cues or ambient encounters. So it
always stays companion-only for missions and keeps its own save untouched.

Limits:

- Gameplay a companion sends (its own player state, intents) is dropped and
  logged. The host and other guests do not see companions yet.
- The runtime holds host output until the primary guest's motion mode is
  agreed, so companions get the stream only while a primary guest is connected
  and agreed.

Next stage: seat-tagged player replication. This means extending
`PlayerState.Slot` (today host or guest) to seats 0-3, relaying each guest's
player to the others, and teaching the bridge to show up to three remote
players.

## Tests

`tests/CoopStory.SelfTest`, test "host seats three guests, rejects a fourth and
hands over primary": three loopback guests on one address, a fourth rejected
with `session-full`, per-seat UDP/TCP routing, targeted and broadcast sends,
primary-only legacy events, primary handover, and reuse of a freed seat.

Test "companion seats negotiate, replay and receive only host broadcasts":
three loopback guests. Checks that the primary gets no companion traffic, that
nothing streams or replays before modes agree, that a mode mismatch gets
`Goodbye` for that seat only, that replay goes to its own seat, that only
allow-listed types are copied, that a companion's gameplay is dropped, and
that a departing seat stops streaming. The runtime hooks in `SidecarRuntime`
are not covered by tests that run outside Windows, because the runtime tests
need Windows named pipes.
