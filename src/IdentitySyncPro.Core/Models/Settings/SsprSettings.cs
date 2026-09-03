namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// Global configuration for the standalone Self-Service Password Reset module.
    /// Single row (Id = 1). Independent from sync tenants — SSPR verifies users
    /// directly against the configured AD domains.
    /// </summary>
    public class SsprSettings
    {
        public int Id { get; set; }

        /// <summary>Master on/off switch for the public /Sspr portal.</summary>
        public bool IsEnabled { get; set; } = false;

        /// <summary>Dedicated SMS provider used to send OTPs (references SmsProviders).</summary>
        public int? SmsProviderId { get; set; }

        // === Tunable limits (admin-configurable) ===

        /// <summary>OTP validity in seconds (also drives the portal countdown bar). Default 300 (5 min).</summary>
        public int OtpLifetimeSeconds { get; set; } = 300;

        /// <summary>Max OTP entry attempts before the request is blocked. Default 3.</summary>
        public int MaxVerifyAttempts { get; set; } = 3;

        /// <summary>Max OTP requests per IP per hour. Default 5.</summary>
        public int MaxRequestsPerIpPerHour { get; set; } = 5;

        /// <summary>Max OTP requests per username per hour. Default 3.</summary>
        public int MaxRequestsPerUserPerHour { get; set; } = 3;

        /// <summary>
        /// Wrong username / national-ID attempts allowed from one IP before that IP
        /// is blocked. Default 5.
        /// </summary>
        public int MaxFailedIdentityAttempts { get; set; } = 5;

        /// <summary>
        /// How long a blocked IP stays blocked, in hours. Doubles as the window over
        /// which failed attempts are counted. Default 24.
        /// </summary>
        public int IpBlockDurationHours { get; set; } = 24;

        /// <summary>
        /// Max SUCCESSFUL password resets allowed per username per 24 hours. Default 3.
        /// </summary>
        public int MaxResetsPerUserPer24h { get; set; } = 3;

        /// <summary>
        /// OTP SMS text (Arabic). {OTP} is replaced with the code.
        /// Sent when the portal is used in Arabic.
        /// </summary>
        public string MessageTemplate { get; set; } =
            "رمز التحقق لإعادة تعيين كلمة المرور: {OTP}\nصالح لمدة 5 دقائق. إن لم تطلب ذلك تجاهل الرسالة.";

        /// <summary>
        /// OTP SMS text (English). {OTP} is replaced with the code.
        /// Sent when the portal is used in English.
        /// </summary>
        public string MessageTemplateEn { get; set; } =
            "Your password reset verification code: {OTP}\nValid for 5 minutes. If you didn't request this, ignore this message.";

        /// <summary>
        /// New-password SMS text (Arabic). {PASSWORD} → generated password,
        /// {USERNAME} → a partially masked username so the recipient knows the account.
        /// Sent after a correct OTP — the system generates the password automatically.
        /// </summary>
        public string NewPasswordTemplate { get; set; } =
            "تم إعادة تعيين كلمة المرور للمستخدم {USERNAME}.\nكلمة المرور الجديدة: {PASSWORD}\nيُنصح بتغييرها بعد أول تسجيل دخول.";

        /// <summary>New-password SMS text (English). {PASSWORD} → password, {USERNAME} → masked username.</summary>
        public string NewPasswordTemplateEn { get; set; } =
            "Password reset for user {USERNAME}.\nNew password: {PASSWORD}\nWe recommend changing it after your first sign-in.";

        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    }
}
