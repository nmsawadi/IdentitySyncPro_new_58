namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Outcome of step 1 (username + national ID) on the SSPR portal.
    ///
    /// ⚠️ Security note: by design (business decision) the portal now tells the user
    /// WHY the request failed instead of the previous uniform response. That makes
    /// account enumeration possible, which is why <see cref="IpBlocked"/> exists:
    /// repeated <see cref="InvalidIdentity"/> results from one IP block that IP.
    /// </summary>
    public enum SsprRequestOutcome
    {
        /// <summary>Identity verified and an OTP was sent — proceed to the code screen.</summary>
        OtpSent,

        /// <summary>Wrong username or wrong national ID. Counts toward the IP block.</summary>
        InvalidIdentity,

        /// <summary>This IP is blocked after too many failed identity attempts.</summary>
        IpBlocked,

        /// <summary>The account already used all its allowed resets in the last 24 hours.</summary>
        UserResetLimit,

        /// <summary>Hourly request rate limit (per IP or per username) hit.</summary>
        RateLimited,

        /// <summary>Identity verified, but the account has no mobile number in AD.</summary>
        NoPhone,

        /// <summary>Account is in an excluded group (admin/service) — not eligible.</summary>
        Excluded,

        /// <summary>Identity verified but the OTP SMS could not be delivered.</summary>
        SmsFailed,

        /// <summary>
        /// A domain lookup failed (AD unreachable, bad service credentials, ...). The user's
        /// input was never actually checked, so this never counts toward the IP block.
        /// </summary>
        DirectoryError,

        /// <summary>Portal is switched off.</summary>
        Disabled
    }

    /// <summary>Result of <see cref="SsprService.RequestOtpAsync"/>.</summary>
    /// <param name="Outcome">What happened.</param>
    /// <param name="RequestGuid">Handle for step 2 — only set when <see cref="SsprRequestOutcome.OtpSent"/>.</param>
    /// <param name="RetryAfterMinutes">Minutes until an IP block lifts — only set when <see cref="SsprRequestOutcome.IpBlocked"/>.</param>
    public sealed record SsprRequestResult(
        SsprRequestOutcome Outcome,
        string? RequestGuid = null,
        int? RetryAfterMinutes = null);
}
