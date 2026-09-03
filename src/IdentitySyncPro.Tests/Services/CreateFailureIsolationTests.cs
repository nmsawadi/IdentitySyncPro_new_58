using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Identity;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// ⛔ CRITICAL REGRESSION TESTS — one identity must never end a run.
    ///
    /// Production, 2026-08-05: a single new student's password write timed out against a domain
    /// controller that could not reach the PDC emulator. The connector reported it by throwing,
    /// the exception unwound past the per-identity failure handling to the top-level catch, and
    /// the run ended with "PARTIAL, only 0 identities were processed" — out of 111,464. Every
    /// retry repeated it, and each attempt left a tombstone in the directory because the partly
    /// created account is deleted on the way out.
    ///
    /// The engine already knew how to record a failed identity and carry on; it simply never got
    /// the chance. These tests pin that it does.
    /// </summary>
    public class CreateFailureIsolationTests
    {
        private const int FailingId = 100;
        private const int HealthyId = 200;

        private static (SyncEngine engine, AppDbContext db, Mock<ITargetConnector> target) BuildEngine(
            Func<string, SyncResult> createBehaviour, int[]? sourceIds = null)
        {
            var db = TestDbContext.Create();

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddLogging();
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var source = new Mock<ISourceConnector>();
            source.Setup(s => s.ReadBatchAsync(It.IsAny<int[]>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((int[] ids, CancellationToken _) => ids.Select(BuildRecord).ToArray());
            // Two identities: the first fails, the second must still be processed.
            source.Setup(s => s.ReadAllIdsAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(sourceIds ?? new[] { FailingId, HealthyId });

            var target = new Mock<ITargetConnector>();
            // Nobody exists in AD, so every identity takes the create path.
            target.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(false);
            target.Setup(t => t.CreateDynamicAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                              It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(),
                              It.IsAny<CancellationToken>()))
                  .Returns((string id, Dictionary<string, string> _, string __, IEnumerable<string> ___,
                            string? ____, CancellationToken _____) => Task.FromResult(createBehaviour(id)));

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

        private static SourceRecord BuildRecord(int id)
        {
            var record = new SourceRecord { Key = id, StatusCode = 1 };
            record.Values["IDENTITY_ID"] = id;
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

        /// <summary>
        /// The exact production shape: the connector throws (an LDAP timeout surfaces this way).
        /// The identity must be recorded as failed and the sync must still finish.
        /// </summary>
        [Fact]
        public async Task ConnectorThrowsDuringCreate_IdentityIsRecordedFailed_AndRunCompletes()
        {
            // Deliberately NOT InvalidOperationException: the single-sync path wraps mapping in a
            // catch for that type, which would absorb the throw and make this test pass with the
            // guard removed. Mutation testing caught exactly that.
            var (engine, db, _) = BuildEngine(_ =>
                throw new TimeoutException(
                    "The operation was aborted because the client side timeout limit was exceeded."));
            SeedTenant(db);

            // Must not propagate — before the fix this threw and ended the whole run.
            var result = await engine.SyncSingleAsync(FailingId);

            Assert.Equal(SyncOperationStatus.Failed, result.Status);
            Assert.Contains("timeout", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

            // The run is recorded as a completed-but-failed run, not an aborted one.
            var run = db.SyncRuns.Single();
            Assert.Equal(1, run.TotalFailed);
            Assert.Equal(1, run.TotalProcessed);
        }

        /// <summary>
        /// A connector that reports failure the ordinary way must behave identically — this is the
        /// contract the throwing path is being folded back into.
        /// </summary>
        [Fact]
        public async Task ConnectorReturnsFailure_IdentityIsRecordedFailed_AndRunCompletes()
        {
            var (engine, db, _) = BuildEngine(_ => new SyncResult { Success = false, Error = "boom" });
            SeedTenant(db);

            var result = await engine.SyncSingleAsync(FailingId);

            Assert.Equal(SyncOperationStatus.Failed, result.Status);
            Assert.Equal(1, db.SyncRuns.Single().TotalFailed);
        }

        /// <summary>
        /// Control test: a healthy identity must still be created and marked synced. Without this,
        /// swallowing every create — turning the engine into a no-op — would pass the tests above.
        /// </summary>
        [Fact]
        public async Task HealthyIdentity_IsStillCreated()
        {
            var (engine, db, target) = BuildEngine(_ => new SyncResult { Success = true });
            SeedTenant(db);

            var result = await engine.SyncSingleAsync(HealthyId);

            Assert.Equal(SyncOperationStatus.Success, result.Status);
            Assert.Equal(1, db.SyncRuns.Single().TotalCreated);
            target.Verify(t => t.CreateDynamicAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(),
                It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        /// <summary>
        /// ⛔ The production path. RunFullSyncAsync is where the outage happened, and unlike the
        /// single-identity path it has no surrounding catch to absorb a connector throw.
        ///
        /// A first identity that fails must not prevent the second from being created — that is
        /// the whole failure: 111,464 identities went unprocessed behind one bad account, and the
        /// same shape is about to matter for ~5,000 new students created in one run.
        /// </summary>
        [Fact]
        public async Task FullSync_OneFailingIdentity_DoesNotStopTheRest()
        {
            var (engine, db, target) = BuildEngine(id =>
                id == FailingId.ToString()
                    ? throw new TimeoutException("LDAP client side timeout limit was exceeded")
                    : new SyncResult { Success = true });
            SeedTenant(db);

            var run = await engine.RunFullSyncAsync(batchSize: 10);

            // The run finished rather than aborting...
            Assert.NotEqual(SyncRunStatus.Failed, run.Status);
            // ...both identities were seen...
            Assert.Equal(2, run.TotalProcessed);
            // ...one was recorded as failed...
            Assert.Equal(1, run.TotalFailed);
            // ...and crucially the healthy one was still created.
            Assert.Equal(1, run.TotalCreated);
        }

        /// <summary>
        /// ⛔ The intake scenario, at the shape that matters: several neighbouring records share
        /// one data defect. Before the classifier, three consecutive failures opened the circuit
        /// breaker and the next batch boundary ended the run — so a handful of malformed records
        /// at the front of a ~5,000 student intake would have stopped everyone behind them, on
        /// every run.
        /// </summary>
        [Fact]
        public async Task FullSync_ThreeConsecutiveDataFailures_DoNotStopTheRun()
        {
            var badIds = new[] { 1, 2, 3 };
            var (engine, db, _) = BuildEngine(
                id => badIds.Contains(int.Parse(id))
                    ? new SyncResult
                      {
                          Success = false,
                          Error = "noSuchObject: OU=FEMALE,OU=NEWCITY,DC=std does not exist",
                          FailureKind = SyncFailureKind.Data
                      }
                    : new SyncResult { Success = true },
                sourceIds: new[] { 1, 2, 3, 4, 5 });
            SeedTenant(db);

            var run = await engine.RunFullSyncAsync(batchSize: 1);

            Assert.Equal(5, run.TotalProcessed);
            Assert.Equal(3, run.TotalFailed);
            // The two healthy students behind the bad block must still get accounts.
            Assert.Equal(2, run.TotalCreated);
        }

        /// <summary>
        /// Control: a genuine outage must still stop the run. Excusing data faults must not blind
        /// the breaker to a directory that has stopped answering.
        /// </summary>
        [Fact]
        public async Task FullSync_ConsecutiveTransportFailures_StillTripTheBreaker()
        {
            var (engine, db, _) = BuildEngine(
                _ => new SyncResult
                     {
                         Success = false,
                         Error = "The LDAP server is unavailable.",
                         FailureKind = SyncFailureKind.Transport
                     },
                sourceIds: new[] { 1, 2, 3, 4, 5 });
            SeedTenant(db);

            var run = await engine.RunFullSyncAsync(batchSize: 1);

            // It gives up rather than hammering a dead directory for all five.
            Assert.True(run.TotalProcessed < 5,
                "consecutive transport failures must still open the circuit breaker");
        }

        /// <summary>
        /// A service account without Create/Reset-Password delegation fails on EVERY record, so
        /// the run must stop quickly rather than grind through the whole intake reporting 5,000
        /// failures — and the reason it stopped must name permissions, not just connectivity.
        /// </summary>
        [Fact]
        public async Task FullSync_MissingPermissions_StopsEarly_AndExplainsWhy()
        {
            var (engine, db, _) = BuildEngine(
                _ => new SyncResult
                     {
                         Success = false,
                         Error = "insufficientAccessRights (0x32): the service account may not create this object"
                         // FailureKind deliberately left Unknown — the engine must classify it.
                     },
                sourceIds: Enumerable.Range(1, 50).ToArray());
            SeedTenant(db);

            var run = await engine.RunFullSyncAsync(batchSize: 1);

            // Stopped long before working through all fifty.
            Assert.True(run.TotalProcessed < 10,
                $"missing permissions must stop the run early, but {run.TotalProcessed} identities were attempted");

            // And the recorded reason points at the real cause.
            Assert.Contains("صلاحيات", run.ErrorMessage ?? "");
            Assert.Contains("insufficientAccessRights", run.ErrorMessage ?? "");
        }

        /// <summary>
        /// Cancellation is an operator decision, not an identity fault, and must still stop the run
        /// rather than be recorded as a failed account and swallowed.
        /// </summary>
        [Fact]
        public async Task Cancellation_IsNotSwallowedAsAnIdentityFailure()
        {
            var (engine, db, _) = BuildEngine(_ => throw new OperationCanceledException());
            SeedTenant(db);

            // Cancellation must surface as cancellation, not be recorded as an account failure.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.SyncSingleAsync(FailingId));

            Assert.False(db.SyncStates.Any(s => s.IdentityId == FailingId && s.Status == "Failed"),
                "cancelling a run must not write the in-flight identity off as a failed account");
        }
    }
}
