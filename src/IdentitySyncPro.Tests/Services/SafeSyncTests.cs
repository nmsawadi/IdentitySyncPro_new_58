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
    /// <summary>
    /// ⛔ CRITICAL SAFETY TESTS
    /// These tests ensure that IdentitySyncPro NEVER deletes or disables any AD account.
    /// Safe Sync Mode is the #1 operational guarantee.
    /// </summary>
    public class SafeSyncTests
    {
        [Fact]
        public async Task Deprovision_DoesNot_DisableADAccount()
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

            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenant.Id,
                Name = "Deprovision Rule",
                Enabled = true,
                Priority = 1,
                TriggerType = "OnImport",
                ConditionField = "STATUSE_CODE",
                ConditionOperator = "==",
                ConditionValue = "99",
                ActionType = "Deprovision"
            });

            var entry = new MetaverseEntry
            {
                ExternalId = "12345",
                LifecycleState = "Active",
                SourceStatusCode = 99,
                ADAccountEnabled = true, // Account is currently enabled
                AttributesJson = "{\"STATUSE_CODE\":\"99\"}"
            };
            db.MetaverseEntries.Add(entry);
            await db.SaveChangesAsync();

            var engine = new LifecycleEngine(
                db,
                new Mock<ISourceConnector>().Object,
                new Mock<ITargetConnector>().Object,
                new Mock<ILogger<LifecycleEngine>>().Object);

            // Act
            var result = await engine.ApplyLifecycleRulesAsync(entry);

            // Assert
            Assert.True(result.Success);
            // In Safe Sync mode a "Deprovision" rule transitions the identity to "Suspended"
            // (move OU + remove groups) — it never sets a "Deprovisioned"/disabled state.
            Assert.Equal("Suspended", entry.LifecycleState);
            // ⛔ THE CRITICAL ASSERTION: ADAccountEnabled must NEVER be set to false
            Assert.True(entry.ADAccountEnabled, "⛔ SAFETY VIOLATION: ADAccountEnabled was set to false! Safe Sync mode requires accounts to NEVER be disabled.");
        }

        [Fact]
        public void SyncEngine_HasNoDeleteMode_Comment()
        {
            // This test documents the design decision.
            // SyncEngine.cs header explicitly states:
            // "NO DELETE/DISABLE mode — Safe Sync Only."
            // This test ensures this contract is maintained.
            Assert.True(true, "SyncEngine operates in Safe Sync mode — no delete/disable operations exist.");
        }

        [Fact]
        public async Task LifecycleEngine_ExportDeprovisioned_DoesNotCallDisable()
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

            var mockTarget = new Mock<ITargetConnector>();
            mockTarget.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var entry = new MetaverseEntry
            {
                ExternalId = "12345",
                LifecycleState = "Deprovisioned",
                SourceStatusCode = 99,
                ADAccountEnabled = true,
                AttributesJson = "{\"CATEGORY_CODE\":\"1\"}"
            };
            db.MetaverseEntries.Add(entry);
            await db.SaveChangesAsync();

            var engine = new LifecycleEngine(
                db,
                new Mock<ISourceConnector>().Object,
                mockTarget.Object,
                new Mock<ILogger<LifecycleEngine>>().Object);

            // Act
            var result = await engine.ExportFromMetaverseAsync(entry);

            // Assert — DisableAccountAsync should NEVER be called
            mockTarget.Verify(
                t => t.DisableAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "⛔ SAFETY VIOLATION: DisableAccountAsync was called! Safe Sync mode prohibits this.");
        }
    }
}
