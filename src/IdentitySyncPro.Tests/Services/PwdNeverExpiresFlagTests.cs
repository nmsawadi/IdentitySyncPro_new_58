using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// The bit arithmetic behind "remove the password-never-expires flag".
    ///
    /// userAccountControl is a single integer holding every account flag at once, and AD requires
    /// the whole value on write. Clearing one bit therefore means rewriting all of them — get the
    /// arithmetic wrong and the account silently loses whatever else it had set, which no error
    /// reports and nobody notices until something else breaks.
    /// </summary>
    public class PwdNeverExpiresFlagTests
    {
        private const int UF_ACCOUNTDISABLE = 0x0002;        // 2
        private const int UF_NORMAL_ACCOUNT = 0x0200;        // 512
        private const int UF_DONT_EXPIRE_PASSWORD = 0x10000; // 65536
        private const int UF_SMARTCARD_REQUIRED = 0x40000;   // 262144
        private const int UF_TRUSTED_FOR_DELEGATION = 0x80000;

        private static int ClearNeverExpires(int uac) => uac & ~UF_DONT_EXPIRE_PASSWORD;

        [Fact]
        public void ClearsTheFlagOnAPlainAccount()
        {
            var before = UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD;   // 66048

            var after = ClearNeverExpires(before);

            Assert.Equal(UF_NORMAL_ACCOUNT, after);
            Assert.Equal(0, after & UF_DONT_EXPIRE_PASSWORD);
        }

        [Fact]
        public void PreservesEveryOtherFlag()
        {
            // The whole point of &~ rather than assigning a fresh value: an account may carry
            // smartcard-required and delegation settings that have nothing to do with passwords.
            var before = UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD
                       | UF_SMARTCARD_REQUIRED | UF_TRUSTED_FOR_DELEGATION;

            var after = ClearNeverExpires(before);

            Assert.Equal(0, after & UF_DONT_EXPIRE_PASSWORD);
            Assert.NotEqual(0, after & UF_NORMAL_ACCOUNT);
            Assert.NotEqual(0, after & UF_SMARTCARD_REQUIRED);
            Assert.NotEqual(0, after & UF_TRUSTED_FOR_DELEGATION);
        }

        [Fact]
        public void LeavesTheDisabledBitAlone()
        {
            // Safe Sync: this service disables nothing and re-enables nothing. Clearing a password
            // flag must not change whether the account can log in.
            var disabledWithFlag = UF_NORMAL_ACCOUNT | UF_ACCOUNTDISABLE | UF_DONT_EXPIRE_PASSWORD;

            var after = ClearNeverExpires(disabledWithFlag);

            Assert.Equal(UF_ACCOUNTDISABLE, after & UF_ACCOUNTDISABLE);
        }

        [Fact]
        public void IsIdempotent()
        {
            // A second run over an account already cleared must be a no-op, not a corruption.
            var once = ClearNeverExpires(UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWORD);
            var twice = ClearNeverExpires(once);

            Assert.Equal(once, twice);
        }

        [Fact]
        public void AnAccountWithoutTheFlagIsUnchanged()
        {
            var before = UF_NORMAL_ACCOUNT | UF_SMARTCARD_REQUIRED;

            Assert.Equal(before, ClearNeverExpires(before));
        }

        /// <summary>
        /// Zero is what an unparseable userAccountControl becomes. Writing the cleared value of
        /// zero would strip every flag on the account, so the executor refuses that case instead
        /// of computing with it — this test records why the guard exists.
        /// </summary>
        [Fact]
        public void ZeroWouldWipeEveryFlag_WhichIsWhyItIsRefusedBeforeWriting()
        {
            Assert.Equal(0, ClearNeverExpires(0));
        }
    }
}
