using IdentitySyncPro.Core.Helpers;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The guard's whole value is that it rejects the exact strings we ship. If a placeholder
    /// is ever renamed in appsettings without being added to the marker list, the SCIM endpoint
    /// silently reverts to accepting a key that is printed in the repository — and nothing else
    /// in the system would notice. These tests pin the shipped literals verbatim.
    /// </summary>
    public class ApiKeyGuardTests
    {
        // The literals actually present in appsettings.json / .Production.json / .template.json.
        [Theory]
        [InlineData("CHANGE-THIS-API-KEY")]
        [InlineData("CHANGE-THIS-HANGFIRE-KEY")]
        [InlineData("GENERATE-A-STRONG-API-KEY-HERE")]
        [InlineData("GENERATE-A-STRONG-HANGFIRE-KEY-HERE")]
        public void ShippedPlaceholders_AreRejected(string key)
        {
            Assert.True(ApiKeyGuard.IsPlaceholderOrMissing(key));
            Assert.False(ApiKeyGuard.IsUsable(key));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MissingOrBlank_IsRejected(string? key)
        {
            Assert.True(ApiKeyGuard.IsPlaceholderOrMissing(key));
        }

        [Theory]
        [InlineData("change-this-api-key")]   // lower case
        [InlineData("Change-This-Api-Key")]   // mixed case
        public void PlaceholderDetection_IsCaseInsensitive(string key)
        {
            Assert.True(ApiKeyGuard.IsPlaceholderOrMissing(key));
        }

        [Fact]
        public void PlaceholderEmbeddedInALongerValue_IsStillRejected()
        {
            // A half-edited value ("prefix + the placeholder") is not a chosen secret.
            Assert.True(ApiKeyGuard.IsPlaceholderOrMissing("prod-CHANGE-THIS-API-KEY-01"));
        }

        [Fact]
        public void GenuineKey_IsAccepted()
        {
            const string real = "8f3c9b21d47a4e6fb05c1a9e77d2431b6c0e8a55f9314d7ab2ce60f18d43975e";
            Assert.False(ApiKeyGuard.IsPlaceholderOrMissing(real));
            Assert.True(ApiKeyGuard.IsUsable(real));
            Assert.False(ApiKeyGuard.IsWeak(real));
        }

        /// <summary>
        /// Control test. Without this, making IsPlaceholderOrMissing always return true would
        /// pass every other test in this class while blocking all API access in production.
        /// </summary>
        [Fact]
        public void ShortButGenuineKey_StillWorks_AndOnlyWarns()
        {
            const string shortKey = "k7Qm2Zx9";

            Assert.True(ApiKeyGuard.IsUsable(shortKey));   // must NOT be blocked
            Assert.True(ApiKeyGuard.IsWeak(shortKey));     // but is flagged at startup
        }

        [Fact]
        public void WeakCheck_NeverFiresForAnUnusableKey()
        {
            // IsWeak is a warning path; reporting "weak" for a blocked key would send an
            // operator chasing key length while the real problem is that it is a placeholder.
            Assert.False(ApiKeyGuard.IsWeak("CHANGE-THIS-API-KEY"));
            Assert.False(ApiKeyGuard.IsWeak(null));
        }
    }
}
