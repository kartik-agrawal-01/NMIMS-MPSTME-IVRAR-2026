using System;
using System.Security.Cryptography;
using System.Text;

namespace BoardroomXR.Auth
{
    /// <summary>
    /// RFC 6238-style TOTP. Both the client-side <see cref="OTPAuthenticator"/>
    /// (which renders the code on the virtual token) and the server-side
    /// credential store derive codes through this one function, so the check
    /// is a real comparison of two independently derived values rather than a
    /// length test.
    ///
    /// Deliberately uses HMAC-SHA1 over the time step rather than
    /// string.GetHashCode(): GetHashCode is randomised per process in modern
    /// .NET, so client and server would never agree once they are separate
    /// processes.
    /// </summary>
    public static class TotpGenerator
    {
        /// <summary>Current Unix time step for the given validity window.</summary>
        public static long CurrentTimeStep(int validitySeconds)
        {
            if (validitySeconds <= 0) validitySeconds = 30;
            long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return unixSeconds / validitySeconds;
        }

        /// <summary>Seconds remaining before the current code rotates.</summary>
        public static int SecondsRemaining(int validitySeconds)
        {
            if (validitySeconds <= 0) validitySeconds = 30;
            long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return validitySeconds - (int)(unixSeconds % validitySeconds);
        }

        /// <summary>Derives the code for a specific seed + time step.</summary>
        public static string Generate(string sharedSeed, long timeStep, int digits)
        {
            if (digits < 4) digits = 4;
            if (digits > 9) digits = 9;

            byte[] key = Encoding.UTF8.GetBytes(sharedSeed ?? string.Empty);
            byte[] counter = BitConverter.GetBytes(timeStep);
            if (BitConverter.IsLittleEndian) Array.Reverse(counter); // RFC 6238 uses big-endian

            byte[] hash;
            using (var hmac = new HMACSHA1(key))
            {
                hash = hmac.ComputeHash(counter);
            }

            // Dynamic truncation (RFC 4226 section 5.3).
            int offset = hash[hash.Length - 1] & 0x0F;
            int binary =
                ((hash[offset] & 0x7F) << 24) |
                ((hash[offset + 1] & 0xFF) << 16) |
                ((hash[offset + 2] & 0xFF) << 8) |
                (hash[offset + 3] & 0xFF);

            int modulo = 1;
            for (int i = 0; i < digits; i++) modulo *= 10;

            return (binary % modulo).ToString().PadLeft(digits, '0');
        }

        /// <summary>
        /// Validates a submitted code against the seed, allowing +/- one time
        /// step so a user who starts typing just before a rotation is not
        /// failed unfairly. Returns false for anything that does not match a
        /// genuinely derived code.
        /// </summary>
        public static bool Validate(string sharedSeed, string submittedCode, int digits, int validitySeconds, int driftSteps = 1)
        {
            if (string.IsNullOrWhiteSpace(submittedCode)) return false;

            submittedCode = submittedCode.Trim();
            long step = CurrentTimeStep(validitySeconds);

            for (long d = -driftSteps; d <= driftSteps; d++)
            {
                if (FixedTimeEquals(Generate(sharedSeed, step + d, digits), submittedCode)) return true;
            }
            return false;
        }

        /// <summary>Length-constant comparison so the check does not leak digits by timing.</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
