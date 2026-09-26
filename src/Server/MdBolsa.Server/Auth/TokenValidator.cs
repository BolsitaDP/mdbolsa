using System.Security.Cryptography;
using System.Text;
using MdBolsa.Contracts;

namespace MdBolsa.Server.Auth;

// Phase 9's authentication: one shared token per deployment, presented by every
// device in `X-MdBolsa-Token`. It is deliberately the simplest thing that closes
// "the API is on my network" - see docs/decisions/0011-client-sync.md for what it
// does and does not buy, and for what to replace it with if this ever runs
// anywhere but a home network.
//
// Compared in constant time so a wrong token can't be discovered by timing, and
// compared on the *hashes* so the comparison length doesn't leak the secret's
// length either.
public static class TokenValidator
{
    public static bool IsValid(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented)) return false;
        return FixedTimeEquals(Sha256(presented), Sha256(expected));
    }

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        var difference = a.Length ^ b.Length;
        for (var i = 0; i < a.Length && i < b.Length; i++) difference |= a[i] ^ b[i];
        return difference == 0;
    }
}
