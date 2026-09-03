using IdentitySyncPro.Core.Helpers;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Classifying a failed write as "the directory is down" versus "this record is bad".
    ///
    /// Why it matters: the circuit breaker opens after three consecutive failures, and an intake
    /// batch is ordered by identity number — so a new college or city arrives as a contiguous
    /// block of records that share the same defect. Three neighbours failing together would halt
    /// every run at the same point and leave the rest of a ~5,000 student intake without accounts.
    ///
    /// The classifier is only allowed to *excuse* failures it positively recognises as record
    /// faults; anything else keeps counting, so an actual outage still stops the run.
    /// </summary>
    public class SyncFailureClassifierTests
    {
        [Theory]
        [InlineData("The operation was aborted because the client side timeout limit was exceeded.")]
        [InlineData("The LDAP server is unavailable.")]
        [InlineData("The server is not operational.")]
        [InlineData("A local error occurred: cannot contact the domain controller")]
        [InlineData("Failed to connect to std.nu.edu.sa:389")]
        public void DirectoryLevelFailures_CountTowardsTheBreaker(string error)
        {
            var kind = SyncFailureClassifier.Classify(error);

            Assert.Equal(SyncFailureKind.Transport, kind);
            Assert.True(SyncFailureClassifier.CountsTowardsCircuitBreaker(kind));
        }

        [Theory]
        [InlineData("noSuchObject: the target OU does not exist")]
        [InlineData("entryAlreadyExists")]
        [InlineData("constraintViolation: value rejected")]
        [InlineData("invalidAttributeSyntax on attribute mail")]
        [InlineData("The server is unwilling to perform the requested operation")]
        public void RecordLevelFailures_DoNotCountTowardsTheBreaker(string error)
        {
            var kind = SyncFailureClassifier.Classify(error);

            Assert.Equal(SyncFailureKind.Data, kind);
            Assert.False(SyncFailureClassifier.CountsTowardsCircuitBreaker(kind));
        }

        /// <summary>
        /// Missing permissions look per-record but are not: the service account cannot write any
        /// account, so continuing would fail 5,000 times in a row and report it as bad data.
        /// </summary>
        [Fact]
        public void MissingPermissions_CountAsDirectoryLevel()
        {
            var kind = SyncFailureClassifier.Classify("insufficientAccessRights (0x32)");

            Assert.Equal(SyncFailureKind.Transport, kind);
            Assert.True(SyncFailureClassifier.CountsTowardsCircuitBreaker(kind));
        }

        /// <summary>
        /// ⚠️ The safety default. An unrecognised failure must keep the breaker's existing
        /// sensitivity — the classifier may only ever reduce false stops, never blind it.
        /// </summary>
        [Theory]
        [InlineData("something nobody anticipated")]
        [InlineData("")]
        [InlineData(null)]
        public void UnrecognisedFailures_StillCountTowardsTheBreaker(string? error)
        {
            var kind = SyncFailureClassifier.Classify(error);

            Assert.Equal(SyncFailureKind.Unknown, kind);
            Assert.True(SyncFailureClassifier.CountsTowardsCircuitBreaker(kind));
        }

        [Fact]
        public void TransportIsCheckedFirst_SoATimeoutIsNeverReadAsBadData()
        {
            // A timeout while writing one record still means the directory is not answering.
            var kind = SyncFailureClassifier.Classify(
                "noSuchObject was expected but the operation timed out first");

            Assert.Equal(SyncFailureKind.Transport, kind);
        }

        [Fact]
        public void WrappedExceptions_AreClassifiedFromTheInnerCause()
        {
            // The connector wraps the real LDAP fault in an InvalidOperationException, which on
            // its own says nothing about which kind of failure occurred.
            var ex = new InvalidOperationException(
                "Account created but password setup failed.",
                new Exception("noSuchObject: OU=FEMALE,OU=NEWCITY does not exist"));

            Assert.Equal(SyncFailureKind.Data, SyncFailureClassifier.Classify(ex));
        }

        [Fact]
        public void TimeoutException_IsTransportRegardlessOfMessage()
        {
            Assert.Equal(SyncFailureKind.Transport,
                SyncFailureClassifier.Classify(new TimeoutException()));
        }

        /// <summary>
        /// Cancellation is an operator decision and must not be mistaken for either kind of fault.
        /// </summary>
        [Fact]
        public void Cancellation_IsNotAFault()
        {
            Assert.Equal(SyncFailureKind.Unknown,
                SyncFailureClassifier.Classify(new OperationCanceledException()));
        }
    }
}
