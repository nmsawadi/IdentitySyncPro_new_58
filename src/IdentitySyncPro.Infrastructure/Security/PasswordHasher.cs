using System.Security.Cryptography;

namespace IdentitySyncPro.Infrastructure.Security
{
    /// <summary>
    /// PBKDF2-SHA256 password hashing for local console users.
    /// Format: PBKDF2${iterations}${salt-base64}${hash-base64}
    /// </summary>
    public static class PasswordHasher
    {
        private const int Iterations = 100_000;
        private const int SaltSize = 16;
        private const int HashSize = 32;

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return $"PBKDF2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;

            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "PBKDF2") return false;
            if (!int.TryParse(parts[1], out var iterations)) return false;

            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException) { return false; }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }
}
