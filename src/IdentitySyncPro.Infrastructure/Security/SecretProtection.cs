using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IdentitySyncPro.Infrastructure.Security
{
    /// <summary>
    /// Central gateway for encrypting secrets at rest (AD / Oracle / SMS passwords) in the database.
    /// Backed by ASP.NET Core Data Protection.
    ///
    /// Design goals:
    /// - Transparent: applied via EF value converters, so read/write sites use plaintext unchanged.
    /// - Safe upgrade: stored values are tagged with a version prefix. Legacy plaintext rows (no prefix)
    ///   are returned as-is on read and transparently encrypted on the next save, so nothing breaks
    ///   during the transition.
    /// - Fail-safe: if the protector is unavailable or a value can't be decrypted (e.g. lost keys),
    ///   the raw value is returned rather than throwing — a sync must never crash over this.
    ///
    /// ⚠️ The Data Protection key ring MUST be persisted (see Program.cs). If the keys are lost,
    /// previously encrypted secrets cannot be recovered and must be re-entered.
    /// </summary>
    public static class SecretProtection
    {
        /// <summary>Marker identifying a value produced by this gateway. Bump the suffix if the scheme changes.</summary>
        public const string Prefix = "ENC1:";

        private static IDataProtector? _protector;

        /// <summary>Wire up the protector once at startup, before any DbContext is used.</summary>
        public static void Initialize(IDataProtectionProvider provider)
            => _protector = provider.CreateProtector("IdentitySyncPro.Secrets.v1");

        /// <summary>True once <see cref="Initialize"/> has run.</summary>
        public static bool IsInitialized => _protector != null;

        /// <summary>Returns true if the stored value is already encrypted by this gateway.</summary>
        public static bool IsProtected(string? stored)
            => !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>Encrypt a plaintext secret for storage. Empty/null and already-encrypted values pass through.</summary>
        public static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext ?? string.Empty;
            if (IsProtected(plaintext)) return plaintext;        // already encrypted — don't double-wrap
            if (_protector == null) return plaintext;            // not initialized — store as-is rather than lose data
            return Prefix + _protector.Protect(plaintext);
        }

        /// <summary>Decrypt a stored secret. Legacy plaintext (no prefix) is returned unchanged.</summary>
        public static string Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored ?? string.Empty;
            if (!IsProtected(stored)) return stored;             // legacy plaintext
            if (_protector == null) return stored;
            try { return _protector.Unprotect(stored.Substring(Prefix.Length)); }
            catch { return stored; }                             // corrupt / keys missing — return raw, don't crash
        }
    }

    /// <summary>
    /// EF Core value converter that encrypts a string property on the way to the database and
    /// decrypts it on the way back, using <see cref="SecretProtection"/>. Apply with
    /// <c>entity.Property(e => e.Secret).HasConversion(new EncryptedStringConverter())</c>.
    /// </summary>
    public sealed class EncryptedStringConverter : ValueConverter<string, string>
    {
        public EncryptedStringConverter()
            : base(v => SecretProtection.Protect(v), v => SecretProtection.Unprotect(v))
        {
        }
    }
}
