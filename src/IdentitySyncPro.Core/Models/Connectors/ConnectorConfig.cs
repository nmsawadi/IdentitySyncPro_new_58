using IdentitySyncPro.Core.Enums;

namespace IdentitySyncPro.Core.Models.Connectors
{
    /// <summary>
    /// Configuration for a data connector (Oracle source or AD target).
    /// </summary>
    public class ConnectorConfig
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ConnectorType Type { get; set; }
        public string ConnectorClass { get; set; } = string.Empty; // "Oracle" or "ActiveDirectory"
        public bool Enabled { get; set; } = true;

        // Connection settings stored as JSON
        public string ConnectionSettings { get; set; } = "{}";

        public ConnectorStatus Status { get; set; } = ConnectorStatus.Disconnected;
        public DateTime? LastConnectionTest { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Oracle-specific connection settings.
    /// Supports both direct DataSource (TNS string) and separate Host/Port/ServiceName properties.
    /// When Host is provided and DataSource is empty, DataSource is auto-built from Host/Port/ServiceName.
    /// </summary>
    public class OracleConnectionSettings
    {
        /// <summary>Oracle server hostname or IP address</summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>Oracle listener port (default: 1521)</summary>
        public int Port { get; set; } = 1521;

        /// <summary>Oracle Service Name (not SID)</summary>
        public string ServiceName { get; set; } = string.Empty;

        /// <summary>
        /// Full TNS-style data source string. 
        /// If empty, auto-built from Host/Port/ServiceName.
        /// </summary>
        private string _dataSource = string.Empty;
        public string DataSource
        {
            get => !string.IsNullOrEmpty(_dataSource)
                ? _dataSource
                : !string.IsNullOrEmpty(Host)
                    ? $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Host})(PORT={Port}))(CONNECT_DATA=(SERVICE_NAME={ServiceName})))"
                    : string.Empty;
            set => _dataSource = value;
        }

        public string UserId { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ViewName { get; set; } = string.Empty;
        public int CommandTimeout { get; set; } = 300;

        // === Dynamic source schema (columns are NOT hardcoded) ===
        // Only two columns are structurally required by the engine; everything
        // else flows through attribute mappings by its real column name.

        /// <summary>Column holding the numeric identifier of each row.</summary>
        public string KeyColumn { get; set; } = "IDENTITY_ID";

        /// <summary>
        /// Column holding the numeric lifecycle status code. Deliberately empty: the previous
        /// default was one institution's column name ("STATUSE_CODE", extra E included), which any
        /// other organisation inherited by leaving the field blank — matching no column, yielding
        /// StatusCode 0 for every identity, and killing every lifecycle rule on STATUS_CODE in
        /// silence. Unset is now reported rather than guessed.
        /// </summary>
        public string StatusColumn { get; set; } = string.Empty;

        /// <summary>Optional column holding a human-readable status description.</summary>
        public string? StatusDescColumn { get; set; } = "STATUS_DESC";
    }

    /// <summary>
    /// SQL Server source connection settings (dynamic schema, like Oracle).
    /// </summary>
    public class SqlServerConnectionSettings
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string ViewName { get; set; } = string.Empty;
        public int CommandTimeout { get; set; } = 300;

        /// <summary>Column holding the numeric identifier of each row.</summary>
        public string KeyColumn { get; set; } = "IDENTITY_ID";

        /// <summary>
        /// Column holding the numeric lifecycle status code. Deliberately empty: the previous
        /// default was one institution's column name ("STATUSE_CODE", extra E included), which any
        /// other organisation inherited by leaving the field blank — matching no column, yielding
        /// StatusCode 0 for every identity, and killing every lifecycle rule on STATUS_CODE in
        /// silence. Unset is now reported rather than guessed.
        /// </summary>
        public string StatusColumn { get; set; } = string.Empty;

        /// <summary>Optional column holding a human-readable status description.</summary>
        public string? StatusDescColumn { get; set; } = "STATUS_DESC";
    }

    /// <summary>
    /// Active Directory-specific connection settings.
    /// </summary>
    public class ADConnectionSettings
    {
        public string Server { get; set; } = string.Empty;
        public int Port { get; set; } = 389;

        /// <summary>
        /// Legacy switch kept so existing stored configs keep working. Only consulted when
        /// <see cref="SecurityMode"/> is <see cref="LdapSecurityMode.Auto"/> AND
        /// <see cref="SecurityModeSet"/> is false — see <see cref="ToLdapOptions"/>.
        /// </summary>
        public bool UseSsl { get; set; } = false;

        /// <summary>Explicit channel mode. Overrides <see cref="UseSsl"/> once set.</summary>
        public LdapSecurityMode SecurityMode { get; set; } = LdapSecurityMode.Auto;

        /// <summary>
        /// True once an admin has actually chosen a <see cref="SecurityMode"/>. Without this
        /// we can't tell "Auto by choice" from "old config that predates the field", and the
        /// two must behave differently to avoid silently changing a working deployment.
        /// </summary>
        public bool SecurityModeSet { get; set; } = false;

        /// <summary>Accept an internal-CA / self-signed LDAPS certificate. See <see cref="LdapConnectionOptions.AllowUntrustedCertificate"/>.</summary>
        public bool AllowUntrustedCertificate { get; set; } = false;

        public string? Username { get; set; }
        public string? Password { get; set; }
        public string BaseDN { get; set; } = string.Empty;
        public string DefaultPassword { get; set; } = "ChangeMe@2026";

        /// <summary>
        /// Project these settings onto the shared connection options. Configs saved before the
        /// mode existed fall back to the legacy boolean, so their behaviour is unchanged.
        /// </summary>
        public LdapConnectionOptions ToLdapOptions() => new()
        {
            Server = Server,
            Port = Port,
            Username = Username,
            Password = Password,
            SecurityMode = SecurityModeSet ? SecurityMode : LdapConnectionOptions.FromUseSsl(UseSsl),
            AllowUntrustedCertificate = AllowUntrustedCertificate
        };
    }
}
