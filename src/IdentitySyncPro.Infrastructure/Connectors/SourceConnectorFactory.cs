using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Settings;

namespace IdentitySyncPro.Infrastructure.Connectors
{
    /// <summary>
    /// Factory that builds OracleConnectionSettings from TenantSettings (generic source fields).
    /// Bridges the old connector pattern with the new multi-provider model.
    /// </summary>
    public static class SourceConnectorFactory
    {
        /// <summary>
        /// The view and key column are interpolated into every statement the connectors build —
        /// an object name cannot be a parameter, so ten call sites end up formatting them into SQL
        /// text. Checking them once here, where the settings become connector settings, is what
        /// makes all ten safe; validating at each call site would be ten chances to forget.
        ///
        /// A stored setting is not trusted input: it arrives from the settings form or from an
        /// uploaded settings file, and the value is then used on every sync.
        /// </summary>
        private static string RequireObjectName(string? value, string field, string tenant)
        {
            if (!SqlIdentifierGuard.IsValidObjectName(value))
                throw new InvalidOperationException(
                    $"Tenant '{tenant}': {field} '{value}' is not a valid database object name. " +
                    "Expected letters, digits and underscores, optionally schema-qualified. " +
                    "Correct it in Settings → Source before syncing.");

            return value!;
        }
        /// <summary>
        /// Build OracleConnectionSettings from a TenantSettings that has SourceProvider = "Oracle".
        /// </summary>
        public static OracleConnectionSettings BuildOracleSettings(TenantSettings tenant)
        {
            return new OracleConnectionSettings
            {
                DataSource = tenant.SourceConnectionString.Contains("DESCRIPTION")
                    ? ExtractDataSource(tenant)
                    : tenant.SourceConnectionString,
                UserId = tenant.SourceUsername ?? "",
                Password = tenant.SourcePassword ?? "",
                ViewName = RequireObjectName(tenant.SourceTableOrView ?? "V_IDENTITY_DATA",
                                             "source table/view", tenant.TenantName),
                CommandTimeout = tenant.SourceCommandTimeout,
                // Dynamic schema: which columns hold the key/status — per tenant,
                // with legacy defaults for existing installations.
                KeyColumn = RequireObjectName(
                    string.IsNullOrWhiteSpace(tenant.SourceKeyColumn) ? "IDENTITY_ID" : tenant.SourceKeyColumn,
                    "source key column", tenant.TenantName),
                // No default. "STATUSE_CODE" used to be hardcoded here — the column name of one
                // specific institution's view, extra E and all. Any other organisation that left
                // the setting blank got that name, found nothing, and had StatusCode silently
                // become 0 for every identity, which killed every lifecycle rule written against
                // STATUS_CODE without a word. An unset column is now reported, not guessed.
                StatusColumn = tenant.SourceStatusColumn?.Trim() ?? "",
                StatusDescColumn = string.IsNullOrWhiteSpace(tenant.SourceStatusDescColumn) ? "STATUS_DESC" : tenant.SourceStatusDescColumn
            };
        }

        /// <summary>
        /// Build SqlServerConnectionSettings from a TenantSettings with SourceProvider = "SqlServer".
        /// </summary>
        public static SqlServerConnectionSettings BuildSqlServerSettings(TenantSettings tenant)
        {
            return new SqlServerConnectionSettings
            {
                ConnectionString = tenant.SourceConnectionString,
                ViewName = RequireObjectName(tenant.SourceTableOrView, "source table/view", tenant.TenantName),
                CommandTimeout = tenant.SourceCommandTimeout,
                KeyColumn = RequireObjectName(
                    string.IsNullOrWhiteSpace(tenant.SourceKeyColumn) ? "IDENTITY_ID" : tenant.SourceKeyColumn,
                    "source key column", tenant.TenantName),
                // No default. "STATUSE_CODE" used to be hardcoded here — the column name of one
                // specific institution's view, extra E and all. Any other organisation that left
                // the setting blank got that name, found nothing, and had StatusCode silently
                // become 0 for every identity, which killed every lifecycle rule written against
                // STATUS_CODE without a word. An unset column is now reported, not guessed.
                StatusColumn = tenant.SourceStatusColumn?.Trim() ?? "",
                StatusDescColumn = string.IsNullOrWhiteSpace(tenant.SourceStatusDescColumn) ? "STATUS_DESC" : tenant.SourceStatusDescColumn
            };
        }

        /// <summary>
        /// Build ADConnectionSettings from a TenantSettings.
        /// </summary>
        public static ADConnectionSettings BuildADSettings(TenantSettings tenant)
        {
            return new ADConnectionSettings
            {
                Server = tenant.ADServer ?? "",
                Port = tenant.ADPort,
                UseSsl = tenant.ADUseSsl,
                // Carry the tenant's channel choice through; when it predates the field the
                // legacy UseSsl above still decides (SecurityModeSet = false).
                SecurityMode = tenant.ADSecurityMode,
                SecurityModeSet = tenant.ADSecurityModeSet,
                AllowUntrustedCertificate = tenant.ADAllowUntrustedCertificate,
                Username = tenant.ADUsername,
                Password = tenant.ADPassword,
                BaseDN = tenant.ADBaseDN ?? "",
                DefaultPassword = tenant.ADDefaultPassword ?? "ChangeMe@2026"
            };
        }

        /// <summary>
        /// The SCIM connection for a tenant.
        ///
        /// The timeout is clamped rather than trusted: a zero stored by an older row, or a value
        /// somebody typed as milliseconds, would otherwise become an unbounded or absurd wait — and
        /// an unbounded wait on a socket is what once stopped this system's whole background
        /// pipeline.
        /// </summary>
        public static ScimConnectionSettings BuildScimSettings(TenantSettings tenant) => new()
        {
            BaseUrl = tenant.ScimBaseUrl?.Trim() ?? "",
            BearerToken = tenant.ScimBearerToken,
            AllowUntrustedCertificate = tenant.ScimAllowUntrustedCertificate,
            TimeoutSeconds = tenant.ScimTimeoutSeconds <= 0 ? 30 : Math.Clamp(tenant.ScimTimeoutSeconds, 5, 300),
            DisplayName = tenant.TenantName
        };

        private static string ExtractDataSource(TenantSettings tenant)
        {
            return $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={tenant.SourceHost})(PORT={tenant.SourcePort}))(CONNECT_DATA=(SERVICE_NAME={tenant.SourceDatabase})))";
        }
    }
}
