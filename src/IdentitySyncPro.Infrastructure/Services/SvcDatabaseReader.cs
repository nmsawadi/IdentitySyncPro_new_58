using System.Data;
using System.Data.Common;
using IdentitySyncPro.Core.Helpers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Generic database reader that supports multiple providers (SQL Server, Oracle).
    /// Returns data as List of Dictionary (column name → value) for maximum flexibility.
    /// Completely independent from the IAM module.
    /// </summary>
    public class SvcDatabaseReader
    {
        private readonly ILogger<SvcDatabaseReader> _logger;

        public SvcDatabaseReader(ILogger<SvcDatabaseReader> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Read all rows from a view/table and return as generic dictionaries.
        /// </summary>
        public async Task<List<Dictionary<string, string>>> ReadAllAsync(
            string provider, string connectionString, string tableOrView, CancellationToken ct = default)
        {
            var results = new List<Dictionary<string, string>>();

            try
            {
                using var connection = CreateConnection(provider, connectionString);
                await connection.OpenAsync(ct);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT * FROM {SanitizeIdentifier(tableOrView)}";
                cmd.CommandTimeout = 600;

                using var reader = await cmd.ExecuteReaderAsync(ct);
                var columnNames = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columnNames.Add(reader.GetName(i));
                }

                while (await reader.ReadAsync(ct))
                {
                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var col in columnNames)
                    {
                        var ordinal = reader.GetOrdinal(col);
                        row[col] = reader.IsDBNull(ordinal) ? "" : reader.GetValue(ordinal)?.ToString()?.Trim() ?? "";
                    }
                    results.Add(row);
                }

                _logger.LogInformation("SvcDatabaseReader: Read {Count} rows from {Table} ({Provider})",
                    results.Count, tableOrView, provider);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SvcDatabaseReader: Failed to read from {Table} ({Provider})",
                    tableOrView, provider);
                throw;
            }

            return results;
        }

        /// <summary>
        /// Get column names from a view/table (for mapping UI).
        /// </summary>
        public async Task<List<string>> GetColumnsAsync(
            string provider, string connectionString, string tableOrView, CancellationToken ct = default)
        {
            var columns = new List<string>();

            try
            {
                using var connection = CreateConnection(provider, connectionString);
                await connection.OpenAsync(ct);

                using var cmd = connection.CreateCommand();
                // Read 0 rows, just get schema
                if (provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase))
                {
                    cmd.CommandText = $"SELECT * FROM {SanitizeIdentifier(tableOrView)} WHERE ROWNUM = 0";
                }
                else
                {
                    cmd.CommandText = $"SELECT TOP 0 * FROM {SanitizeIdentifier(tableOrView)}";
                }
                cmd.CommandTimeout = 60;

                using var reader = await cmd.ExecuteReaderAsync(ct);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(reader.GetName(i));
                }

                _logger.LogInformation("SvcDatabaseReader: Found {Count} columns in {Table}", columns.Count, tableOrView);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SvcDatabaseReader: Failed to get columns from {Table}", tableOrView);
                throw;
            }

            return columns;
        }

        /// <summary>
        /// Test connection to a database.
        /// </summary>
        public async Task<(bool Success, string Message)> TestConnectionAsync(
            string provider, string connectionString, CancellationToken ct = default)
        {
            try
            {
                using var connection = CreateConnection(provider, connectionString);
                await connection.OpenAsync(ct);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase)
                    ? "SELECT 1 FROM DUAL"
                    : "SELECT 1";
                cmd.CommandTimeout = 15;

                await cmd.ExecuteScalarAsync(ct);

                var info = $"Connected successfully to {provider}";
                if (connection is SqlConnection sqlConn)
                    info = $"Connected to SQL Server: {sqlConn.ServerVersion}";

                return (true, info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SvcDatabaseReader: Connection test failed for {Provider}", provider);
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Build a connection string from individual parameters.
        /// </summary>
        public static string BuildConnectionString(string provider, string host, int port,
            string? database, string? username, string? password, bool integratedSecurity = false)
        {
            if (provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase))
            {
                var dataSource = $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={database})))";
                return $"Data Source={dataSource};User Id={username};Password={password};";
            }
            else // SqlServer
            {
                // For named instances (e.g., PCDEV\SQLEXPRESS), don't append port
                // SQL Browser service handles port resolution for named instances
                var serverPart = host;
                if (!host.Contains('\\') && port != 1433)
                {
                    serverPart = $"{host},{port}";
                }

                if (integratedSecurity)
                {
                    return $"Server={serverPart};Database={database};Integrated Security=True;TrustServerCertificate=True;MultipleActiveResultSets=true;Connection Timeout=15";
                }
                return $"Server={serverPart};Database={database};User Id={username};Password={password};TrustServerCertificate=True;MultipleActiveResultSets=true;Connection Timeout=15";
            }
        }

        private DbConnection CreateConnection(string provider, string connectionString)
        {
            if (provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase))
            {
                return new OracleConnection(connectionString);
            }
            return new SqlConnection(connectionString);
        }

        /// <summary>
        /// Validates a table or view name before it is placed into SQL text.
        ///
        /// This replaced a blacklist that stripped ";", "--", "/*" and "*/". That approach cannot
        /// work: the payload that matters needs none of those characters.
        ///
        ///     V_STUDENTS WHERE 1=0 UNION SELECT username, password FROM dba_users
        ///
        /// passes through untouched. And the reach is wider than it looks — ServicesController is
        /// open to AdminOrOperator, so an Operator can set a service's source table and run it.
        ///
        /// A whitelist is the only sound form here: an object name cannot be a parameter, so the
        /// name must be proven to be nothing but a name. Throwing rather than cleaning is
        /// deliberate — a helper that silently repairs its input teaches callers not to check.
        /// </summary>
        private static string SanitizeIdentifier(string identifier)
        {
            if (!SqlIdentifierGuard.IsValidObjectName(identifier))
                throw new ArgumentException(
                    $"Source table or view '{identifier}' is not a valid object name. " +
                    "Expected letters, digits and underscores, optionally schema-qualified.",
                    nameof(identifier));

            return identifier;
        }
    }
}
