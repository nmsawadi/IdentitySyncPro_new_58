using System.Security.Cryptography;

namespace IdentitySyncPro.Core.Security
{
    /// <summary>
    /// Time-based one-time passwords, RFC 6238 (HMAC-SHA1, 30-second step, 6 digits).
    ///
    /// Implemented here rather than taken from a package for one reason that matters to this
    /// system: it must work with **no outbound network at all**, and the algorithm is small,
    /// frozen, and covered by published test vectors — the tests pin the exact values from
    /// RFC 6238 Appendix B, so a mistake cannot pass quietly.
    ///
    /// SHA-1 is used deliberately: it is what every authenticator app implements for TOTP, and
    /// its weakness (collision resistance) is irrelevant to HMAC. The requirement document's ban
    /// on SHA-1 targets signatures and TLS, not HMAC-based OTP.
    /// </summary>
    public static class TotpGenerator
    {
        public const int StepSeconds = 30;
        public const int Digits = 6;

        /// <summary>
        /// How many steps either side of "now" are accepted. One step = ±30s, which covers the
        /// usual device clock drift and a user who starts typing as the code rolls over.
        /// Widening this multiplies the number of codes valid at any instant.
        /// </summary>
        public const int DefaultDriftSteps = 1;

        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Creates a 160-bit secret — the size RFC 4226 recommends for HMAC-SHA1.</summary>
        public static string GenerateSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

        /// <summary>The counter value for a moment in time.</summary>
        public static long GetTimeStep(DateTime utc) =>
            (long)((utc - UnixEpoch).TotalSeconds / StepSeconds);

        /// <summary>Computes the code for an explicit counter value.</summary>
        public static string ComputeCode(byte[] secret, long timeStep)
        {
            var counter = BitConverter.GetBytes(timeStep);
            if (BitConverter.IsLittleEndian) Array.Reverse(counter); // RFC: big-endian

            // CA5350 flags HMACSHA1 as weak, and in general it is. Here it is required, not chosen:
            // RFC 6238 defines TOTP over HMAC-SHA1, and every authenticator app — Microsoft
            // Authenticator, Google Authenticator, Authy — implements that. Switching to SHA-256
            // would produce codes no enrolled device could verify.
            //
            // The known SHA-1 weakness is collision resistance, which HMAC does not depend on; HMAC
            // rests on the hash behaving as a PRF, and HMAC-SHA1 is not broken in that role. Codes
            // are also six digits valid for thirty seconds, so the construction is not the limiting
            // factor here.
#pragma warning disable CA5350 // HMAC-SHA1 is mandated by RFC 6238 for TOTP interoperability
            using var hmac = new HMACSHA1(secret);
#pragma warning restore CA5350
            var hash = hmac.ComputeHash(counter);

            // Dynamic truncation (RFC 4226 §5.3)
            var offset = hash[^1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);

            return (binary % (int)Math.Pow(10, Digits)).ToString(new string('0', Digits));
        }

        /// <summary>Computes the code valid at a given moment.</summary>
        public static string ComputeCode(string base32Secret, DateTime utc) =>
            ComputeCode(Base32.Decode(base32Secret), GetTimeStep(utc));

        /// <summary>
        /// Validates a code against the accepted drift window.
        /// </summary>
        /// <param name="lastUsedTimeStep">
        /// The most recent step already spent by this user, or null. A correct code stays valid
        /// for the whole window, so without this an observer who captures one code can replay it
        /// — the code is only single-use if the step it belongs to is remembered and refused.
        /// </param>
        /// <param name="matchedTimeStep">The step that matched, to be persisted by the caller.</param>
        public static bool VerifyCode(
            string? base32Secret,
            string? code,
            DateTime utc,
            long? lastUsedTimeStep,
            out long matchedTimeStep,
            int driftSteps = DefaultDriftSteps)
        {
            matchedTimeStep = 0;

            if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
                return false;

            // Users read the code in groups; strip anything that is not a digit.
            var digits = new string(code.Where(char.IsDigit).ToArray());
            if (digits.Length != Digits) return false;

            byte[] secret;
            try { secret = Base32.Decode(base32Secret); }
            catch (FormatException) { return false; }
            if (secret.Length == 0) return false;

            var current = GetTimeStep(utc);

            for (var offset = -driftSteps; offset <= driftSteps; offset++)
            {
                var step = current + offset;

                // Already spent — refuse even though the arithmetic would match.
                if (lastUsedTimeStep.HasValue && step <= lastUsedTimeStep.Value) continue;

                var expected = ComputeCode(secret, step);
                if (CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(expected),
                        System.Text.Encoding.ASCII.GetBytes(digits)))
                {
                    matchedTimeStep = step;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The otpauth:// URI an authenticator app consumes from a QR code.
        /// Issuer is repeated in the label by convention so older apps group entries correctly.
        /// </summary>
        public static string BuildUri(string issuer, string account, string base32Secret)
        {
            var i = Uri.EscapeDataString(issuer);
            var a = Uri.EscapeDataString(account);
            return $"otpauth://totp/{i}:{a}?secret={base32Secret}&issuer={i}" +
                   $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
        }
    }
}
