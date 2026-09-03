using IdentitySyncPro.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Verifies the secret-encryption gateway used to protect AD/Oracle/SMS passwords at rest.
    /// </summary>
    public class SecretProtectionTests
    {
        public SecretProtectionTests()
        {
            // Deterministic, in-memory keys for the test run.
            SecretProtection.Initialize(new EphemeralDataProtectionProvider());
        }

        [Fact]
        public void Protect_ThenUnprotect_RoundTrips()
        {
            const string secret = "P@ssw0rd-كلمة!#$";

            var encrypted = SecretProtection.Protect(secret);

            Assert.StartsWith(SecretProtection.Prefix, encrypted);
            Assert.NotEqual(secret, encrypted);                 // actually transformed
            Assert.DoesNotContain(secret, encrypted);           // plaintext not visible in ciphertext
            Assert.Equal(secret, SecretProtection.Unprotect(encrypted));
        }

        [Fact]
        public void Unprotect_LegacyPlaintext_PassesThrough()
        {
            // A value with no ENC prefix is treated as legacy plaintext and returned unchanged.
            Assert.Equal("legacy-plain", SecretProtection.Unprotect("legacy-plain"));
        }

        [Fact]
        public void Protect_AlreadyEncrypted_IsIdempotent()
        {
            var once = SecretProtection.Protect("hunter2");
            var twice = SecretProtection.Protect(once);

            Assert.Equal(once, twice);                          // no double-wrapping
            Assert.Equal("hunter2", SecretProtection.Unprotect(twice));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Protect_EmptyOrNull_ReturnsEmpty(string? input)
        {
            Assert.Equal(string.Empty, SecretProtection.Protect(input));
            Assert.Equal(string.Empty, SecretProtection.Unprotect(input));
        }

        [Fact]
        public void IsProtected_DistinguishesEncryptedFromPlaintext()
        {
            Assert.True(SecretProtection.IsProtected(SecretProtection.Protect("x")));
            Assert.False(SecretProtection.IsProtected("x"));
            Assert.False(SecretProtection.IsProtected(""));
        }
    }
}
