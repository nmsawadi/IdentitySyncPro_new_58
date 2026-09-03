using System.Data;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Identity;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace IdentitySyncPro.Infrastructure.Connectors
{
    /// <summary>
    /// Oracle connector for reading identity data from the configured source view.
    /// Fully dynamic schema: rows are read with SELECT * and every column flows
    /// into the mapping engine by its real name — nothing is hardcoded.
    /// Only the key column (numeric identifier) and status column are declared
    /// per tenant in the connection settings.
    /// </summary>
    public class OracleConnector : ISourceConnector
    {
        private readonly OracleConnectionSettings _settings;
        private readonly ILogger<OracleConnector> _logger;

        public string Name => "Oracle Source Database";
        public string Type => "Source";

        public OracleConnector(OracleConnectionSettings settings, ILogger<OracleConnector> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync(ct);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM DUAL";
                await cmd.ExecuteScalarAsync(ct);
                _logger.LogInformation("Oracle connection test successful");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Oracle connection test failed");
                return false;
            }
        }

        public async Task<string> GetConnectionInfoAsync(CancellationToken ct = default)
        {
            try
            {
                using var connection = CreateConnection();
                await connection.OpenAsync(ct);
                return $"Connected to Oracle: {connection.ServerVersion}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        public async Task<int> GetTotalCountAsync(CancellationToken ct = default)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {_settings.ViewName}";
            cmd.CommandTimeout = _settings.CommandTimeout;
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }

        /// <summary>
        /// Get all identifiers from the source (optimized — SELECT only the key column).
        /// </summary>
        public async Task<IEnumerable<int>> ReadAllIdsAsync(CancellationToken ct = default)
        {
            _logger.LogInformation("Reading all identifiers from Oracle ({KeyColumn})...", _settings.KeyColumn);
            var ids = new List<int>();

            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {_settings.KeyColumn} FROM {_settings.ViewName} ORDER BY {_settings.KeyColumn}";
            cmd.CommandTimeout = 600; // 10 minutes

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(Convert.ToInt32(reader.GetValue(0)));
            }

            _logger.LogInformation("Retrieved {Count} identifiers from Oracle", ids.Count);
            return ids;
        }

        /// <summary>
        /// Read a batch of records by specific identifiers.
        ///
        /// ⚠️ Oracle has a hard limit of 1000 elements per IN clause.
        /// This method splits IDs into chunks of 900 to stay safely under the limit.
        /// </summary>
        public async Task<IEnumerable<SourceRecord>> ReadBatchAsync(int[] ids, CancellationToken ct = default)
        {
            if (ids == null || ids.Length == 0) return Enumerable.Empty<SourceRecord>();

            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            // ✅ Split into chunks of 900 (Oracle IN clause limit = 1000)
            const int chunkSize = 900;
            var records = new List<SourceRecord>();

            for (int chunk = 0; chunk < ids.Length; chunk += chunkSize)
            {
                var chunkIds = ids.Skip(chunk).Take(chunkSize).ToArray();
                var idList = string.Join(",", chunkIds);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT * FROM {_settings.ViewName} WHERE {_settings.KeyColumn} IN ({idList})";
                cmd.CommandTimeout = _settings.CommandTimeout;

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    records.Add(MapRecord(reader));
                }
            }

            _logger.LogInformation("Retrieved {Count} records for batch of {BatchSize} IDs", records.Count, ids.Length);
            return records;
        }

        /// <summary>
        /// Read all records from the source view.
        /// </summary>
        public async Task<IEnumerable<SourceRecord>> ReadAllAsync(CancellationToken ct = default)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {_settings.ViewName} ORDER BY {_settings.KeyColumn}";
            cmd.CommandTimeout = _settings.CommandTimeout;

            var records = new List<SourceRecord>();

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                records.Add(MapRecord(reader));
            }

            _logger.LogInformation("Retrieved {Count} records from Oracle", records.Count);
            return records;
        }

        /// <summary>
        /// Column names exposed by the configured view (for the mapping UI).
        /// </summary>
        public async Task<List<string>> GetColumnNamesAsync(CancellationToken ct = default)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {_settings.ViewName} WHERE 1=0";
            cmd.CommandTimeout = _settings.CommandTimeout;

            using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);
            var columns = new List<string>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            return columns;
        }

        // === Private helpers ===

        private OracleConnection CreateConnection()
        {
            // Use the builder so values are correctly quoted/escaped — a password containing ';' or '"'
            // would otherwise corrupt a hand-built connection string.
            var builder = new OracleConnectionStringBuilder
            {
                DataSource = _settings.DataSource,
                UserID = _settings.UserId,
                Password = _settings.Password
            };
            return new OracleConnection(builder.ConnectionString);
        }

        /// <summary>
        /// Maps a data reader row to a dynamic SourceRecord — all columns by their real names.
        /// </summary>
        private SourceRecord MapRecord(OracleDataReader reader)
        {
            var record = new SourceRecord();

            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);

                // Normalize strings: trim, empty → null (matches legacy behavior)
                if (value is string s)
                {
                    s = s.Trim();
                    value = string.IsNullOrEmpty(s) ? null : s;
                }

                record.Values[name] = value;
            }

            // Extract the structurally required values using the configured column names
            if (!record.Values.TryGetValue(_settings.KeyColumn, out var keyVal) || keyVal == null)
            {
                throw new InvalidOperationException(
                    $"Key column '{_settings.KeyColumn}' was not found (or is null) in view '{_settings.ViewName}'. " +
                    $"Available columns: {string.Join(", ", record.Values.Keys)}");
            }
            record.Key = Convert.ToInt32(keyVal);

            // Note the asymmetry this used to have with the key column above: a missing key throws
            // and names the available columns, while a missing status column left StatusCode at its
            // default 0 in silence. That silence is what kills every lifecycle rule written against
            // STATUS_CODE — the rules never match and nothing says why. Reported once per run
            // (six-figure row counts make per-row logging useless).
            if (string.IsNullOrWhiteSpace(_settings.StatusColumn))
            {
                WarnAboutStatusColumnOnce(
                    "No status column is configured for this tenant, so StatusCode is 0 for every identity. " +
                    "Lifecycle rules conditioned on STATUS_CODE will never match. " +
                    "Set the tenant's status column in Settings → Source.");
            }
            else if (!record.Values.TryGetValue(_settings.StatusColumn, out var statusVal) || statusVal == null)
            {
                WarnAboutStatusColumnOnce(
                    $"Status column '{_settings.StatusColumn}' was not found (or is null) in '{_settings.ViewName}', " +
                    $"so StatusCode is 0 for every identity and lifecycle rules on STATUS_CODE will never match. " +
                    $"Available columns: {string.Join(", ", record.Values.Keys)}");
            }
            else
            {
                try { record.StatusCode = Convert.ToInt32(statusVal); }
                catch
                {
                    record.StatusCode = 0;
                    WarnAboutStatusColumnOnce(
                        $"Status column '{_settings.StatusColumn}' holds a value that is not a number " +
                        $"(\"{statusVal}\"), so StatusCode is 0. Rules on STATUS_CODE will not match; " +
                        $"condition on the source column name directly instead.");
                }
            }

            record.StatusDesc = record.GetString(_settings.StatusDescColumn);

            return record;
        }

        private int _statusColumnWarnings;

        /// <summary>
        /// The status-column fault is identical for every row of the run, so it is stated once.
        /// Interlocked because rows are mapped concurrently on large batches.
        /// </summary>
        private void WarnAboutStatusColumnOnce(string message)
        {
            if (Interlocked.Exchange(ref _statusColumnWarnings, 1) == 0)
                _logger.LogError("OracleConnector: {Problem}", message);
        }
    }
}
