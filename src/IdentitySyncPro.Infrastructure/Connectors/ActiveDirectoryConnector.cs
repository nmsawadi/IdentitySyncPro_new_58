using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Models.Identity;
using Microsoft.Extensions.Logging;
using SyncResultModel = IdentitySyncPro.Core.Interfaces.SyncResult;

namespace IdentitySyncPro.Infrastructure.Connectors
{
    /// <summary>
    /// Active Directory connector for creating/updating identity accounts.
    /// All attribute/OU/group decisions are driven by tenant configuration (MappingEngine).
    /// Uses System.DirectoryServices.Protocols for cross-platform LDAP support.
    ///
    /// Performance: supports shared LDAP connection for batch operations
    /// to avoid repeated Bind calls when processing large identity sets.
    /// </summary>
    public class ActiveDirectoryConnector : ITargetConnector
    {
        private readonly ADConnectionSettings _settings;
        private readonly ILogger<ActiveDirectoryConnector> _logger;
        private LdapConnection? _sharedConnection;
        private readonly object _connectionLock = new();

        public string Name => "Active Directory";
        public string Type => "Target";

        public ActiveDirectoryConnector(ADConnectionSettings settings, ILogger<ActiveDirectoryConnector> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// Get or create a shared LDAP connection to minimize Bind calls.
        /// Thread-safe: only one connection is created and reused.
        /// If the connection becomes invalid, it is disposed and recreated on next call.
        /// </summary>
        private LdapConnection GetConnection(out bool isOwned)
        {
            lock (_connectionLock)
            {
                if (_sharedConnection != null)
                {
                    isOwned = false;
                    return _sharedConnection;
                }

                _sharedConnection = CreateConnection();
                _sharedConnection.Bind();
                isOwned = false;
                return _sharedConnection;
            }
        }

        /// <summary>
        /// Invalidate the shared connection (e.g., after an LDAP error).
        /// Forces the next call to GetConnection to create a fresh one.
        /// </summary>
        private void InvalidateSharedConnection()
        {
            lock (_connectionLock)
            {
                if (_sharedConnection != null)
                {
                    try { _sharedConnection.Dispose(); } catch { /* ignore */ }
                    _sharedConnection = null;
                }
            }
        }

        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                using var connection = CreateConnection();
                connection.Bind();
                _logger.LogInformation("AD connection test successful to {Server}", _settings.Server);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AD connection test failed");
                return false;
            }
        }

        public Task<string> GetConnectionInfoAsync(CancellationToken ct = default)
        {
            try
            {
                using var connection = CreateConnection();
                connection.Bind();
                return Task.FromResult($"Connected to AD: {_settings.Server}:{_settings.Port}");
            }
            catch (Exception ex)
            {
                return Task.FromResult($"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if an account exists in AD by sAMAccountName.
        /// </summary>
        public Task<bool> ExistsAsync(string identity, CancellationToken ct = default)
        {
            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})",
                        SearchScope.Subtree,
                        "sAMAccountName"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    return Task.FromResult(response.Entries.Count > 0);
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "Error checking if user {Identity} exists in AD", identity);
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Finds an account by an arbitrary attribute (e.g. extensionAttribute2 = employee number)
        /// and returns its sAMAccountName.
        ///
        /// Deliberately returns null on an ambiguous match rather than the first hit: two accounts
        /// stamped with the same employee number is a data fault, and choosing one would write one
        /// person's attributes onto the other's account on every sync from then on, with no error
        /// to notice. The warning names the value so it can be found and fixed.
        /// </summary>
        public Task<string?> FindAccountByAttributeAsync(string attributeName, string value, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(attributeName) || string.IsNullOrWhiteSpace(value))
                return Task.FromResult<string?>(null);

            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(&(objectClass=user)({LdapSanitizer.EscapeFilterValue(attributeName)}={LdapSanitizer.EscapeFilterValue(value)}))",
                        SearchScope.Subtree,
                        "sAMAccountName"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);

                    if (response.Entries.Count == 0)
                        return Task.FromResult<string?>(null);

                    if (response.Entries.Count > 1)
                    {
                        var names = string.Join(", ", response.Entries.Cast<SearchResultEntry>()
                            .Select(e => e.Attributes["sAMAccountName"]?[0]?.ToString() ?? e.DistinguishedName));

                        // Throwing, not returning null: null means "no account has this value" and
                        // makes the caller create one, which would add a THIRD account to a value
                        // that already has two.
                        throw new InvalidOperationException(
                            $"AD match is ambiguous: {response.Entries.Count} accounts carry {attributeName}={value} " +
                            $"({names}). Remove the duplicate stamp in AD.");
                    }

                    var sam = response.Entries[0].Attributes["sAMAccountName"]?[0]?.ToString();
                    return Task.FromResult(string.IsNullOrWhiteSpace(sam) ? null : sam);
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                throw; // ambiguity is a data fault, not a transport failure — keep it distinct
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();

