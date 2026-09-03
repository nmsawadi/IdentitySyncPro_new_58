using System.Security.Cryptography;
using System.Text;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Connectors;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Standalone Self-Service Password Reset (public portal).
    ///
    /// Independent from sync tenants: the user is identified by AD account name
    /// (letters or digits) and verified DIRECTLY against the configured AD
    /// domains by national ID. When both match, the mobile number is pulled
    /// FROM AD and the OTP is sent there.
    ///
    /// Security posture:
    /// - Uniform responses: the caller can never learn whether an account exists.
    /// - OTP: 6 digits, stored as SHA256 only, 5-minute expiry, 3 verify attempts.
    /// - Rate limits: max 5 requests/hour per IP and 3 per username.
    /// - Excluded AD groups (admins/service accounts) are denied; membership
    ///   check fails CLOSED (deny on error).
    /// - Every step is written to the audit trail.
    /// </summary>
    public class SsprService
    {
        /// <summary>Fallback OTP lifetime used only when no settings row exists yet.</summary>
        public const int DefaultOtpLifetimeSeconds = 300;

        // Clamp admin-entered values to sane bounds
        private static int OtpLifetime(SsprSettings s) => Math.Clamp(s.OtpLifetimeSeconds <= 0 ? DefaultOtpLifetimeSeconds : s.OtpLifetimeSeconds, 60, 1800);
        private static int MaxAttempts(SsprSettings s) => Math.Clamp(s.MaxVerifyAttempts <= 0 ? 3 : s.MaxVerifyAttempts, 1, 10);
        private static int MaxPerIp(SsprSettings s) => Math.Clamp(s.MaxRequestsPerIpPerHour <= 0 ? 5 : s.MaxRequestsPerIpPerHour, 1, 100);
        private static int MaxPerUser(SsprSettings s) => Math.Clamp(s.MaxRequestsPerUserPerHour <= 0 ? 3 : s.MaxRequestsPerUserPerHour, 1, 50);
        private static int MaxFailedIdentity(SsprSettings s) => Math.Clamp(s.MaxFailedIdentityAttempts <= 0 ? 5 : s.MaxFailedIdentityAttempts, 1, 20);
        private static int IpBlockHours(SsprSettings s) => Math.Clamp(s.IpBlockDurationHours <= 0 ? 24 : s.IpBlockDurationHours, 1, 720);
        private static int MaxResetsPer24h(SsprSettings s) => Math.Clamp(s.MaxResetsPerUserPer24h <= 0 ? 3 : s.MaxResetsPerUserPer24h, 1, 20);

        private readonly AppDbContext _db;
        private readonly ISmsService _smsService;
        private readonly IAuditService _audit;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<SsprService> _logger;

        public SsprService(
            AppDbContext db,
            ISmsService smsService,
            IAuditService audit,
            ILoggerFactory loggerFactory,
            ILogger<SsprService> logger)
        {
            _db = db;
            _smsService = smsService;
            _audit = audit;
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        /// <summary>True when the portal is enabled and at least one active domain exists.</summary>
        public async Task<bool> IsPortalEnabledAsync(CancellationToken ct = default)
        {
            var settings = await _db.SsprSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null || !settings.IsEnabled) return false;
            return await _db.SsprDomains.AsNoTracking().AnyAsync(d => d.IsActive, ct);
        }

        /// <summary>Configured OTP lifetime in seconds (clamped) — used by the portal countdown.</summary>
        public async Task<int> GetOtpLifetimeSecondsAsync(CancellationToken ct = default)
        {
            var settings = await _db.SsprSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            return settings == null ? DefaultOtpLifetimeSeconds : OtpLifetime(settings);
        }

        /// <summary>
        /// Step 1 — verify username + national ID against each AD domain; on a
        /// match, pull the mobile from AD and send an OTP.
        ///
        /// Returns a specific outcome so the portal can tell the user what went wrong
        /// and keep them on the entry screen instead of advancing to the code screen.
        /// A wrong username/national ID counts toward the per-IP block.
        /// </summary>
        public async Task<SsprRequestResult> RequestOtpAsync(string username, string nationalId, string? clientIp, string lang = "ar", CancellationToken ct = default)
        {
            username = (username ?? "").Trim();
            nationalId = (nationalId ?? "").Trim();

            var settings = await _db.SsprSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null || !settings.IsEnabled) return new SsprRequestResult(SsprRequestOutcome.Disabled);

            // An already-blocked IP is rejected before any AD work or empty-field check,
            // so a blocked client can't probe at all.
            var activeBlock = await GetActiveBlockAsync(clientIp, ct);
            if (activeBlock != null)
            {
                var mins = (int)Math.Ceiling((activeBlock.BlockedUntilUtc!.Value - DateTime.UtcNow).TotalMinutes);
                return new SsprRequestResult(SsprRequestOutcome.IpBlocked, null, Math.Max(mins, 1));
            }

            // Empty fields never reach AD and are not worth counting as an attack attempt.
            if (username.Length == 0 || nationalId.Length == 0)
                return new SsprRequestResult(SsprRequestOutcome.InvalidIdentity);

            var since = DateTime.UtcNow.AddHours(-1);

            // Per-IP hourly limit. Safe to check up front: it only describes the caller's
            // own IP and discloses nothing about any account.
            if (!string.IsNullOrEmpty(clientIp) &&
                await _db.PasswordResetRequests.CountAsync(r => r.ClientIp == clientIp && r.CreatedAtUtc >= since, ct) >= MaxPerIp(settings))
            {
                await _audit.LogAsync($"SSPR rate-limited (IP): {clientIp}", "Security", Core.Enums.AuditSeverity.Warning);
                return new SsprRequestResult(SsprRequestOutcome.RateLimited);
            }

            // NOTE: the per-username limits are deliberately NOT checked here. Now that the
            // portal reveals why a request failed, answering them before the national ID is
            // verified would confirm a username exists to someone who only guessed the name.
            // They are enforced below, once the identity is proven.

            var domains = await _db.SsprDomains.AsNoTracking()
                .Where(d => d.IsActive)
                .OrderBy(d => d.Id)
                .ToListAsync(ct);

            // Tracks a lookup that blew up (AD unreachable / bad service account) as opposed
            // to a genuine "no such user". See the fall-through below.
            var directoryFailed = false;

            foreach (var domain in domains)
            {
                try
                {
                    var connector = BuildConnector(domain);

                    var wanted = new[] { domain.NationalIdAttribute, domain.MobileAttribute };
                    var attrs = await connector.GetAttributesAsync(username, wanted, ct);
                    if (attrs == null) continue; // username not in this domain — try next

                    // ✅ BOTH must belong to the SAME account: the username locates the exact
                    // account (sAMAccountName is unique in the domain), and the national ID
                    // stored ON THAT account must match. A wrong national ID → deny, even if
                    // the username exists. Employees with several accounts reset only the one
                    // whose username they entered. Empty AD national ID also denies.
                    attrs.TryGetValue(domain.NationalIdAttribute, out var adNationalId);
                    if (!NationalIdMatches(adNationalId, nationalId))
                    {
                        await _audit.LogAsync($"SSPR national-ID mismatch for {username}@{domain.Name} from {clientIp}", "Security", Core.Enums.AuditSeverity.Warning);
                        continue;
                    }

                    // === Identity verified from here on ===
                    await ClearIpFailuresAsync(clientIp, ct);

                    // Deny excluded groups (fail-closed inside the connector)
                    var excluded = (domain.ExcludedGroups ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (excluded.Length > 0 && await connector.IsMemberOfAnyAsync(username, excluded, ct))
                    {
                        await _audit.LogAsync($"SSPR denied (excluded group): {username}@{domain.Name}", "Security", Core.Enums.AuditSeverity.Warning);
                        return new SsprRequestResult(SsprRequestOutcome.Excluded);
                    }

                    // Per-username limits — only now that the caller proved who they are.
                    if (await _db.PasswordResetRequests.CountAsync(r => r.Username == username && r.CreatedAtUtc >= since, ct) >= MaxPerUser(settings))
                    {
                        await _audit.LogAsync($"SSPR rate-limited (user): {username}", "Security", Core.Enums.AuditSeverity.Warning);
                        return new SsprRequestResult(SsprRequestOutcome.RateLimited);
                    }

                    // Max SUCCESSFUL resets per username per 24h
                    var since24 = DateTime.UtcNow.AddHours(-24);
                    var doneIn24 = await _db.PasswordResetRequests
                        .CountAsync(r => r.Username == username && r.Status == "Completed" && r.CreatedAtUtc >= since24, ct);
                    if (doneIn24 >= MaxResetsPer24h(settings))
                    {
                        await _audit.LogAsync($"SSPR 24h reset limit reached ({doneIn24}/{MaxResetsPer24h(settings)}): {username} from {clientIp}", "Security", Core.Enums.AuditSeverity.Warning);
                        return new SsprRequestResult(SsprRequestOutcome.UserResetLimit);
                    }

                    // Normalize the AD phone to the gateway format (handles 05.., +966.., 00966.., bare 5..)
                    attrs.TryGetValue(domain.MobileAttribute, out var rawPhone);
                    var phone = PhoneHelper.NormalizePhone(rawPhone);
                    if (phone.Length == 0)
                    {
                        // Verified but no phone on record — cannot deliver OTP
                        await _audit.LogAsync($"SSPR no phone in AD for {username}@{domain.Name}", "Security", Core.Enums.AuditSeverity.Warning);
                        return new SsprRequestResult(SsprRequestOutcome.NoPhone);
                    }

                    var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
                    var request = new PasswordResetRequest
                    {
                        SsprDomainId = domain.Id,
                        Username = username,
                        PhoneNumber = phone,
                        OtpHash = Sha256(otp),
                        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(OtpLifetime(settings)),
                        ClientIp = clientIp
                    };
                    _db.PasswordResetRequests.Add(request);
                    await _db.SaveChangesAsync(ct);

                    var sent = await SendOtpSmsAsync(settings, phone, otp, username, lang, ct);
                    if (!sent)
                    {
                        request.Status = "Blocked";
                        await _db.SaveChangesAsync(ct);
                        await _audit.LogAsync($"SSPR OTP send failed for {username}@{domain.Name}", "Security", Core.Enums.AuditSeverity.Warning);
                        return new SsprRequestResult(SsprRequestOutcome.SmsFailed);
                    }

                    await _audit.LogAsync($"SSPR username+nationalID matched — OTP sent to {PhoneHelper.MaskPhone(phone)}: {username}@{domain.Name} from {clientIp}", "Security", Core.Enums.AuditSeverity.Info);
                    return new SsprRequestResult(SsprRequestOutcome.OtpSent, request.RequestGuid);
                }
                catch (Exception ex)
                {
                    directoryFailed = true;
                    _logger.LogWarning(ex, "SSPR lookup failed for domain {Domain}", domain.Name);
                }
            }

            // A domain lookup threw, so we never actually got to compare the user's input.
            // Counting this as a failed attempt would block innocent users' IPs whenever AD
            // is down or a service account is misconfigured — so bail out without counting.
            if (directoryFailed)
            {
                await _audit.LogAsync($"SSPR directory error while checking {username} from {clientIp} — attempt NOT counted", "Security", Core.Enums.AuditSeverity.Error);
                return new SsprRequestResult(SsprRequestOutcome.DirectoryError);
            }

            // Wrong username, or right username with the wrong national ID.
            var (count, blockedMins) = await RecordIdentityFailureAsync(clientIp, username, settings, ct);
            await _audit.LogAsync($"SSPR invalid identity ({count}/{MaxFailedIdentity(settings)}): {username} from {clientIp}", "Security", Core.Enums.AuditSeverity.Warning);

            return blockedMins.HasValue
                ? new SsprRequestResult(SsprRequestOutcome.IpBlocked, null, blockedMins)
                : new SsprRequestResult(SsprRequestOutcome.InvalidIdentity);
        }

        /// <summary>Returns the row only when this IP is currently blocked.</summary>
        private async Task<SsprIpBlock?> GetActiveBlockAsync(string? clientIp, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(clientIp)) return null;
            var row = await _db.SsprIpBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.ClientIp == clientIp, ct);
            return row?.BlockedUntilUtc > DateTime.UtcNow ? row : null;
        }

        /// <summary>
        /// Count one wrong username/national-ID attempt for this IP and block it once the
        /// configured threshold is reached. Returns the running count and, when the block
        /// just took effect, how many minutes it lasts.
        /// </summary>
        private async Task<(int Count, int? BlockedMinutes)> RecordIdentityFailureAsync(
            string? clientIp, string username, SsprSettings settings, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(clientIp)) return (0, null);

            var now = DateTime.UtcNow;
            var windowHours = IpBlockHours(settings);

            var row = await _db.SsprIpBlocks.FirstOrDefaultAsync(b => b.ClientIp == clientIp, ct);
            if (row == null)
            {
                row = new SsprIpBlock { ClientIp = clientIp, FirstFailureUtc = now };
                _db.SsprIpBlocks.Add(row);
            }
            else if (row.LastFailureUtc < now.AddHours(-windowHours))
            {
                // Previous window elapsed with no further failures — start counting again.
                row.FailedCount = 0;
                row.FirstFailureUtc = now;
                row.BlockedUntilUtc = null;
            }

            row.FailedCount++;
            row.LastFailureUtc = now;
            row.LastUsername = username;

            int? blockedMinutes = null;
            if (row.FailedCount >= MaxFailedIdentity(settings))
            {
                row.BlockedUntilUtc = now.AddHours(windowHours);
                blockedMinutes = windowHours * 60;
                await _audit.LogAsync(
                    $"SSPR IP BLOCKED for {windowHours}h after {row.FailedCount} failed attempts: {clientIp} (last username tried: {username})",
                    "Security", Core.Enums.AuditSeverity.Error);
            }

            await _db.SaveChangesAsync(ct);
            return (row.FailedCount, blockedMinutes);
        }

        /// <summary>Successful identity check → forget this IP's failed attempts.</summary>
        private async Task ClearIpFailuresAsync(string? clientIp, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(clientIp)) return;
            var row = await _db.SsprIpBlocks.FirstOrDefaultAsync(b => b.ClientIp == clientIp, ct);
            if (row == null || row.FailedCount == 0) return;

            row.FailedCount = 0;
            row.BlockedUntilUtc = null;
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Step 2 — verify the OTP. On a correct code the system GENERATES a new
        /// password, resets the AD account, and sends the new password by SMS.
        /// The user never types a password.
        /// </summary>
        public async Task<(bool Success, string ErrorCode)> VerifyAndSendPasswordAsync(string requestGuid, string otp, string? clientIp, string lang = "ar", CancellationToken ct = default)
        {
            var request = await _db.PasswordResetRequests.FirstOrDefaultAsync(r => r.RequestGuid == requestGuid, ct);
            // Unknown/used request → treat like a wrong code (uniform, no disclosure)
            if (request == null || request.IsUsed || request.Status != "Pending")
                return (false, "wrong_otp");

            if (request.ExpiresAtUtc < DateTime.UtcNow)
            {
                request.Status = "Expired";
                await _db.SaveChangesAsync(ct);
                return (false, "expired");
            }

            var settingsRow = await _db.SsprSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new SsprSettings();
            if (request.Attempts >= MaxAttempts(settingsRow))
            {
                request.Status = "Blocked";
                await _db.SaveChangesAsync(ct);
                await _audit.LogAsync($"SSPR blocked after attempts: {request.Username}", "Security", Core.Enums.AuditSeverity.Warning);
                return (false, "blocked");
            }

            request.Attempts++;
            await _db.SaveChangesAsync(ct);

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Sha256(otp ?? "")),
                    Encoding.UTF8.GetBytes(request.OtpHash)))
            {
                await _audit.LogAsync($"SSPR wrong OTP (attempt {request.Attempts}): {request.Username} from {clientIp}", "Security", Core.Enums.AuditSeverity.Warning);
                return (false, "wrong_otp");
            }

            var domain = await _db.SsprDomains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.SsprDomainId, ct);
            if (domain == null) return (false, "invalid");

            // OTP correct → generate a strong password and reset AD
            var newPassword = PasswordGenerator.Generate();
            var connector = BuildConnector(domain);
            var (ok, error) = await connector.ResetPasswordAsync(request.Username, newPassword, ct);
            if (!ok)
            {
                await _audit.LogAsync($"SSPR AD reset failed for {request.Username}: {error}", "Security", Core.Enums.AuditSeverity.Error);
                return (false, "ad_rejected");
            }

            // Send the NEW password by SMS to the same verified mobile
            var settings = await _db.SsprSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var sent = settings != null && await SendNewPasswordSmsAsync(settings, request.PhoneNumber, newPassword, request.Username, lang, ct);

            request.IsUsed = true;
            request.Status = "Completed";
            await _db.SaveChangesAsync(ct);

            if (!sent)
            {
                // The password WAS changed but the SMS failed — the user can't see it.
                await _audit.LogAsync($"SSPR reset done but new-password SMS FAILED for {request.Username} — manual delivery required", "Security", Core.Enums.AuditSeverity.Error);
                return (false, "sms_failed");
            }

            await _audit.LogAsync($"SSPR password reset + new password sent: {request.Username} from {clientIp}", "Security", Core.Enums.AuditSeverity.Info);
            return (true, "");
        }

        private ActiveDirectoryConnector BuildConnector(SsprDomain domain)
        {
            var opts = domain.ToLdapOptions();
            var settings = new ADConnectionSettings
            {
                Server = domain.AdServer,
                Port = domain.AdPort,
                // Carry the domain's resolved channel choice through verbatim.
                SecurityMode = opts.SecurityMode,
                SecurityModeSet = true,
                AllowUntrustedCertificate = opts.AllowUntrustedCertificate,
                Username = domain.AdUsername,
                Password = domain.AdPassword,
                BaseDN = domain.AdBaseDN
            };
            return new ActiveDirectoryConnector(settings, _loggerFactory.CreateLogger<ActiveDirectoryConnector>());
        }

        private async Task<bool> SendOtpSmsAsync(SsprSettings settings, string phone, string otp, string username, string lang, CancellationToken ct)
        {
            if (!settings.SmsProviderId.HasValue) return false;

            var provider = await _db.SmsProviders.FindAsync(new object[] { settings.SmsProviderId.Value }, ct);
            if (provider == null || !provider.IsActive) return false;

            // Message follows the portal language the user chose
            var template = lang == "en" ? settings.MessageTemplateEn : settings.MessageTemplate;
            var message = (template ?? "").Replace("{OTP}", otp);
            if (!message.Contains(otp)) message = $"{message} {otp}".Trim();

            var result = await _smsService.SendCredentialsAsync(new SmsRequest
            {
                PhoneNumber = phone,
                IdentityId = username,
                MessageTemplate = message // literal text (already contains the code)
            }.WithProvider(provider));

            return result.Success;
        }

        private async Task<bool> SendNewPasswordSmsAsync(SsprSettings settings, string phone, string password, string username, string lang, CancellationToken ct)
        {
            if (!settings.SmsProviderId.HasValue) return false;

            var provider = await _db.SmsProviders.FindAsync(new object[] { settings.SmsProviderId.Value }, ct);
            if (provider == null || !provider.IsActive) return false;

            var template = lang == "en" ? settings.NewPasswordTemplateEn : settings.NewPasswordTemplate;
            var message = (template ?? "")
                .Replace("{USERNAME}", MaskUsername(username))
                .Replace("{PASSWORD}", password);
            if (!message.Contains(password)) message = $"{message} {password}".Trim();

            var result = await _smsService.SendCredentialsAsync(new SmsRequest
            {
                PhoneNumber = phone,
                IdentityId = username,
                MessageTemplate = message // literal text (already contains the new password)
            }.WithProvider(provider));

            return result.Success;
        }

        /// <summary>Digits-only exact comparison (tolerates spaces/leading zeros formatting).</summary>
        private static bool NationalIdMatches(string? adValue, string? input)
        {
            var a = DigitsOnly(adValue);
            var b = DigitsOnly(input);
            return a.Length > 0 && a == b;
        }

        private static string DigitsOnly(string? value) =>
            new((value ?? "").Where(char.IsDigit).ToArray());

        /// <summary>
        /// Reveals part of the username so the SMS recipient recognizes the account
        /// without exposing it fully, e.g. "ahmed.ali" → "ahm***li".
        /// </summary>
        private static string MaskUsername(string? username)
        {
            var u = (username ?? "").Trim();
            if (u.Length == 0) return "****";
            if (u.Length <= 4) return u[0] + new string('*', u.Length - 1);
            var start = u.Substring(0, 3);
            var end = u.Length >= 7 ? u[^2..] : "";
            return start + "***" + end;
        }

        private static string Sha256(string value) =>
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
