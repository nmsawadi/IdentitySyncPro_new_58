using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IdentitySyncPro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AuthType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    FailedLoginAttempts = table.Column<int>(type: "int", nullable: false),
                    LockoutUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastLoginUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OldValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValues = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PerformedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ConnectorClass = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ConnectionSettings = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastConnectionTest = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeadLetterEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdentityId = table.Column<int>(type: "int", nullable: false),
                    OperationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    FailedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReplayedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsReplayed = table.Column<bool>(type: "bit", nullable: false),
                    ReplayResult = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetterEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailProviders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SmtpHost = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SmtpPort = table.Column<int>(type: "int", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Password = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    FromEmail = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EnableSsl = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IdentityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LifecycleState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PreviousState = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StateChangedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceStatusCode = table.Column<int>(type: "int", nullable: false),
                    SourceStatusDesc = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceSystemsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProvisionedTargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ADDistinguishedName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ADObjectGuid = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ADAccountEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ADCurrentOU = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CurrentHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PreviousHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NeedsRuleEval = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastImportDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastExportDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PasswordResetRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestGuid = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    SsprDomainId = table.Column<int>(type: "int", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PhoneNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OtpHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    IsUsed = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ClientIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordResetRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuarantinedIdentities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdentityId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FailureCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FailedOperation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    QuarantinedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsResolved = table.Column<bool>(type: "bit", nullable: false),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuarantinedIdentities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SmsProviders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApiUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ApiUsername = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApiPassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    SenderName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    HttpMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    BodyFormat = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RequestTemplate = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    HeadersJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SuccessBodyContains = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SmsSendLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IdentityId = table.Column<int>(type: "int", nullable: false),
                    Account = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GatewayResponse = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SentMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SyncRunId = table.Column<int>(type: "int", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsSendLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SsprDomains",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AdServer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AdPort = table.Column<int>(type: "int", nullable: false),
                    AdUseSsl = table.Column<bool>(type: "bit", nullable: false),
                    AdSecurityMode = table.Column<int>(type: "int", nullable: false),
                    AdSecurityModeSet = table.Column<bool>(type: "bit", nullable: false),
                    AdAllowUntrustedCertificate = table.Column<bool>(type: "bit", nullable: false),
                    AdUsername = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AdPassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    AdBaseDN = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    NationalIdAttribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    MobileAttribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ExcludedGroups = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SsprDomains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SsprIpBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    FirstFailureUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastFailureUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BlockedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsername = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SsprIpBlocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SsprSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SmsProviderId = table.Column<int>(type: "int", nullable: true),
                    OtpLifetimeSeconds = table.Column<int>(type: "int", nullable: false),
                    MaxVerifyAttempts = table.Column<int>(type: "int", nullable: false),
                    MaxRequestsPerIpPerHour = table.Column<int>(type: "int", nullable: false),
                    MaxRequestsPerUserPerHour = table.Column<int>(type: "int", nullable: false),
                    MaxFailedIdentityAttempts = table.Column<int>(type: "int", nullable: false),
                    IpBlockDurationHours = table.Column<int>(type: "int", nullable: false),
                    MaxResetsPerUserPer24h = table.Column<int>(type: "int", nullable: false),
                    MessageTemplate = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    MessageTemplateEn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    NewPasswordTemplate = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    NewPasswordTemplateEn = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ModifiedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SsprSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    RuleType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Condition = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Configuration = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CorrelationId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: true),
                    RunType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TotalProcessed = table.Column<int>(type: "int", nullable: false),
                    TotalCreated = table.Column<int>(type: "int", nullable: false),
                    TotalUpdated = table.Column<int>(type: "int", nullable: false),
                    TotalSkipped = table.Column<int>(type: "int", nullable: false),
                    TotalFailed = table.Column<int>(type: "int", nullable: false),
                    TotalNoChange = table.Column<int>(type: "int", nullable: false),
                    TotalAlreadyExisted = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BatchSize = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    IdentityId = table.Column<int>(type: "int", nullable: false),
                    CurrentHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedInAD = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastSyncDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastModified = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SourceProvider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceHost = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SourcePort = table.Column<int>(type: "int", nullable: false),
                    SourceDatabase = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourcePassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    SourceTableOrView = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceCommandTimeout = table.Column<int>(type: "int", nullable: false),
                    SourceKeyColumn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceStatusColumn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceStatusDescColumn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourcePhoneColumn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceDisplayNameColumn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ADServer = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ADPort = table.Column<int>(type: "int", nullable: false),
                    ADUseSsl = table.Column<bool>(type: "bit", nullable: false),
                    ADSecurityMode = table.Column<int>(type: "int", nullable: false),
                    ADSecurityModeSet = table.Column<bool>(type: "bit", nullable: false),
                    ADAllowUntrustedCertificate = table.Column<bool>(type: "bit", nullable: false),
                    ADUsername = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ADPassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ADBaseDN = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ADDefaultPassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    DatabaseProvider = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DbHost = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DbPort = table.Column<int>(type: "int", nullable: false),
                    DbName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DbUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DbPassword = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    DbIntegratedSecurity = table.Column<bool>(type: "bit", nullable: false),
                    DbTrustServerCertificate = table.Column<bool>(type: "bit", nullable: false),
                    DefaultBatchSize = table.Column<int>(type: "int", nullable: false),
                    FullSyncMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FullSyncTime = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    FullSyncDays = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FullSyncIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    FullSyncSchedule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DeltaSyncMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DeltaSyncTime = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DeltaSyncDays = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DeltaSyncIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    DeltaSyncSchedule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    HealthCheckMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    HealthCheckTime = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    HealthCheckIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    HealthCheckSchedule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EnableAutoSync = table.Column<bool>(type: "bit", nullable: false),
                    EnableLifecycleDuringSync = table.Column<bool>(type: "bit", nullable: false),
                    UseGlobalDefaultForEmptyFields = table.Column<bool>(type: "bit", nullable: false),
                    GlobalDefaultValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EnableSmsNotification = table.Column<bool>(type: "bit", nullable: false),
                    SmsProviderId = table.Column<int>(type: "int", nullable: true),
                    SmsApiUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SmsSenderName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SmsApiUsername = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SmsApiPassword = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SmsMessageTemplate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetaverseHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MetaverseEntryId = table.Column<int>(type: "int", nullable: false),
                    ChangeType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OldState = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewState = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedAttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TriggeredBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetaverseHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetaverseHistory_MetaverseEntries_MetaverseEntryId",
                        column: x => x.MetaverseEntryId,
                        principalTable: "MetaverseEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttributeMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncRuleId = table.Column<int>(type: "int", nullable: false),
                    SourceAttribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetAttribute = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TransformExpression = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    DefaultValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttributeMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttributeMappings_SyncRules_SyncRuleId",
                        column: x => x.SyncRuleId,
                        principalTable: "SyncRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncOperations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncRunId = table.Column<int>(type: "int", nullable: false),
                    IdentityId = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangedFields = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncOperations_SyncRuns_SyncRunId",
                        column: x => x.SyncRunId,
                        principalTable: "SyncRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConditionField = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConditionOperator = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConditionValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActionType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActionValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GracePeriodDays = table.Column<int>(type: "int", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LifecycleRules_TenantSettings_TenantId",
                        column: x => x.TenantId,
                        principalTable: "TenantSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRulesV2",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    RuleType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TargetSystem = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScopeFilter = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConditionJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRulesV2", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRulesV2_TenantSettings_TenantId",
                        column: x => x.TenantId,
                        principalTable: "TenantSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantAttributeMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    SourceColumn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetAttribute = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Transform = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    IsIdentifier = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Condition = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAttributeMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantAttributeMappings_TenantSettings_TenantId",
                        column: x => x.TenantId,
                        principalTable: "TenantSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantGroupRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    GroupDN = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    ConditionField = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConditionOperator = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ConditionValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantGroupRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantGroupRules_TenantSettings_TenantId",
                        column: x => x.TenantId,
                        principalTable: "TenantSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantOURules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    OUTemplate = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    ConditionField = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ConditionOperator = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ConditionValue = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ValueMappings = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantOURules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantOURules_TenantSettings_TenantId",
                        column: x => x.TenantId,
                        principalTable: "TenantSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleFlowMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncRuleV2Id = table.Column<int>(type: "int", nullable: false),
                    SourceAttribute = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TargetAttribute = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Transform = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    DefaultValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleFlowMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleFlowMappings_SyncRulesV2_SyncRuleV2Id",
                        column: x => x.SyncRuleV2Id,
                        principalTable: "SyncRulesV2",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuleVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncRuleV2Id = table.Column<int>(type: "int", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangeNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ChangedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuleVersions_SyncRulesV2_SyncRuleV2Id",
                        column: x => x.SyncRuleV2Id,
                        principalTable: "SyncRulesV2",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Id", "Key", "ModifiedDate", "Value" },
                values: new object[] { 1, "Language", new DateTime(2026, 7, 17, 17, 58, 56, 713, DateTimeKind.Utc).AddTicks(3213), "ar" });

            migrationBuilder.InsertData(
                table: "ConnectorConfigs",
                columns: new[] { "Id", "ConnectionSettings", "ConnectorClass", "CreatedDate", "Enabled", "LastConnectionTest", "LastError", "ModifiedDate", "Name", "Status", "Type" },
                values: new object[,]
                {
                    { 1, "{\"Host\":\"\",\"Port\":1521,\"ServiceName\":\"\",\"DataSource\":\"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=ORCL)))\",\"UserId\":\"source_view_user\",\"Password\":\"\",\"ViewName\":\"V_IDENTITY_DATA\",\"CommandTimeout\":300,\"KeyColumn\":\"IDENTITY_ID\",\"StatusColumn\":\"STATUSE_CODE\",\"StatusDescColumn\":\"STATUS_DESC\"}", "Oracle", new DateTime(2026, 7, 17, 17, 58, 56, 711, DateTimeKind.Utc).AddTicks(6945), true, null, null, new DateTime(2026, 7, 17, 17, 58, 56, 711, DateTimeKind.Utc).AddTicks(6948), "Source Database (Oracle)", 1, 0 },
                    { 2, "{\"Server\":\"dc.example.local\",\"Port\":389,\"UseSsl\":false,\"SecurityMode\":0,\"SecurityModeSet\":false,\"AllowUntrustedCertificate\":false,\"Username\":null,\"Password\":null,\"BaseDN\":\"DC=example,DC=local\",\"DefaultPassword\":\"ChangeMe@2026\"}", "ActiveDirectory", new DateTime(2026, 7, 17, 17, 58, 56, 711, DateTimeKind.Utc).AddTicks(7452), true, null, null, new DateTime(2026, 7, 17, 17, 58, 56, 711, DateTimeKind.Utc).AddTicks(7452), "Active Directory", 1, 1 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Key",
                table: "AppSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Username",
                table: "AppUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttributeMappings_SyncRuleId",
                table: "AttributeMappings",
                column: "SyncRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Category",
                table: "AuditEntries",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_CorrelationId",
                table: "AuditEntries",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Timestamp",
                table: "AuditEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEntries_IsReplayed",
                table: "DeadLetterEntries",
                column: "IsReplayed");

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleRules_TenantId",
                table: "LifecycleRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseEntries_LifecycleState",
                table: "MetaverseEntries",
                column: "LifecycleState");

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseEntries_TenantId_ExternalId",
                table: "MetaverseEntries",
                columns: new[] { "TenantId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetaverseHistory_MetaverseEntryId",
                table: "MetaverseHistory",
                column: "MetaverseEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetRequests_ClientIp_CreatedAtUtc",
                table: "PasswordResetRequests",
                columns: new[] { "ClientIp", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetRequests_RequestGuid",
                table: "PasswordResetRequests",
                column: "RequestGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetRequests_Username_CreatedAtUtc",
                table: "PasswordResetRequests",
                columns: new[] { "Username", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QuarantinedIdentities_IdentityId",
                table: "QuarantinedIdentities",
                column: "IdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_SmsSendLogs_Account",
                table: "SmsSendLogs",
                column: "Account");

            migrationBuilder.CreateIndex(
                name: "IX_SmsSendLogs_CreatedAt",
                table: "SmsSendLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SmsSendLogs_Status",
                table: "SmsSendLogs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SsprIpBlocks_ClientIp",
                table: "SsprIpBlocks",
                column: "ClientIp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncOperations_IdentityId",
                table: "SyncOperations",
                column: "IdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncOperations_SyncRunId",
                table: "SyncOperations",
                column: "SyncRunId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncOperations_Timestamp",
                table: "SyncOperations",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleFlowMappings_SyncRuleV2Id",
                table: "SyncRuleFlowMappings",
                column: "SyncRuleV2Id");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRulesV2_TenantId_RuleType",
                table: "SyncRulesV2",
                columns: new[] { "TenantId", "RuleType" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuleVersions_SyncRuleV2Id_VersionNumber",
                table: "SyncRuleVersions",
                columns: new[] { "SyncRuleV2Id", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_CorrelationId",
                table: "SyncRuns",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_TenantId_IdentityId",
                table: "SyncStates",
                columns: new[] { "TenantId", "IdentityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantAttributeMappings_TenantId",
                table: "TenantAttributeMappings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantGroupRules_TenantId",
                table: "TenantGroupRules",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantOURules_TenantId",
                table: "TenantOURules",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "AttributeMappings");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "ConnectorConfigs");

            migrationBuilder.DropTable(
                name: "DeadLetterEntries");

            migrationBuilder.DropTable(
                name: "EmailProviders");

            migrationBuilder.DropTable(
                name: "LifecycleRules");

            migrationBuilder.DropTable(
                name: "MetaverseHistory");

            migrationBuilder.DropTable(
                name: "PasswordResetRequests");

            migrationBuilder.DropTable(
                name: "QuarantinedIdentities");

            migrationBuilder.DropTable(
                name: "SmsProviders");

            migrationBuilder.DropTable(
                name: "SmsSendLogs");

            migrationBuilder.DropTable(
                name: "SsprDomains");

            migrationBuilder.DropTable(
                name: "SsprIpBlocks");

            migrationBuilder.DropTable(
                name: "SsprSettings");

            migrationBuilder.DropTable(
                name: "SyncOperations");

            migrationBuilder.DropTable(
                name: "SyncRuleFlowMappings");

            migrationBuilder.DropTable(
                name: "SyncRuleVersions");

            migrationBuilder.DropTable(
                name: "SyncStates");

            migrationBuilder.DropTable(
                name: "TenantAttributeMappings");

            migrationBuilder.DropTable(
                name: "TenantGroupRules");

            migrationBuilder.DropTable(
                name: "TenantOURules");

            migrationBuilder.DropTable(
                name: "SyncRules");

            migrationBuilder.DropTable(
                name: "MetaverseEntries");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "SyncRulesV2");

            migrationBuilder.DropTable(
                name: "TenantSettings");
        }
    }
}
