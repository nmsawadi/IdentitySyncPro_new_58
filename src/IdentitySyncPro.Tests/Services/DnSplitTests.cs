using IdentitySyncPro.Infrastructure.Connectors;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// MoveToOUAsync compares an account's current parent container against the target OU so it
    /// does not ask AD to move an object into the container it already occupies — which raises an
    /// error, returns false, and leaves ADCurrentOU null, making a correctly-placed account look
    /// like a failed move.
    ///
    /// On this tenant many graduates were already filed under OU=Graduates, so a bulk run over
    /// 47,526 of them would have produced tens of thousands of errors that were not errors.
    ///
    /// The comparison is only as good as the DN split, which is what these cover.
    /// </summary>
    public class DnSplitTests
    {
        [Fact]
        public void SplitsAnOrdinaryDn()
        {
            var (rdn, parent) = ActiveDirectoryConnector.SplitDn(
                "CN=441234567,OU=Graduates,DC=std,DC=nu,DC=edu,DC=sa");

            Assert.Equal("CN=441234567", rdn);
            Assert.Equal("OU=Graduates,DC=std,DC=nu,DC=edu,DC=sa", parent);
        }

        [Fact]
        public void ParentMatchesTheTargetOu_ForAnAccountAlreadyInPlace()
        {
            // The exact case that produced the false failures.
            const string targetOu = "OU=Graduates,DC=std,DC=nu,DC=edu,DC=sa";
            var (_, parent) = ActiveDirectoryConnector.SplitDn($"CN=431840119,{targetOu}");

            Assert.Equal(targetOu, parent, ignoreCase: true);
        }

        [Fact]
        public void ParentDiffers_ForAnAccountThatStillNeedsMoving()
        {
            var (_, parent) = ActiveDirectoryConnector.SplitDn(
                "CN=431840119,OU=MALE,OU=NAJRAN,DC=std,DC=nu,DC=edu,DC=sa");

            Assert.NotEqual("OU=Graduates,DC=std,DC=nu,DC=edu,DC=sa", parent);
        }

        [Fact]
        public void EscapedCommaInsideTheRdn_IsNotASeparator()
        {
            // A plain Split(',') would cut here and compare the wrong parent, so an account whose
            // CN contains a comma would be "moved" on every run.
            var (rdn, parent) = ActiveDirectoryConnector.SplitDn(
                @"CN=Smith\, John,OU=Staff,DC=test");

            Assert.Equal(@"CN=Smith\, John", rdn);
            Assert.Equal("OU=Staff,DC=test", parent);
        }

        [Fact]
        public void CasingDoesNotMakeAnAccountLookMisplaced()
        {
            // AD returns the DN using the directory's own casing, which need not match the
            // ActionValue an admin typed into the rule.
            var (_, parent) = ActiveDirectoryConnector.SplitDn(
                "CN=1,ou=graduates,dc=std,dc=nu");

            Assert.Equal("OU=Graduates,DC=std,DC=nu", parent, ignoreCase: true);
        }

        [Fact]
        public void DnWithNoComma_YieldsAnEmptyParent()
        {
            var (rdn, parent) = ActiveDirectoryConnector.SplitDn("DC=test");

            Assert.Equal("DC=test", rdn);
            Assert.Equal(string.Empty, parent);
        }
    }
}
