#pragma once

#include "coopstory/bridge/FrameCodec.hpp"

#include <array>
#include <cstdint>
#include <optional>
#include <span>
#include <vector>

namespace coopstory::bridge {

// Players beyond the two-player replica (sessions with three or four players).
// The host relays each extra guest's PlayerState tagged with its transport seat; every game shows those players
// as simple companion avatars driven by position, velocity and heading. The full replica (AnimGraph, actions,
// mount, revive) stays reserved for the one remote player of the original two-player design.
inline constexpr std::uint8_t kMinCompanionSeat = 1U;
inline constexpr std::uint8_t kMaxCompanionSeat = 3U;
inline constexpr std::size_t kCompanionPlayerStatePayloadSize =
    1U + kPlayerStatePayloadSize;
// Matches the two-player replica despawn timeout.
inline constexpr std::uint64_t kCompanionAvatarTimeoutMilliseconds = 5'000U;
// Farther than this from its target, an avatar is placed instead of walking there.
inline constexpr float kCompanionAvatarPlaceDistanceMeters = 8.0F;
inline constexpr std::uint64_t kCompanionAvatarTaskRefreshMilliseconds = 500U;
inline constexpr float kCompanionAvatarRetargetMeters = 1.5F;
inline constexpr float kCompanionAvatarHeadingToleranceDegrees = 30.0F;
// Aim the movement task slightly ahead so the avatar is not always one network delay behind.
inline constexpr float kCompanionAvatarLeadSeconds = 0.35F;

struct CompanionPlayerStatePayload final {
    std::uint8_t seat{kMinCompanionSeat};
    PlayerStatePayload state{};
};

[[nodiscard]] std::vector<std::uint8_t> EncodeCompanionPlayerState(
    const CompanionPlayerStatePayload& payload);
[[nodiscard]] std::optional<CompanionPlayerStatePayload>
DecodeCompanionPlayerState(std::span<const std::uint8_t> bytes);

enum class CompanionMotion : std::uint8_t {
    // Spawn or warp to the target and stand there.
    Place = 0,
    Idle = 1,
    Walk = 2,
    Run = 3,
    Sprint = 4,
};

struct CompanionAvatarCommand final {
    std::uint8_t seat{kMinCompanionSeat};
    Vec3 target{};
    float heading{};
    CompanionMotion motion{CompanionMotion::Place};
};

// Decides, per seat, what the game should do with each companion sample. Pure logic: no natives.
class CompanionAvatarTracker final {
public:
    // avatarPosition is the avatar's current game position, or nullopt when it does not exist.
    // Returns nullopt when the sample is stale or the avatar is already doing the right thing.
    [[nodiscard]] std::optional<CompanionAvatarCommand> Observe(
        const CompanionPlayerStatePayload& payload,
        std::uint32_t sequence,
        std::uint64_t nowMs,
        const std::optional<Vec3>& avatarPosition);

    // The game failed to apply the last command; the next sample re-places the avatar.
    void MarkLost(std::uint8_t seat) noexcept;

    // Seats with no sample for the timeout. They are forgotten and must be despawned.
    [[nodiscard]] std::vector<std::uint8_t> Expire(std::uint64_t nowMs);

    // Forgets every seat and returns the ones that must be despawned.
    [[nodiscard]] std::vector<std::uint8_t> Clear();

    [[nodiscard]] std::vector<std::uint8_t> ActiveSeats() const;

private:
    struct SeatState final {
        bool active{};
        std::uint32_t lastSequence{};
        std::uint64_t lastSeenMs{};
        std::uint64_t lastTaskMs{};
        bool hasTask{};
        CompanionMotion lastMotion{CompanionMotion::Place};
        Vec3 lastTarget{};
        float lastHeading{};
    };

    std::array<SeatState, kMaxCompanionSeat> seats_{};
};

[[nodiscard]] CompanionMotion SelectCompanionMotion(
    const PlayerStatePayload& state) noexcept;

// Native move-blend speed for a movement task: the same walk/run/sprint values the two-player replica uses.
[[nodiscard]] float CompanionMoveSpeed(CompanionMotion motion) noexcept;

}  // namespace coopstory::bridge
