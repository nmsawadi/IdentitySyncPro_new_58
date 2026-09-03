using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Metaverse;
using IdentitySyncPro.Core.Models.Rules;
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
    /// A lifecycle rule's ActionValue only ever had {BaseDN} substituted, so it can name one fixed
    /// OU for everybody. That archives graduates fine, and cannot send a returning identity home:
    /// the home OU is per-identity, built by the tenant's OU rules from source data.
    ///
    /// The result was a reactivation that restored the state and the groups but not the location —
    /// the database read Active while the account sat in the OU it was parked in, and nothing
    /// reported a problem. {OURules} resolves through the same MappingEngine.ResolveOU that account
    /// creation uses, mirroring {GroupRules} on the group side.
    /// </summary>
    public class OuRulesTokenTests
    {
        private const string BaseDn = "DC=students,DC=lab,DC=local";
        private const string HomeOu = "OU=MALE,OU=SHARORAH,DC=students,DC=lab,DC=local";
        private const string ParkedOu = "OU=LEAVINGUNIVERSITY,DC=students,DC=lab,DC=local";
        private const string Identity = "450000048";

        private static (LifecycleEngine engine, IServiceScopeFactory scopes, AppDbContext db, Mock<ITargetConnector> ad)
            Setup(string actionValue, bool withOuRules = true)
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true,
                ADUsername = "a", ADPassword = "p", ADBaseDN = BaseDn,
                SourceStatusColumn = "STATUSE_CODE"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();

            if (withOuRules)
            {
                db.TenantOURules.Add(new TenantOURule
                {
                    TenantId = tenant.Id,
                    OUTemplate = "OU={GENDER_CODE},OU={CITY_NO},{BaseDN}",
                    ValueMappings = "{\"CITY_NO\":{\"14\":\"NAJRAN\",\"20\":\"SHARORAH\"}," +
                                    "\"GENDER_CODE\":{\"1\":\"MALE\",\"2\":\"FEMALE\"}}"
                });
            }

            db.LifecycleRules.Add(new LifecycleRule
            {
                TenantId = tenant.Id, Name = "إعادة تفعيل هوية عائدة ونقل حسابها",
                Enabled = true, Priority = 50, TriggerType = "OnImport",
                ConditionField = "STATUSE_CODE", ConditionOperator = "==", ConditionValue = "1",
                ActionType = "Reactivate", ActionValue = actionValue
            });

            db.MetaverseEntries.Add(new MetaverseEntry
            {
                TenantId = tenant.Id,
                ExternalId = Identity,
                LifecycleState = "Active",
                SourceStatusCode = 1,
                ADCurrentOU = ParkedOu,
                AttributesJson = """{"STATUSE_CODE":1,"CITY_NO":20,"GENDER_CODE":1}""",
                StateChangedDate = DateTime.UtcNow,
                LastExportDate = null
            });
            db.SaveChanges();

            var ad = new Mock<ITargetConnector>();
            ad.Setup(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
            ad.Setup(t => t.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddSingleton(ad.Object);
            services.AddLogging();
            var provider = services.BuildServiceProvider();

            var engine = new LifecycleEngine(db, Mock.Of<ISourceConnector>(), ad.Object,
                Mock.Of<ILogger<LifecycleEngine>>());

            return (engine, provider.GetRequiredService<IServiceScopeFactory>(), db, ad);
        }

        [Fact]
        public async Task AReturningIdentity_GoesToTheOuItsOwnDataResolves()
        {
            // CITY_NO 20 and GENDER_CODE 1 must produce OU=MALE,OU=SHARORAH — the same DN account
            // creation would have built, not a fixed OU shared by everyone.
            var (engine, scopes, db, ad) = Setup("{OURules}");

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            ad.Verify(t => t.MoveToOUAsync(Identity, HomeOu, It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal(HomeOu, db.MetaverseEntries.Single().ADCurrentOU);
        }

        [Fact]
        public async Task ALiteralActionValue_StillWorksExactlyAsBefore()
        {
            // The token is additive. Every existing tenant names its OU literally.
            var (engine, scopes, db, ad) = Setup("OU=RETURNED,{BaseDN}");

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(Identity, "OU=RETURNED,DC=students,DC=lab,DC=local",
                It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal("OU=RETURNED,DC=students,DC=lab,DC=local", db.MetaverseEntries.Single().ADCurrentOU);
        }

        [Fact]
        public async Task TheTokenIsMatchedCaseInsensitively()
        {
            // Typed by hand into a settings field; {ourules} must not silently become a literal DN.
            var (engine, scopes, _, ad) = Setup("{ourules}");

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(Identity, HomeOu, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task WithNoOuRulesConfigured_TheAccountIsNotMovedAtAll()
        {
            // The dangerous alternative is moving it to the bare BaseDN — the domain root.
            var (engine, scopes, db, ad) = Setup("{OURules}", withOuRules: false);

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Equal(ParkedOu, db.MetaverseEntries.Single().ADCurrentOU);
        }

        [Fact]
        public async Task AnUnresolvableToken_IsAFailureNotASilentSuccess()
        {
            // It must land in the same bucket as a refused move: not stamped, retried next run.
            var (engine, scopes, db, _) = Setup("{OURules}", withOuRules: false);

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(0, exported);
            Assert.Null(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task AnUnresolvableToken_SaysSoInTheAuditTrail()
        {
            var (engine, scopes, db, _) = Setup("{OURules}", withOuRules: false);

            await engine.BulkExportAsync(scopes);

            var history = db.MetaverseHistory.Single(h => h.ChangeType == "Export");
            Assert.Contains("FAILED", history.Details);
            Assert.Contains("Reactivate", history.Details);
        }

        // ── An ActionValue that is a template in its own right ───────────────
        // {OURules} gives the whole home OU; a literal gives one OU for everybody. An archive is
        // neither: a fixed root with the same subdivision underneath — OU=MALE and OU=FEMALE inside
        // OU=GRADUATES. "OU=GRADUATES,{BaseDN}" put every graduate in the archive root, because
        // {GENDER_CODE} was substituted nowhere but the tenant's OU rules.

        [Fact]
        public async Task AFixedRootWithAPlaceholder_SubdividesPerIdentity()
        {
            var (engine, scopes, db, ad) = Setup("OU={GENDER_CODE},OU=GRADUATES,{BaseDN}");

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            ad.Verify(t => t.MoveToOUAsync(Identity,
                "OU=MALE,OU=GRADUATES,DC=students,DC=lab,DC=local", It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal("OU=MALE,OU=GRADUATES,DC=students,DC=lab,DC=local",
                db.MetaverseEntries.Single().ADCurrentOU);
        }

        [Fact]
        public async Task ThePlaceholderIsTranslatedByTheTenantsOwnValueMappings()
        {
            // GENDER_CODE 2 must become FEMALE, not the literal "2". The translation is the one the
            // tenant already declared for its OU rules — not a second copy on the lifecycle rule.
            var (engine, scopes, db, ad) = Setup("OU={GENDER_CODE},OU=GRADUATES,{BaseDN}");
            var entry = db.MetaverseEntries.Single();
            entry.AttributesJson = """{"STATUSE_CODE":1,"CITY_NO":20,"GENDER_CODE":2}""";
            db.SaveChanges();

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(Identity,
                "OU=FEMALE,OU=GRADUATES,DC=students,DC=lab,DC=local", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SeveralPlaceholdersInOneActionValue_AreAllSubstituted()
        {
            var (engine, scopes, _, ad) = Setup("OU={GENDER_CODE},OU={CITY_NO},OU=GRADUATES,{BaseDN}");

            await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(Identity,
                "OU=MALE,OU=SHARORAH,OU=GRADUATES,DC=students,DC=lab,DC=local",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task APlaceholderActionValue_WithoutTenantOuRules_IsAFailureNotAMoveToALiteralDn()
        {
            // Without the tenant's mappings there is nothing to translate {GENDER_CODE} with, and
            // moving to a DN containing a literal "{GENDER_CODE}" is what AD rejected with
            // "problem 5012 (DIR_ERROR)" — an error naming neither the cause nor the OU.
            var (engine, scopes, db, ad) = Setup("OU={GENDER_CODE},OU=GRADUATES,{BaseDN}", withOuRules: false);

            var exported = await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Equal(0, exported);
            Assert.Null(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task ALiteralActionValue_NeverConsultsTheOuRulesAtAll()
        {
            // The path every existing tenant is on. It must stay a plain {BaseDN} substitution, so
            // that adding this feature cannot change where any current identity lands — proven by
            // the literal resolving correctly even when the tenant has no OU rules to consult.
            var (engine, scopes, db, ad) = Setup("OU=GRADUATES,{BaseDN}", withOuRules: false);

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            ad.Verify(t => t.MoveToOUAsync(Identity, "OU=GRADUATES,DC=students,DC=lab,DC=local",
                It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal("OU=GRADUATES,DC=students,DC=lab,DC=local", db.MetaverseEntries.Single().ADCurrentOU);
        }

        [Fact]
        public async Task AnEmptyActionValue_ReactivatesWithoutMoving_AndSaysSo()
        {
            // What production does today. It is a valid configuration — a tenant may want Reactivate
            // with no move — so it stays a clean success rather than becoming a retried failure on a
            // system handling six figures of identities. But it is the exact route by which an
            // identity ends up Active in the OU it was parked in, so the run must not be silent
            // about it: the audit trail names it.
            var (engine, scopes, db, ad) = Setup("");

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Equal(ParkedOu, db.MetaverseEntries.Single().ADCurrentOU);

            var history = db.MetaverseHistory.Single(h => h.ChangeType == "Export");
            Assert.Contains("not moved", history.Details);
        }

        [Fact]
        public async Task WhenEveryOuRulesConditionMissesTheIdentity_TheAccountIsNotMovedToTheDomainRoot()
        {
            // ResolveOU falls back to the bare BaseDN when no rule's condition matches. Acting on
            // that would move a student to the root of the domain — worse than leaving them parked,
            // and hard to undo across a batch. Distinct from having no OU rules at all: here the
            // rules exist and simply do not apply to this identity.
            var (engine, scopes, db, ad) = Setup("{OURules}", withOuRules: false);
            db.TenantOURules.Add(new TenantOURule
            {
                TenantId = db.TenantSettings.Single().Id,
                OUTemplate = "OU={GENDER_CODE},OU={CITY_NO},{BaseDN}",
                ConditionField = "CITY_NO", ConditionOperator = "==", ConditionValue = "99",
                ValueMappings = "{\"CITY_NO\":{\"99\":\"ELSEWHERE\"},\"GENDER_CODE\":{\"1\":\"MALE\"}}"
            });
            db.SaveChanges();

            var exported = await engine.BulkExportAsync(scopes);

            ad.Verify(t => t.MoveToOUAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Equal(ParkedOu, db.MetaverseEntries.Single().ADCurrentOU);
            Assert.Equal(0, exported);
            Assert.Null(db.MetaverseEntries.Single().LastExportDate);
        }

        [Fact]
        public async Task OnceTheOuRulesExist_TheRetrySucceeds()
        {
            // The whole reason a failure must not be stamped: the operator fixes the cause and the
            // next run finishes the job without anything else changing.
            var (engine, scopes, db, ad) = Setup("{OURules}", withOuRules: false);

            await engine.BulkExportAsync(scopes);

            db.TenantOURules.Add(new TenantOURule
            {
                TenantId = db.TenantSettings.Single().Id,
                OUTemplate = "OU={GENDER_CODE},OU={CITY_NO},{BaseDN}",
                ValueMappings = "{\"CITY_NO\":{\"20\":\"SHARORAH\"},\"GENDER_CODE\":{\"1\":\"MALE\"}}"
            });
            db.SaveChanges();

            var exported = await engine.BulkExportAsync(scopes);

            Assert.Equal(1, exported);
            ad.Verify(t => t.MoveToOUAsync(Identity, HomeOu, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
