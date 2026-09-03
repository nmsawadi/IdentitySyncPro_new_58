using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Core.Security;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Multi-factor authentication (TOTP) for privileged console accounts.
    ///
    /// Enrollment is never accepted on trust: the user must produce a working code before the
    /// secret is stored. Saving it first and hoping the authenticator was configured correctly
    /// is how an administrator ends up locked out by a mis-scanned QR code.
    /// </summary>
    public class MfaService
    {
        public const int RecoveryCodeCount = 10;

        private readonly AppDbContext _db;
        private readonly ILogger<MfaService> _logger;

        public MfaService(AppDbContext db, ILogger<MfaService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>Reads the policy row, creating the default (disabled) one if absent.</summary>
        public async Task<MfaSettings> GetSettingsAsync(CancellationToken ct = default)
        {
            var settings = await _db.MfaSettings.FirstOrDefaultAsync(ct);
            if (settings != null) return settings;

            settings = new MfaSettings();
            _db.MfaSettings.Add(settings);
            await _db.SaveChangesAsync(ct);
            return settings;
        }

        /// <summary>What the sign-in flow must do next for this user.</summary>
        public enum MfaRequirement
        {
            /// <summary>Not in scope, or disabled — sign in normally.</summary>
            NotRequired,
            /// <summary>In scope and enrolled — ask for a code.</summary>
            Challenge,
            /// <summary>In scope, not enrolled, enrollment enforced — walk through setup.</summary>
            MustEnroll
        }

        public async Task<MfaRequirement> GetRequirementAsync(AppUser user, CancellationToken ct = default)
        {
            var settings = await GetSettingsAsync(ct);
            if (!settings.AppliesToRole(user.Role)) return MfaRequirement.NotRequired;

            if (user.IsMfaEnrolled) return MfaRequirement.Challenge;

            return settings.EnforceEnrollment
                ? MfaRequirement.MustEnroll
                : MfaRequirement.NotRequired;
        }

        /// <summary>
        /// Creates a candidate secret. It is intentionally NOT persisted here — it is carried
        /// through the setup screen and only written once a code proves the app has it.
        /// </summary>
        public static (string Secret, string Uri) BeginEnrollment(string issuer, string account)
        {
            var secret = TotpGenerator.GenerateSecret();
            return (secret, TotpGenerator.BuildUri(issuer, account, secret));
        }

        /// <summary>
        /// Verifies a code against a candidate secret and, on success, enrolls the user and
        /// returns freshly generated recovery codes (shown once — only hashes are kept).
        /// </summary>
        public async Task<(bool Success, IReadOnlyList<string> RecoveryCodes)> CompleteEnrollmentAsync(
            int userId, string candidateSecret, string code, CancellationToken ct = default)
        {
            var user = await _db.AppUsers.FindAsync(new object[] { userId }, ct);
            if (user == null) return (false, Array.Empty<string>());

            if (!TotpGenerator.VerifyCode(candidateSecret, code, DateTime.UtcNow, null, out var step))
                return (false, Array.Empty<string>());

            var plain = GenerateRecoveryCodes();

            user.MfaSecret = candidateSecret;
            user.MfaEnabledUtc = DateTime.UtcNow;
            user.MfaLastUsedTimeStep = step;
            user.MfaRecoveryCodes = string.Join('\n', plain.Select(PasswordHasher.Hash));
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("MFA enrolled for console user {Username}", user.Username);
            return (true, plain);
        }

        public enum VerifyResult { Failed, Totp, RecoveryCode }

        /// <summary>
        /// Verifies a challenge response: a TOTP code, or one of the single-use recovery codes.
        /// A spent recovery code is removed, and a spent time step is recorded, so neither can
        /// be replayed.
        /// </summary>
        public async Task<VerifyResult> VerifyAsync(int userId, string code, CancellationToken ct = default)
        {
            var user = await _db.AppUsers.FindAsync(new object[] { userId }, ct);
            if (user == null || !user.IsMfaEnrolled) return VerifyResult.Failed;

            if (TotpGenerator.VerifyCode(user.MfaSecret, code, DateTime.UtcNow, user.MfaLastUsedTimeStep, out var step))
            {
                user.MfaLastUsedTimeStep = step;
                await _db.SaveChangesAsync(ct);
                return VerifyResult.Totp;
            }

            // Recovery codes are checked second so a normal code never burns one by accident.
            var hashes = (user.MfaRecoveryCodes ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var normalized = (code ?? string.Empty).Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
            if (normalized.Length == 0) return VerifyResult.Failed;

            var match = hashes.FirstOrDefault(h => PasswordHasher.Verify(normalized, h));
            if (match == null) return VerifyResult.Failed;

            hashes.Remove(match); // single use
            user.MfaRecoveryCodes = string.Join('\n', hashes);
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "MFA recovery code used by console user {Username} — {Remaining} remaining",
                user.Username, hashes.Count);
            return VerifyResult.RecoveryCode;
        }

        /// <summary>
        /// Clears a user's enrollment so they can register a new device. The operational escape
        /// hatch for a lost or replaced phone.
        /// </summary>
        public async Task<bool> ResetAsync(int userId, CancellationToken ct = default)
        {
            var user = await _db.AppUsers.FindAsync(new object[] { userId }, ct);
            if (user == null) return false;

            user.MfaSecret = null;
            user.MfaEnabledUtc = null;
            user.MfaLastUsedTimeStep = null;
            user.MfaRecoveryCodes = null;
            await _db.SaveChangesAsync(ct);

            _logger.LogWarning("MFA reset for console user {Username}", user.Username);
            return true;
        }

        public static int CountRemainingRecoveryCodes(AppUser user) =>
            (user.MfaRecoveryCodes ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        /// <summary>
        /// Codes are drawn from an unambiguous alphabet: no O/0, I/1 or similar, because these
        /// get written on paper and typed back under pressure.
        /// </summary>
        private static List<string> GenerateRecoveryCodes()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var codes = new List<string>(RecoveryCodeCount);

            for (var i = 0; i < RecoveryCodeCount; i++)
            {
                var chars = new char[10];
                for (var c = 0; c < chars.Length; c++)
                    chars[c] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
                codes.Add(new string(chars));
            }
            return codes;
        }
    }
}
