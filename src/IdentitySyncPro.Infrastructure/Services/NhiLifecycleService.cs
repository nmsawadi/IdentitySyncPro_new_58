using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Governance;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// What people do to non-human accounts: take responsibility for one, confirm it is still
    /// needed, hand it back, or put it deliberately outside the lifecycle.
    ///
    /// Separate from the reconciler because these run from a screen while somebody waits, and touch
    /// nothing but this system's own database. No directory connection, no assumption that the
    /// owner exists in any directory at all — an owner is an identifier a person signed in with,
    /// and the institution decides what those look like.
    /// </summary>
    public class NhiLifecycleService
    {
        private readonly GovernanceDbContext _gov;
        private readonly IAuditService _audit;
        private readonly ILogger<NhiLifecycleService> _logger;

        private const string AuditCategory = "NonHumanIdentity";

        public NhiLifecycleService(
            GovernanceDbContext gov,
            IAuditService audit,
            ILogger<NhiLifecycleService> logger)
        {
            _gov = gov;
            _audit = audit;
            _logger = logger;
        }

        /// <param name="Ok">هل نُفِّذ</param>
        /// <param name="Problem">لماذا لم يُنفَّذ — يُعرض كما هو</param>
        public sealed record Outcome(bool Ok, string? Problem)
        {
            public static readonly Outcome Success = new(true, null);
            public static Outcome No(string problem) => new(false, problem);
        }

        public Task<GovNhiAccount?> FindAsync(long id, CancellationToken ct = default) =>
            _gov.NhiAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);

        /// <summary>الحسابات التي يملكها هذا الشخص — ما تعرضه شاشته</summary>
        public Task<List<GovNhiAccount>> OwnedByAsync(string username, CancellationToken ct = default) =>
            _gov.NhiAccounts
                .Where(a => a.OwnerUsername != null && a.OwnerUsername.ToLower() == username.ToLower())
                .OrderBy(a => a.Account)
                .ToListAsync(ct);

        // ══════════════════════════════════════
        // المطالبة
        // ══════════════════════════════════════

        /// <summary>
        /// Somebody puts their name against this account.
        ///
        /// A claim also lifts a quarantine — that is what quarantine is for. What it does not do is
        /// undo whatever was already carried out in the directory: if the account was disabled, a
        /// claim records the owner and leaves the effect standing, because re-enabling an account
        /// is a directory write and belongs to the run that holds the connection, not to a click.
        /// The row says plainly which state it is in.
        /// </summary>
        public async Task<Outcome> ClaimAsync(long id, string username, DateTime nowUtc, CancellationToken ct = default)
        {
            var a = await FindAsync(id, ct);
            if (a == null) return Outcome.No("هذا الحساب غير متعقَّب. / This account is not tracked.");

            if (NhiLifecyclePolicy.CanClaim(a, username) is { } problem)
                return Outcome.No(problem);

            var wasQuarantined = string.Equals(a.State, GovNhiStates.Quarantined, StringComparison.Ordinal);

            a.OwnerUsername = username;
            a.OwnerConfirmedUtc = nowUtc;
            a.LastAttestedUtc = nowUtc;      // claiming it is saying it is needed today
            a.LastAttestedBy = username;
            a.DisownedBy = null;
            a.DisownedUtc = null;
            a.State = GovNhiStates.Claimed;

            if (wasQuarantined)
            {
                a.QuarantineReason = null;
                a.QuarantinedUtc = null;
            }

            await _gov.SaveChangesAsync(ct);

            await _audit.LogAsync("NhiClaimed", AuditCategory, AuditSeverity.Info,
                entityType: nameof(GovNhiAccount), entityId: a.Id.ToString(),
                details: $"'{a.Account}' claimed by {username}" + (wasQuarantined ? " — released from quarantine" : ""),
                performedBy: username);

            _logger.LogInformation("NHI '{Account}' claimed by {User}{Released}",
                a.Account, username, wasQuarantined ? " — released from quarantine" : "");

            return Outcome.Success;
        }

        /// <summary>
        /// The owner hands it back.
        ///
        /// Always allowed for the owner: the alternative is people staying nominally answerable for
        /// accounts they know nothing about, which is worse than an honest gap. The original claim
        /// deadline still stands, so releasing buys no extension — and who released it is recorded,
        /// because "three people have declined this account" is worth knowing.
        /// </summary>
        public async Task<Outcome> DisownAsync(long id, string username, DateTime nowUtc, CancellationToken ct = default)
        {
            var a = await FindAsync(id, ct);
            if (a == null) return Outcome.No("هذا الحساب غير متعقَّب. / This account is not tracked.");

            if (NhiLifecyclePolicy.CanDisown(a, username) is { } problem)
                return Outcome.No(problem);

            a.DisownedBy = username;
            a.DisownedUtc = nowUtc;
            a.OwnerUsername = null;
            a.OwnerConfirmedUtc = null;
            a.State = GovNhiStates.Discovered;

            await _gov.SaveChangesAsync(ct);
            await _audit.LogAsync("NhiReleased", AuditCategory, AuditSeverity.Warning,
                entityType: nameof(GovNhiAccount), entityId: a.Id.ToString(),
                details: $"'{a.Account}' released by {username} — it has no owner again",
                performedBy: username);
            _logger.LogInformation("NHI '{Account}' released by {User}", a.Account, username);

            return Outcome.Success;
        }

        // ══════════════════════════════════════
        // الإقرار
        // ══════════════════════════════════════

        /// <summary>
        /// The owner confirms the account is still needed, restarting its clock.
        ///
        /// Only the owner may: an attestation made by anybody else records a confirmation that
        /// nobody answerable actually gave, which is worse than no attestation at all because it
        /// reads as one.
        /// </summary>
        public async Task<Outcome> AttestAsync(long id, string username, string? note, DateTime nowUtc, CancellationToken ct = default)
        {
            var a = await FindAsync(id, ct);
            if (a == null) return Outcome.No("هذا الحساب غير متعقَّب. / This account is not tracked.");

            if (NhiLifecyclePolicy.CanAttest(a, username) is { } problem)
                return Outcome.No(problem);

            a.LastAttestedUtc = nowUtc;
            a.LastAttestedBy = username;
            a.AttestationNote = note;

            if (string.Equals(a.State, GovNhiStates.Quarantined, StringComparison.Ordinal))
            {
                a.State = GovNhiStates.Claimed;
                a.QuarantineReason = null;
                a.QuarantinedUtc = null;
            }

            await _gov.SaveChangesAsync(ct);
            await _audit.LogAsync("NhiAttested", AuditCategory, AuditSeverity.Info,
                entityType: nameof(GovNhiAccount), entityId: a.Id.ToString(),
                details: $"'{a.Account}' attested by {username}" + (note is { Length: > 0 } ? $" — {note}" : ""),
                performedBy: username);

            return Outcome.Success;
        }

        // ══════════════════════════════════════
        // الاستثناء
        // ══════════════════════════════════════

        /// <summary>
        /// Puts an account outside the lifecycle for a stated period and a stated reason.
        ///
        /// Both are required. An exemption with no end date is a permanent hole opened for a
        /// temporary reason and closed by nobody, because nothing ever brings it back up; when this
        /// one expires the account returns to the lifecycle and the question is asked again.
        /// </summary>
        public async Task<Outcome> ExemptAsync(
            long id, string username, string? reason, DateTime? untilUtc, DateTime nowUtc, CancellationToken ct = default)
        {
            var a = await FindAsync(id, ct);
            if (a == null) return Outcome.No("هذا الحساب غير متعقَّب. / This account is not tracked.");

            if (NhiLifecyclePolicy.ValidateExemption(reason, untilUtc, nowUtc) is { } problem)
                return Outcome.No(problem);

            a.State = GovNhiStates.Exempt;
            a.ExemptReason = reason;
            a.ExemptBy = username;
            a.ExemptUntilUtc = untilUtc;
            a.QuarantineReason = null;
            a.QuarantinedUtc = null;

            await _gov.SaveChangesAsync(ct);
            await _audit.LogAsync("NhiExempted", AuditCategory, AuditSeverity.Warning,
                entityType: nameof(GovNhiAccount), entityId: a.Id.ToString(),
                details: $"'{a.Account}' exempted until {untilUtc:yyyy-MM-dd} — {reason}",
                performedBy: username);

            _logger.LogInformation("NHI '{Account}' exempted by {User} until {Until:yyyy-MM-dd}",
                a.Account, username, untilUtc);

            return Outcome.Success;
        }

        /// <summary>Ends an exemption early, returning the account to the lifecycle immediately.</summary>
        public async Task<Outcome> EndExemptionAsync(long id, string username, DateTime nowUtc, CancellationToken ct = default)
        {
            var a = await FindAsync(id, ct);
            if (a == null) return Outcome.No("هذا الحساب غير متعقَّب. / This account is not tracked.");

            if (!string.Equals(a.State, GovNhiStates.Exempt, StringComparison.Ordinal))
                return Outcome.No("هذا الحساب غير مستثنى. / This account is not exempt.");

            a.ExemptUntilUtc = nowUtc;
            a.State = a.OwnerUsername != null ? GovNhiStates.Claimed : GovNhiStates.Discovered;

            await _gov.SaveChangesAsync(ct);
            await _audit.LogAsync("NhiExemptionEnded", AuditCategory, AuditSeverity.Info,
                entityType: nameof(GovNhiAccount), entityId: a.Id.ToString(),
                details: $"the exemption on '{a.Account}' was ended early",
                performedBy: username);

            return Outcome.Success;
        }
    }
}
