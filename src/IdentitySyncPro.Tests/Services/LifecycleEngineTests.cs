using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Rules;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    public class LifecycleEngineTests
    {
        private readonly Mock<ISourceConnector> _sourceConnector = new();
        private readonly Mock<ITargetConnector> _targetConnector = new();
        private readonly Mock<ILogger<LifecycleEngine>> _logger = new();

        private LifecycleEngine CreateEngine(Infrastructure.Data.AppDbContext db)
        {
            return new LifecycleEngine(db, _sourceConnector.Object, _targetConnector.Object, _logger.Object);
        }

        [Fact]
        public async Task ApplyLifecycleRules_NoRules_AppliesDefaultTransition()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test Org",
                IsActive = true,
                ADUsername = "admin",
                ADPassword = "pass",
                ADBaseDN = "DC=test,DC=com"
            };
            db.TenantSettings.Add(tenant);

            var entry = new MetaverseEntry
            {
                ExternalId = "12345",
                LifecycleState = "Pending",
                SourceStatusCode = 1, // Active status
                AttributesJson = "{}"
            };
            db.MetaverseEntries.Add(entry);
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);

            // Act
            var result = await engine.ApplyLifecycleRulesAsync(entry);

            // Assert
            Assert.True(result.Success);
            Assert.Equal("Active", entry.LifecycleState);
        }

        [Fact]
        public async Task ApplyLifecycleRules_MatchingRule_SetsCorrectState()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test Org",
                IsActive = true,
                ADUsername = "admin",
                ADPassword = "pass",
                ADBaseDN = "DC=test,DC=com"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenant.Id,
                Name = "Suspend Withdrawn",
                Enabled = true,
                Priority = 1,
                TriggerType = "OnImport",
                ConditionField = "STATUSE_CODE",
                ConditionOperator = "==",
                ConditionValue = "99",
                ActionType = "SetState",
                ActionValue = "Suspended"
            });

            var entry = new MetaverseEntry
            {
                ExternalId = "12345",
                LifecycleState = "Active",
                SourceStatusCode = 99,
                AttributesJson = "{\"STATUSE_CODE\":\"99\"}"
            };
            db.MetaverseEntries.Add(entry);
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);

            // Act
            var result = await engine.ApplyLifecycleRulesAsync(entry);

            // Assert
            Assert.True(result.Success);
            Assert.Equal("Suspended", entry.LifecycleState);
            Assert.Equal("Active", result.PreviousState);
        }

        [Fact]
        public async Task ApplyLifecycleRules_GracePeriodNotExpired_SkipsRule()
        {
            // Arrange
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "Test Org",
                IsActive = true,
                ADUsername = "admin",
                ADPassword = "pass",
                ADBaseDN = "DC=test,DC=com"
            };
            db.TenantSettings.Add(tenant);
            await db.SaveChangesAsync();

            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenant.Id,
                Name = "Deprovision After 30 Days",
                Enabled = true,
                Priority = 1,
                TriggerType = "OnImport",
                ConditionField = "STATUSE_CODE",
                ConditionOperator = "==",
                ConditionValue = "99",
                ActionType = "Deprovision",
                GracePeriodDays = 30
            });

            var entry = new MetaverseEntry
            {
                ExternalId = "12345",
                LifecycleState = "Suspended",
                SourceStatusCode = 99,
                StateChangedDate = DateTime.UtcNow.AddDays(-5), // Only 5 days ago
                AttributesJson = "{\"STATUSE_CODE\":\"99\"}"
            };
            db.MetaverseEntries.Add(entry);
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);

            // Act
            var result = await engine.ApplyLifecycleRulesAsync(entry);

            // Assert
            Assert.True(result.Success);
            // Should NOT have changed to Deprovisioned because grace period hasn't expired
            Assert.NotEqual("Deprovisioned", entry.LifecycleState);
        }

        [Fact]
        public async Task ApplyLifecycleRules_NoTenant_ReturnsError()
        {
            // Arrange
            var db = TestDbContext.Create();
            // Status 1 (Active) maps to "Stay", so no default status transition fires. With no active
            // tenant AND no transition applied, ApplyLifecycleRulesAsync reports failure.
            var entry = new MetaverseEntry
            {
                ExternalId = "12345",
                LifecycleState = "Active",
                SourceStatusCode = 1,
                AttributesJson = "{}"
            };

            var engine = CreateEngine(db);

            // Act
            var result = await engine.ApplyLifecycleRulesAsync(entry);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("No active tenant", result.Error);
        }

        [Fact]
        public async Task GetStats_ReturnsCorrectCounts()
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

            db.MetaverseEntries.AddRange(
                new MetaverseEntry { ExternalId = "1", LifecycleState = "Active", AttributesJson = "{}" },
                new MetaverseEntry { ExternalId = "2", LifecycleState = "Active", AttributesJson = "{}" },
                new MetaverseEntry { ExternalId = "3", LifecycleState = "Suspended", AttributesJson = "{}" },
                new MetaverseEntry { ExternalId = "4", LifecycleState = "Pending", AttributesJson = "{}" }
            );
            await db.SaveChangesAsync();

            var engine = CreateEngine(db);

            // Act
            var stats = await engine.GetStatsAsync();

            // Assert
            Assert.Equal(4, stats.TotalIdentities);
            Assert.Equal(2, stats.ActiveCount);
            Assert.Equal(1, stats.SuspendedCount);
            Assert.Equal(1, stats.PendingCount);
        }
    }
}
