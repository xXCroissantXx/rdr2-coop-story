#include "coopstory/bridge/CompanionAvatars.hpp"

#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace coopstory::bridge {
namespace {
[[nodiscard]] bool IsValidSeat(const std::uint8_t seat) noexcept {
    return seat >= kMinCompanionSeat && seat <= kMaxCompanionSeat;
}

[[nodiscard]] float HorizontalSpeed(const Vec3& velocity) noexcept {
    return std::sqrt((velocity.x * velocity.x) + (velocity.y * velocity.y));
}

[[nodiscard]] float HeadingDifferenceDegrees(
    const float lhs,
    const float rhs) noexcept {
    auto difference = std::fmod(std::abs(lhs - rhs), 360.0F);
    if (difference > 180.0F) {
        difference = 360.0F - difference;
    }
    return difference;
}

// Wrap-aware: true when candidate was sent after previous.
[[nodiscard]] bool IsNewerSequence(
    const std::uint32_t candidate,
    const std::uint32_t previous) noexcept {
    return candidate != previous &&
           static_cast<std::int32_t>(candidate - previous) > 0;
}
}  // namespace

std::vector<std::uint8_t> EncodeCompanionPlayerState(
    const CompanionPlayerStatePayload& payload) {
    if (!IsValidSeat(payload.seat) ||
        payload.state.slot != PlayerSlot::Guest) {
        throw std::invalid_argument("invalid CompanionPlayerState");
    }
    auto state = EncodePlayerState(payload.state);
    std::vector<std::uint8_t> bytes;
    bytes.reserve(kCompanionPlayerStatePayloadSize);
    bytes.push_back(payload.seat);
    bytes.insert(bytes.end(), state.begin(), state.end());
    return bytes;
}

std::optional<CompanionPlayerStatePayload> DecodeCompanionPlayerState(
    const std::span<const std::uint8_t> bytes) {
    if (bytes.size() != kCompanionPlayerStatePayloadSize ||
        !IsValidSeat(bytes[0])) {
        return std::nullopt;
    }
    const auto state = DecodePlayerState(bytes.subspan(1U));
    if (!state.has_value() || state->slot != PlayerSlot::Guest) {
        return std::nullopt;
    }
    return CompanionPlayerStatePayload{bytes[0], *state};
}

CompanionMotion SelectCompanionMotion(
    const PlayerStatePayload& state) noexcept {
    // A simple avatar cannot ride, climb or play a cutscene; it holds the reported spot instead of guessing.
    constexpr auto kPlaceFlags =
        static_cast<std::uint32_t>(PlayerStateFlag::Mounted) |
        static_cast<std::uint32_t>(PlayerStateFlag::InCutscene) |
        static_cast<std::uint32_t>(PlayerStateFlag::Climbing) |
        static_cast<std::uint32_t>(PlayerStateFlag::Swimming);
    if (state.lifecycle != PlayerLifecycle::Alive ||
        (state.flags & kPlaceFlags) != 0U) {
        return CompanionMotion::Place;
    }
    const auto speed = HorizontalSpeed(state.velocity);
    if (speed < 0.25F) {
        return CompanionMotion::Idle;
    }
    if (speed < 2.2F) {
        return CompanionMotion::Walk;
    }
    if (speed < 5.0F) {
        return CompanionMotion::Run;
    }
    return CompanionMotion::Sprint;
}

float CompanionMoveSpeed(const CompanionMotion motion) noexcept {
    switch (motion) {
        case CompanionMotion::Walk:
            return 1.0F;
        case CompanionMotion::Run:
            return 2.0F;
        case CompanionMotion::Sprint:
            return 3.0F;
        case CompanionMotion::Place:
        case CompanionMotion::Idle:
            break;
    }
    return 0.0F;
}

std::optional<CompanionAvatarCommand> CompanionAvatarTracker::Observe(
    const CompanionPlayerStatePayload& payload,
    const std::uint32_t sequence,
    const std::uint64_t nowMs,
    const std::optional<Vec3>& avatarPosition) {
    if (!IsValidSeat(payload.seat)) {
        return std::nullopt;
    }
    auto& seat = seats_[payload.seat - 1U];
    if (seat.active && !IsNewerSequence(sequence, seat.lastSequence)) {
        return std::nullopt;
    }
    const auto& state = payload.state;
    seat.active = true;
    seat.lastSequence = sequence;
    seat.lastSeenMs = nowMs;

    auto motion = SelectCompanionMotion(state);
    auto target = state.position;
    if (motion != CompanionMotion::Place && motion != CompanionMotion::Idle) {
        target.x += state.velocity.x * kCompanionAvatarLeadSeconds;
        target.y += state.velocity.y * kCompanionAvatarLeadSeconds;
    }
    const bool needsPlacement =
        !avatarPosition.has_value() || !seat.hasTask ||
        Distance(*avatarPosition, state.position) >
            kCompanionAvatarPlaceDistanceMeters;
    if (needsPlacement) {
        motion = CompanionMotion::Place;
        target = state.position;
    }

    // A placed avatar is already standing, so standing still after a placement only matters for a turn.
    const bool stillStanding =
        motion == CompanionMotion::Idle &&
        (seat.lastMotion == CompanionMotion::Idle ||
         seat.lastMotion == CompanionMotion::Place);
    const bool refresh =
        motion == CompanionMotion::Place ||
        (motion != seat.lastMotion && !stillStanding) ||
        (motion == CompanionMotion::Idle
             ? HeadingDifferenceDegrees(state.heading, seat.lastHeading) >
                   kCompanionAvatarHeadingToleranceDegrees
             : nowMs - seat.lastTaskMs >=
                       kCompanionAvatarTaskRefreshMilliseconds &&
                   Distance(target, seat.lastTarget) >
                       kCompanionAvatarRetargetMeters);
    if (!refresh) {
        return std::nullopt;
    }

    seat.hasTask = true;
    seat.lastTaskMs = nowMs;
    seat.lastMotion = motion;
    seat.lastTarget = target;
    seat.lastHeading = state.heading;
    return CompanionAvatarCommand{payload.seat, target, state.heading, motion};
}

void CompanionAvatarTracker::MarkLost(const std::uint8_t seat) noexcept {
    if (IsValidSeat(seat)) {
        seats_[seat - 1U].hasTask = false;
    }
}

std::vector<std::uint8_t> CompanionAvatarTracker::Expire(
    const std::uint64_t nowMs) {
    std::vector<std::uint8_t> expired;
    for (std::uint8_t index{}; index < kMaxCompanionSeat; ++index) {
        auto& seat = seats_[index];
        if (seat.active &&
            nowMs - seat.lastSeenMs > kCompanionAvatarTimeoutMilliseconds) {
            seat = SeatState{};
            expired.push_back(static_cast<std::uint8_t>(index + 1U));
        }
    }
    return expired;
}

std::vector<std::uint8_t> CompanionAvatarTracker::Clear() {
    auto active = ActiveSeats();
    seats_ = {};
    return active;
}

std::vector<std::uint8_t> CompanionAvatarTracker::ActiveSeats() const {
    std::vector<std::uint8_t> active;
    for (std::uint8_t index{}; index < kMaxCompanionSeat; ++index) {
        if (seats_[index].active) {
            active.push_back(static_cast<std::uint8_t>(index + 1U));
        }
    }
    return active;
}

}  // namespace coopstory::bridge
