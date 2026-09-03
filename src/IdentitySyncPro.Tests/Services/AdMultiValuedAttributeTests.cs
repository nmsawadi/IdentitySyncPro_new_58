using System.DirectoryServices.Protocols;
using System.Text;
using IdentitySyncPro.Infrastructure.Connectors;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Multi-valued AD attributes (proxyAddresses in practice) come back from LDAP as byte[],
    /// not string. A plain ToString() on those produced the literal text "System.Byte[]", so the
    /// current value never matched the desired one and the attribute was rewritten on every
    /// single sync — visible in production logs as:
    ///
    ///   proxyAddresses: System.Byte[]|System.Byte[]|System.Byte[] → SMTP:...@nu.edu.sa|...
    ///
    /// The second half of the fix is order-normalisation: LDAP does not guarantee the order it
    /// returns values in, so an identical set in a different order also looked like a change.
    /// </summary>
    public class AdMultiValuedAttributeTests
    {
        [Fact]
        public void ByteArrayValues_DecodeToText_NotSystemByteArray()
        {
            // Exactly how the directory hands these back for a multi-valued attribute.
            var attr = new DirectoryAttribute("proxyAddresses",
                Encoding.UTF8.GetBytes("SMTP:441234567@nu.edu.sa"),
                Encoding.UTF8.GetBytes("smtp:441234567@example.mail.onmicrosoft.com"));

            var values = ActiveDirectoryConnector.GetStringValues(attr);

            Assert.DoesNotContain("System.Byte[]", string.Join("|", values));
            Assert.Contains("SMTP:441234567@nu.edu.sa", values);
            Assert.Contains("smtp:441234567@example.mail.onmicrosoft.com", values);
        }

        [Fact]
        public void ArabicByteValues_SurviveDecoding()
        {
            // The directory holds Arabic as UTF-8 bytes; mis-decoding would corrupt it silently.
            var attr = new DirectoryAttribute("otherMailbox", Encoding.UTF8.GetBytes("طالب@nu.edu.sa"));

            Assert.Equal("طالب@nu.edu.sa", ActiveDirectoryConnector.GetStringValues(attr).Single());
        }

        [Fact]
        public void StringValues_StillWork()
        {
            var attr = new DirectoryAttribute("proxyAddresses", "SMTP:a@x.com", "smtp:b@y.com");

            var values = ActiveDirectoryConnector.GetStringValues(attr);

            Assert.Equal(2, values.Length);
            Assert.Contains("SMTP:a@x.com", values);
        }

        [Fact]
        public void SameValues_InAnyOrder_CompareEqual()
        {
            // The whole point: AD's order must not make an unchanged attribute look changed.
            var fromDirectory = ActiveDirectoryConnector.JoinMultiValued(
                new[] { "smtp:b@y.com", "SMTP:a@x.com" });
            var fromMapping = ActiveDirectoryConnector.JoinMultiValued(
                new[] { "SMTP:a@x.com", "smtp:b@y.com" });

            Assert.Equal(fromDirectory, fromMapping);
        }

        [Fact]
        public void GenuinelyDifferentValues_StillCompareUnequal()
        {
            // Guards against "fixing" the noise by making everything compare equal.
            var before = ActiveDirectoryConnector.JoinMultiValued(new[] { "SMTP:a@x.com" });
            var after = ActiveDirectoryConnector.JoinMultiValued(new[] { "SMTP:a@x.com", "smtp:b@y.com" });

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void BlankEntries_AreIgnored()
        {
            Assert.Equal("SMTP:a@x.com",
                ActiveDirectoryConnector.JoinMultiValued(new[] { "SMTP:a@x.com", "", "   " }));
        }

        // ═══════════════════════════════════════
        // Unmanaged values must survive a Replace
        // ═══════════════════════════════════════

        // The literal value read off a production account before the fix removed it.
        private const string RealX500 =
            "x500:/o=ExchangeLabs/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=6d9dbd88a6354cb9aa1b8bcb4aaca20e-431840119";

        [Fact]
        public void X500Address_SurvivesTheSync()
        {
            // Exchange generates this (the mailbox LegacyExchangeDN) and Outlook needs it to
            // resolve replies to older mail, free/busy, and routing for migrated mailboxes.
            // The mapping produces only the two SMTP entries, and Replace wrote exactly those —
            // deleting this one on every account it touched.
            var current = $"smtp:431840119@nejranuniversity.mail.onmicrosoft.com|SMTP:431840119@nu.edu.sa|{RealX500}";
            var mapped = "SMTP:431840119@nu.edu.sa|smtp:431840119@nejranuniversity.mail.onmicrosoft.com";

            var merged = ActiveDirectoryConnector.MergeMultiValued(mapped, current);

            Assert.Contains(RealX500, merged);
            Assert.Equal(3, merged.Count);
        }

        [Fact]
        public void AccountAlreadyCorrect_ProducesNoChange()
        {
            // With the x500 preserved, an account that is already right must compare equal —
            // otherwise every sync rewrites every mailbox forever.
            var current = ActiveDirectoryConnector.JoinMultiValued(new[]
                { "SMTP:a@nu.edu.sa", "smtp:a@x.mail.onmicrosoft.com", RealX500 });
            var mapped = "SMTP:a@nu.edu.sa|smtp:a@x.mail.onmicrosoft.com";

            var merged = ActiveDirectoryConnector.JoinMultiValued(
                ActiveDirectoryConnector.MergeMultiValued(mapped, current));

            Assert.Equal(current, merged);
        }

        [Fact]
        public void ManagedSmtpAddresses_AreStillReplaced()
        {
            // Protecting x500 must not turn the attribute read-only: a stale SMTP address the
            // mapping no longer produces still has to go.
            var current = $"SMTP:old@nu.edu.sa|{RealX500}";
            var mapped = "SMTP:new@nu.edu.sa";

            var merged = ActiveDirectoryConnector.MergeMultiValued(mapped, current);

            Assert.Contains("SMTP:new@nu.edu.sa", merged);
            Assert.DoesNotContain("SMTP:old@nu.edu.sa", merged);
            Assert.Contains(RealX500, merged);
        }

        [Fact]
        public void X400Address_IsAlsoProtected()
        {
            var current = "SMTP:a@x.com|X400:c=US;a= ;p=Org;o=Exchange;s=User;";
            var merged = ActiveDirectoryConnector.MergeMultiValued("SMTP:a@x.com", current);

            Assert.Contains("X400:c=US;a= ;p=Org;o=Exchange;s=User;", merged);
        }

        [Fact]
        public void ProtectedValue_IsNotDuplicated_WhenAlsoMapped()
        {
            var merged = ActiveDirectoryConnector.MergeMultiValued(
                $"SMTP:a@x.com|{RealX500}", $"SMTP:a@x.com|{RealX500}");

            Assert.Equal(2, merged.Count);
        }

        [Fact]
        public void EmptyCurrentValue_JustUsesTheMappedValues()
        {
            // A brand-new account has nothing to preserve.
            var merged = ActiveDirectoryConnector.MergeMultiValued("SMTP:a@x.com|smtp:b@y.com", null);

            Assert.Equal(2, merged.Count);
        }
    }
}
