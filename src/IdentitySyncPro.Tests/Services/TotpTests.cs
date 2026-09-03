using System.Text;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Core.Security;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// TOTP is the one piece here that cannot be verified by inspection: a subtly wrong
    /// implementation still produces six plausible digits, and the only symptom is that
    /// authenticator apps disagree with the server — which reads as "the user typed it wrong".
    /// The published RFC 6238 vectors are therefore pinned verbatim.
    /// </summary>
    public class TotpTests
    {
        private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // RFC 6238 Appendix B uses the ASCII seed "12345678901234567890" with HMAC-SHA1.
        private static readonly byte[] RfcSeed = Encoding.ASCII.GetBytes("12345678901234567890");

        [Theory]
        [InlineData(59L, "287082")]
        [InlineData(1111111109L, "081804")]
        [InlineData(1111111111L, "050471")]
        [InlineData(1234567890L, "005924")]
        [InlineData(2000000000L, "279037")]
        [InlineData(20000000000L, "353130")]
        public void MatchesRfc6238TestVectors(long unixSeconds, string expected)
        {
            var utc = Epoch.AddSeconds(unixSeconds);
            var step = TotpGenerator.GetTimeStep(utc);

            Assert.Equal(expected, TotpGenerator.ComputeCode(RfcSeed, step));
        }

        [Fact]
        public void Base32_RoundTripsArbitraryBytes()
        {
            var data = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF, 0xAB, 0xCD };
            Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        }

        [Fact]
        public void Base32_MatchesKnownVector()
        {
            // RFC 4648: "foobar" -> MZXW6YTBOI
            Assert.Equal("MZXW6YTBOI", Base32.Encode(Encoding.ASCII.GetBytes("foobar")));
        }

        [Fact]
        public void Base32_TolerantOfHumanFormatting()
        {
            // The setup screen shows the key in groups of four; users paste it back with spaces.
            var secret = Base32.Encode(Encoding.ASCII.GetBytes("foobar"));
            Assert.Equal(Base32.Decode(secret), Base32.Decode("mzxw 6ytb-oi=="));
        }

        [Fact]
        public void CurrentCode_Verifies()
        {
            var secret = TotpGenerator.GenerateSecret();
            var now = DateTime.UtcNow;

            var code = TotpGenerator.ComputeCode(secret, now);
            Assert.True(TotpGenerator.VerifyCode(secret, code, now, null, out _));
        }

        [Fact]
        public void CodeFromAdjacentStep_IsAcceptedForClockDrift()
        {
            var secret = TotpGenerator.GenerateSecret();
            var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

            var previous = TotpGenerator.ComputeCode(secret, now.AddSeconds(-TotpGenerator.StepSeconds));
            Assert.True(TotpGenerator.VerifyCode(secret, previous, now, null, out _));
        }

        [Fact]
        public void CodeOutsideDriftWindow_IsRejected()
        {
            var secret = TotpGenerator.GenerateSecret();
            var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

            var stale = TotpGenerator.ComputeCode(secret, now.AddMinutes(-5));
            Assert.False(TotpGenerator.VerifyCode(secret, stale, now, null, out _));
        }

        [Fact]
        public void AlreadyUsedCode_IsRejected()
        {
            // Without this a code captured over the shoulder (or from a log) stays usable for the
            // rest of its window — the whole point of a *one-time* password is lost.
            var secret = TotpGenerator.GenerateSecret();
            var now = DateTime.UtcNow;
            var code = TotpGenerator.ComputeCode(secret, now);

            Assert.True(TotpGenerator.VerifyCode(secret, code, now, null, out var usedStep));
            Assert.False(TotpGenerator.VerifyCode(secret, code, now, usedStep, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("12345")]     // too short
        [InlineData("1234567")]   // too long
        [InlineData("abcdef")]    // not digits
        public void MalformedCode_IsRejected(string? code)
        {
            var secret = TotpGenerator.GenerateSecret();
            Assert.False(TotpGenerator.VerifyCode(secret, code, DateTime.UtcNow, null, out _));
        }

        [Fact]
        public void MissingSecret_IsRejected()
        {
            // A user row with no secret must never authenticate, whatever is typed.
            Assert.False(TotpGenerator.VerifyCode(null, "000000", DateTime.UtcNow, null, out _));
            Assert.False(TotpGenerator.VerifyCode("", "000000", DateTime.UtcNow, null, out _));
        }

        [Fact]
        public void OtpAuthUri_CarriesTheParametersAppsExpect()
        {
            var uri = TotpGenerator.BuildUri("IdentitySync Pro", "nasser.admin", "JBSWY3DPEHPK3PXP");

            Assert.StartsWith("otpauth://totp/", uri);
            Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri);
            Assert.Contains("algorithm=SHA1", uri);
            Assert.Contains("digits=6", uri);
            Assert.Contains("period=30", uri);
            Assert.Contains("issuer=IdentitySync%20Pro", uri); // spaces escaped, not dropped
        }
    }

    /// <summary>
    /// Scope rules for the policy. The disabled-by-default case is the one that keeps an upgrade
    /// from locking a running system out of its own console.
    /// </summary>
    public class MfaSettingsTests
    {
        [Fact]
        public void DefaultsToDisabled()
        {
            var s = new MfaSettings();
            Assert.False(s.IsEnabled);
            Assert.False(s.AppliesToRole(AppUserRoles.Admin));
        }

        [Fact]
        public void WhenDisabled_NoRoleIsInScope()
        {
            var s = new MfaSettings { IsEnabled = false, RequiredRoles = "Admin,Operator,Viewer" };
            Assert.False(s.AppliesToRole(AppUserRoles.Admin));
        }

        [Fact]
        public void OnlyListedRolesAreInScope()
        {
            var s = new MfaSettings { IsEnabled = true, RequiredRoles = "Admin" };

            Assert.True(s.AppliesToRole(AppUserRoles.Admin));
            Assert.False(s.AppliesToRole(AppUserRoles.Operator));
            Assert.False(s.AppliesToRole(AppUserRoles.Viewer));
        }

        [Theory]
        [InlineData("admin")]
        [InlineData("ADMIN")]
        public void RoleMatchingIsCaseInsensitive(string role)
        {
            var s = new MfaSettings { IsEnabled = true, RequiredRoles = "Admin" };
            Assert.True(s.AppliesToRole(role));
        }

        [Fact]
        public void SpacedRoleListIsParsed()
        {
            var s = new MfaSettings { IsEnabled = true, RequiredRoles = "Admin , Operator" };
            Assert.True(s.AppliesToRole(AppUserRoles.Operator));
        }

        [Fact]
        public void EmptyRoleListMatchesNobody()
        {
            var s = new MfaSettings { IsEnabled = true, RequiredRoles = "" };
            Assert.False(s.AppliesToRole(AppUserRoles.Admin));
        }

        [Fact]
        public void NullRoleMatchesNobody()
        {
            var s = new MfaSettings { IsEnabled = true, RequiredRoles = "Admin" };
            Assert.False(s.AppliesToRole(null));
        }
    }
}
