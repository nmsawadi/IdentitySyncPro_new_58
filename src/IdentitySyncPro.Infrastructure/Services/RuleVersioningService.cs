using System.Text.Json;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Rule Versioning Service — saves snapshots of rules on every change,
    /// supports rollback and version comparison.
    /// </summary>
    public class RuleVersioningService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<RuleVersioningService> _logger;

        public RuleVersioningService(AppDbContext db, ILogger<RuleVersioningService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>Save a version snapshot of a rule.</summary>
        public async Task SaveVersionAsync(int ruleId, string? changeNotes = null, string? changedBy = null)
        {
            var db = _db;

            var rule = await db.SyncRulesV2
                .Include(r => r.FlowMappings)
                .FirstOrDefaultAsync(r => r.Id == ruleId);

            if (rule == null) return;

            // Get next version number
            var lastVersion = await db.SyncRuleVersions
                .Where(v => v.SyncRuleV2Id == ruleId)
                .MaxAsync(v => (int?)v.VersionNumber) ?? 0;

            // Mark all previous versions as non-current
            await db.SyncRuleVersions
                .Where(v => v.SyncRuleV2Id == ruleId && v.IsCurrent)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false));

            // Create snapshot
            var snapshot = JsonSerializer.Serialize(new
            {
                rule.Name,
                rule.Description,
                rule.Enabled,
                rule.Priority,
                rule.RuleType,
                rule.Direction,
                rule.SourceSystem,
                rule.TargetSystem,
                rule.ScopeFilter,
                rule.ConditionJson,
                rule.ConfigurationJson,
                FlowMappings = rule.FlowMappings.Select(m => new
                {
                    m.SourceAttribute,
                    m.TargetAttribute,
                    m.Transform,
                    m.IsRequired,
                    m.DefaultValue,
                    m.DisplayOrder
                })
            });

            db.SyncRuleVersions.Add(new SyncRuleVersion
            {
                SyncRuleV2Id = ruleId,
                VersionNumber = lastVersion + 1,
                SnapshotJson = snapshot,
                ChangeNotes = changeNotes,
                ChangedBy = changedBy ?? "System",
                IsCurrent = true
            });

            await db.SaveChangesAsync();
            _logger.LogInformation("Rule {RuleId} saved as version {Version}", ruleId, lastVersion + 1);
        }

        /// <summary>Get all versions of a rule.</summary>
        public async Task<List<SyncRuleVersion>> GetVersionsAsync(int ruleId)
        {
            var db = _db;

            return await db.SyncRuleVersions
                .Where(v => v.SyncRuleV2Id == ruleId)
                .OrderByDescending(v => v.VersionNumber)
                .ToListAsync();
        }

        /// <summary>Rollback a rule to a specific version.</summary>
        public async Task<bool> RollbackAsync(int ruleId, int versionNumber)
        {
            var db = _db;

            var version = await db.SyncRuleVersions
                .FirstOrDefaultAsync(v => v.SyncRuleV2Id == ruleId && v.VersionNumber == versionNumber);

            if (version == null) return false;

            var rule = await db.SyncRulesV2
                .Include(r => r.FlowMappings)
                .FirstOrDefaultAsync(r => r.Id == ruleId);

            if (rule == null) return false;

            // Save current state before rollback
            await SaveVersionAsync(ruleId, $"Auto-save before rollback to v{versionNumber}", "System");

            // Restore from snapshot
            var snapshot = JsonSerializer.Deserialize<JsonElement>(version.SnapshotJson);

            rule.Name = snapshot.GetProperty("Name").GetString() ?? rule.Name;
            rule.Description = snapshot.TryGetProperty("Description", out var desc) ? desc.GetString() : null;
            rule.Enabled = snapshot.GetProperty("Enabled").GetBoolean();
            rule.Priority = snapshot.GetProperty("Priority").GetInt32();
            rule.RuleType = snapshot.GetProperty("RuleType").GetString() ?? rule.RuleType;
            rule.Direction = snapshot.GetProperty("Direction").GetString() ?? rule.Direction;
            rule.ScopeFilter = snapshot.TryGetProperty("ScopeFilter", out var sf) ? sf.GetString() : null;
            rule.ConditionJson = snapshot.TryGetProperty("ConditionJson", out var cj) ? cj.GetString() : null;
            rule.ConfigurationJson = snapshot.TryGetProperty("ConfigurationJson", out var cfg) ? cfg.GetString() ?? "{}" : "{}";
            rule.ModifiedDate = DateTime.UtcNow;

            // Restore flow mappings
            db.SyncRuleFlowMappings.RemoveRange(rule.FlowMappings);

            if (snapshot.TryGetProperty("FlowMappings", out var mappings))
            {
                foreach (var m in mappings.EnumerateArray())
                {
                    rule.FlowMappings.Add(new SyncRuleFlowMapping
                    {
                        SourceAttribute = m.GetProperty("SourceAttribute").GetString() ?? "",
                        TargetAttribute = m.GetProperty("TargetAttribute").GetString() ?? "",
                        Transform = m.TryGetProperty("Transform", out var t) ? t.GetString() ?? "none" : "none",
                        IsRequired = m.TryGetProperty("IsRequired", out var ir) && ir.GetBoolean(),
                        DefaultValue = m.TryGetProperty("DefaultValue", out var dv) ? dv.GetString() : null,
                        DisplayOrder = m.TryGetProperty("DisplayOrder", out var d) ? d.GetInt32() : 0
                    });
                }
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Rule {RuleId} rolled back to version {Version}", ruleId, versionNumber);
            return true;
        }

        /// <summary>Compare two versions of a rule.</summary>
        public async Task<object?> CompareVersionsAsync(int ruleId, int v1, int v2)
        {
            var db = _db;

            var ver1 = await db.SyncRuleVersions
                .FirstOrDefaultAsync(v => v.SyncRuleV2Id == ruleId && v.VersionNumber == v1);
            var ver2 = await db.SyncRuleVersions
                .FirstOrDefaultAsync(v => v.SyncRuleV2Id == ruleId && v.VersionNumber == v2);

            if (ver1 == null || ver2 == null) return null;

            return new { Version1 = ver1, Version2 = ver2 };
        }
    }
}
