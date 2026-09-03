using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Identity;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Core.Models.Sync;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// ⛔ CRITICAL REGRESSION TESTS — a dry run must leave NO trace.
    ///
    /// The bug these pin down: a dry run stamped the current hash into SyncStates. The next REAL
    /// sync then compared hashes, found them identical, reported NoChange for every identity and
    /// pushed nothing to AD — silently swallowing a whole sync's worth of updates.
    ///
    /// It hid on established installs (the hashes it wrote matched what was already stored) and
    /// only surfaced on a fresh database where the dry run ran first: 111,465 identities went from
    /// "Local: 0" to "Local: 111,465" during the dry run, after which the full sync had nothing
    /// left to do.
    /// </summary>
    public class DryRunIsolationTests
    {
        private const int IdentityId = 12345;

        /// <summary>Wires a SyncEngine whose scopes all resolve the same in-memory context.</summary>
        private static (SyncEngine engine, AppDbContext db, Mock<ITargetConnector> target) BuildEngine(
            bool accountExistsInAd)
        {
            var db = TestDbContext.Create();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var source = new Mock<ISourceConnector>();
            source.Setup(s => s.ReadBatchAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { BuildRecord() });

            var target = new Mock<ITargetConnector>();
            target.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(accountExistsInAd);

            var engine = new SyncEngine(
                scopeFactory,
                source.Object,
                target.Object,
                Mock.Of<ISmsService>(),
                Mock.Of<ILogger<SyncEngine>>(),
                progressNotifier: null,
                resilience: new ResilienceService(scopeFactory, Mock.Of<ILogger<ResilienceService>>()));

            return (engine, db, target);
        }

        private static SourceRecord BuildRecord()
        {
            // Key/StatusCode are extracted by the connector from the tenant's configured
            // columns, so a hand-built record must set them explicitly.
            var record = new SourceRecord { Key = IdentityId, StatusCode = 1 };
            record.Values["IDENTITY_ID"] = IdentityId;
            record.Values["STATUSE_CODE"] = 1;
            record.Values["FIRST_NAME"] = "Test";
            return record;
        }

        private static TenantSettings SeedTenant(AppDbContext db)
        {
            var tenant = new TenantSettings
            {
                TenantName = "T1", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = "DC=test"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            db.TenantAttributeMappings.Add(new TenantAttributeMapping
            {
                TenantId = tenant.Id,
                SourceColumn = "IDENTITY_ID",
                TargetAttribute = "sAMAccountName",
                IsIdentifier = true
            });
            db.SaveChanges();
            return tenant;
        }

        [Fact]
        public async Task DryRun_DoesNotCreate_SyncState()
        {
            // The exact production scenario: empty SyncStates, account already in AD.
            var (engine, db, target) = BuildEngine(accountExistsInAd: true);
            SeedTenant(db);
            Assert.Empty(db.SyncStates);

            await engine.SyncSingleAsync(IdentityId, dryRun: true);

            // A dry run that writes state tells the next real sync "already synced" — the bug.
            Assert.Empty(db.SyncStates);
            // ...and it must not have touched AD either.
            target.Verify(t => t.UpdateDynamicAsync(It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task DryRun_DoesNotOverwrite_ExistingHash()
        {
            var (engine, db, _) = BuildEngine(accountExistsInAd: true);
            var tenant = SeedTenant(db);

            db.SyncStates.Add(new SyncState
            {
                TenantId = tenant.Id,
                IdentityId = IdentityId,
                CurrentHash = "STALE-HASH",
                Status = "Synced",
                CreatedInAD = true
            });
            await db.SaveChangesAsync();

            await engine.SyncSingleAsync(IdentityId, dryRun: true);

            // The stale hash must survive, so the next real sync still sees work to do.
            var state = db.SyncStates.Single(s => s.IdentityId == IdentityId);
            Assert.Equal("STALE-HASH", state.CurrentHash);
        }

        [Fact]
        public async Task DryRun_DoesNotCreateAccount_WhenMissingFromAd()
        {
            var (engine, db, target) = BuildEngine(accountExistsInAd: false);
            SeedTenant(db);

            await engine.SyncSingleAsync(IdentityId, dryRun: true);

            Assert.Empty(db.SyncStates);
            target.Verify(t => t.CreateDynamicAsync(It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RealRun_DoesWrite_SyncState()
        {
            // The counterpart: without this, the tests above would also pass on an engine that
            // never records anything at all.
            var (engine, db, target) = BuildEngine(accountExistsInAd: true);
            SeedTenant(db);
            target.Setup(t => t.UpdateDynamicAsync(It.IsAny<string>(),
                      It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new Core.Interfaces.SyncResult { Success = true, ChangedFields = "sn" });

            var op = await engine.SyncSingleAsync(IdentityId, dryRun: false);
            Assert.True(op.Status == Core.Enums.SyncOperationStatus.Success, $"real run failed: {op.ErrorMessage}");

            var all = db.SyncStates.ToList();
            var state = all.SingleOrDefault(s => s.IdentityId == IdentityId);
            Assert.True(state != null,
                $"op={op.Operation}/{op.Status} err={op.ErrorMessage} rows={all.Count} " +
                $"ids=[{string.Join(",", all.Select(s => $"{s.TenantId}:{s.IdentityId}"))}]");
            Assert.False(string.IsNullOrEmpty(state!.CurrentHash));
        }
    }
}
