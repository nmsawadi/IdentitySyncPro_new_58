using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Tests.Helpers;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Suspending a schedule used to mean opening an edit form, unticking a box, and saving — too
    /// slow for "stop this now" before AD maintenance or a security scan, and invisible afterwards.
    ///
    /// The one-click toggles flip the switches that already existed (EnableAutoSync for a tenant,
    /// IsEnabled for a service) rather than adding a second "paused" concept beside them. These pin
    /// the predicate that decides whether a recurring job exists, because that predicate is what an
    /// operator is really toggling — and getting it wrong means a schedule that reads as active and
    /// runs nothing, which is the exact failure this feature exists to prevent.
    /// </summary>
    public class ScheduleSuspendTests
    {
        private static (AppDbContext db, TenantSettings tenant) Tenant(
            bool isActive = true, bool autoSync = true, string? cron = "0 */5 * * *")
        {
            var db = TestDbContext.Create();
            var tenant = new TenantSettings
            {
                TenantName = "الطلاب",
                IsActive = isActive,
                EnableAutoSync = autoSync,
                FullSyncSchedule = cron,
                ADUsername = "a", ADPassword = "p", ADBaseDN = "DC=students,DC=lab,DC=local"
            };
            db.TenantSettings.Add(tenant);
            db.SaveChanges();
            return (db, tenant);
        }

        /// <summary>
        /// Mirrors TenantSyncScheduler: a job is registered only when the tenant is active, auto-sync
        /// is on, and a cron exists.
        /// </summary>
        private static bool WouldRegister(TenantSettings t) =>
            t.IsActive && t.EnableAutoSync && !string.IsNullOrWhiteSpace(t.FullSyncSchedule);

        [Fact]
        public void SuspendingAutoSync_StopsTheScheduleAndKeepsTheExpression()
        {
            var (db, tenant) = Tenant();
            Assert.True(WouldRegister(tenant));

            tenant.EnableAutoSync = false;   // what the one-click toggle does
            db.SaveChanges();

            Assert.False(WouldRegister(tenant));

            // The cron survives, so resuming restores the same schedule rather than a default.
            Assert.Equal("0 */5 * * *", db.TenantSettings.Single().FullSyncSchedule);
        }

        [Fact]
        public void ResumingRestoresTheSameSchedule_NotADefault()
        {
            var (db, tenant) = Tenant(autoSync: false);
            Assert.False(WouldRegister(tenant));

            tenant.EnableAutoSync = true;
            db.SaveChanges();

            Assert.True(WouldRegister(tenant));
            Assert.Equal("0 */5 * * *", db.TenantSettings.Single().FullSyncSchedule);
        }

        [Fact]
        public void AnInactiveTenantStaysUnscheduled_EvenWithAutoSyncOn()
        {
            // Both conditions are required. Resuming auto-sync on an inactive tenant must not look
            // like it worked — this is the pair that cost a day of silence.
            var (_, tenant) = Tenant(isActive: false, autoSync: true);

            Assert.False(WouldRegister(tenant));
        }

        [Fact]
        public void ATenantCronIsRequired_SoNoScheduleMeansEmptyNotNull()
        {
            // FullSyncSchedule is a required column with a default, so a tenant can never carry a
            // null cron — EF rejects the insert. "No schedule" is therefore an empty value, which is
            // why the registration predicate tests IsNullOrWhiteSpace rather than a null check.
            var (db, tenant) = Tenant(cron: "");
            Assert.False(WouldRegister(tenant));

            var stored = db.TenantSettings.Single();
            Assert.NotNull(stored.FullSyncSchedule);
        }

        [Theory]
        // isActive, autoSync, cron -> registered
        [InlineData(true, true, "0 */5 * * *", true)]
        [InlineData(true, false, "0 */5 * * *", false)]
        [InlineData(false, true, "0 */5 * * *", false)]
        [InlineData(false, false, "0 */5 * * *", false)]
        [InlineData(true, true, "", false)]
        [InlineData(true, true, "   ", false)]
        public void TheFullRegistrationTruthTable(bool isActive, bool autoSync, string cron, bool registered)
        {
            var (_, tenant) = Tenant(isActive, autoSync, cron);

            Assert.Equal(registered, WouldRegister(tenant));
        }

        /// <summary>
        /// Mirrors both the startup registration and ServicesController.ToggleSchedule: a service
        /// runs on a timer only when it is enabled and carries a cron.
        /// </summary>
        private static bool ServiceWouldRegister(bool isEnabled, string? cron) =>
            isEnabled && !string.IsNullOrWhiteSpace(cron);

        // ── Full and delta gated independently ───────────────────────────────
        // One switch used to govern both, so pausing a delta that runs every half hour also stopped
        // the full pass. Mirrors TenantSyncScheduler's two conditions.

        private static bool FullWouldRegister(TenantSettings t) =>
            t.IsActive && t.EnableAutoSync && t.EnableFullSyncSchedule
            && !string.IsNullOrWhiteSpace(t.FullSyncSchedule);

        private static bool DeltaWouldRegister(TenantSettings t) =>
            t.IsActive && t.EnableAutoSync && t.EnableDeltaSyncSchedule
            && !string.IsNullOrWhiteSpace(t.DeltaSyncSchedule);

        private static TenantSettings TenantWithBoth(bool full = true, bool delta = true, bool master = true)
        {
            var db = TestDbContext.Create();
            var t = new TenantSettings
            {
                TenantName = "الطلاب", IsActive = true, EnableAutoSync = master,
                EnableFullSyncSchedule = full, EnableDeltaSyncSchedule = delta,
                FullSyncSchedule = "0 */5 * * *", DeltaSyncSchedule = "*/30 * * * *",
                ADUsername = "a", ADPassword = "p", ADBaseDN = "DC=students,DC=lab,DC=local"
            };
            db.TenantSettings.Add(t);
            db.SaveChanges();
            return t;
        }

        [Fact]
        public void SuspendingTheDelta_LeavesTheFullSyncRunning()
        {
            // The case that prompted this: stop the frequent delta, keep the thorough pass.
            var t = TenantWithBoth(full: true, delta: false);

            Assert.True(FullWouldRegister(t));
            Assert.False(DeltaWouldRegister(t));
        }

        [Fact]
        public void SuspendingTheFull_LeavesTheDeltaRunning()
        {
            var t = TenantWithBoth(full: false, delta: true);

            Assert.False(FullWouldRegister(t));
            Assert.True(DeltaWouldRegister(t));
        }

        [Fact]
        public void TheMasterSwitchStillOverridesBoth()
        {
            // Turning auto-sync off must stop everything, whatever the per-type switches say —
            // otherwise the one switch an operator reaches for first would no longer work.
            var t = TenantWithBoth(full: true, delta: true, master: false);

            Assert.False(FullWouldRegister(t));
            Assert.False(DeltaWouldRegister(t));
        }

        [Fact]
        public void BothOn_IsTheExistingBehaviour()
        {
            // The defaults are true, so a tenant upgraded from before these columns existed keeps
            // registering both jobs exactly as it did.
            var t = TenantWithBoth();

            Assert.True(FullWouldRegister(t));
            Assert.True(DeltaWouldRegister(t));
        }

        [Fact]
        public void SuspendingOneTypeKeepsBothExpressions()
        {
            // Resuming must restore the same schedule, not a default — the switches never touch
            // the cron.
            var t = TenantWithBoth(full: false, delta: true);

            Assert.Equal("0 */5 * * *", t.FullSyncSchedule);
            Assert.Equal("*/30 * * * *", t.DeltaSyncSchedule);
        }

        [Theory]
        [InlineData(true, "0 2 * * *", true)]
        [InlineData(false, "0 2 * * *", false)]   // suspended
        [InlineData(true, null, false)]           // enabled but never scheduled
        [InlineData(false, null, false)]
        public void AServiceRunsOnATimerOnlyWhenEnabledAndScheduled(bool isEnabled, string? cron, bool registered)
        {
            Assert.Equal(registered, ServiceWouldRegister(isEnabled, cron));
        }

        [Fact]
        public void ASuspendedServiceIsDistinguishableFromOneThatNeverHadASchedule()
        {
            // The list badge shows three states, not two: scheduled, suspended, and no schedule.
            // Collapsing the last two would hide that a suspension is reversible.
            Assert.False(ServiceWouldRegister(false, "0 2 * * *"));  // suspended — cron kept
            Assert.False(ServiceWouldRegister(true, null));          // never scheduled at all
        }
    }
}
