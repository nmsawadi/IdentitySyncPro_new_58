using System.Security.Cryptography;

namespace IdentitySyncPro.Infrastructure.Services
{
    public static class PasswordGenerator
    {
        private const string AllChars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789!@#$%&*";
        private const string LowerChars = "abcdefghjkmnpqrstuvwxyz";
        private const string UpperChars = "ABCDEFGHJKMNPQRSTUVWXYZ";
        private const string DigitChars = "23456789";
        private const string SpecialChars = "!@#$%&*";

        public static string Generate(int length = 10)
        {
            if (length < 4) length = 4;

            var password = new char[length];
            var randomBytes = RandomNumberGenerator.GetBytes(length);

            for (int i = 0; i < length; i++)
            {
                var index = randomBytes[i] % AllChars.Length;
                password[i] = AllChars[index];
            }

            // Ensure at least one of each required type
            password[0] = LowerChars[RandomNumberGenerator.GetInt32(LowerChars.Length)];
            password[1] = UpperChars[RandomNumberGenerator.GetInt32(UpperChars.Length)];
            password[2] = DigitChars[RandomNumberGenerator.GetInt32(DigitChars.Length)];
            password[3] = SpecialChars[RandomNumberGenerator.GetInt32(SpecialChars.Length)];

            // Shuffle using Fisher-Yates
            for (int i = length - 1; i > 0; i--)
            {
                var swapIndex = RandomNumberGenerator.GetInt32(i + 1);
                (password[i], password[swapIndex]) = (password[swapIndex], password[i]);
            }

            return new string(password);
        }
    }
}
