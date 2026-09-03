using System.Text.Json;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    public class RulesEngineTests
    {
        private readonly Mock<ISourceConnector> _sourceConnector = new();
        private readonly Mock<ILogger<RulesEngineService>> _logger = new();

        private RulesEngineService CreateEngine(Infrastructure.Data.AppDbContext db)
        {
            return new RulesEngineService(db, _sourceConnector.Object, _logger.Object);
        }

        [Fact]
        public async Task GetRules_NoTenant_ReturnsEmptyList()
        {
            var db = TestDbContext.Create();
            var engine = CreateEngine(db);

            var rules = await engine.GetRulesAsync();

            Assert.Empty(rules);
        }

        [Fact]
        public async Task GetRules_WithTenant_ReturnsRulesForTenant()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test",
                IsActive = true,
                ADUsername = "a",
                ADPassword = "p",
                ADBaseDN = "DC=test"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            db.SyncRulesV2.AddRange(
                new SyncRuleV2 { TenantId = tenant.Id, Name = "Rule 1", RuleType = "ImportFlow", Priority = 1 },
                new SyncRuleV2 { TenantId = tenant.Id, Name = "Rule 2", RuleType = "ExportFlow", Priority = 2 }
            );
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);

            // Act
            var rules = await engine.GetRulesAsync();

            // Assert
            Assert.Equal(2, rules.Count);
            Assert.Equal("Rule 1", rules[0].Name); // Ordered by priority
        }

        [Fact]
        public async Task GetRules_FilterByType_ReturnsOnlyMatchingRules()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test",
                IsActive = true,
                ADUsername = "a",
                ADPassword = "p",
                ADBaseDN = "DC=test"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            db.SyncRulesV2.AddRange(
                new SyncRuleV2 { TenantId = tenant.Id, Name = "Import Rule", RuleType = "ImportFlow", Priority = 1 },
                new SyncRuleV2 { TenantId = tenant.Id, Name = "Export Rule", RuleType = "ExportFlow", Priority = 2 }
            );
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);

            // Act
            var rules = await engine.GetRulesAsync("ImportFlow");

            // Assert
            Assert.Single(rules);
            Assert.Equal("Import Rule", rules[0].Name);
        }

        [Fact]
        public async Task EvaluateRules_ConditionMatch_ReturnsMatched()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test",
                IsActive = true,
                ADUsername = "a",
                ADPassword = "p",
                ADBaseDN = "DC=test"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            var condition = JsonSerializer.Serialize(new { Field = "CITY", Operator = "==", Value = "CityA" });
            db.SyncRulesV2.Add(new SyncRuleV2
            {
                TenantId = tenant.Id,
                Name = "CityA Rule",
                RuleType = "ImportFlow",
                Enabled = true,
                Priority = 1,
                ConditionJson = condition
            });
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);
            var sourceAttrs = new Dictionary<string, object?> { { "CITY", "CityA" } };

            // Act
            var results = await engine.EvaluateRulesAsync(sourceAttrs, "ImportFlow");

            // Assert
            Assert.Single(results);
            Assert.True(results[0].Matched);
        }

        [Fact]
        public async Task EvaluateRules_ConditionNoMatch_ReturnsNotMatched()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test",
                IsActive = true,
                ADUsername = "a",
                ADPassword = "p",
                ADBaseDN = "DC=test"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            var condition = JsonSerializer.Serialize(new { Field = "CITY", Operator = "==", Value = "CityB" });
            db.SyncRulesV2.Add(new SyncRuleV2
            {
                TenantId = tenant.Id,
                Name = "CityB Rule",
                RuleType = "ImportFlow",
                Enabled = true,
                Priority = 1,
                ConditionJson = condition
            });
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);
            var sourceAttrs = new Dictionary<string, object?> { { "CITY", "CityA" } };

            // Act
            var results = await engine.EvaluateRulesAsync(sourceAttrs, "ImportFlow");

            // Assert
            Assert.Single(results);
            Assert.False(results[0].Matched);
        }

        [Fact]
        public async Task EvaluateRules_DisabledRule_SkipsEvaluation()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test",
                IsActive = true,
                ADUsername = "a",
                ADPassword = "p",
                ADBaseDN = "DC=test"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            db.SyncRulesV2.Add(new SyncRuleV2
            {
                TenantId = tenant.Id,
                Name = "Disabled Rule",
                RuleType = "ImportFlow",
                Enabled = false,
                Priority = 1
            });
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);
            var sourceAttrs = new Dictionary<string, object?>();

            // Act
            var results = await engine.EvaluateRulesAsync(sourceAttrs, "ImportFlow");

            // Assert - disabled rules are returned but not matched
            Assert.Empty(results); // Disabled rules are filtered out before evaluation
        }

        [Fact]
        public async Task GetStats_ReturnsCorrectBreakdown()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test",
                IsActive = true,
                ADUsername = "a",
                ADPassword = "p",
                ADBaseDN = "DC=test"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            db.SyncRulesV2.AddRange(
                new SyncRuleV2 { TenantId = tenant.Id, Name = "R1", RuleType = "ImportFlow", Enabled = true, Priority = 1 },
                new SyncRuleV2 { TenantId = tenant.Id, Name = "R2", RuleType = "Join", Enabled = true, Priority = 2 },
                new SyncRuleV2 { TenantId = tenant.Id, Name = "R3", RuleType = "Provisioning", Enabled = false, Priority = 3 }
            );
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);

            // Act
            var stats = await engine.GetStatsAsync();

            // Assert
            Assert.Equal(3, stats.TotalRules);
            Assert.Equal(2, stats.EnabledRules);
            Assert.Equal(1, stats.ImportFlowRules);
            Assert.Equal(1, stats.JoinRules);
            Assert.Equal(1, stats.ProvisioningRules);
        }
    }
}
