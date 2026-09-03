using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Security
{
    /// <summary>
    /// One-time, idempotent upgrade for existing installations:
    ///  1. Widens the secret columns so they can hold ciphertext (encrypted text is longer than plaintext).
    ///  2. Encrypts any existing plaintext secret values in place.
    ///
    /// Safe to run on every startup: it only touches small config tables, skips tables/columns that
    /// don't exist, never shrinks a column, and skips values already tagged with the encryption prefix.
    /// Requires <see cref="SecretProtection.Initialize"/> to have run first.
    /// </summary>
    public static class SecretsMigrator
    {
        // table, primary-key column, secret columns
        private static readonly (string Table, string Key, string[] Columns)[] Targets =
        {
            ("TenantSettings",     "Id", new[] { "SourcePassword", "ADPassword", "ADDefaultPassword", "DbPassword", "SmsApiPassword" }),
            ("SmsProviders",       "Id", new[] { "ApiPassword" }),
            ("Svc_Services",       "Id", new[] { "SourcePassword", "ADPassword" }),
            ("Acct_CustomDomains", "Id", new[] { "Password" }),
        };

        public static async Task MigrateAsync(string connectionString, ILogger logger, CancellationToken ct = default)
        {
            if (!SecretProtection.IsInitialized)
            {
                logger.LogWarning("SecretsMigrator skipped — SecretProtection is not initialized");
                return;
            }

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);

            foreach (var (table, key, columns) in Targets)
            {
                if (!await TableExistsAsync(conn, table, ct)) continue;

                foreach (var column in columns)
                {
                    if (!await ColumnExistsAsync(conn, table, column, ct)) continue;

                    try
                    {
                        await WidenColumnAsync(conn, table, column, ct);
                        var encrypted = await EncryptPlaintextAsync(conn, table, key, column, ct);
                        if (encrypted > 0)
                            logger.LogInformation("SecretsMigrator: encrypted {Count} plaintext value(s) in {Table}.{Column}", encrypted, table, column);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "SecretsMigrator: failed to migrate {Table}.{Column} (continuing)", table, column);
                    }
                }
            }
        }

        private static async Task<bool> TableExistsAsync(SqlConnection conn, string table, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @t) THEN 1 ELSE 0 END";
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
        }

        private static async Task<bool> ColumnExistsAsync(SqlConnection conn, string table, string column, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c) THEN 1 ELSE 0 END";
            cmd.Parameters.AddWithValue("@t", table);
            cmd.Parameters.AddWithValue("@c", column);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
        }

        /// <summary>Widen to nvarchar(1024) only if currently smaller and not already nvarchar(max). Preserves NOT NULL.</summary>
        private static async Task WidenColumnAsync(SqlConnection conn, string table, string column, CancellationToken ct)
        {
            int maxLen;
            bool isNullable;
            await using (var info = conn.CreateCommand())
            {
                info.CommandText = @"SELECT CHARACTER_MAXIMUM_LENGTH, CASE WHEN IS_NULLABLE = 'YES' THEN 1 ELSE 0 END
                                     FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c";
                info.Parameters.AddWithValue("@t", table);
                info.Parameters.AddWithValue("@c", column);
                await using var reader = await info.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return;
                maxLen = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                isNullable = reader.GetInt32(1) == 1;
            }

            // -1 == nvarchar(max) already big enough; >= 1024 already wide enough
            if (maxLen == -1 || maxLen >= 1024) return;

            var nullClause = isNullable ? "NULL" : "NOT NULL";
            // Table/column names come from a fixed internal allow-list, not user input — safe to interpolate.
            await using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE [{table}] ALTER COLUMN [{column}] nvarchar(1024) {nullClause}";
            await alter.ExecuteNonQueryAsync(ct);
        }

        /// <summary>Encrypt every plaintext (non-prefixed, non-empty) value in the column.</summary>
        private static async Task<int> EncryptPlaintextAsync(SqlConnection conn, string table, string key, string column, CancellationToken ct)
        {
            var toEncrypt = new List<(object Id, string Value)>();

            await using (var select = conn.CreateCommand())
            {
                select.CommandText = $"SELECT [{key}], [{column}] FROM [{table}] " +
                                     $"WHERE [{column}] IS NOT NULL AND [{column}] <> '' AND [{column}] NOT LIKE @prefix";
                select.Parameters.AddWithValue("@prefix", SecretProtection.Prefix + "%");
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    toEncrypt.Add((reader.GetValue(0), reader.GetString(1)));
            }

            foreach (var (id, value) in toEncrypt)
            {
                await using var update = conn.CreateCommand();
                update.CommandText = $"UPDATE [{table}] SET [{column}] = @v WHERE [{key}] = @id";
                update.Parameters.AddWithValue("@v", SecretProtection.Protect(value));
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync(ct);
            }

            return toEncrypt.Count;
        }
    }
}
