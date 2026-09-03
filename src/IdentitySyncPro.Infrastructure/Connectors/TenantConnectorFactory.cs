using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Settings;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Connectors
{
    /// <summary>
    /// Builds per-tenant connectors from each tenant's own connection fields (stored in the
    /// system database). There is NO appsettings fallback: a tenant with no real source/AD
    /// connection configured fails with a clear error rather than silently using a shared default.
    /// </summary>
    public class TenantConnectorFactory : ITenantConnectorFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public TenantConnectorFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        public ISourceConnector CreateSourceConnector(TenantSettings tenant)
        {
            if (!HasOwnSourceConfig(tenant))
                throw new InvalidOperationException(
                    $"Tenant '{tenant.TenantName}' has no source database connection configured. " +
                    "Enter the source connection in Settings — there is no appsettings fallback.");

            var provider = string.IsNullOrWhiteSpace(tenant.SourceProvider) ? "Oracle" : tenant.SourceProvider;

            if (provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase))
            {
                return new OracleConnector(
                    SourceConnectorFactory.BuildOracleSettings(tenant),
                    _loggerFactory.CreateLogger<OracleConnector>());
            }

            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlServerConnector(
                    SourceConnectorFactory.BuildSqlServerSettings(tenant),
                    _loggerFactory.CreateLogger<SqlServerConnector>());
            }

            throw new InvalidOperationException(
                $"Tenant '{tenant.TenantName}' uses source provider '{provider}' — supported providers: Oracle, SqlServer.");
        }

        /// <summary>
        /// The target this tenant provisions into.
        ///
        /// Until this branch existed the method built an Active Directory connector and nothing
        /// else — so the system was single-target not because the architecture prevented a second
        /// one, but because nobody had opened the door. The source side had branched on
        /// <see cref="TenantSettings.SourceProvider"/> for a long time; this is the same shape.
        ///
        /// An unrecognised provider throws rather than falling back. A tenant configured for SCIM
        /// that quietly received an Active Directory connector would try to provision people into
        /// a domain that was never meant to hold them.
        /// </summary>
        public ITargetConnector CreateTargetConnector(TenantSettings tenant)
        {
            var provider = TargetProviders.Normalise(tenant.TargetProvider);

            if (provider == TargetProviders.Scim)
            {
                if (string.IsNullOrWhiteSpace(tenant.ScimBaseUrl))
                    throw new InvalidOperationException(
                        $"Tenant '{tenant.TenantName}' targets SCIM but has no service URL configured. " +
                        "Enter it in Settings — there is no fallback.");

                return new ScimConnector(
                    SourceConnectorFactory.BuildScimSettings(tenant),
                    _loggerFactory.CreateLogger<ScimConnector>());
            }

            if (provider == TargetProviders.ActiveDirectory)
            {
                if (!HasOwnAdConfig(tenant))
                    throw new InvalidOperationException(
                        $"Tenant '{tenant.TenantName}' has no Active Directory connection configured. " +
                        "Enter the AD connection in Settings — there is no appsettings fallback.");

                return new ActiveDirectoryConnector(
                    SourceConnectorFactory.BuildADSettings(tenant),
                    _loggerFactory.CreateLogger<ActiveDirectoryConnector>());
            }

            throw new InvalidOperationException(
                $"Tenant '{tenant.TenantName}' uses target provider '{tenant.TargetProvider}' — " +
                $"supported providers: {string.Join(", ", TargetProviders.All)}.");
        }

        /// <summary>True when the tenant carries a real (non-placeholder) source host.</summary>
        private static bool HasOwnSourceConfig(TenantSettings tenant) =>
            IsRealValue(tenant.SourceHost);

        /// <summary>True when the tenant carries a real (non-placeholder) AD server.</summary>
        private static bool HasOwnAdConfig(TenantSettings tenant) =>
            IsRealValue(tenant.ADServer);

        private static bool IsRealValue(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("example.local", StringComparison.OrdinalIgnoreCase);
    }
}
