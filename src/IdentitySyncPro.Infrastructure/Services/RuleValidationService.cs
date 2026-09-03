using IdentitySyncPro.Core.Models.Rules;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Validates sync rules before saving to prevent configuration errors
    /// that could cause sync failures in production.
    /// </summary>
    public class RuleValidationService
    {
        private static readonly HashSet<string> ValidRuleTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Join", "Projection", "ImportFlow", "ExportFlow", "Provisioning", "Deprovisioning"
        };

        private static readonly HashSet<string> ValidDirections = new(StringComparer.OrdinalIgnoreCase)
        {
            "Inbound", "Outbound"
        };

        private static readonly HashSet<string> ValidTransforms = new(StringComparer.OrdinalIgnoreCase)
        {
            "none", "ToUpper", "ToLower", "Trim", "GetInitials"
        };

        /// <summary>Validate a SyncRuleV2 before save. Returns list of errors (empty = valid).</summary>
        public List<string> Validate(SyncRuleV2 rule)
        {
            var errors = new List<string>();

            // Name
            if (string.IsNullOrWhiteSpace(rule.Name))
                errors.Add("Rule name is required.");

            if (rule.Name?.Length > 200)
                errors.Add("Rule name cannot exceed 200 characters.");

            // RuleType
            if (!ValidRuleTypes.Contains(rule.RuleType ?? ""))
                errors.Add($"Invalid rule type '{rule.RuleType}'. Valid types: {string.Join(", ", ValidRuleTypes)}");

            // Direction
            if (!ValidDirections.Contains(rule.Direction ?? ""))
                errors.Add($"Invalid direction '{rule.Direction}'. Must be 'Inbound' or 'Outbound'.");

            // Priority
            if (rule.Priority < 1 || rule.Priority > 999)
                errors.Add("Priority must be between 1 and 999.");

            // ConditionJson — validate JSON if present
            if (!string.IsNullOrEmpty(rule.ConditionJson))
            {
                try
                {
                    var doc = System.Text.Json.JsonDocument.Parse(rule.ConditionJson);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("Field", out var field) &&
                        string.IsNullOrWhiteSpace(field.GetString()))
                    {
                        errors.Add("Condition 'Field' cannot be empty.");
                    }

                    if (root.TryGetProperty("Operator", out var op))
                    {
                        var validOps = new[] { "==", "!=", "in", "not_in", "exists", "not_exists" };
                        if (!validOps.Contains(op.GetString()))
                            errors.Add($"Invalid condition operator '{op.GetString()}'. Valid: {string.Join(", ", validOps)}");
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    errors.Add("ConditionJson is not valid JSON.");
                }
            }

            // ConfigurationJson — validate JSON if present
            if (!string.IsNullOrEmpty(rule.ConfigurationJson) && rule.ConfigurationJson != "{}")
            {
                try
                {
                    System.Text.Json.JsonDocument.Parse(rule.ConfigurationJson);
                }
                catch (System.Text.Json.JsonException)
                {
                    errors.Add("ConfigurationJson is not valid JSON.");
                }
            }

            // FlowMappings — validate if present
            if (rule.FlowMappings != null)
            {
                foreach (var mapping in rule.FlowMappings)
                {
                    if (string.IsNullOrWhiteSpace(mapping.SourceAttribute))
                        errors.Add($"Flow mapping has empty source attribute.");

                    if (string.IsNullOrWhiteSpace(mapping.TargetAttribute))
                        errors.Add($"Flow mapping has empty target attribute.");

                    // Validate transform (allow Format: and Expression: prefixes)
                    if (!string.IsNullOrEmpty(mapping.Transform) &&
                        !ValidTransforms.Contains(mapping.Transform) &&
                        !mapping.Transform.StartsWith("Format:") &&
                        !mapping.Transform.StartsWith("Expression:"))
                    {
                        errors.Add($"Invalid transform '{mapping.Transform}' on mapping {mapping.SourceAttribute} → {mapping.TargetAttribute}.");
                    }
                }
            }

            return errors;
        }
    }
}
