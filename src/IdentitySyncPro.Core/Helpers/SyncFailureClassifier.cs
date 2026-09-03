namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>What a failed write says about the directory as a whole.</summary>
    public enum SyncFailureKind
    {
        /// <summary>Could not be classified. Treated as Transport — see the classifier's remarks.</summary>
        Unknown = 0,

        /// <summary>The directory itself is unreachable or unhealthy: connect, bind, timeout.</summary>
        Transport = 1,

        /// <summary>This one record is bad: missing OU, invalid attribute value, duplicate entry.</summary>
        Data = 2
    }

    /// <summary>
    /// Decides whether a failed write is evidence that Active Directory is down, or evidence that
    /// one record is malformed.
    ///
    /// The circuit breaker exists to stop a sync hammering a dead directory, and it opens after
    /// three consecutive failures. That is right for an outage and wrong for bad data — and bad
    /// data arrives in clusters, because an intake batch is ordered by identity number and a whole
    /// new college or city reaches the source together. Three neighbouring records with the same
    /// defect would stop the run at the same point on every attempt, leaving everyone behind them
    /// without an account: the same outage as before, by a different mechanism.
    ///
    /// ⚠️ <see cref="SyncFailureKind.Unknown"/> is deliberately treated as Transport by callers.
    /// The classifier only ever *relaxes* the breaker for failures it positively recognises as
    /// per-record faults. Anything unrecognised keeps the existing, cautious behaviour — the cost
    /// of a wrong guess in that direction is a run that stops early, against a run that keeps
    /// writing into a directory that cannot answer.
    /// </summary>
    public static class SyncFailureClassifier
    {
        /// <summary>
        /// LDAP result names and text that identify a fault in the record being written rather
        /// than in the connection carrying it.
        /// </summary>
        private static readonly string[] DataFaultMarkers =
        {
            "noSuchObject",              // target OU does not exist
            "no such object",
            "entryAlreadyExists",        // name already taken
            "already exists",
            "constraintViolation",       // value rejected by a schema/AD constraint
            "constraint violation",
            "objectClassViolation",
            "invalidAttributeSyntax",    // malformed attribute value
            "invalid attribute syntax",
            "undefinedAttributeType",
            "invalidDNSyntax",
            "invalid dn syntax",
            "namingViolation",
            "attributeOrValueExists",
            "willNotPerform",            // AD refused this specific write
            "will not perform",
            "unwillingToPerform",
            "unwilling to perform"
        };

        /// <summary>
        /// Connection-level trouble. Checked first: a timeout while writing one record still means
        /// the directory is not answering, which is exactly what the breaker is for.
        /// </summary>
        private static readonly string[] TransportFaultMarkers =
        {
            "timeout",
            "timed out",
            "server is not operational",
            "server down",
            "unavailable",
            "cannot contact",
            "connect",                   // connection refused / failed to connect
            "network",
            "busy",
            "invalidCredentials",        // the bind account is wrong — nothing will succeed
            "invalid credentials",
            "insufficientAccessRights",  // permissions missing — applies to every record, not one
            "insufficient access"
        };

        /// <summary>Classify from an exception thrown by a connector.</summary>
        public static SyncFailureKind Classify(Exception? ex)
        {
            if (ex == null) return SyncFailureKind.Unknown;

            // Cancellation is not a fault at all; callers handle it separately.
            if (ex is OperationCanceledException) return SyncFailureKind.Unknown;
            if (ex is TimeoutException) return SyncFailureKind.Transport;

            // Walk the chain: the connector wraps the real cause in an InvalidOperationException.
            for (var current = ex; current != null; current = current.InnerException)
            {
                var kind = Classify(current.Message);
                if (kind != SyncFailureKind.Unknown) return kind;
            }

            return SyncFailureKind.Unknown;
        }

        /// <summary>Classify from an error message, for failures already reduced to text.</summary>
        public static SyncFailureKind Classify(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return SyncFailureKind.Unknown;

            foreach (var marker in TransportFaultMarkers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return SyncFailureKind.Transport;
            }

            foreach (var marker in DataFaultMarkers)
            {
                if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return SyncFailureKind.Data;
            }

            return SyncFailureKind.Unknown;
        }

        /// <summary>
        /// Whether this failure should count towards opening the circuit breaker.
        /// Only a positively identified record fault is excused.
        /// </summary>
        public static bool CountsTowardsCircuitBreaker(SyncFailureKind kind)
            => kind != SyncFailureKind.Data;
    }
}
