using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Infrastructure.Connectors;
using System.DirectoryServices.Protocols;
using System.Net;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Settings;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Console authentication: local users (PBKDF2) or per-user Active Directory
    /// bind, with lockout after repeated failures and full audit logging.
    /// </summary>
    public class AuthService
    {
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

        private readonly AppDbContext _db;
        private readonly ILogger<AuthService> _logger;
        private readonly PasswordPolicy _passwordPolicy;

        public AuthService(AppDbContext db, ILogger<AuthService> logger, PasswordPolicy? passwordPolicy = null)
        {
            _db = db;
            _logger = logger;
            _passwordPolicy = passwordPolicy ?? new PasswordPolicy();
        }

        public enum AuthResult { Success, InvalidCredentials, LockedOut, Inactive }

        /// <summary>
        /// Validates credentials. Never reveals whether the account exists —
        /// callers should show one generic message for anything but Success/LockedOut.
        /// </summary>
        public async Task<(AuthResult Result, AppUser? User)> AuthenticateAsync(string username, string password, CancellationToken ct = default)
        {
            username = (username ?? "").Trim();
            if (username.Length == 0 || string.IsNullOrEmpty(password))
                return (AuthResult.InvalidCredentials, null);

            var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
            if (user == null)
            {
                // Equalize timing with a real hash verification
                PasswordHasher.Verify(password, PasswordHasher.Hash("timing-equalizer"));
                return (AuthResult.InvalidCredentials, null);
            }

            if (!user.IsActive)
                return (AuthResult.Inactive, null);

            if (user.LockoutUntilUtc.HasValue && user.LockoutUntilUtc.Value > DateTime.UtcNow)
                return (AuthResult.LockedOut, null);

            var ok = user.AuthType == AppUserAuthTypes.ActiveDirectory
                ? TryActiveDirectoryBind(username, password)
                : PasswordHasher.Verify(password, user.PasswordHash);

            if (!ok)
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= MaxFailedAttempts)
                {
                    user.LockoutUntilUtc = DateTime.UtcNow.Add(LockoutDuration);
                    user.FailedLoginAttempts = 0;
                    _logger.LogWarning("Account {Username} locked out after repeated failed logins", username);
                }
                await _db.SaveChangesAsync(ct);
                return (AuthResult.InvalidCredentials, null);
            }

            user.FailedLoginAttempts = 0;
            user.LockoutUntilUtc = null;
            user.LastLoginUtc = DateTime.UtcNow;

            // Maximum password age. The credentials were correct, so this is NOT a rejection —
            // raising the existing must-change flag routes the user through the change-password
            // screen they already know. Inventing a separate "expired" outcome would mean a second
            // path to keep working, and the failure mode there is being locked out of a console
            // whose own password-reset screen is behind the login.
            if (_passwordPolicy.IsExpired(
                    user.PasswordChangedUtc,
                    user.AuthType == AppUserAuthTypes.Local,
                    DateTime.UtcNow)
                && !user.MustChangePassword)
            {
                user.MustChangePassword = true;
                _logger.LogInformation(
                    "Password for console user {Username} exceeded the maximum age of {Days} days — a change is now required",
                    user.Username, _passwordPolicy.MaxAgeDays);
            }

            await _db.SaveChangesAsync(ct);

            return (AuthResult.Success, user);
        }

        /// <summary>
        /// Changes a LOCAL user's password after verifying the current one.
        /// Enforces the minimum password policy.
        /// </summary>
        public async Task<(bool Success, string? Error)> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
        {
            var user = await _db.AppUsers.FindAsync(new object[] { userId }, ct);
            if (user == null || !user.IsActive) return (false, "invalid_user");
            if (user.AuthType != AppUserAuthTypes.Local) return (false, "ad_user");

            if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
                return (false, "wrong_current");

            var policyError = ValidatePasswordPolicy(newPassword);
            if (policyError != null) return (false, policyError);

            // Re-setting the same password would reset the age clock and satisfy the expiry
            // prompt without changing anything — the one-line defeat of the whole policy.
            if (PasswordHasher.Verify(newPassword, user.PasswordHash))
                return (false, "same_as_current");

            user.PasswordHash = PasswordHasher.Hash(newPassword);
            user.MustChangePassword = false;
            user.PasswordChangedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Password changed for console user {Username}", user.Username);
            return (true, null);
        }

        /// <summary>Minimum policy: 10+ chars, letters and digits. Returns error code or null.</summary>
        public static string? ValidatePasswordPolicy(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 10) return "policy_length";
            if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit)) return "policy_complexity";
            return null;
        }

        /// <summary>
        /// Proves a person holds a directory account, without requiring a console account for them.
        ///
        /// The employee portal needs this: the people it serves are the 118,000 identities the
        /// system provisions, and none of them has a row in <c>AppUsers</c>. Console sign-in starts
        /// by looking one up, so it cannot answer this question at all.
        ///
        /// It deliberately does NOT consult <c>AppUsers</c>, and grants nothing on its own — the
        /// caller decides what a proven directory identity is allowed to do. That separation is why
        /// the portal can issue a principal with no role claim: this method never had a role to
        /// give it.
        ///
        /// The same <c>AuthDomains</c> list console AD sign-in uses, for the same reason it exists:
        /// who may sign in stays a deliberate setting rather than a side effect of which tenants
        /// happen to be active.
        /// </summary>
        public Task<bool> AuthenticateDirectoryOnlyAsync(string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return Task.FromResult(false);

            // An empty password is an anonymous LDAP bind, which succeeds and would admit anyone.
            // TryActiveDirectoryBind refuses it too; repeated here because this entry point is
            // reachable from an anonymous page and must not depend on that.
            if (string.IsNullOrWhiteSpace(password)) return Task.FromResult(false);

            return Task.FromResult(TryActiveDirectoryBind(username.Trim(), password));
        }

        /// <summary>
        /// Attempts an LDAP bind with the USER's own credentials against the domains configured for
        /// console sign-in. The first successful bind wins, so a user living in a second domain
        /// still signs in. An empty password is rejected explicitly — LDAP treats it as an
        /// anonymous bind, which succeeds and would let anyone in.
        /// </summary>
        private bool TryActiveDirectoryBind(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(password)) return false;

            var candidates = GetAdLoginCandidates();
            if (candidates.Count == 0)
            {
                _logger.LogWarning(
                    "AD sign-in attempted for '{Username}' but no sign-in domain is configured. " +
                    "Add one under Users → sign-in domains — otherwise AD users can never sign in.",
                    username);
                return false;
            }

            foreach (var (label, opts) in candidates)
            {
                try
                {
                    // Same server/port/channel as the configured connection, but bind as the USER.
                    using var connection = LdapConnectionFactory.Create(new LdapConnectionOptions
                    {
                        Server = opts.Server,
                        Port = opts.Port,
                        SecurityMode = opts.SecurityMode,
                        AllowUntrustedCertificate = opts.AllowUntrustedCertificate,
                        Username = username,
                        Password = password
                    });
                    connection.Bind();
                    _logger.LogInformation("AD sign-in succeeded for '{Username}' via {Source}", username, label);
                    return true;
                }
                catch (LdapException ex)
                {
                    // Wrong credentials for THIS domain — try the next configured one.
                    _logger.LogDebug("AD bind failed for '{Username}' via {Source}: {Error}", username, label, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AD bind error for '{Username}' via {Source}", username, label);
                }
            }

            _logger.LogWarning("AD sign-in failed for '{Username}' against all {Count} configured AD connection(s)",
                username, candidates.Count);
            return false;
        }

        /// <summary>
        /// The domains console sign-in binds against, in the administrator's chosen order.
        ///
        /// This reads <c>AuthDomains</c> and nothing else. It used to fall back to whichever AD
        /// connection existed elsewhere — active sync tenants, then SSPR domains — which made who
        /// can sign in a side effect of unrelated settings: deactivating a tenant silently changed
        /// it. Existing installations had their working connections copied into this table once on
        /// upgrade (see <c>AuthDomainSeeder</c>), so nobody lost access in the move.
        /// </summary>
        internal static List<(string Label, LdapConnectionOptions Opts)> BuildCandidates(IEnumerable<AuthDomain> domains) =>
            domains
                .Where(d => d.IsActive && !string.IsNullOrWhiteSpace(d.AdServer))
                .OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
                .Select(d => ($"domain '{d.Name}'", d.ToLdapOptions()))
                .ToList();

        private List<(string Label, LdapConnectionOptions Opts)> GetAdLoginCandidates() =>
            BuildCandidates(_db.AuthDomains.AsNoTracking().ToList());
    }
}
