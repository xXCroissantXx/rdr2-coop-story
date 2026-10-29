# Multi-guest seats (experimental)

The host can accept up to three guests (four players in total) when
`network.maxGuests` is set to 2 or 3 in the host's sidecar configuration. The
default is 1, which keeps the two-player behaviour unchanged.

This branch uses protocol 37, so every PC in a session needs this build. It
cannot connect to the upstream protocol 36 tester release.

## Guest seats (stage 1)

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

## Seeing every player (stage 2b, protocol 37)

Player states are relayed between seats as `CompanionPlayerState` (message 51,
`[seat u8][PlayerState, 104 bytes]`, UDP):

| Sender | Its PlayerState goes to |
|---|---|
| Host | every guest, as the normal two-player PlayerState (full replica) |
| Primary guest (seat 1) | host as the full replica; companion seats as `CompanionPlayerState` |
| Companion seat (2 or 3) | host game, primary guest and the other companion, as `CompanionPlayerState` |

Nobody is sent their own state. The host rejects a relayed state with the
wrong slot or the online-mode flag.

Rules:

- A guest accepts `CompanionPlayerState` only from the host.
- No game bridge may originate one.
- The sidecar's bridge queue keeps the newest sample **per seat**, so one
  player's sample never replaces another's.

In each game, `CompanionAvatarTracker` (bridge, pure logic) turns samples into
commands for a simple per-seat avatar. `ScriptHookSdkFacade` carries them out:

- **Spawn:** the ped uses the local player's model with a random outfit. It
  can't be damaged, ignores AI events and has a friendly minimap blip.
- **Place:** warp to the spot. Used on spawn, when the avatar is more than 8 m
  off, and while the player is mounted, in a cutscene, climbing, swimming or
  not alive.
- **Walk / Run / Sprint:** a nav-mesh move to the reported position, led by
  0.35 s of velocity. The task refreshes at most every 500 ms unless the gait
  changes.
- **Idle:** turn to the reported heading when it is more than 30 degrees off.
- **Despawn:** after 5 s without a sample, on session stop and on pipe
  reconnect.

Only the primary remote player keeps the full replica (AnimGraph, actions,
mount, revive, identity). Companion avatars don't show weapons, aiming,
combat, horses, nicknames or downed state, and they can't be interacted with.

Remaining limits:

- Gameplay a companion sends, other than its player state, is dropped and
  logged. Its combat does not affect the host's world.
- The runtime holds host output until the primary guest's motion mode is
  agreed, so companions get the stream only while a primary guest is connected
  and agreed.
- The avatar natives in `ScriptHookSdkFacade.cpp` have not been compiled
  against the ScriptHookRDR2 SDK or run in the game. They reuse the native
  calls and argument shapes already used in that file.

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

Stage 2b tests:

- Managed test "companion player state wire, queue and authorization rules":
  the codec layout matches the bridge, bad seats and host slots are rejected,
  only the guest role accepts the type, and the queue keeps one sample per
  seat, newest wins.
- Managed test "host relays player states between all four seats": a real host
  transport and three guests. Checks every relay path in the table above,
  that nobody gets their own state, and that host-slot and online-mode states
  are rejected.
- Bridge tests `CompanionPlayerStateWireAndTracker` and
  `RuntimeCompanionAvatarsFollowSeatsAndExpire`: codec, gait and placement
  choice, reorder guard, refresh limits, timeout and wrap-around, plus
  per-seat avatars driven through `BridgeRuntime` with a recording facade.

On Linux the bridge self-tests can be built with g++ against a small
`Windows.h`/`bcrypt.h` shim that implements SHA-256 with OpenSSL, leaving out
`NamedPipeClient.cpp` and `Win32GameIdentity.cpp`. The shim is not part of the
repository.
