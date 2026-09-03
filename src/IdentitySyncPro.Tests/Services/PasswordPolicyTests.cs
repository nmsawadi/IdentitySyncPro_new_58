using IdentitySyncPro.Core.Models.Settings;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Maximum password age. Two of these tests guard exemptions rather than the rule itself —
    /// they are the ones that matter. Expiring an Active Directory user demands a change the
    /// application cannot perform (the password lives in the domain), and treating an untracked
    /// date as ancient forces every existing account to change on upgrade day. Both look like
    /// "the policy is working" from the outside.
    /// </summary>
    public class PasswordPolicyTests
    {
        private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void PasswordOlderThanMaxAge_IsExpired()
        {
            var policy = new PasswordPolicy(90);
            Assert.True(policy.IsExpired(Now.AddDays(-91), isLocalUser: true, Now));
        }

        [Fact]
        public void PasswordExactlyAtMaxAge_IsExpired()
        {
            // The requirement reads "change every 90 days at least", so day 90 must already count.
            var policy = new PasswordPolicy(90);
            Assert.True(policy.IsExpired(Now.AddDays(-90), isLocalUser: true, Now));
        }

        [Fact]
        public void PasswordWithinMaxAge_IsNotExpired()
        {
            var policy = new PasswordPolicy(90);
            Assert.False(policy.IsExpired(Now.AddDays(-89), isLocalUser: true, Now));
        }

        [Fact]
        public void ActiveDirectoryUser_IsNeverExpired()
        {
            // Forcing this would send the user to a change screen that rejects AD accounts
            // outright ("ad_user") — an unrecoverable loop for anyone who signs in via the domain.
            var policy = new PasswordPolicy(90);
            Assert.False(policy.IsExpired(Now.AddDays(-3650), isLocalUser: false, Now));
        }

        [Fact]
        public void UntrackedChangeDate_IsNotExpired()
        {
            // Null means "never recorded", not "very old". Startup stamps existing rows; if this
            // returned true instead, every account on an upgraded install would be forced to
            // change at once — including people who changed their password the day before.
            var policy = new PasswordPolicy(90);
            Assert.False(policy.IsExpired(null, isLocalUser: true, Now));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NonPositiveMaxAge_DisablesExpiry(int days)
        {
            var policy = new PasswordPolicy(days);
            Assert.False(policy.IsEnabled);
            Assert.False(policy.IsExpired(Now.AddDays(-9999), isLocalUser: true, Now));
        }

        [Fact]
        public void DefaultMaxAge_Is90Days()
        {
            Assert.Equal(90, PasswordPolicy.DefaultMaxAgeDays);
            Assert.Equal(90, new PasswordPolicy().MaxAgeDays);
        }

        [Fact]
        public void DaysRemaining_CountsDownAndGoesNegativePastExpiry()
        {
            var policy = new PasswordPolicy(90);

            Assert.Equal(90, policy.DaysRemaining(Now, isLocalUser: true, Now));
            Assert.Equal(30, policy.DaysRemaining(Now.AddDays(-60), isLocalUser: true, Now));
            Assert.True(policy.DaysRemaining(Now.AddDays(-100), isLocalUser: true, Now) < 0);
        }

        [Fact]
        public void DaysRemaining_IsNullWhereExpiryDoesNotApply()
        {
            var policy = new PasswordPolicy(90);

            Assert.Null(policy.DaysRemaining(Now, isLocalUser: false, Now));          // AD user
            Assert.Null(policy.DaysRemaining(null, isLocalUser: true, Now));          // untracked
            Assert.Null(new PasswordPolicy(0).DaysRemaining(Now, true, Now));         // disabled
        }
    }
}
