namespace NoiseCut.Core.Simulation;

/// <summary>
/// UUIDv7 (RFC 9562): 48 bits of Unix milliseconds, then random bits. Time-ordered, so it indexes well.
/// <see cref="Guid.CreateVersion7()"/> uses a non-seedable random source; the simulator needs the same IDs on every run
/// with the same seed, so it builds them from its own <see cref="Random"/>.
/// </summary>
public static class Uuid7
{
    public static Guid Create(DateTimeOffset timestamp, Random rng)
    {
        Span<byte> bytes = stackalloc byte[16];
        rng.NextBytes(bytes);

        var ms = timestamp.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = (byte)(ms >> (8 * (5 - i)));
        }

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC variant
        return new Guid(bytes, bigEndian: true);
    }
}
