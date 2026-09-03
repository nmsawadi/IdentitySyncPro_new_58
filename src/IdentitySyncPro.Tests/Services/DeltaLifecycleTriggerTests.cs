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
    /// The delta sync used to run lifecycle rules only inside its "an AD attribute actually
    /// changed" branch, so an identity that changed status produced no lifecycle action unless
    /// some mapped attribute happened to change with it.
    ///
    /// Those are independent concerns: a lifecycle rule reacts to the source status, while the
    /// attribute update depends on what the tenant chose to map. It worked in production only
    /// because that tenant maps its status description to two AD attributes — an entirely
    /// optional mapping. Removing it, or adding a status whose description does not change,
    /// would have stopped graduates being archived with nothing reported anywhere.
    /// </summary>
    public class DeltaLifecycleTriggerTests
    {
        private const int IdentityId = 441234567;

        private static (SyncEngine engine, AppDbContext db, Mock<ILifecycleEngine> lifecycle)
            Build(int newStatusCode, int? lastStatusCode, string adResult)
        {
            var db = TestDbContext.Create();
            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddLogging();

            var lifecycle = new Mock<ILifecycleEngine>();
            lifecycle.Setup(l => l.ProcessIdentityAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new LifecycleActionResult { Success = true, ActionsTaken = "Moved" });
            services.AddSingleton(lifecycle.Object);

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var tenant = new TenantSettings
            {
                TenantName = "T", IsActive = true, EnableLifecycleDuringSync = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = "DC=test",
                SourceKeyColumn = "ID", SourceStatusColumn = "SRC_STATUS"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();
            db.TenantAttributeMappings.Add(new TenantAttributeMapping
            {
                TenantId = tenant.Id, SourceColumn = "ID",
                TargetAttribute = "sAMAccountName", IsIdentifier = true
            });
            db.SyncStates.Add(new SyncState
            {
                TenantId = tenant.Id, IdentityId = IdentityId,
                CurrentHash = "OLD-HASH", Status = "Synced", CreatedInAD = true,
                LastStatusCode = lastStatusCode
            });
            db.SaveChanges();

            var record = new SourceRecord { Key = IdentityId, StatusCode = newStatusCode };
            record.Values["ID"] = IdentityId;
            record.Values["SRC_STATUS"] = newStatusCode;

            var source = new Mock<ISourceConnector>();
            source.Setup(s => s.ReadAllIdsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { IdentityId });
            source.Setup(s => s.ReadBatchAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new[] { record });

            var target = new Mock<ITargetConnector>();
            target.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            target.Setup(t => t.UpdateDynamicAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new Core.Interfaces.SyncResult { Success = true, ChangedFields = adResult });

            var engine = new SyncEngine(scopeFactory, source.Object, target.Object,
                Mock.Of<ISmsService>(), Mock.Of<ILogger<SyncEngine>>(),
                progressNotifier: null,
                resilience: new ResilienceService(scopeFactory, Mock.Of<ILogger<ResilienceService>>()));

            return (engine, db, lifecycle);
        }

        [Fact]
        public async Task StatusChanged_ButNoAdAttributeChanged_StillRunsLifecycle()
        {
            // A tenant that does not map its status description to any AD attribute: the student
            // graduates, the source hash changes, and AD has nothing to update.
            var (engine, _, lifecycle) = Build(newStatusCode: 7, lastStatusCode: 1, adResult: "NoChanges");

            await engine.RunDeltaSyncAsync();

            lifecycle.Verify(l => l.ProcessIdentityAsync(IdentityId, false, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Once, "a status change must drive the lifecycle regardless of AD attribute changes");
        }

        [Fact]
        public async Task StatusChangedAndAttributesChanged_StillRunsLifecycleOnce()
        {
            var (engine, _, lifecycle) = Build(newStatusCode: 7, lastStatusCode: 1, adResult: "employeeType");

            await engine.RunDeltaSyncAsync();

            lifecycle.Verify(l => l.ProcessIdentityAsync(IdentityId, false, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task StatusUnchanged_DoesNotRunLifecycle()
        {
            // Guards against the fix turning into "run the lifecycle for everything".
            var (engine, _, lifecycle) = Build(newStatusCode: 1, lastStatusCode: 1, adResult: "sn");

            await engine.RunDeltaSyncAsync();

            lifecycle.Verify(l => l.ProcessIdentityAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task NoChangesResult_StillStampsTheHash()
        {
            // Otherwise an identity whose source changed without an AD difference is re-read and
            // re-compared on every delta run, forever.
            var (engine, db, _) = Build(newStatusCode: 1, lastStatusCode: 1, adResult: "NoChanges");

            await engine.RunDeltaSyncAsync();

            Assert.NotEqual("OLD-HASH", db.SyncStates.Single().CurrentHash);
        }
    }
}
