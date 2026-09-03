using IdentitySyncPro.Core.Models.Identity;

namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Interface for data connectors (Oracle source, AD target).
    /// </summary>
    public interface IConnector
    {
        string Name { get; }
        string Type { get; }
        Task<bool> TestConnectionAsync(CancellationToken ct = default);
        Task<string> GetConnectionInfoAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Source connector for reading identity data.
    /// Rows are returned as dynamic SourceRecords carrying ALL view columns by
    /// their real names — the source schema is fully configurable per tenant.
    /// </summary>
    public interface ISourceConnector : IConnector
    {
        Task<IEnumerable<int>> ReadAllIdsAsync(CancellationToken ct = default);
        Task<IEnumerable<SourceRecord>> ReadBatchAsync(int[] ids, CancellationToken ct = default);
        Task<IEnumerable<SourceRecord>> ReadAllAsync(CancellationToken ct = default);
        Task<int> GetTotalCountAsync(CancellationToken ct = default);

        /// <summary>Column names exposed by the configured source view (for the mapping UI).</summary>
        Task<List<string>> GetColumnNamesAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Target connector for writing identity data.
    /// </summary>
    public interface ITargetConnector : IConnector
    {
        Task<bool> ExistsAsync(string identity, CancellationToken ct = default);

        /// <summary>
        /// Finds the sAMAccountName of the account carrying <paramref name="value"/> in
        /// <paramref name="attributeName"/>, or null when no account does.
        ///
        /// This is the join for tenants whose account name is derived from a person's name and can
        /// therefore change: the name is not a reliable key, so an immutable attribute
        /// (extensionAttribute2 holding the employee number) is matched instead.
        ///
        /// Returns null — never a guess — when more than one account matches, because picking one
        /// arbitrarily would silently write a second person's identity onto the first person's
        /// account. The caller is expected to report the ambiguity.
        ///
        /// Default implementation returns null so connectors that do not support attribute lookup
        /// keep the sAMAccountName behaviour rather than failing to compile.
        /// </summary>
        Task<string?> FindAccountByAttributeAsync(string attributeName, string value, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        /// <summary>
        /// Create a new account using dynamically mapped attributes from MappingEngine.
        /// The target OU and group memberships are resolved from tenant configuration
        /// (TenantOURule / TenantGroupRule) — nothing is hardcoded per organization.
        /// </summary>
        /// <param name="identity">The account identifier (sAMAccountName)</param>
        /// <param name="mappedAttributes">AD attributes produced by MappingEngine.ApplyMappings</param>
        /// <param name="targetOU">Full OU distinguished name where the account is created</param>
        /// <param name="groups">Group names/DNs the account should be added to</param>
        /// <param name="password">Initial password (falls back to the connector default when null)</param>
        Task<SyncResult> CreateDynamicAsync(string identity, Dictionary<string, string> mappedAttributes,
            string targetOU, IEnumerable<string> groups, string? password, CancellationToken ct = default);

        /// <summary>
        /// Update AD user using dynamically mapped attributes from MappingEngine.
        /// Reads current AD values for the given attributes, compares, and updates changed ones.
        /// </summary>
        Task<SyncResult> UpdateDynamicAsync(string identity, Dictionary<string, string> mappedAttributes, CancellationToken ct = default);

        Task<Dictionary<string, string>> GetCurrentAttributesAsync(string identity, CancellationToken ct = default);
        Task<bool> MoveToOUAsync(string identity, string targetOU, CancellationToken ct = default);
        Task<bool> DisableAccountAsync(string identity, CancellationToken ct = default);

        /// <summary>
        /// Remove user from all AD security/distribution groups (except primary group Domain Users).
        /// Used by LifecycleEngine when identities are no longer active (suspended, offboarded, etc.).
        /// </summary>
        Task<(bool Success, int RemovedCount, List<string> GroupNames)> RemoveFromAllGroupsAsync(string identity, CancellationToken ct = default);

        /// <summary>
        /// Remove user from specific AD groups by name (comma-separated).
        /// Used by LifecycleEngine when a rule specifies particular groups to remove.
        /// </summary>
        Task<(bool Success, int RemovedCount, List<string> GroupNames)> RemoveFromSpecificGroupsAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default);

        /// <summary>
        /// Add user to specific AD groups by name.
        /// Used by LifecycleEngine when an identity is reactivated and needs to be added back to groups.
        /// </summary>
        Task<(bool Success, int AddedCount, List<string> GroupNames)> AddToGroupsAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default);

        /// <summary>
        /// Get the current OU path of a user in AD.
        /// Used to determine if an OU move is needed during lifecycle transitions.
        /// </summary>
        Task<string?> GetCurrentOUAsync(string identity, CancellationToken ct = default);

        /// <summary>
        /// Load specific AD attributes for an account by sAMAccountName.
        /// Returns null when the account is not found. Keys are attribute names
        /// (plus "dn"); missing attributes are simply absent.
        /// </summary>
        Task<Dictionary<string, string>?> GetAttributesAsync(string identity, string[] attributes, CancellationToken ct = default);

        /// <summary>
        /// Reset an account's password (self-service reset). Also clears the AD
        /// lockout flag so a previously locked user can sign in immediately.
        /// </summary>
        Task<(bool Success, string? Error)> ResetPasswordAsync(string identity, string newPassword, CancellationToken ct = default);

        /// <summary>
        /// True when the account is a (nested) member of ANY of the given groups.
        /// Used to deny self-service reset for admin/service accounts.
        ///
        /// <b>Answers true when the directory cannot be reached</b> — correct for an exclusion
        /// question ("is this account forbidden?") and dangerous for a permission one, where it
        /// would grant the right to everyone during an outage. Permission checks use
        /// <see cref="TryIsMemberOfAnyAsync"/>.
        /// </summary>
        Task<bool> IsMemberOfAnyAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default);

        /// <summary>
        /// Nested membership as three values: true, false, or <c>null</c> when the directory could
        /// not answer — so a caller asking a permission question can fail closed in its own
        /// direction instead of inheriting one chosen for a different question.
        /// </summary>
        /// <remarks>
        /// Default returns <c>null</c>: a connector that has not implemented this genuinely cannot
        /// tell, and saying so denies the permission rather than inventing an answer.
        /// </remarks>
        Task<bool?> TryIsMemberOfAnyAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
            => Task.FromResult<bool?>(null);

        /// <summary>
        /// Every account in a group, as a complete list or an explicit failure.
        ///
        /// Certification depends on this being complete rather than merely plausible. A snapshot
        /// that quietly stops at the directory's page limit produces a campaign that reviews the
        /// first thousand members and closes reporting that everything was certified — the rest
        /// were never in it, so nobody declined to review them and nothing says they were missed.
        /// That is why the result carries success separately from the list: a partial read has to be
        /// reportable as a failure, not returned as a shorter answer.
        /// </summary>
        /// <returns>Success=false when the group could not be read in full; Members is then meaningless.</returns>
        Task<(bool Success, IReadOnlyList<GroupMember> Members, string? Error)> GetGroupMembersAsync(
            string groupName, bool nested = false, CancellationToken ct = default)
            => Task.FromResult<(bool, IReadOnlyList<GroupMember>, string?)>(
                (false, Array.Empty<GroupMember>(), "This connector cannot list group members."));
    }

    /// <summary>One account found in a group.</summary>
    /// <param name="Account">sAMAccountName — how every other part of the system names an identity.</param>
    /// <param name="DisplayName">For the reviewer, who does not recognise account names.</param>
    /// <param name="DistinguishedName">Where it lives, for the record.</param>
    public sealed record GroupMember(string Account, string? DisplayName, string DistinguishedName);

    public class SyncResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? ChangedFields { get; set; }
        public int DurationMs { get; set; }

        /// <summary>
        /// Whether a failure means the directory is unhealthy or that this one record is bad.
        /// Only <see cref="Helpers.SyncFailureKind.Data"/> is excused from the circuit breaker,
        /// so leaving this unset keeps the cautious pre-existing behaviour.
        /// </summary>
        public Helpers.SyncFailureKind FailureKind { get; set; } = Helpers.SyncFailureKind.Unknown;
    }
}
