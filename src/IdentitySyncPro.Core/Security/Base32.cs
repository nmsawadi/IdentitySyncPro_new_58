using System.Text;

namespace IdentitySyncPro.Core.Security
{
    /// <summary>
    /// RFC 4648 Base32 (no padding on encode, tolerant on decode).
    ///
    /// This is the alphabet every authenticator app expects for a TOTP setup key, which is the
    /// only reason it exists here. Decoding is deliberately forgiving about spaces, lower case
    /// and '=' padding: the secret is typed by a human off a screen, and rejecting "jbsw y3dp"
    /// as invalid would be a support ticket, not a security control.
    /// </summary>
    public static class Base32
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Encode(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;

            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bitsLeft = 0;

            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;
                while (bitsLeft >= 5)
                {
                    sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                    bitsLeft -= 5;
                }
            }

            // Trailing bits are left-aligned into a final character.
            if (bitsLeft > 0)
                sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);

            return sb.ToString();
        }

        public static byte[] Decode(string? encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded)) return Array.Empty<byte>();

            var bytes = new List<byte>(encoded.Length * 5 / 8 + 1);
            int buffer = 0, bitsLeft = 0;

            foreach (var raw in encoded)
            {
                if (raw is ' ' or '-' or '=') continue; // formatting the user may have copied

                var index = Alphabet.IndexOf(char.ToUpperInvariant(raw));
                if (index < 0) throw new FormatException($"'{raw}' is not a Base32 character.");

                buffer = (buffer << 5) | index;
                bitsLeft += 5;
                if (bitsLeft >= 8)
                {
                    bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                    bitsLeft -= 8;
                }
            }

            // Leftover bits (< 8) are padding by construction and are discarded.
            return bytes.ToArray();
        }

        /// <summary>Groups into blocks of four for on-screen transcription.</summary>
        public static string FormatForDisplay(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < secret.Length; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append(' ');
                sb.Append(secret[i]);
            }
            return sb.ToString();
        }
    }
}
