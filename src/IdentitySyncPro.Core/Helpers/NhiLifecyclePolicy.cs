using IdentitySyncPro.Core.Models.Governance;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// The rules that move a non-human account through its life: who may claim it, when its owner
    /// must say it is still needed, and what happens when nobody ever answers.
    ///
    /// Kept as pure decisions, apart from the directory and the database, because this is the part
    /// that can quietly do harm. The inventory that feeds it was deliberately read-only — the note
    /// on it says the first thing this feature is allowed to do is count. This is the module where
    /// that stops being true, so every rule here is written to be testable without a domain
    /// controller and every dangerous one is a guard rather than a code path.
    /// </summary>
    public static class NhiLifecyclePolicy
    {
        /// <summary>الحدود التي يعمل ضمنها كل هذا — إعدادات الخدمة، مجموعةً في شيء يمكن اختباره</summary>
        /// <param name="Enabled">دورة الحياة كلها مُطفأة افتراضياً؛ الجرد وحده لا يحتاجها</param>
        /// <param name="ClaimDays">مهلة المطالبة بالمالك من أول ظهور</param>
        /// <param name="AttestationDays">كل كم يوماً يُعيد المالك التأكيد</param>
        /// <param name="GraceDays">مهلة إضافية بعد فوات الإقرار قبل الحجر</param>
        /// <param name="Enforcement">ما يُسمح للحجر أن يفعله بالدليل</param>
        /// <param name="MaxQuarantinePercent">سقف نسبة المحجور في تشغيلة واحدة</param>
        public sealed record LifecycleConfig(
            bool Enabled,
            int ClaimDays,
            int AttestationDays,
            int GraceDays,
            string Enforcement,
            int MaxQuarantinePercent);

        // ══════════════════════════════════════
        // التحقق من الإعدادات
        // ══════════════════════════════════════

        /// <summary>
        /// Refuses a configuration that cannot mean anything sensible, before a run starts.
        ///
        /// Zero-day windows are the dangerous case: a claim window of zero quarantines every
        /// account the first scan finds, which is a whole directory of service accounts in one
        /// sweep. It is refused rather than clamped, because clamping would carry out something
        /// close to — but not — what was asked for, without saying so.
        /// </summary>
        public static string? ValidateConfig(LifecycleConfig c)
        {
            if (!GovNhiEnforcement.IsKnown(c.Enforcement))
                return $"Unknown quarantine mode '{c.Enforcement}'. Expected Report, RemovePrivilege or Disable.";

            if (c.ClaimDays < 1)
                return "The owner-claim window must be at least 1 day — zero would quarantine every account found by the first scan.";

            if (c.AttestationDays < 1)
                return "The attestation period must be at least 1 day.";

            if (c.GraceDays < 0)
                return "The attestation grace period cannot be negative.";

            if (c.MaxQuarantinePercent is < 1 or > 100)
                return "The quarantine ceiling must be between 1 and 100 percent.";

            return null;
        }

        // ══════════════════════════════════════
        // المواعيد
        // ══════════════════════════════════════

        public static DateTime ClaimDeadline(DateTime firstSeenUtc, LifecycleConfig c) =>
            firstSeenUtc.AddDays(c.ClaimDays);

        /// <summary>
        /// When this account's owner next has to confirm it is still needed.
        ///
        /// Counted from the last attestation, or from the moment ownership was accepted when there
        /// has not been one — accepting an account is itself a statement that it is needed today.
        /// Returns null for an account nobody owns: there is nobody to ask.
        /// </summary>
        public static DateTime? AttestationDue(GovNhiAccount a, LifecycleConfig c)
        {
            var from = a.LastAttestedUtc ?? a.OwnerConfirmedUtc;
            return from?.AddDays(c.AttestationDays);
        }

        // ══════════════════════════════════════
        // القرار
        // ══════════════════════════════════════

        /// <param name="TargetState">الحالة التي ينبغي أن يكون عليها الآن</param>
        /// <param name="QuarantineReason">سبب الحجر متى كان الهدف حجراً</param>
        /// <param name="AttestationOverdue">فات الإقرار ولم تنتهِ المهلة بعد — وقت التذكير</param>
        /// <param name="Note">لماذا، بنص يُقرأ في سجلّ أو على شاشة</param>
        /// <param name="SuppressedQuarantine">
        /// سبب حجرٍ استحقّته القواعد ومُنع لأن الحساب محميّ. يُرفع ولا يُنفَّذ — فحساب ربط بلا
        /// مالك مشكلةٌ حقيقية تستحق أن تُقال، وحجرُه ليس علاجها.
        /// </param>
        public sealed record Verdict(
            string TargetState,
            string? QuarantineReason,
            bool AttestationOverdue,
            string? Note,
            string? SuppressedQuarantine = null)
        {
            public bool Changed(GovNhiAccount a) => !string.Equals(a.State, TargetState, StringComparison.Ordinal);
        }

        /// <summary>
        /// Where this account stands right now.
        ///
        /// One function, so that the screen, the nightly sweep and the notification all read the
        /// same answer. Two implementations of "is this overdue?" would drift, and the drift would
        /// show up as an account quarantined without the warning that was supposed to precede it.
        ///
        /// <para><b>Protection is applied here, not left to the caller.</b> An earlier shape had
        /// this return "quarantine" and a separate <see cref="ProtectedReason"/> saying "never
        /// touch this one", which is two answers every caller has to remember to combine — and the
        /// one that forgets disables IdentitySyncPro's own bind account. There is no way to ask
        /// this function about a protected account and be told to quarantine it.</para>
        /// </summary>
        public static Verdict Evaluate(GovNhiAccount a, LifecycleConfig c, DateTime nowUtc)
        {
            var verdict = Assess(a, c, nowUtc);

            if (!string.Equals(verdict.TargetState, GovNhiStates.Quarantined, StringComparison.Ordinal))
                return verdict;

            if (ProtectedReason(a) is not { } protection)
                return verdict;

            // The rules did conclude quarantine. Saying so out loud is the point: an unclaimed bind
            // account is a real gap, and swallowing it here would hide the very accounts nobody is
            // watching. What is refused is the action, not the finding.
            var held = string.Equals(a.State, GovNhiStates.Quarantined, StringComparison.Ordinal)
                ? GovNhiStates.Quarantined
                : a.OwnerUsername != null ? GovNhiStates.Claimed : GovNhiStates.Discovered;

            return new Verdict(held, null, verdict.AttestationOverdue,
                $"quarantine withheld — {protection}. {verdict.Note}",
                verdict.QuarantineReason);
        }

        private static Verdict Assess(GovNhiAccount a, LifecycleConfig c, DateTime nowUtc)
        {
            // Gone from the directory. Terminal: nothing to claim, nothing to disable.
            if (a.RetiredUtc != null || string.Equals(a.State, GovNhiStates.Retired, StringComparison.Ordinal))
                return new Verdict(GovNhiStates.Retired, null, false, "no longer present in the directory");

            // An exemption that has run out returns the account to the lifecycle rather than
            // holding it out forever. This is the reason the end date is required.
            if (string.Equals(a.State, GovNhiStates.Exempt, StringComparison.Ordinal))
            {
                if (a.ExemptUntilUtc is { } until && until > nowUtc)
                    return new Verdict(GovNhiStates.Exempt, null, false, $"exempt until {until:yyyy-MM-dd}");

                var back = a.OwnerUsername != null ? GovNhiStates.Claimed : GovNhiStates.Discovered;
                return new Verdict(back, null, false, "the exemption has expired — back in the lifecycle");
            }

            // A quarantined account stays quarantined until a person acts on it. Letting the sweep
            // release it would undo the only thing that makes anybody look for the owner.
            if (string.Equals(a.State, GovNhiStates.Quarantined, StringComparison.Ordinal))
                return new Verdict(GovNhiStates.Quarantined, a.QuarantineReason, false, "quarantined — awaiting a claim or an exemption");

            if (a.OwnerUsername == null)
            {
                var due = a.ClaimDueUtc ?? ClaimDeadline(a.FirstSeenUtc, c);
                if (nowUtc >= due)
                    return new Verdict(GovNhiStates.Quarantined, GovNhiQuarantineReasons.UnclaimedPastDeadline,
                        false, $"no owner claimed it by {due:yyyy-MM-dd}");

                return new Verdict(GovNhiStates.Discovered, null, false, $"awaiting an owner until {due:yyyy-MM-dd}");
            }

            var attestDue = AttestationDue(a, c);
            if (attestDue is { } d)
            {
                if (nowUtc >= d.AddDays(c.GraceDays))
                    return new Verdict(GovNhiStates.Quarantined, GovNhiQuarantineReasons.AttestationLapsed,
                        true, $"the owner did not re-attest by {d:yyyy-MM-dd} plus {c.GraceDays} day(s) of grace");

                if (nowUtc >= d)
                    return new Verdict(GovNhiStates.Claimed, null, true, $"attestation was due {d:yyyy-MM-dd} — inside the grace period");
            }

            return new Verdict(GovNhiStates.Claimed, null, false, $"owned by {a.OwnerUsername}");
        }

        // ══════════════════════════════════════
        // ما لا يُلمَس أبداً
        // ══════════════════════════════════════

        /// <summary>
        /// Accounts the sweep must never quarantine, whatever the rules conclude.
        ///
        /// <see cref="GovNhiAccount.IsSelfAccount"/> is the one that matters. IdentitySyncPro's own
        /// bind accounts are non-human accounts by every definition here, they will match the
        /// classifier, and they will go unclaimed for exactly as long as nobody thinks to claim the
        /// system's own credentials. Quarantining one stops every sync, every password reset and
        /// every AD login in the institution — at an hour nobody would connect to a governance
        /// sweep.
        /// </summary>
        public static string? ProtectedReason(GovNhiAccount a)
        {
            if (a.IsSelfAccount) return "an IdentitySyncPro bind account — quarantining it would stop the product";
            if (string.Equals(a.State, GovNhiStates.Exempt, StringComparison.Ordinal)) return "exempt";
            if (a.RetiredUtc != null) return "no longer in the directory";
            return null;
        }

        // ══════════════════════════════════════
        // الحارسان قبل أي كتابة في الدليل
        // ══════════════════════════════════════

        /// <param name="Allowed">هل يُسمح للتشغيلة بلمس الدليل</param>
        /// <param name="Reason">لماذا مُنعت — يُرفع كما هو</param>
        public sealed record EnforcementRight(bool Allowed, string? Reason);

        /// <summary>
        /// Whether this run may write to the directory at all.
        ///
        /// The self-account registry resolves IdentitySyncPro's own bind credentials to
        /// distinguished names on every run. An entry it could not resolve is an account that
        /// cannot be proved <i>not</i> to be the one about to be disabled — so anything beyond
        /// reporting is refused for the whole run. The registry's own contract asks for exactly
        /// this, and this is the first caller that acts on accounts, so this is where it takes
        /// effect.
        ///
        /// Refused for the run rather than skipped per account: skipping would leave the sweep
        /// doing most of its work while silently omitting the part it could not verify, which is
        /// the shape of failure this codebase keeps refusing.
        /// </summary>
        public static EnforcementRight MayEnforce(string enforcement, int unresolvedSelfAccounts)
        {
            if (!GovNhiEnforcement.IsKnown(enforcement))
                return new EnforcementRight(false, $"unknown quarantine mode '{enforcement}'");

            if (!GovNhiEnforcement.TouchesDirectory(enforcement))
                return new EnforcementRight(true, null);   // Report never writes; nothing to guard

            if (unresolvedSelfAccounts > 0)
                return new EnforcementRight(false,
                    $"{unresolvedSelfAccounts} IdentitySyncPro bind account(s) could not be resolved in the directory — " +
                    "an unresolved bind account cannot be proved not to be the one about to be acted on, so enforcement is refused for this run");

            return new EnforcementRight(true, null);
        }

        /// <param name="Allowed">هل تُنفَّذ سحوبات هذه التشغيلة</param>
        /// <param name="Quarantining">كم حساباً سيُحجر</param>
        /// <param name="Total">حجم المجتمع المُتتبَّع</param>
        public sealed record QuarantineVerdict(bool Allowed, int Quarantining, int Total, string? Reason);

        /// <summary>
        /// Stops a sweep that is about to quarantine an implausible share of the population.
        ///
        /// Quarantining most of the service accounts in a domain is not policy working; it is a
        /// classifier that is too broad, a lifecycle switched on with the windows left at a day, or
        /// a scan that ran before anybody was told to claim anything. The same reasoning as the
        /// campaign auto-revoke ceiling, and as the empty-source guard in OrphanCleanup: the run
        /// stops and says so, rather than carrying out a mass action nobody asked for.
        /// </summary>
        public static QuarantineVerdict MayQuarantine(int total, int quarantining, int maxPercent)
        {
            if (total <= 0)
                return new QuarantineVerdict(false, quarantining, total,
                    "the tracked population is empty — nothing was scanned, so nothing can be concluded");

            var percent = quarantining * 100.0 / total;
            if (percent > maxPercent)
                return new QuarantineVerdict(false, quarantining, total,
                    $"{quarantining} of {total} accounts ({percent:F0}%) would be quarantined, above the {maxPercent}% ceiling — " +
                    "review the classifier and the lifecycle windows before letting this run act");

            return new QuarantineVerdict(true, quarantining, total, null);
        }

        // ══════════════════════════════════════
        // أفعال البشر
        // ══════════════════════════════════════

        /// <summary>
        /// Whether this person may take ownership of this account.
        ///
        /// Anyone signed in may claim: the difficulty with non-human accounts is finding somebody
        /// willing to be answerable at all, and a claim is a person putting their name against
        /// something, not a grant of privilege. What the claim must not do is overwrite an owner
        /// who already exists — that would let an account be quietly taken from the person the
        /// audit trail names.
        /// </summary>
        public static string? CanClaim(GovNhiAccount a, string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return "اسم المستخدم مطلوب. / A username is required.";
            if (a.RetiredUtc != null) return "هذا الحساب لم يعد موجوداً في الدليل. / This account is no longer in the directory.";

            if (a.OwnerUsername != null &&
                !string.Equals(a.OwnerUsername, username, StringComparison.OrdinalIgnoreCase))
                return $"هذا الحساب يملكه {a.OwnerUsername} — على المالك الحالي إعادته أولاً. / This account is already owned by {a.OwnerUsername}. The current owner must release it first.";

            return null;
        }

        /// <summary>
        /// Whether this person may attest. Only the owner may — an attestation is the owner saying
        /// the account is still needed, and one made by anybody else records a confirmation nobody
        /// answerable actually gave.
        /// </summary>
        public static string? CanAttest(GovNhiAccount a, string username)
        {
            if (a.OwnerUsername == null) return "لا مالك لهذا الحساب بعد — يجب أن يُطالَب به قبل الإقرار. / This account has no owner yet — it must be claimed before it can be attested.";
            if (a.RetiredUtc != null) return "هذا الحساب لم يعد موجوداً في الدليل. / This account is no longer in the directory.";

            if (!string.Equals(a.OwnerUsername, username, StringComparison.OrdinalIgnoreCase))
                return $"المالك ({a.OwnerUsername}) وحده من يُقرّ بهذا الحساب. / Only the owner ({a.OwnerUsername}) can attest to this account.";

            return null;
        }

        /// <summary>
        /// Whether the owner may hand the account back.
        ///
        /// Releasing is always allowed for the owner — the alternative is people staying nominally
        /// responsible for accounts they know nothing about, which is worse than an honest gap. The
        /// account returns to the unowned pool and the original claim deadline still applies, so
        /// releasing does not buy anybody an extension.
        /// </summary>
        public static string? CanDisown(GovNhiAccount a, string username)
        {
            if (a.OwnerUsername == null) return "لا مالك لهذا الحساب لإعادته. / This account has no owner to release.";

            if (!string.Equals(a.OwnerUsername, username, StringComparison.OrdinalIgnoreCase))
                return $"المالك ({a.OwnerUsername}) وحده من يُعيد هذا الحساب. / Only the owner ({a.OwnerUsername}) can release this account.";

            return null;
        }

        /// <summary>
        /// An exemption has to say why and until when. Both are refused when missing, because an
        /// exemption without either is indistinguishable from an account that was quietly dropped
        /// out of the inventory.
        /// </summary>
        public static string? ValidateExemption(string? reason, DateTime? untilUtc, DateTime nowUtc, int maxDays = 365)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return "الاستثناء يجب أن يذكر سببه. / An exemption must state a reason.";

            if (untilUtc == null)
                return "الاستثناء يجب أن يكون له تاريخ انتهاء — الاستثناء الدائم ثغرة لا يُذكّر بها شيء. / An exemption must have an end date — one that never expires is a permanent gap nothing brings back up.";

            if (untilUtc <= nowUtc)
                return "تاريخ انتهاء الاستثناء يجب أن يكون في المستقبل. / The exemption end date must be in the future.";

            if (untilUtc > nowUtc.AddDays(maxDays))
                return $"لا يجوز أن يتجاوز الاستثناء {maxDays} يوماً — جدّده بدلاً من ذلك، فالتجديد قرار يُتخذ من جديد. / An exemption cannot run longer than {maxDays} days. Renew it instead — a renewal is a decision somebody makes again.";

            return null;
        }
    }
}
