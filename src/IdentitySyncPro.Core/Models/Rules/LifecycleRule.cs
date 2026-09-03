using IdentitySyncPro.Core.Models.Settings;

namespace IdentitySyncPro.Core.Models.Rules
{
    /// <summary>
    /// Defines lifecycle transition rules for identity identities.
    /// Controls when and how accounts transition between states:
    ///   Pending → Active → Suspended → Deprovisioned
    /// 
    /// Each rule has a trigger condition and an action to execute.
    /// </summary>
    public class LifecycleRule
    {
        public int Id { get; set; }
        public int TenantId { get; set; }

        /// <summary>Human-readable rule name</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Rule description for administrators</summary>
        public string? Description { get; set; }

        /// <summary>Whether this rule is active</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Execution priority — lower number = higher priority (first match wins)</summary>
        public int Priority { get; set; } = 100;

        // ══════════════════════════════════════
        // TRIGGER — When to fire this rule
        // ══════════════════════════════════════

        /// <summary>
        /// When this rule triggers: OnImport, OnSchedule, OnStateChange
        /// - OnImport: fires during sync when data is imported from source
        /// - OnSchedule: fires on a scheduled basis (e.g., grace period expiration)
        /// - OnStateChange: fires when lifecycle state changes
        /// </summary>
        public string TriggerType { get; set; } = "OnImport";

        /// <summary>Source field to evaluate (e.g., STATUS_CODE, DEPARTMENT_CODE)</summary>
        public string? ConditionField { get; set; }

        /// <summary>Comparison operator: ==, !=, in, not_in, gt, lt</summary>
        public string? ConditionOperator { get; set; }

        /// <summary>Value(s) to compare against (e.g., "4,5,6" for multiple status codes)</summary>
        public string? ConditionValue { get; set; }

        // ══════════════════════════════════════
        // ACTION — What to do when triggered
        // ══════════════════════════════════════

        /// <summary>
        /// Action to perform:
        /// - SetState: Change lifecycle state (ActionValue = new state)
        /// - MoveOU: Move account to a different OU (ActionValue = OU path or template)
        /// - DisableAD: Disable the AD account
        /// - EnableAD: Re-enable the AD account
        /// - RemoveGroups: Remove from all security groups
        /// - AddGroups: Add to specified groups (ActionValue = comma-separated group names)
        /// - SendSMS: Send SMS notification
        /// - Deprovision: Full deprovisioning (disable + move + remove groups)
        /// - Reactivate: Full reactivation (enable + move back + re-add groups)
        /// </summary>
        public string ActionType { get; set; } = "SetState";

        /// <summary>Action parameter — meaning depends on ActionType</summary>
        public string? ActionValue { get; set; }

        // ══════════════════════════════════════
        // GRACE PERIOD
        // ══════════════════════════════════════

        /// <summary>Wait N days before executing the action (null = immediate)</summary>
        public int? GracePeriodDays { get; set; }

        // ══════════════════════════════════════
        // METADATA
        // ══════════════════════════════════════

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

        // Navigation
        public TenantSettings? Tenant { get; set; }
    }
}
