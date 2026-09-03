namespace IdentitySyncPro.Core.Models.Audit
{
    /// <summary>
    /// Who triggered a run, as stored in <c>TriggeredBy</c> / <c>PerformedBy</c>.
    ///
    /// The rule: a run started by a person stores that person's **username**. Everything else
    /// stores one of the tokens below. So the column answers "who" directly, and only the handful
    /// of non-human origins need translating for display.
    ///
    /// This replaces a column that said <c>"Manual"</c> for every run including the scheduled ones —
    /// the Hangfire job passed the literal string "Manual" too, so the field distinguished nothing
    /// at all and named nobody.
    /// </summary>
    public static class ActorNames
    {
        /// <summary>A recurring Hangfire schedule — nobody pressed anything.</summary>
        public const string Schedule = "Schedule";

        /// <summary>Internal work with no originator: startup, health checks, retention.</summary>
        public const string System = "System";

        /// <summary>The lifecycle engine acting on its own rules.</summary>
        public const string LifecycleEngine = "LifecycleEngine";

        /// <summary>The bulk lifecycle pipeline.</summary>
        public const string BulkPipeline = "BulkPipeline";

        /// <summary>
        /// Rows written before the username was recorded. Kept only so historic rows read
        /// honestly — nothing writes it any more, and it must never be presented as if a user
        /// were identified.
        /// </summary>
        public const string LegacyManual = "Manual";

        /// <summary>
        /// Display text for a stored value. Anything unrecognised is a username and is returned
        /// unchanged — inventing a label for it would hide the very thing the column is for.
        /// </summary>
        public static string Describe(string? stored, bool isArabic)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return isArabic ? "غير معروف" : "Unknown";

            return stored switch
            {
                Schedule => isArabic ? "الجدولة" : "Schedule",
                System => isArabic ? "النظام" : "System",
                LifecycleEngine => isArabic ? "محرك دورة الحياة" : "Lifecycle engine",
                BulkPipeline => isArabic ? "معالجة جماعية" : "Bulk pipeline",
                // Deliberately says the user was not recorded rather than "manual", which reads
                // as though somebody had been identified.
                LegacyManual => isArabic ? "يدوي — المستخدم غير مسجَّل" : "Manual — user not recorded",
                _ => stored
            };
        }

        /// <summary>
        /// Width of the TriggeredBy / PerformedBy columns, matching AppUser.Username.
        /// </summary>
        public const int MaxLength = 200;

        /// <summary>
        /// Fits an actor to the column. The value is written at the very END of a run, so one
        /// character too many throws on SaveChanges and takes the run's final status with it —
        /// the same way an over-long run summary would. Truncating a name is survivable; losing
        /// the record of what the run did is not.
        /// </summary>
        public static string Clamp(string? actor)
        {
            if (string.IsNullOrWhiteSpace(actor)) return System;
            var trimmed = actor!.Trim();
            return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength];
        }

        /// <summary>
        /// The actor for a background job. Blank means the job payload predates the actor argument,
        /// and the only payloads that can be that old are the recurring registrations — a manual
        /// run always carries the username that enqueued it.
        /// </summary>
        public static string OrSchedule(string? triggeredBy) =>
            string.IsNullOrWhiteSpace(triggeredBy) ? Schedule : Clamp(triggeredBy);

        /// <summary>True when the value names a person rather than an automated origin.</summary>
        public static bool IsUser(string? stored) =>
            !string.IsNullOrWhiteSpace(stored) &&
            stored is not (Schedule or System or LifecycleEngine or BulkPipeline or LegacyManual);
    }
}