                // A failed lookup must NOT be reported as "no such account". ExistsAsync can afford
                // to return false on error because a create then fails harmlessly on a duplicate
                // name; here the name is generated fresh, so "not found" would create a SECOND
                // account for someone who already has one — silently, once per sync.
                _logger.LogError(ex, "Error searching AD for {Attribute}={Value}", attributeName, value);
                throw new InvalidOperationException(
                    $"Could not search AD for {attributeName}={value}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Create a new AD account using dynamically mapped attributes.
        /// Attributes come from MappingEngine.ApplyMappings, the target OU from
        /// MappingEngine.ResolveOU, and groups from MappingEngine.ResolveGroups —
        /// everything is driven by tenant configuration, nothing is hardcoded.
        /// </summary>
        public Task<SyncResultModel> CreateDynamicAsync(string identity, Dictionary<string, string> mappedAttributes,
            string targetOU, IEnumerable<string> groups, string? password, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrWhiteSpace(identity))
                {
                    return Task.FromResult(new SyncResultModel
                    {
                        Success = false,
                        Error = "Missing account identifier (no identifier mapping produced a value)",
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }

                if (mappedAttributes == null || mappedAttributes.Count == 0)
                {
                    return Task.FromResult(new SyncResultModel
                    {
                        Success = false,
                        Error = "No attribute mappings configured — cannot create account",
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }

                if (string.IsNullOrWhiteSpace(targetOU))
                {
                    return Task.FromResult(new SyncResultModel
                    {
                        Success = false,
                        Error = "No target OU resolved — configure OU rules for the tenant",
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }

                var connection = GetConnection(out var isOwned);
                try
                {

                var dn = $"CN={identity},{targetOU}";

                var addRequest = new AddRequest(dn);

                // Required object class + identifier
                addRequest.Attributes.Add(new DirectoryAttribute("objectClass", "user"));
                addRequest.Attributes.Add(new DirectoryAttribute("sAMAccountName", identity));

                foreach (var kvp in mappedAttributes)
                {
                    var attrName = kvp.Key;
                    var value = kvp.Value;

                    if (string.IsNullOrWhiteSpace(value)) continue;
                    // sAMAccountName is set explicitly above; objectClass is not mappable
                    if (attrName.Equals("sAMAccountName", StringComparison.OrdinalIgnoreCase)) continue;
                    if (attrName.Equals("objectClass", StringComparison.OrdinalIgnoreCase)) continue;

                    if (MultiValuedAttrs.Contains(attrName))
                    {
                        // Multi-valued values arrive pipe-delimited from MappingEngine
                        var values = value.Split('|')
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Cast<object>()
                            .ToArray();
                        if (values.Length > 0)
                            addRequest.Attributes.Add(new DirectoryAttribute(attrName, values));
                    }
                    else
                    {
                        addRequest.Attributes.Add(new DirectoryAttribute(attrName, value));
                    }
                }

                // Create the user
                connection.SendRequest(addRequest);

                // Try to set password and enable account
                // If these fail, delete the created user and return error
                try
                {
                    SetPassword(connection, dn, password ?? _settings.DefaultPassword);
                    EnableAccount(connection, dn);
                }
                catch (Exception passwordEx)
                {
                    _logger.LogWarning(passwordEx, "Failed to set password/enable for {Identity}, deleting partially created account", identity);

                    // Delete the partially created account
                    try
                    {
                        var deleteRequest = new DeleteRequest(dn);
                        connection.SendRequest(deleteRequest);
                        _logger.LogInformation("Deleted partially created account for {Identity}", identity);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogError(deleteEx, "Failed to delete partially created account for {Identity}. MANUAL CLEANUP REQUIRED!", identity);
                    }

                    // Reported, not thrown. This is one identity's failure, and every other
                    // failure in this method is reported the same way so the caller can record it
                    // and move on. It used to throw an InvalidOperationException, which the
                    // rethrow filter below deliberately lets escape — a rule meant for the
                    // ambiguous-match data fault. Sharing the exception type made a single
                    // account's password timeout end an entire 111,464-identity sync run.
                    sw.Stop();
                    return Task.FromResult(new SyncResultModel
                    {
                        Success = false,
                        Error = $"Account created but password setup failed: {passwordEx.Message}. Account has been deleted.",
                        DurationMs = (int)sw.ElapsedMilliseconds,
                        FailureKind = Core.Helpers.SyncFailureClassifier.Classify(passwordEx)
                    });
                }

                // Add to groups resolved from tenant group rules
                foreach (var group in groups ?? Enumerable.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(group))
                        AddToGroup(connection, group, dn);
                }

                sw.Stop();
                _logger.LogInformation("Created AD account {Identity} in {OU} ({Duration}ms)", identity, targetOU, sw.ElapsedMilliseconds);

                return Task.FromResult(new SyncResultModel
                {
                    Success = true,
                    DurationMs = (int)sw.ElapsedMilliseconds
                });
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                sw.Stop();
                _logger.LogError(ex, "Failed to create AD account for {Identity}", identity);
                return Task.FromResult(new SyncResultModel
                {
                    Success = false,
                    Error = ex.Message,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    FailureKind = Core.Helpers.SyncFailureClassifier.Classify(ex)
                });
            }
        }

        public Task<Dictionary<string, string>> GetCurrentAttributesAsync(string identity, CancellationToken ct = default)
        {
            try
            {
                using var connection = CreateConnection();
                connection.Bind();
                var attrs = GetUserAttributes(connection, identity);
                return Task.FromResult(attrs ?? new Dictionary<string, string>());
            }
            catch
            {
                return Task.FromResult(new Dictionary<string, string>());
            }
        }

        // === Private Helpers ===

        /// <summary>
        /// Channel setup lives in <see cref="LdapConnectionFactory"/> so every module encrypts
        /// identically. An unencrypted channel makes AD reject password writes with
        /// WILL_NOT_PERFORM while reads keep working.
        /// </summary>
        private LdapConnection CreateConnection() => LdapConnectionFactory.Create(_settings.ToLdapOptions());

        private static readonly HashSet<string> MultiValuedAttrs = new(StringComparer.OrdinalIgnoreCase)
        {
            "proxyAddresses", "otherMailbox", "url", "otherTelephone",
            "otherHomePhone", "otherFacsimileTelephoneNumber"
        };

        /// <summary>
        /// Reads an attribute's values as strings.
        ///
        /// Enumerating a <see cref="DirectoryAttribute"/> yields <c>object</c>, and for
        /// multi-valued attributes the runtime hands back <c>byte[]</c> rather than
        /// <c>string</c> — so a plain <c>ToString()</c> produced the literal text
        /// "System.Byte[]". The comparison in UpdateDynamicAsync then never matched, and every
        /// multi-valued attribute (proxyAddresses in practice) was rewritten on every single
        /// sync and reported as a change. The single-valued path escaped this because the
        /// indexer <c>attr[0]</c> decodes to a string.
        ///
        /// GetValues(typeof(string)) decodes properly; the fallback covers any attribute whose
        /// bytes are not valid text.
        /// </summary>
        internal static string[] GetStringValues(DirectoryAttribute attr)
        {
            try
            {
                return attr.GetValues(typeof(string)).Cast<string>().ToArray();
            }
            catch
            {
                return attr.Cast<object>()
                    .Select(v => v is byte[] bytes ? Encoding.UTF8.GetString(bytes) : v?.ToString() ?? "")
                    .ToArray();
            }
        }

        /// <summary>
        /// Joins a multi-valued attribute into one comparable string.
        /// Ordered, because LDAP does not guarantee the order values come back in — without
        /// this the values could match yet compare unequal, causing a pointless rewrite.
        /// </summary>
        internal static string JoinMultiValued(IEnumerable<string> values) =>
            string.Join("|", values.Where(v => !string.IsNullOrWhiteSpace(v))
                                   .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Splits a distinguished name into its RDN and its parent container.
        /// "CN=441234567,OU=Graduates,DC=std,DC=nu" → ("CN=441234567", "OU=Graduates,DC=std,DC=nu")
        ///
        /// Honours the LDAP escape for a literal comma inside an RDN (CN=Smith\, John), which a
        /// plain Split(',') would break apart.
        /// </summary>
        internal static (string Rdn, string Parent) SplitDn(string dn)
        {
            for (int i = 0; i < dn.Length; i++)
            {
                if (dn[i] == ',' && (i == 0 || dn[i - 1] != '\\'))
                    return (dn[..i], dn[(i + 1)..]);
            }
            return (dn, string.Empty);
        }

        /// <summary>
        /// Value prefixes on a multi-valued attribute that this system never manages and must
        /// never remove.
        ///
        /// x500 / X400 entries on proxyAddresses are generated by Exchange (the mailbox's
        /// LegacyExchangeDN). Outlook uses them to resolve replies to older messages, free/busy
        /// lookups, and routing for migrated mailboxes. An LDAP Replace rewrites the entire
        /// attribute, so mapping only the SMTP addresses silently deleted them.
        /// </summary>
        private static readonly string[] ProtectedValuePrefixes = { "x500:", "X400:" };

        /// <summary>
        /// The values to write for a multi-valued attribute: everything the mapping produced,
        /// plus any existing value this system does not manage.
        ///
        /// Without this, syncing proxyAddresses destroyed each mailbox's x500 address — the sync
        /// wrote exactly the two mapped SMTP entries and Replace discarded everything else. Safe
        /// Sync applies to attribute values too, not only to accounts: never remove what nobody
        /// asked to remove.
        /// </summary>
        internal static List<string> MergeMultiValued(string newValue, string? currentValue)
        {
            var merged = newValue.Split('|')
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToList();

            if (string.IsNullOrEmpty(currentValue)) return merged;

            var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);

            foreach (var existing in currentValue.Split('|').Select(v => v.Trim()))
            {
                if (existing.Length == 0 || seen.Contains(existing)) continue;
                if (!ProtectedValuePrefixes.Any(p => existing.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                merged.Add(existing);
                seen.Add(existing);
            }

            return merged;
        }

        private Dictionary<string, string>? GetUserAttributes(LdapConnection connection, string samAccountName)
        {
            var searchRequest = new SearchRequest(
                _settings.BaseDN,
                $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(samAccountName)})",
                SearchScope.Subtree,
                "distinguishedName", "givenName", "sn", "initials", "displayName",
                "department", "extensionAttribute4", "extensionAttribute13",
                "extensionAttribute14", "extensionAttribute15"
            );

            var response = (SearchResponse)connection.SendRequest(searchRequest);

            if (response.Entries.Count == 0) return null;

            var entry = response.Entries[0];
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dn"] = entry.DistinguishedName
            };

            foreach (string attrName in entry.Attributes.AttributeNames)
            {
                var attr = entry.Attributes[attrName];
                if (attr.Count > 0)
                {
                    var values = GetStringValues(attr);
                    attrs[attrName] = MultiValuedAttrs.Contains(attrName)
                        ? JoinMultiValued(values)
                        : (values.FirstOrDefault() ?? "");
                }
            }

            return attrs;
        }

        /// <summary>
        /// Dynamic overload: loads only the specified AD attributes for comparison.
        /// </summary>
        private Dictionary<string, string>? GetUserAttributes(LdapConnection connection, string samAccountName, string[] attributesToLoad)
        {
            // Always include distinguishedName
            var allAttrs = new HashSet<string>(attributesToLoad, StringComparer.OrdinalIgnoreCase)
            {
                "distinguishedName"
            };

            var searchRequest = new SearchRequest(
                _settings.BaseDN,
                $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(samAccountName)})",
                SearchScope.Subtree,
                allAttrs.ToArray()
            );

            var response = (SearchResponse)connection.SendRequest(searchRequest);

            if (response.Entries.Count == 0) return null;

            var entry = response.Entries[0];
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dn"] = entry.DistinguishedName
            };

            foreach (string attrName in entry.Attributes.AttributeNames)
            {
                var attr = entry.Attributes[attrName];
                if (attr.Count > 0)
                {
                    var values = GetStringValues(attr);
                    attrs[attrName] = MultiValuedAttrs.Contains(attrName)
                        ? JoinMultiValued(values)
                        : (values.FirstOrDefault() ?? "");
                }
            }

            return attrs;
        }

        /// <summary>
        /// Update AD user using dynamically mapped attributes.
        /// Reads current AD values for the mapped attributes, compares, and updates changed ones.
        /// This method is driven by TenantAttributeMapping — any attribute in the mapping is synced.
        /// </summary>
        public Task<SyncResult> UpdateDynamicAsync(string identity, Dictionary<string, string> mappedAttributes, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (mappedAttributes == null || mappedAttributes.Count == 0)
                {
                    sw.Stop();
                    return Task.FromResult(new SyncResultModel
                    {
                        Success = true,
                        ChangedFields = "NoChanges",
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }

                var connection = GetConnection(out var isOwned);
                try
                {
                    // Determine which AD attributes to read (only the ones we want to compare)
                    var attributeNames = mappedAttributes.Keys.ToArray();
                    var currentAttrs = GetUserAttributes(connection, identity, attributeNames);

                    if (currentAttrs == null)
                    {
                        sw.Stop();
                        return Task.FromResult(new SyncResultModel
                        {
                            Success = false,
                            Error = "User not found in AD",
                            DurationMs = (int)sw.ElapsedMilliseconds
                        });
                    }

                    var modifications = new List<DirectoryAttributeModification>();
                    var changes = new List<string>();

                    // Compare each mapped attribute with current AD value
                    foreach (var kvp in mappedAttributes)
                    {
                        var adAttribute = kvp.Key;
                        var newValue = kvp.Value;

                        // Skip empty values and identifier attributes (sAMAccountName shouldn't be updated)
                        if (string.IsNullOrWhiteSpace(newValue)) continue;
                        if (adAttribute.Equals("sAMAccountName", StringComparison.OrdinalIgnoreCase)) continue;

                        currentAttrs.TryGetValue(adAttribute, out var currentValue);

                        // currentValue for a multi-valued attribute is stored order-normalised,
                        // so normalise the incoming value identically — otherwise the same set of
                        // values in a different order reads as a change and is rewritten forever.
                        var isMultiValued = MultiValuedAttrs.Contains(adAttribute);

                        // Values to actually write: the mapped ones plus any unmanaged value
                        // already on the account (x500/X400 — see MergeMultiValued).
                        var mergedValues = isMultiValued ? MergeMultiValued(newValue, currentValue) : null;
                        var comparableNewValue = isMultiValued
                            ? JoinMultiValued(mergedValues!)
                            : newValue;

                        if (currentValue != comparableNewValue)
                        {
                            if (isMultiValued)
                            {
                                // Multi-valued: add values individually to the Replace operation
                                var mod = new DirectoryAttributeModification
                                {
                                    Name = adAttribute,
                                    Operation = DirectoryAttributeOperation.Replace
                                };
                                foreach (var val in mergedValues!)
                                    mod.Add(val);
                                modifications.Add(mod);

                                var oldDisplay = string.IsNullOrEmpty(currentValue) ? "(empty)" : currentValue;
                                changes.Add($"{adAttribute}: {oldDisplay}→{string.Join("|", mergedValues)}");
                            }
                            else
                            {
                                var mod = new DirectoryAttributeModification
                                {
                                    Name = adAttribute,
                                    Operation = DirectoryAttributeOperation.Replace
                                };
                                mod.Add(newValue);
                                modifications.Add(mod);

                                var oldDisplay = string.IsNullOrEmpty(currentValue) ? "(empty)" : currentValue;
                                changes.Add($"{adAttribute}: {oldDisplay}→{newValue}");
                            }
                        }
                    }

                    if (modifications.Count == 0)
                    {
                        sw.Stop();
                        return Task.FromResult(new SyncResultModel
                        {
                            Success = true,
                            ChangedFields = "NoChanges",
                            DurationMs = (int)sw.ElapsedMilliseconds
                        });
                    }

                    // Apply all modifications in a single LDAP request
                    var modifyRequest = new ModifyRequest(currentAttrs["dn"], modifications.ToArray());
                    connection.SendRequest(modifyRequest);

                    sw.Stop();
                    var changedFieldsStr = string.Join(", ", changes.Distinct());
                    _logger.LogInformation("Dynamic update AD account {Identity}: {Changes} ({Count} attributes)",
                        identity, changedFieldsStr, modifications.Count);

                    return Task.FromResult(new SyncResultModel
                    {
                        Success = true,
                        ChangedFields = changedFieldsStr,
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                sw.Stop();
                _logger.LogError(ex, "Failed to dynamic-update AD account for {Identity}", identity);
                return Task.FromResult(new SyncResultModel
                {
                    Success = false,
                    Error = ex.Message,
                    DurationMs = (int)sw.ElapsedMilliseconds
                });
            }
        }

        private void SetPassword(LdapConnection connection, string dn, string password)
        {
            var passwordBytes = Encoding.Unicode.GetBytes($"\"{password}\"");
            var mod = new DirectoryAttributeModification
            {
                Name = "unicodePwd",
                Operation = DirectoryAttributeOperation.Replace
            };
            mod.Add(passwordBytes);

            var modifyRequest = new ModifyRequest(dn, mod);
            connection.SendRequest(modifyRequest);
        }

        private void EnableAccount(LdapConnection connection, string dn)
        {
            var mod = new DirectoryAttributeModification
            {
                Name = "userAccountControl",
                Operation = DirectoryAttributeOperation.Replace
            };
            mod.Add("512"); // NORMAL_ACCOUNT

            var modifyRequest = new ModifyRequest(dn, mod);
            connection.SendRequest(modifyRequest);
        }

        private void AddToGroup(LdapConnection connection, string groupNameOrDn, string memberDn)
        {
            try
            {
                string groupDn;

                // Group rules may supply either a full DN or a plain group name
                if (groupNameOrDn.Contains('='))
                {
                    groupDn = groupNameOrDn;
                }
                else
                {
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(groupNameOrDn)})",
                        SearchScope.Subtree,
                        "distinguishedName"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    if (response.Entries.Count == 0)
                    {
                        _logger.LogWarning("Group {GroupName} not found in AD — skipping membership for {MemberDn}", groupNameOrDn, memberDn);
                        return;
                    }
                    groupDn = response.Entries[0].DistinguishedName;
                }

                var mod = new DirectoryAttributeModification
                {
                    Name = "member",
                    Operation = DirectoryAttributeOperation.Add
                };
                mod.Add(memberDn);

                var modifyRequest = new ModifyRequest(groupDn, mod);
                connection.SendRequest(modifyRequest);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add {MemberDn} to group {GroupName}", memberDn, groupNameOrDn);
            }
        }

        /// <summary>Move an account to a new OU.</summary>
        public async Task<bool> MoveToOUAsync(string identity, string targetOU, CancellationToken ct = default)
        {
            const int maxRetries = 3;
            const int baseDelayMs = 1000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var connection = CreateConnection();
                    connection.Bind();

                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})",
                        SearchScope.Subtree,
                        "distinguishedName"
                    );
                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    if (response.Entries.Count == 0)
                    {
                        // Used to return false with no explanation, which surfaced later as an
                        // unexplained "not moved" with nothing in the log to act on.
                        _logger.LogWarning("Cannot move {Identity}: no account found under {BaseDN}",
                            identity, _settings.BaseDN);
                        return false;
                    }

                    var userDn = response.Entries[0].DistinguishedName;
                    var (newRdn, currentParent) = SplitDn(userDn);

                    // Already where it belongs. Asking AD to move an object into its own parent
                    // raises an error, so this used to return false and leave ADCurrentOU null —
                    // making a correctly-placed account look like a failed move. On this tenant
                    // many graduates were already filed in the Graduates OU, so a bulk run would
                    // have produced tens of thousands of errors that were not errors at all.
                    if (string.Equals(currentParent, targetOU, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("{Identity} is already in {TargetOU} — no move needed", identity, targetOU);
                        return true;
                    }

                    var modifyDnRequest = new ModifyDNRequest(userDn, targetOU, newRdn);
                    connection.SendRequest(modifyDnRequest);
                    _logger.LogInformation("Moved {Identity} to {TargetOU}", identity, targetOU);
                    return true;
                }
                catch (DirectoryOperationException ex) when (attempt < maxRetries && (ex.Message.Contains("BUSY") || ex.Message.Contains("依忙")))
                {
                    _logger.LogWarning("AD server busy for {Identity} (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms...",
                        identity, attempt, maxRetries, baseDelayMs * attempt);
                    await Task.Delay(baseDelayMs * attempt, ct);
                }
                catch (DirectoryOperationException ex)
                {
                    // A move into an OU that does not exist comes back as
                    //   "An unknown error occurred. 00002089: UpdErr: ... problem 5012 (DIR_ERROR)"
                    // which names neither the cause nor the OU. The create path is luckier: its
                    // error carries a "best match of:" hint showing how far the DN resolved, and
                    // that hint is what makes a missing OU obvious there and invisible here.
                    //
                    // Rather than parse Windows error codes, ask the directory the question
                    // directly. This runs only on the failure path, so it costs nothing normally.
                    if (!TargetOuExists(targetOU))
                    {
                        _logger.LogError(
                            "Failed to move {Identity}: the target OU does not exist — {TargetOU}. " +
                            "Check the MoveOU rule's ActionValue and the tenant's OU rule template.",
                            identity, targetOU);
                    }
                    else
                    {
                        _logger.LogError(ex, "Failed to move {Identity} to {TargetOU}", identity, targetOU);
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move {Identity} to {TargetOU}", identity, targetOU);
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// Does this DN exist? Used only to turn an opaque move failure into a message that names
        /// the cause.
        ///
        /// A probe that fails for any reason OTHER than "not found" returns true on purpose: the
        /// job here is to add detail when we are certain, never to accuse an OU of being missing
        /// on the strength of a lookup that itself broke.
        /// </summary>
        private bool TargetOuExists(string dn)
        {
            try
            {
                using var connection = CreateConnection();
                connection.Bind();
                var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, "distinguishedName");
                var response = (SearchResponse)connection.SendRequest(request);
                return response.Entries.Count > 0;
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not probe {Dn} while explaining a failed move", dn);
                return true;
            }
        }

        /// <summary>
        /// ⛔ SAFETY BLOCK: This method is intentionally disabled.
        /// IdentitySyncPro operates in SAFE SYNC mode — NO accounts are ever deleted or disabled.
        /// This method exists only to satisfy the ITargetConnector interface.
        /// </summary>
        public Task<bool> DisableAccountAsync(string identity, CancellationToken ct = default)
        {
            _logger.LogWarning("⛔ BLOCKED: Attempt to disable account {Identity} was rejected. Safe Sync mode is enforced.", identity);
            return Task.FromResult(false);
        }

        /// <summary>
        /// Remove a user from all AD groups (except the primary group "Domain Users").
        /// Reads the memberOf attribute, then removes the user's DN from each group's member list.
        /// Used by LifecycleEngine when identities are no longer active (suspended, deprovisioned, etc.).
        /// </summary>
        public Task<(bool Success, int RemovedCount, List<string> GroupNames)> RemoveFromAllGroupsAsync(string identity, CancellationToken ct = default)
        {
            var removedGroups = new List<string>();
            var failedGroups = new List<string>();
            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    // Step 1: Find the user and get their DN + memberOf
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})",
                        SearchScope.Subtree,
                        "distinguishedName", "memberOf"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    if (response.Entries.Count == 0)
                    {
                        _logger.LogWarning("RemoveFromAllGroups: User {Identity} not found in AD", identity);
                        return Task.FromResult((false, 0, removedGroups));
                    }

                    var entry = response.Entries[0];
                    var userDn = entry.DistinguishedName;

                    // Step 2: Get all groups the user is a member of
                    if (!entry.Attributes.Contains("memberOf") || entry.Attributes["memberOf"].Count == 0)
                    {
                        _logger.LogInformation("RemoveFromAllGroups: User {Identity} is not a member of any groups", identity);
                        return Task.FromResult((true, 0, removedGroups));
                    }

                    var memberOfAttr = entry.Attributes["memberOf"];

                    for (int i = 0; i < memberOfAttr.Count; i++)
                    {
                        var groupDn = memberOfAttr[i]?.ToString();
                        if (string.IsNullOrEmpty(groupDn)) continue;

                        try
                        {
                            // Step 3: Remove user from this group
                            var mod = new DirectoryAttributeModification
                            {
                                Name = "member",
                                Operation = DirectoryAttributeOperation.Delete
                            };
                            mod.Add(userDn);

                            var modifyRequest = new ModifyRequest(groupDn, mod);
                            connection.SendRequest(modifyRequest);

                            // Extract group name from DN for logging (e.g., "CN=GroupName,OU=..." → "GroupName")
                            var groupName = groupDn.Split(',')[0].Replace("CN=", "");
                            removedGroups.Add(groupName);

                            _logger.LogInformation("RemoveFromAllGroups: Removed {Identity} from group {Group}", identity, groupName);
                        }
                        catch (DirectoryOperationException ex) when (ex.Message.Contains("unwilling") || ex.Message.Contains("primary"))
                        {
                            // Skip primary group (Domain Users) — AD doesn't allow removing it
                            _logger.LogDebug("RemoveFromAllGroups: Skipped primary group {GroupDn} for {Identity}", groupDn, identity);
                        }
                        catch (Exception ex)
                        {
                            // The primary group is handled by the filter above and stays a success —
                            // AD refuses to remove it and nothing should pretend otherwise. Anything
                            // else is a group the identity keeps against the rule's intent.
                            failedGroups.Add(groupDn.Split(',')[0].Replace("CN=", ""));
                            _logger.LogWarning(ex, "RemoveFromAllGroups: Failed to remove {Identity} from group {GroupDn}", identity, groupDn);
                        }
                    }

                    _logger.LogInformation("RemoveFromAllGroups: Removed {Identity} from {Count} groups ({Failed} failed)",
                        identity, removedGroups.Count, failedGroups.Count);

                    if (failedGroups.Count > 0)
                    {
                        _logger.LogError(
                            "RemoveFromAllGroups: {Identity} still belongs to {Count} group(s) that should have been removed: {Groups}",
                            identity, failedGroups.Count, string.Join(", ", failedGroups));
                    }

                    return Task.FromResult((failedGroups.Count == 0, removedGroups.Count, removedGroups));
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "RemoveFromAllGroups: Failed for {Identity}", identity);
                return Task.FromResult((false, 0, removedGroups));
            }
        }
        /// <summary>
        /// Remove a user from specific AD groups by name.
        /// Searches for the user, then removes them only from groups matching the specified names.
        /// </summary>
        public Task<(bool Success, int RemovedCount, List<string> GroupNames)> RemoveFromSpecificGroupsAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            var removedGroups = new List<string>();
            var failedGroups = new List<string>();
            var targetGroups = groupNames.Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (targetGroups.Count == 0)
            {
                _logger.LogWarning("RemoveFromSpecificGroups: No group names specified for {Identity}", identity);
                return Task.FromResult((true, 0, removedGroups));
            }

            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    // Step 1: Find the user and get their DN + memberOf
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})",
                        SearchScope.Subtree,
                        "distinguishedName", "memberOf"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    if (response.Entries.Count == 0)
                    {
                        _logger.LogWarning("RemoveFromSpecificGroups: User {Identity} not found in AD", identity);
                        return Task.FromResult((false, 0, removedGroups));
                    }

                    var entry = response.Entries[0];
                    var userDn = entry.DistinguishedName;

                    if (!entry.Attributes.Contains("memberOf") || entry.Attributes["memberOf"].Count == 0)
                    {
                        _logger.LogInformation("RemoveFromSpecificGroups: User {Identity} is not a member of any groups", identity);
                        return Task.FromResult((true, 0, removedGroups));
                    }

                    var memberOfAttr = entry.Attributes["memberOf"];

                    for (int i = 0; i < memberOfAttr.Count; i++)
                    {
                        var groupDn = memberOfAttr[i]?.ToString();
                        if (string.IsNullOrEmpty(groupDn)) continue;

                        // Extract group name from DN (e.g., "CN=GroupName,OU=..." → "GroupName")
                        var groupName = groupDn.Split(',')[0].Replace("CN=", "");

                        // Only remove from specified groups
                        if (!targetGroups.Contains(groupName)) continue;

                        try
                        {
                            var mod = new DirectoryAttributeModification
                            {
                                Name = "member",
                                Operation = DirectoryAttributeOperation.Delete
                            };
                            mod.Add(userDn);

                            var modifyRequest = new ModifyRequest(groupDn, mod);
                            connection.SendRequest(modifyRequest);

                            removedGroups.Add(groupName);
                            _logger.LogInformation("RemoveFromSpecificGroups: Removed {Identity} from group {Group}", identity, groupName);
                        }
                        catch (Exception ex)
                        {
                            // Counted, not just logged. Returning Success = true here meant a
                            // graduate could keep a licence group while the run reported
                            // "1 exported, 0 failed" — the only trace a single [WRN] line.
                            failedGroups.Add(groupName);
                            _logger.LogWarning(ex, "RemoveFromSpecificGroups: Failed to remove {Identity} from group {GroupDn}", identity, groupDn);
                        }
                    }

                    // A group the user was never in is NOT a failure: the loop walks memberOf, so
                    // "removed from 1/2 specified" is the normal reading of a rule that names two
                    // licence groups for an identity who held one.
                    _logger.LogInformation("RemoveFromSpecificGroups: Removed {Identity} from {Count}/{Total} specified groups ({Failed} failed)",
                        identity, removedGroups.Count, targetGroups.Count, failedGroups.Count);

                    if (failedGroups.Count > 0)
                    {
                        _logger.LogError(
                            "RemoveFromSpecificGroups: {Identity} still belongs to {Count} group(s) that should have been removed: {Groups}",
                            identity, failedGroups.Count, string.Join(", ", failedGroups));
                    }

                    return Task.FromResult((failedGroups.Count == 0, removedGroups.Count, removedGroups));
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "RemoveFromSpecificGroups: Failed for {Identity}", identity);
                return Task.FromResult((false, 0, removedGroups));
            }
        }

        /// <summary>
        /// Add a user to specific AD groups by name.
        /// Uses the existing private AddToGroup method for each group.
        /// </summary>
        /// <summary>
        /// Did an AddGroups action leave the identity in every group the rule named?
        ///
        /// Extracted from the LDAP loop so it can be tested at all: the loop itself needs a
        /// directory, and mutation testing showed that inverting this decision inside the method
        /// killed no test — the engine-level tests mock the connector and never run this code.
        ///
        /// Already-a-member counts as applied, and that exclusion is the whole subtlety. Without it
        /// every identity whose group was added at creation time would be reported failed on every
        /// later run, since the lifecycle export re-applies the same rule and AD answers
        /// ENTRY_EXISTS. With it, only a group that could not be applied at all — missing from AD,
        /// or refused — is a failure.
        /// </summary>
        public static bool AddGroupsSucceeded(int namedByRule, int added, int alreadyMember) =>
            namedByRule - added - alreadyMember <= 0;

        public Task<(bool Success, int AddedCount, List<string> GroupNames)> AddToGroupsAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            var addedGroups = new List<string>();
            var alreadyMember = new List<string>();
            var targetGroups = groupNames.Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g)).ToList();

            if (targetGroups.Count == 0)
            {
                _logger.LogWarning("AddToGroups: No group names specified for {Identity}", identity);
                return Task.FromResult((true, 0, addedGroups));
            }

            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    // Step 1: Find the user and get their DN
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})",
                        SearchScope.Subtree,
                        "distinguishedName"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    if (response.Entries.Count == 0)
                    {
                        _logger.LogWarning("AddToGroups: User {Identity} not found in AD", identity);
                        return Task.FromResult((false, 0, addedGroups));
                    }

                    var userDn = response.Entries[0].DistinguishedName;

                    // Step 2: Add user to each specified group
                    foreach (var groupName in targetGroups)
                    {
                        try
                        {
                            // Search for the group by sAMAccountName
                            var groupSearch = new SearchRequest(
                                _settings.BaseDN,
                                $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(groupName)})",
                                SearchScope.Subtree,
                                "distinguishedName"
                            );

                            var groupResponse = (SearchResponse)connection.SendRequest(groupSearch);
                            if (groupResponse.Entries.Count == 0)
                            {
                                _logger.LogWarning("AddToGroups: Group {GroupName} not found in AD", groupName);
                                continue;
                            }

                            var groupDn = groupResponse.Entries[0].DistinguishedName;
                            var mod = new DirectoryAttributeModification
                            {
                                Name = "member",
                                Operation = DirectoryAttributeOperation.Add
                            };
                            mod.Add(userDn);

                            var modifyRequest = new ModifyRequest(groupDn, mod);
                            connection.SendRequest(modifyRequest);

                            addedGroups.Add(groupName);
                            _logger.LogInformation("AddToGroups: Added {Identity} to group {Group}", identity, groupName);
                        }
                        catch (DirectoryOperationException ex) when (ex.Message.Contains("already") || ex.Message.Contains("ENTRY_EXISTS"))
                        {
                            // User is already a member — not an error
                            alreadyMember.Add(groupName);
                            _logger.LogDebug("AddToGroups: {Identity} is already a member of {GroupName}", identity, groupName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "AddToGroups: Failed to add {Identity} to group {GroupName}", identity, groupName);
                        }
                    }

                    // Spell out WHY a group was not added. "0/1" alone reads the same whether the
                    // user was already a member (the normal case when creation added the group and
                    // the lifecycle export re-applies it) or whether the group silently went
                    // missing — and only one of those needs anyone's attention.
                    var unresolved = targetGroups.Count - addedGroups.Count - alreadyMember.Count;
                    _logger.LogInformation(
                        "AddToGroups: Added {Identity} to {Count}/{Total} groups ({Already} already a member, {Unresolved} not applied)",
                        identity, addedGroups.Count, targetGroups.Count, alreadyMember.Count, unresolved);

                    // "Not applied" was computed, printed, and then thrown away: the method returned
                    // Success = true regardless, so a rule naming a group that does not exist in AD
                    // left every matching identity without it while the run reported no failures.
                    // Already-a-member is excluded — nothing needed doing there.
                    if (unresolved > 0)
                    {
                        _logger.LogError(
                            "AddToGroups: {Identity} was NOT added to {Count} of the {Total} group(s) the rule names. " +
                            "Check that every named group exists in AD and is reachable.",
                            identity, unresolved, targetGroups.Count);
                    }

                    return Task.FromResult((AddGroupsSucceeded(targetGroups.Count, addedGroups.Count, alreadyMember.Count),
                                           addedGroups.Count, addedGroups));
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "AddToGroups: Failed for {Identity}", identity);
                return Task.FromResult((false, 0, addedGroups));
            }
        }

        /// <summary>
        /// Get the current OU path of a user from their Distinguished Name.
        /// Extracts the OU portion from the user's DN (everything after the first comma).
        /// </summary>
        public Task<string?> GetCurrentOUAsync(string identity, CancellationToken ct = default)
        {
            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})",
                        SearchScope.Subtree,
                        "distinguishedName"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    if (response.Entries.Count == 0)
                    {
                        _logger.LogWarning("GetCurrentOU: User {Identity} not found in AD", identity);
                        return Task.FromResult<string?>(null);
                    }

                    var dn = response.Entries[0].DistinguishedName;
                    // Extract OU from DN: "CN=username,OU=Identities,DC=..." → "OU=Identities,DC=..."
                    var commaIndex = dn.IndexOf(',');
                    if (commaIndex >= 0)
                    {
                        var ou = dn[(commaIndex + 1)..];
                        return Task.FromResult<string?>(ou);
                    }

                    return Task.FromResult<string?>(null);
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "GetCurrentOU: Failed for {Identity}", identity);
                return Task.FromResult<string?>(null);
            }
        }

        /// <summary>
        /// Load specific AD attributes for an account by sAMAccountName.
        /// </summary>
        public Task<Dictionary<string, string>?> GetAttributesAsync(string identity, string[] attributes, CancellationToken ct = default)
        {
            try
            {
                using var connection = CreateConnection();
                connection.Bind();
                var attrs = GetUserAttributes(connection, identity, attributes);
                return Task.FromResult(attrs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAttributes failed for {Identity}", identity);
                return Task.FromResult<Dictionary<string, string>?>(null);
            }
        }

        /// <summary>
        /// Reset an account's password (self-service reset) and clear the AD lockout flag.
        /// </summary>
        public Task<(bool Success, string? Error)> ResetPasswordAsync(string identity, string newPassword, CancellationToken ct = default)
        {
            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    var searchRequest = new SearchRequest(
                        _settings.BaseDN,
                        $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})",
                        SearchScope.Subtree,
                        "distinguishedName"
                    );

                    var response = (SearchResponse)connection.SendRequest(searchRequest);
                    if (response.Entries.Count == 0)
                        return Task.FromResult<(bool, string?)>((false, "Account not found"));

                    var dn = response.Entries[0].DistinguishedName;

                    try
                    {
                        SetPassword(connection, dn, newPassword);
                    }
                    catch (DirectoryOperationException dex)
                    {
                        // Read/search already succeeded on this connection, so the bind is fine.
                        // A failure specifically on the unicodePwd write is almost always one of:
                        //   1) the connection isn't encrypted — AD only accepts password writes over
                        //      LDAPS or a Kerberos/NTLM sign+seal channel (WILL_NOT_PERFORM), or
                        //   2) the service account lacks the "Reset Password" delegated right on the OU
                        //      (that surfaces as insufficientAccessRights / 0x32, not WILL_NOT_PERFORM).
                        var raw = dex.Response?.ErrorMessage ?? dex.Message;
                        var hint = raw.Contains("WILL_NOT_PERFORM", StringComparison.OrdinalIgnoreCase) || raw.Contains("0000001F")
                            ? "AD refused the password write (WILL_NOT_PERFORM) — the LDAP channel is not encrypted. AD only allows password writes over LDAPS or a sign+seal channel. Fix the domain's connection: either turn OFF 'Use SSL' on port 389 (the app then uses Kerberos sign+seal, no certificate needed), or turn ON 'Use SSL' with port 636 and a valid LDAPS certificate on the DC. A 'Use SSL'=true + port 389 combination is contradictory and yields a plaintext connection."
                            : raw;
                        _logger.LogError(dex, "ResetPassword: AD rejected unicodePwd for {Identity} — {Hint}", identity, hint);
                        return Task.FromResult<(bool, string?)>((false, hint));
                    }

                    // Clear lockout so a locked user can sign in right away
                    try
                    {
                        var unlock = new DirectoryAttributeModification
                        {
                            Name = "lockoutTime",
                            Operation = DirectoryAttributeOperation.Replace
                        };
                        unlock.Add("0");
                        connection.SendRequest(new ModifyRequest(dn, unlock));
                    }
                    catch { /* unlock is best-effort */ }

                    _logger.LogInformation("Self-service password reset completed for {Identity}", identity);
                    return Task.FromResult<(bool, string?)>((true, null));
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "ResetPassword failed for {Identity}", identity);
                return Task.FromResult<(bool, string?)>((false, ex.Message));
            }
        }

        /// <summary>
        /// Every account in a group, read with paging so the answer is the whole group.
        ///
        /// <b>Searched by memberOf rather than by reading the group's own member attribute.</b> That
        /// attribute is capped — Active Directory returns the first 1,500 values and says nothing
        /// about the rest unless the caller asks for ranges. A certification campaign built on a
        /// truncated list reviews the first part of a group and closes reporting that everything was
        /// certified: the missing members were never in it, so nobody declined to review them and
        /// nothing records that they were skipped.
        ///
        /// A paged memberOf search has no such ceiling. Failure is returned as failure rather than
        /// as a shorter list, because a partial read must never be usable as a complete one.
        /// </summary>
        public Task<(bool Success, IReadOnlyList<GroupMember> Members, string? Error)> GetGroupMembersAsync(
            string groupName, bool nested = false, CancellationToken ct = default)
        {
            var members = new List<GroupMember>();

            try
            {
                var connection = GetConnection(out var isOwned);
                try
                {
                    var group = groupName?.Trim();
                    if (string.IsNullOrEmpty(group))
                        return Fail("No group name was given.");

                    // Accept a full DN or a plain name, as every other group setting here does.
                    string groupDn;
                    if (group.Contains('='))
                    {
                        groupDn = group;
                    }
                    else
                    {
                        var lookup = new SearchRequest(
                            _settings.BaseDN,
                            $"(&(objectClass=group)(sAMAccountName={LdapSanitizer.EscapeFilterValue(group)}))",
                            SearchScope.Subtree, "distinguishedName");
                        var found = (SearchResponse)connection.SendRequest(lookup);

                        // A group that does not resolve is an error, not an empty group. Reading it
                        // as empty would put zero rows into a campaign and close it as clean.
                        if (found.Entries.Count == 0)
                            return Fail($"Group '{group}' was not found.");

                        groupDn = found.Entries[0].DistinguishedName;
                    }

                    var rule = nested ? ":1.2.840.113556.1.4.1941:" : "";
                    var request = new SearchRequest(
                        _settings.BaseDN,
                        $"(&(objectCategory=person)(objectClass=user)(memberOf{rule}={LdapSanitizer.EscapeFilterValue(groupDn)}))",
                        SearchScope.Subtree,
                        "sAMAccountName", "displayName", "distinguishedName");

                    var page = new PageResultRequestControl(500);
                    request.Controls.Add(page);

                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        var response = (SearchResponse)connection.SendRequest(request);
                        foreach (SearchResultEntry entry in response.Entries)
                        {
                            members.Add(new GroupMember(
                                Attr(entry, "sAMAccountName") ?? entry.DistinguishedName,
                                Attr(entry, "displayName"),
                                entry.DistinguishedName));
                        }

                        var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                        if (cookie == null || cookie.Cookie.Length == 0) break;
                        page.Cookie = cookie.Cookie;
                    }

                    _logger.LogInformation("GetGroupMembers: '{Group}' has {Count} member(s)", group, members.Count);
                    return Task.FromResult<(bool, IReadOnlyList<GroupMember>, string?)>((true, members, null));
                }
                finally
                {
                    if (isOwned) connection.Dispose();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "GetGroupMembers failed for '{Group}'", groupName);
                return Fail(ex.Message);
            }

            static Task<(bool, IReadOnlyList<GroupMember>, string?)> Fail(string error) =>
                Task.FromResult<(bool, IReadOnlyList<GroupMember>, string?)>(
                    (false, Array.Empty<GroupMember>(), error));

            static string? Attr(SearchResultEntry entry, string name) =>
                entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0
                    ? entry.Attributes[name][0]?.ToString()
                    : null;
        }

        /// <summary>
        /// True when the account is a (nested) member of ANY of the given groups
        /// (LDAP_MATCHING_RULE_IN_CHAIN). Unresolvable group names are skipped.
        ///
        /// <b>On failure this answers <c>true</c>, and that is only safe for one kind of question.</b>
        /// It was written for SSPR's exclusion list, where the caller asks "is this account in a
        /// group that forbids a reset?" — so an unanswerable directory means "assume forbidden",
        /// and the reset is denied.
        ///
        /// Asked the other way round — "is this person an approver?", "may this person request?" —
        /// the same <c>true</c> grants the right to everybody the moment the directory is
        /// unreachable, and does it silently. Callers asking a permission question must use
        /// <see cref="TryIsMemberOfAnyAsync"/>, which reports "could not tell" as its own answer.
        /// </summary>
        public Task<bool> IsMemberOfAnyAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            try
            {
                return Task.FromResult(QueryMemberOfAny(identity, groupNames));
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "IsMemberOfAny failed for {Identity} — denying by safety", identity);
                return Task.FromResult(true); // fail-closed for an EXCLUSION question: deny when membership can't be verified
            }
        }

        /// <summary>
        /// Nested membership as a three-valued answer: true, false, or <c>null</c> when the
        /// directory could not be asked.
        ///
        /// Exists so a permission check can fail closed in its own direction. The caller decides
        /// what "could not tell" means for the question it is asking, instead of inheriting a
        /// default that happens to suit a different one.
        /// </summary>
        public Task<bool?> TryIsMemberOfAnyAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            try
            {
                return Task.FromResult<bool?>(QueryMemberOfAny(identity, groupNames));
            }
            catch (Exception ex)
            {
                InvalidateSharedConnection();
                _logger.LogError(ex, "TryIsMemberOfAny could not determine membership for {Identity}", identity);
                return Task.FromResult<bool?>(null);
            }
        }

        /// <summary>
        /// The membership query itself, written once. Both public forms differ only in what they
        /// report when it throws — duplicating the query instead would let the two drift, and a
        /// membership test that disagrees with itself is worse than either answer.
        /// </summary>
        private bool QueryMemberOfAny(string identity, IEnumerable<string> groupNames)
        {
            var connection = GetConnection(out var isOwned);
            try
            {
                foreach (var raw in groupNames ?? Enumerable.Empty<string>())
                {
                    var group = raw?.Trim();
                    if (string.IsNullOrEmpty(group)) continue;

                    // Resolve the group DN (accept a full DN or a plain name)
                    string? groupDn = group.Contains('=') ? group : null;
                    if (groupDn == null)
                    {
                        var groupSearch = new SearchRequest(
                            _settings.BaseDN,
                            $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(group)})",
                            SearchScope.Subtree,
                            "distinguishedName");
                        var groupResp = (SearchResponse)connection.SendRequest(groupSearch);
                        if (groupResp.Entries.Count == 0) continue;
                        groupDn = groupResp.Entries[0].DistinguishedName;
                    }

                    var memberSearch = new SearchRequest(
                        _settings.BaseDN,
                        $"(&(sAMAccountName={LdapSanitizer.EscapeFilterValue(identity)})" +
                        $"(memberOf:1.2.840.113556.1.4.1941:={LdapSanitizer.EscapeFilterValue(groupDn)}))",
                        SearchScope.Subtree,
                        "distinguishedName");
                    var memberResp = (SearchResponse)connection.SendRequest(memberSearch);
                    if (memberResp.Entries.Count > 0) return true;
                }

                return false;
            }
            finally
            {
                if (isOwned) connection.Dispose();
            }
        }
    }
}
