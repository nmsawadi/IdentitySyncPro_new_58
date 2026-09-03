using IdentitySyncPro.Core.Models.Rules;

namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Advanced Rules Engine interface — evaluates and executes SyncRuleV2 rules.
    /// Supports: Join, Projection, ImportFlow, ExportFlow, Provisioning, Deprovisioning.
    /// </summary>
    public interface IRulesEngine
    {
        /// <summary>
        /// Get rules ordered by priority. <paramref name="tenantId"/> null = every tenant
        /// (admin listing); a value scopes to that tenant only.
        /// </summary>
        Task<List<SyncRuleV2>> GetRulesAsync(string? ruleType = null, int? tenantId = null, CancellationToken ct = default);

        /// <summary>
        /// Evaluate which rules apply to a given identity. <paramref name="tenantId"/> null falls
        /// back to the first active tenant — pass the identity's tenant when evaluating per-tenant.
        /// </summary>
        Task<List<RuleEvaluationResult>> EvaluateRulesAsync(
            Dictionary<string, object?> sourceAttributes,
            string? ruleType = null,
            int? tenantId = null,
            CancellationToken ct = default);

        /// <summary>Preview what a rule would do without executing it.</summary>
        Task<RulePreviewResult> PreviewRuleAsync(int ruleId, int identityId, CancellationToken ct = default);

        /// <summary>Get rules statistics. <paramref name="tenantId"/> null = across every tenant.</summary>
        Task<RulesStats> GetStatsAsync(int? tenantId = null, CancellationToken ct = default);
    }

    public class RuleEvaluationResult
    {
        public int RuleId { get; set; }
        public string RuleName { get; set; } = string.Empty;
        public string RuleType { get; set; } = string.Empty;
        public bool Matched { get; set; }
        public string? Action { get; set; }
        public Dictionary<string, string>? AttributeChanges { get; set; }
    }

    public class RulePreviewResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int RuleId { get; set; }
        public string IdentityId { get; set; } = string.Empty;
        public List<RuleEvaluationResult> Results { get; set; } = new();
        public Dictionary<string, string>? BeforeAttributes { get; set; }
        public Dictionary<string, string>? AfterAttributes { get; set; }
    }

    public class RulesStats
    {
        public int TotalRules { get; set; }
        public int EnabledRules { get; set; }
        public int JoinRules { get; set; }
        public int ImportFlowRules { get; set; }
        public int ExportFlowRules { get; set; }
        public int ProvisioningRules { get; set; }
        public int DeprovisioningRules { get; set; }
        public int ProjectionRules { get; set; }
    }
}
