using IdentitySyncPro.Core.Helpers;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards who an access notification reaches.
    ///
    /// The failure this protects against is the module's worst: an approval request delivered to
    /// nobody. The request is raised, the row reads "Pending", the screen shows a queue — and the
    /// person who could clear it never learned it exists. Nothing in that sequence is an error, so
    /// "reached nobody" has to be a value the code carries, not an empty list somebody may forget
    /// to look at.
    /// </summary>
    public class AccessNotificationPlanTests
    {
        private static readonly Dictionary<string, string?> NoAddresses = new();

        // ══════════════════════════════════════
        // REACHING NOBODY
        // ══════════════════════════════════════

        [Fact]
        public void NoMailboxAndNoResolvedAddress_ReachesNobody()
        {
            var plan = AccessNotificationPlan.ForApprovers(null, new[] { "manager1" }, NoAddresses);

            Assert.False(plan.HasRecipients);
            Assert.Equal(new[] { "manager1" }, plan.Unreachable);
        }

        [Fact]
        public void NoApproversAtAll_ReachesNobodyAndBlamesNobody()
        {
            var plan = AccessNotificationPlan.ForApprovers(null, Array.Empty<string>(), NoAddresses);

            Assert.False(plan.HasRecipients);
            Assert.Empty(plan.Unreachable);
        }

        /// <summary>An approver mapped to a blank address is unreachable, not reachable at "".</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyResolvedAddress_CountsAsUnreachable(string? address)
        {
            var plan = AccessNotificationPlan.ForApprovers(
                null, new[] { "manager1" }, new Dictionary<string, string?> { ["manager1"] = address });

            Assert.False(plan.HasRecipients);
            Assert.Contains("manager1", plan.Unreachable);
        }

        // ══════════════════════════════════════
        // REACHING SOMEBODY
        // ══════════════════════════════════════

        [Fact]
        public void AConfiguredMailbox_IsUsed()
        {
            var plan = AccessNotificationPlan.ForApprovers("approvers@njran.edu.sa", Array.Empty<string>(), NoAddresses);

            Assert.Equal(new[] { "approvers@njran.edu.sa" }, plan.Recipients);
            Assert.Empty(plan.Unreachable);
        }

        [Fact]
        public void ResolvedApproverAddresses_AreUsed()
        {
            var plan = AccessNotificationPlan.ForApprovers(
                null, new[] { "manager1", "manager2" },
                new Dictionary<string, string?> { ["manager1"] = "m1@x.sa", ["manager2"] = "m2@x.sa" });

            Assert.Equal(2, plan.Recipients.Count);
            Assert.Empty(plan.Unreachable);
        }

        /// <summary>
        /// The half-delivered case has to stay visible. Two approvers, one address: the mail goes
        /// out, and reporting that as a complete delivery is the same silence in a quieter form.
        /// </summary>
        [Fact]
        public void APartialDelivery_IsBothSentAndIncomplete()
        {
            var plan = AccessNotificationPlan.ForApprovers(
                null, new[] { "manager1", "manager2" },
                new Dictionary<string, string?> { ["manager1"] = "m1@x.sa" });

            Assert.True(plan.HasRecipients);
            Assert.Single(plan.Recipients);
            Assert.Equal(new[] { "manager2" }, plan.Unreachable);
        }

        [Fact]
        public void TheSameAddressTwice_IsSentOnce()
        {
            var plan = AccessNotificationPlan.ForApprovers(
                "shared@x.sa", new[] { "manager1" },
                new Dictionary<string, string?> { ["manager1"] = "SHARED@x.sa" });

            Assert.Single(plan.Recipients);
        }

        /// <summary>Usernames are matched case-insensitively — the directory and the settings rarely agree on case.</summary>
        [Fact]
        public void ApproverLookup_IsCaseInsensitive()
        {
            var plan = AccessNotificationPlan.ForApprovers(
                null, new[] { "Manager1" },
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["manager1"] = "m1@x.sa" });

            Assert.Single(plan.Recipients);
            Assert.Empty(plan.Unreachable);
        }

        // ══════════════════════════════════════
        // THE ADDRESS FIELD ITSELF
        // ══════════════════════════════════════

        [Theory]
        [InlineData("a@x.sa,b@x.sa", 2)]
        [InlineData("a@x.sa; b@x.sa", 2)]
        [InlineData("  a@x.sa  ", 1)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void RecipientFields_AreSplitOnWhatPeopleActuallyType(string? value, int expected)
        {
            Assert.Equal(expected, AccessNotificationPlan.SplitAddresses(value).Count);
        }

        /// <summary>
        /// A value with no '@' is not an address. Passing it to the mail server would produce a
        /// send failure whose message names the malformed string rather than the missing approver.
        /// </summary>
        [Theory]
        [InlineData("not-an-address")]
        [InlineData("approvers")]
        public void SomethingThatIsNotAnAddress_IsNotARecipient(string value)
        {
            Assert.Empty(AccessNotificationPlan.SplitAddresses(value));
        }

        [Fact]
        public void AMixOfGoodAndBadAddresses_KeepsOnlyTheGood()
        {
            var addresses = AccessNotificationPlan.SplitAddresses("good@x.sa, rubbish, other@x.sa");
            Assert.Equal(new[] { "good@x.sa", "other@x.sa" }, addresses);
        }

        // ══════════════════════════════════════
        // ONE PERSON
        // ══════════════════════════════════════

        [Fact]
        public void APersonWithAnAddress_IsReachable()
        {
            var plan = AccessNotificationPlan.ForPerson("ahmed.s", "ahmed@x.sa");

            Assert.Equal(new[] { "ahmed@x.sa" }, plan.Recipients);
            Assert.Empty(plan.Unreachable);
        }

        [Fact]
        public void APersonWithoutAnAddress_IsNamedAsUnreachable()
        {
            var plan = AccessNotificationPlan.ForPerson("ahmed.s", null);

            Assert.False(plan.HasRecipients);
            Assert.Equal(new[] { "ahmed.s" }, plan.Unreachable);
        }
    }
}
