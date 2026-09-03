using IdentitySyncPro.Infrastructure.Services;

namespace IdentitySyncPro.Tests.Services
{
    public class PasswordGeneratorTests
    {
        [Fact]
        public void Generate_ReturnsCorrectLength()
        {
            var password = PasswordGenerator.Generate(12);
            Assert.Equal(12, password.Length);
        }

        [Fact]
        public void Generate_DefaultLength_Returns10Characters()
        {
            var password = PasswordGenerator.Generate();
            Assert.Equal(10, password.Length);
        }

        [Fact]
        public void Generate_ContainsVariedCharacters()
        {
            // Generate multiple passwords to ensure randomness
            var passwords = Enumerable.Range(0, 100).Select(_ => PasswordGenerator.Generate()).ToList();

            // All should be unique (statistically very likely)
            var uniqueCount = passwords.Distinct().Count();
            Assert.True(uniqueCount > 90, $"Expected most passwords to be unique, but only {uniqueCount}/100 were unique");
        }

        [Fact]
        public void Generate_MultipleCalls_ProduceDifferentPasswords()
        {
            var p1 = PasswordGenerator.Generate();
            var p2 = PasswordGenerator.Generate();

            // Two sequential calls should produce different passwords
            Assert.NotEqual(p1, p2);
        }
    }
}
