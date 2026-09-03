using System.Text.Json;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Advanced Rules Engine — evaluates SyncRuleV2 rules against identity data.
    /// Supports all FIM/MIM rule types with condition evaluation, transform execution, and preview.
    /// </summary>
    public class RulesEngineService : IRulesEngine
    {
        private readonly AppDbContext _db;
        private readonly ISourceConnector _sourceConnector;
        private readonly ILogger<RulesEngineService> _logger;

        public RulesEngineService(
            AppDbContext db,
            ISourceConnector sourceConnector,
            ILogger<RulesEngineService> logger)
        {
            _db = db;
            _sourceConnector = sourceConnector;
            _logger = logger;
        }

        public async Task<List<SyncRuleV2>> GetRulesAsync(string? ruleType = null, int? tenantId = null, CancellationToken ct = default)
        {
            var db = _db;

            var query = db.SyncRulesV2
                .Include(r => r.FlowMappings.OrderBy(m => m.DisplayOrder))
                .AsQueryable();

            // null tenantId = every tenant (admin listing). A value scopes to that tenant.
            if (tenantId.HasValue)
                query = query.Where(r => r.TenantId == tenantId.Value);

            if (!string.IsNullOrEmpty(ruleType))
                query = query.Where(r => r.RuleType == ruleType);

            return await query.OrderBy(r => r.TenantId).ThenBy(r => r.Priority).ToListAsync(ct);
        }

        public async Task<List<RuleEvaluationResult>> EvaluateRulesAsync(
            Dictionary<string, object?> sourceAttributes,
            string? ruleType = null,
            int? tenantId = null,
            CancellationToken ct = default)
        {
            // Evaluation must never mix tenants: without an explicit tenant, fall back to the
            // first active one (the pre-multi-tenant behaviour) rather than every tenant's rules.
            var scopeTenantId = tenantId
                ?? (await _db.TenantSettings.FirstOrDefaultAsync(t => t.IsActive, ct))?.Id;
            if (scopeTenantId == null) return new List<RuleEvaluationResult>();

            var rules = await GetRulesAsync(ruleType, scopeTenantId, ct);
            var results = new List<RuleEvaluationResult>();

            foreach (var rule in rules.Where(r => r.Enabled))
            {
                var result = new RuleEvaluationResult
                {
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    RuleType = rule.RuleType
                };

                // Check scope filter
                if (!EvaluateScopeFilter(rule, sourceAttributes))
                {
                    result.Matched = false;
                    results.Add(result);
                    continue;
                }

                // Check JSON condition
                if (!EvaluateCondition(rule, sourceAttributes))
                {
                    result.Matched = false;
                    results.Add(result);
                    continue;
                }

                result.Matched = true;

                // Compute attribute changes for flow rules
                if (rule.RuleType is "ImportFlow" or "ExportFlow")
                {
                    result.AttributeChanges = ComputeFlowMappings(rule, sourceAttributes);
                    result.Action = $"Map {result.AttributeChanges.Count} attributes";
                }
                else
                {
                    result.Action = $"{rule.RuleType}: {rule.ConfigurationJson}";
                }

                results.Add(result);
            }

            return results;
        }

        public async Task<RulePreviewResult> PreviewRuleAsync(int ruleId, int identityId, CancellationToken ct = default)
        {
            var db = _db;

            var rule = await db.SyncRulesV2
                .Include(r => r.FlowMappings)
                .FirstOrDefaultAsync(r => r.Id == ruleId, ct);

            if (rule == null)
                return new RulePreviewResult { Success = false, Error = "Rule not found" };

            // Get identity data from source
            var identities = await _sourceConnector.ReadBatchAsync(new[] { identityId }, ct);
            var identity = identities.FirstOrDefault();
            if (identity == null)
                return new RulePreviewResult { Success = false, Error = $"Identity {identityId} not found" };

            var sourceAttrs = identity.ToDictionary();
            var beforeAttrs = sourceAttrs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value?.ToString() ?? "");

            // Evaluate against the previewed rule's own tenant, not whichever tenant is active.
            var evalResults = await EvaluateRulesAsync(sourceAttrs, rule.RuleType, rule.TenantId, ct);

            // Compute after state
            var afterAttrs = new Dictionary<string, string>(beforeAttrs);
            foreach (var r in evalResults.Where(e => e.Matched && e.AttributeChanges != null))
            {
                foreach (var change in r.AttributeChanges!)
                    afterAttrs[change.Key] = change.Value;
            }

            return new RulePreviewResult
            {
                Success = true,
                RuleId = ruleId,
                IdentityId = identityId.ToString(),
                Results = evalResults,
                BeforeAttributes = beforeAttrs,
                AfterAttributes = afterAttrs
            };
        }

        public async Task<RulesStats> GetStatsAsync(int? tenantId = null, CancellationToken ct = default)
        {
            var db = _db;

            var query = db.SyncRulesV2.AsQueryable();

            // null tenantId = totals across every tenant.
            if (tenantId.HasValue)
                query = query.Where(r => r.TenantId == tenantId.Value);

            var rules = await query.ToListAsync(ct);

            return new RulesStats
            {
                TotalRules = rules.Count,
                EnabledRules = rules.Count(r => r.Enabled),
                JoinRules = rules.Count(r => r.RuleType == "Join"),
                ImportFlowRules = rules.Count(r => r.RuleType == "ImportFlow"),
                ExportFlowRules = rules.Count(r => r.RuleType == "ExportFlow"),
                ProvisioningRules = rules.Count(r => r.RuleType == "Provisioning"),
                DeprovisioningRules = rules.Count(r => r.RuleType == "Deprovisioning"),
                ProjectionRules = rules.Count(r => r.RuleType == "Projection")
            };
        }

        // ═══════════════════════════════════════
        // PRIVATE HELPERS
        // ═══════════════════════════════════════

        private bool EvaluateScopeFilter(SyncRuleV2 rule, Dictionary<string, object?> attrs)
        {
            if (string.IsNullOrEmpty(rule.ScopeFilter)) return true;

            try
            {
                // Simple scope: "field==value" or "field!=value" or "field in value1,value2"
                var parts = rule.ScopeFilter.Split(new[] { "==", "!=", " in " }, 2, StringSplitOptions.TrimEntries);
                if (parts.Length < 2) return true;

                var fieldName = parts[0];
                var expectedValue = parts[1];
                var fieldValue = attrs.TryGetValue(fieldName, out var v) ? v?.ToString() : null;

                if (rule.ScopeFilter.Contains("!="))
                    return fieldValue != expectedValue;
                if (rule.ScopeFilter.Contains(" in "))
                    return expectedValue.Split(',').Any(x => x.Trim() == fieldValue);
                return fieldValue == expectedValue;
            }
            catch
            {
                return true;
            }
        }

        private bool EvaluateCondition(SyncRuleV2 rule, Dictionary<string, object?> attrs)
        {
            if (string.IsNullOrEmpty(rule.ConditionJson)) return true;

            try
            {
                var condition = JsonSerializer.Deserialize<RuleCondition>(rule.ConditionJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (condition == null) return true;

                var fieldValue = attrs.TryGetValue(condition.Field ?? "", out var v) ? v?.ToString() : null;

                return condition.Operator switch
                {
                    "==" => fieldValue == condition.Value,
                    "!=" => fieldValue != condition.Value,
                    "in" => condition.Value?.Split(',').Any(x => x.Trim() == fieldValue) ?? false,
                    "not_in" => !(condition.Value?.Split(',').Any(x => x.Trim() == fieldValue) ?? false),
                    "exists" => !string.IsNullOrEmpty(fieldValue),
                    "not_exists" => string.IsNullOrEmpty(fieldValue),
                    _ => true
                };
            }
            catch
            {
                return true;
            }
        }

        private Dictionary<string, string> ComputeFlowMappings(SyncRuleV2 rule, Dictionary<string, object?> sourceAttrs)
        {
            var changes = new Dictionary<string, string>();

            foreach (var mapping in rule.FlowMappings)
            {
                var sourceValue = sourceAttrs.TryGetValue(mapping.SourceAttribute, out var v) ? v?.ToString() : null;

                if (string.IsNullOrEmpty(sourceValue))
                {
                    if (!string.IsNullOrEmpty(mapping.DefaultValue))
                        sourceValue = mapping.DefaultValue;
                    else if (mapping.IsRequired)
                        continue;
                    else
                        continue;
                }

                var targetValue = ApplyTransform(sourceValue, mapping.Transform, sourceAttrs);
                changes[mapping.TargetAttribute] = targetValue;
            }

            return changes;
        }

        private string ApplyTransform(string value, string transform, Dictionary<string, object?> allAttrs)
        {
            if (string.IsNullOrEmpty(transform) || transform == "none")
                return value;

            if (transform == "ToUpper") return value.ToUpperInvariant();
            if (transform == "ToLower") return value.ToLowerInvariant();
            if (transform == "Trim") return value.Trim();

            if (transform.StartsWith("Format:"))
            {
                var template = transform.Substring(7);
                return template.Replace("{0}", value);
            }

            if (transform == "GetInitials")
            {
                // If name > 4 chars → take first letter only (e.g., "MOHAMMED" → "M")
                // If name <= 4 chars → keep as-is (e.g., "ALI" → "ALI")
                value = value.Trim();
                return value.Length > 4 ? value.Substring(0, 1).ToUpperInvariant() : value;
            }

            return value;
        }

        private class RuleCondition
        {
            public string? Field { get; set; }
            public string? Operator { get; set; }
            public string? Value { get; set; }
        }
    }
}
