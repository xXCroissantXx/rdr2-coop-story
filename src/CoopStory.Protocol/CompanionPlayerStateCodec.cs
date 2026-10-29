namespace CoopStory.Protocol;

// A guest player relayed by the host to the other players in a session with more than two seats.
// Seat is the host transport seat (1-3) the player sits in; State is that guest's own PlayerState, which always carries the Guest slot.
public readonly record struct CompanionPlayerStatePayload(
    byte Seat,
    PlayerStatePayload State);

// Layout: [seat u8][PlayerState, 104 bytes].
public static class CompanionPlayerStateCodec
{
    public const byte MinSeat = 1;

    public const byte MaxSeat = 3;

    public const int Size = 1 + BinaryPayloadCodec.PlayerStateSize;

    public static byte[] Encode(CompanionPlayerStatePayload payload)
    {
        ValidateSeat(payload.Seat);
        ValidateSlot(payload.State);
        var state = BinaryPayloadCodec.EncodePlayerState(payload.State);
        var bytes = new byte[Size];
        bytes[0] = payload.Seat;
        state.CopyTo(bytes.AsSpan(1));
        return bytes;
    }

    public static CompanionPlayerStatePayload Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != Size)
        {
            throw new ProtocolException(
                $"CompanionPlayerState must be {Size} bytes, received {payload.Length}.");
        }

        ValidateSeat(payload[0]);
        var state = BinaryPayloadCodec.DecodePlayerState(payload[1..]);
        ValidateSlot(state);
        return new CompanionPlayerStatePayload(payload[0], state);
    }

    // Cheap seat read for queue keying; the payload is fully decoded before it is trusted.
    public static bool TryReadSeat(ReadOnlySpan<byte> payload, out byte seat)
    {
        seat = payload.Length == Size ? payload[0] : (byte)0;
        return seat is >= MinSeat and <= MaxSeat;
    }

    private static void ValidateSeat(byte seat)
    {
        if (seat is < MinSeat or > MaxSeat)
        {
            throw new ProtocolException(
                $"CompanionPlayerState seat {seat} is outside {MinSeat}-{MaxSeat}.");
        }
    }

    private static void ValidateSlot(PlayerStatePayload state)
    {
        if (state.Slot != (byte)SessionRole.Guest)
        {
            throw new ProtocolException(
                "CompanionPlayerState must wrap a guest PlayerState.");
        }
    }
}
