using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Interfaces;
using IdentitySyncPro.Core.Models.Connectors;
using Microsoft.Extensions.Logging;
using SyncResultModel = IdentitySyncPro.Core.Interfaces.SyncResult;

namespace IdentitySyncPro.Infrastructure.Connectors
{
    /// <summary>
    /// A SCIM 2.0 provisioning target.
    ///
    /// The second directory this system can write to, and the one that stops it being an
    /// Active-Directory product: anything speaking SCIM — Entra, Google Workspace, a local IdP —
    /// becomes reachable without another connector.
    ///
    /// <b>Two things about SCIM shape everything here.</b>
    ///
    /// It is silent about what it does not understand: a server answers 201, returns a resource,
    /// and simply omits attributes it did not recognise. Every write therefore compares the reply
    /// against what was sent and reports the difference, because otherwise a mapping that means
    /// nothing to this server produces a clean-looking success on a half-empty account.
    ///
    /// And it has no notion of place. There are no organisational units, no containers, nothing to
    /// move an account between. The sync engine and the lifecycle rules both issue moves, so those
    /// are refused out loud rather than absorbed into a false success.
    /// </summary>
    public class ScimConnector : ITargetConnector, IDisposable
    {
        private readonly ScimConnectionSettings _settings;
        private readonly ILogger<ScimConnector> _logger;
        private readonly HttpClient _http;
        private readonly bool _ownsClient;

        public string Name => $"SCIM ({_settings.DisplayName})";
        public string Type => "Target";

        public ScimConnector(ScimConnectionSettings settings, ILogger<ScimConnector> logger, HttpClient? http = null)
        {
            _settings = settings;
            _logger = logger;

            if (http != null)
            {
                _http = http;
                _ownsClient = false;
            }
            else
            {
                var handler = new HttpClientHandler();
                if (settings.AllowUntrustedCertificate)
                {
                    // Deliberate and per-tenant, for a SCIM service behind an internal CA. Never a
                    // default: it is switched on in the settings screen beside a warning.
                    handler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                _http = new HttpClient(handler)
                {
                    BaseAddress = new Uri(settings.BaseUrl.TrimEnd('/') + "/"),
                    // The bound that matters. HttpClient.Timeout does apply to the whole
                    // send-and-read, unlike SmtpClient.Timeout, which ignores its async path — and
                    // that difference once cost this system every background job it had.
                    Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 300))
                };
                _ownsClient = true;
            }

            if (!string.IsNullOrWhiteSpace(settings.BearerToken))
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", settings.BearerToken);

            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/scim+json"));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        // ══════════════════════════════════════
        // CONNECTION
        // ══════════════════════════════════════

        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                // One user, not the service configuration: plenty of services omit
                // /ServiceProviderConfig, and a probe that fails on a working target is worse than
                // no probe. This proves the token is accepted and the collection is readable.
                using var response = await _http.GetAsync("Users?count=1", ct);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("SCIM connection test succeeded against {Url}", _settings.BaseUrl);
                    return true;
                }

                _logger.LogError("SCIM connection test failed: {Status} from {Url}",
                    (int)response.StatusCode, _settings.BaseUrl);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SCIM connection test failed against {Url}", _settings.BaseUrl);
                return false;
            }
        }

        public Task<string> GetConnectionInfoAsync(CancellationToken ct = default) =>
            Task.FromResult($"SCIM 2.0 at {_settings.BaseUrl} (timeout {_settings.TimeoutSeconds}s)");

        // ══════════════════════════════════════
        // FINDING
        // ══════════════════════════════════════

        public async Task<bool> ExistsAsync(string identity, CancellationToken ct = default) =>
            await FindIdAsync(identity, ct) != null;

        /// <summary>The SCIM id for a userName, or null when no user carries it.</summary>
        private async Task<string?> FindIdAsync(string userName, CancellationToken ct)
        {
            var resource = await FindUserAsync(userName, ct);
            return resource?["id"]?.ToString();
        }

        private async Task<JsonObject?> FindUserAsync(string userName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;

            var filter = Uri.EscapeDataString($"userName eq \"{ScimPayload.EscapeFilterValue(userName.Trim())}\"");
            using var response = await _http.GetAsync($"Users?filter={filter}", ct);
            if (!response.IsSuccessStatusCode) return null;

            var body = await ReadJsonAsync(response, ct);
            var resources = body?["Resources"]?.AsArray();
            if (resources == null || resources.Count == 0) return null;

            // More than one user for a unique userName is a fault in the target, not a choice to
            // make here — the same reasoning as the AD connector's ambiguous-match refusal.
            if (resources.Count > 1)
            {
                _logger.LogError(
                    "SCIM: {Count} users answer to userName '{UserName}' — refusing to pick one", resources.Count, userName);
                return null;
            }

            return resources[0] as JsonObject;
        }

        public async Task<string?> FindAccountByAttributeAsync(string attributeName, string value, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(attributeName) || string.IsNullOrWhiteSpace(value)) return null;

            try
            {
                var filter = Uri.EscapeDataString(
                    $"{attributeName.Trim()} eq \"{ScimPayload.EscapeFilterValue(value.Trim())}\"");
                using var response = await _http.GetAsync($"Users?filter={filter}", ct);
                if (!response.IsSuccessStatusCode) return null;

                var resources = (await ReadJsonAsync(response, ct))?["Resources"]?.AsArray();
                if (resources == null || resources.Count == 0) return null;

                if (resources.Count > 1)
                {
                    _logger.LogError("SCIM: {Count} users carry {Attribute}={Value} — refusing to pick one",
                        resources.Count, attributeName, value);
                    return null;
                }

                return resources[0]?["userName"]?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SCIM lookup by {Attribute} failed", attributeName);
                return null;
            }
        }

        // ══════════════════════════════════════
        // WRITING
        // ══════════════════════════════════════

        public async Task<SyncResultModel> CreateDynamicAsync(
            string identity, Dictionary<string, string> mappedAttributes,
            string targetOU, IEnumerable<string> groups, string? password, CancellationToken ct = default)
        {
            var started = DateTime.UtcNow;

            try
            {
                var attributes = new Dictionary<string, string>(mappedAttributes);
                if (!string.IsNullOrEmpty(password)) attributes["password"] = password;

                var body = ScimPayload.BuildUser(identity, attributes);

                using var response = await _http.PostAsync("Users", JsonContent(body), ct);
                if (!response.IsSuccessStatusCode)
                    return await FailureAsync(response, $"creating '{identity}'", started, ct);

                var created = await ReadJsonAsync(response, ct);

                // ⛔ The silence. Everything the server did not understand is simply missing from
                // the reply — no error, no warning, a 201 and a resource that looks right.
                var dropped = ScimPayload.AttributesDroppedBy(attributes, created);
                var note = ReportDropped(identity, dropped, "create");

                var addedGroups = groups?.ToList() ?? new List<string>();
                if (addedGroups.Count > 0)
                    await AddToGroupsAsync(identity, addedGroups, ct);

                return new SyncResultModel
                {
                    Success = true,
                    ChangedFields = string.Join(",", attributes.Keys) + note,
                    DurationMs = Elapsed(started)
                };
            }
            catch (Exception ex)
            {
                return Failed(ex, $"creating '{identity}'", started);
            }
        }

        public async Task<SyncResultModel> UpdateDynamicAsync(
            string identity, Dictionary<string, string> mappedAttributes, CancellationToken ct = default)
        {
            var started = DateTime.UtcNow;

            try
            {
                var id = await FindIdAsync(identity, ct);
                if (id == null)
                    return new SyncResultModel
                    {
                        Success = false,
                        Error = $"SCIM: no user answers to '{identity}'",
                        DurationMs = Elapsed(started),
                        FailureKind = SyncFailureKind.Data
                    };

                if (mappedAttributes.Count == 0)
                    return new SyncResultModel { Success = true, DurationMs = Elapsed(started) };

                using var request = new HttpRequestMessage(HttpMethod.Patch, $"Users/{Uri.EscapeDataString(id)}")
                {
                    Content = JsonContent(ScimPayload.BuildPatch(mappedAttributes))
                };
                using var response = await _http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    return await FailureAsync(response, $"updating '{identity}'", started, ct);

                // A PATCH reply is usually the updated resource; when it is a 204 there is nothing
                // to compare, and claiming everything landed would be the very assumption this
                // check exists to avoid — so it is only reported when the body is there to read.
                var note = "";
                if (response.StatusCode != HttpStatusCode.NoContent)
                {
                    var updated = await ReadJsonAsync(response, ct);
                    if (updated != null)
                        note = ReportDropped(identity, ScimPayload.AttributesDroppedBy(mappedAttributes, updated), "update");
                }

                return new SyncResultModel
                {
                    Success = true,
                    ChangedFields = string.Join(",", mappedAttributes.Keys) + note,
                    DurationMs = Elapsed(started)
                };
            }
            catch (Exception ex)
            {
                return Failed(ex, $"updating '{identity}'", started);
            }
        }

        /// <summary>
        /// Names the attributes the target quietly discarded.
        ///
        /// Logged at warning rather than folded into the error, because the write did succeed —
        /// the account exists and carries what the server understood. What must not happen is the
        /// run reporting a clean success while a mapping the operator configured went nowhere.
        /// </summary>
        private string ReportDropped(string identity, IReadOnlyList<string> dropped, string operation)
        {
            if (dropped.Count == 0) return "";

            _logger.LogWarning(
                "SCIM {Operation} of '{Identity}': the target accepted the request but did not store {Count} attribute(s) — {Names}. " +
                "A SCIM service silently ignores paths it does not recognise; check these against its schema.",
                operation, identity, dropped.Count, string.Join(", ", dropped));

            return $" (not stored: {string.Join(", ", dropped)})";
        }

        public async Task<Dictionary<string, string>> GetCurrentAttributesAsync(string identity, CancellationToken ct = default)
        {
            var user = await FindUserAsync(identity, ct);
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (user == null) return flat;

            Flatten(user, "", flat);
            return flat;
        }

        public async Task<Dictionary<string, string>?> GetAttributesAsync(
            string identity, string[] attributes, CancellationToken ct = default)
        {
            var user = await FindUserAsync(identity, ct);
            if (user == null) return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in attributes ?? Array.Empty<string>())
            {
                if (ScimPayload.ReadPath(user, path) is { } value) result[path] = value;
            }

            // The AD connector answers "dn" for the account's location; the SCIM equivalent an
            // operator can act on is the resource id.
            if (user["id"]?.ToString() is { } id) result["id"] = id;
            return result;
        }

        private static void Flatten(JsonNode node, string prefix, IDictionary<string, string> into)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                        if (child != null) Flatten(child, prefix.Length == 0 ? key : $"{prefix}.{key}", into);
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                        if (array[i] is { } child) Flatten(child, $"{prefix}[{i}]", into);
                    break;
                default:
                    into[prefix] = node.ToString();
                    break;
            }
        }

        // ══════════════════════════════════════
        // WHAT SCIM CANNOT DO
        // ══════════════════════════════════════

        /// <summary>
        /// Always false, always logged.
        ///
        /// SCIM has no organisational units — no path, no container, nothing to move between. The
        /// sync engine and the lifecycle rules both issue moves, so this is reached on a SCIM
        /// tenant whose rules were written for a directory that has them. Returning true would let
        /// the run record a placement that never happened, and every report afterwards would agree
        /// with it.
        /// </summary>
        public Task<bool> MoveToOUAsync(string identity, string targetOU, CancellationToken ct = default)
        {
            _logger.LogError(
                "SCIM: '{Identity}' cannot be moved to '{TargetOU}' — SCIM has no organisational units. " +
                "The OU rules on this tenant were written for a directory target and do not apply here.",
                identity, targetOU);
            return Task.FromResult(false);
        }

        /// <summary>Null, for the same reason: there is no container to report.</summary>
        public Task<string?> GetCurrentOUAsync(string identity, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        // ══════════════════════════════════════
        // STATE AND CREDENTIALS
        // ══════════════════════════════════════

        public async Task<bool> DisableAccountAsync(string identity, CancellationToken ct = default)
        {
            // "false" is turned into a JSON boolean by the payload builder — a server handed the
            // string either rejects it or reads a non-empty value and leaves the account enabled.
            var result = await UpdateDynamicAsync(identity, new Dictionary<string, string> { ["active"] = "false" }, ct);
            return result.Success;
        }

        public async Task<(bool Success, string? Error)> ResetPasswordAsync(
            string identity, string newPassword, CancellationToken ct = default)
        {
            var result = await UpdateDynamicAsync(identity, new Dictionary<string, string> { ["password"] = newPassword }, ct);
            return (result.Success, result.Error);
        }

        // ══════════════════════════════════════
        // GROUPS
        // ══════════════════════════════════════

        private async Task<JsonObject?> FindGroupAsync(string displayName, CancellationToken ct)
        {
            var filter = Uri.EscapeDataString(
                $"displayName eq \"{ScimPayload.EscapeFilterValue(displayName.Trim())}\"");
            using var response = await _http.GetAsync($"Groups?filter={filter}", ct);
            if (!response.IsSuccessStatusCode) return null;

            var resources = (await ReadJsonAsync(response, ct))?["Resources"]?.AsArray();
            return resources != null && resources.Count > 0 ? resources[0] as JsonObject : null;
        }

        public async Task<(bool Success, int AddedCount, List<string> GroupNames)> AddToGroupsAsync(
            string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            var names = groupNames?.Where(g => !string.IsNullOrWhiteSpace(g)).ToList() ?? new List<string>();
            if (names.Count == 0) return (true, 0, names);

            var userId = await FindIdAsync(identity, ct);
            if (userId == null)
            {
                _logger.LogWarning("SCIM AddToGroups: no user answers to '{Identity}'", identity);
                return (false, 0, names);
            }

            var added = 0;
            foreach (var name in names)
            {
                if (await PatchMembershipAsync(name, userId, identity, add: true, ct)) added++;
            }

            // The same completeness rule the AD connector applies: a partial result is a failure,
            // because a caller told "success" stops looking.
            return (added == names.Count, added, names);
        }

        public async Task<(bool Success, int RemovedCount, List<string> GroupNames)> RemoveFromSpecificGroupsAsync(
            string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            var names = groupNames?.Where(g => !string.IsNullOrWhiteSpace(g)).ToList() ?? new List<string>();
            if (names.Count == 0) return (true, 0, names);

            var userId = await FindIdAsync(identity, ct);
            if (userId == null)
            {
                _logger.LogWarning("SCIM RemoveFromGroups: no user answers to '{Identity}'", identity);
                return (false, 0, names);
            }

            var removed = 0;
            foreach (var name in names)
            {
                if (await PatchMembershipAsync(name, userId, identity, add: false, ct)) removed++;
            }

            return (removed == names.Count, removed, names);
        }

        private async Task<bool> PatchMembershipAsync(
            string groupName, string userId, string identity, bool add, CancellationToken ct)
        {
            try
            {
                var group = await FindGroupAsync(groupName, ct);
                var groupId = group?["id"]?.ToString();
                if (groupId == null)
                {
                    _logger.LogWarning("SCIM: group '{Group}' was not found — '{Identity}' not {Action}",
                        groupName, identity, add ? "added" : "removed");
                    return false;
                }

                using var request = new HttpRequestMessage(HttpMethod.Patch, $"Groups/{Uri.EscapeDataString(groupId)}")
                {
                    Content = JsonContent(ScimPayload.BuildMemberPatch(userId, add))
                };
                using var response = await _http.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("SCIM: {Action} '{Identity}' {Direction} '{Group}' returned {Status}: {Body}",
                        add ? "adding" : "removing", identity, add ? "to" : "from", groupName,
                        (int)response.StatusCode, await SafeBodyAsync(response, ct));
                    return false;
                }

                _logger.LogInformation("SCIM: '{Identity}' {Action} '{Group}'",
                    identity, add ? "added to" : "removed from", groupName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SCIM membership change failed for '{Identity}' on '{Group}'", identity, groupName);
                return false;
            }
        }

        public async Task<(bool Success, int RemovedCount, List<string> GroupNames)> RemoveFromAllGroupsAsync(
            string identity, CancellationToken ct = default)
        {
            var user = await FindUserAsync(identity, ct);
            var userId = user?["id"]?.ToString();
            if (userId == null) return (false, 0, new List<string>());

            var names = (user?["groups"]?.AsArray() ?? new JsonArray())
                .Select(g => g?["display"]?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList();

            if (names.Count == 0) return (true, 0, names);

            var removed = 0;
            foreach (var name in names)
                if (await PatchMembershipAsync(name, userId, identity, add: false, ct)) removed++;

            return (removed == names.Count, removed, names);
        }

        /// <summary>
        /// Every member of a group, paged.
        ///
        /// SCIM pages with <c>startIndex</c> and <c>itemsPerPage</c>, and a caller that reads only
        /// the first response gets the first page and no indication there was another. For a
        /// certification campaign that is the difference between reviewing a group and reviewing
        /// the beginning of one — so a page that cannot be read fails the whole call rather than
        /// shortening the answer.
        /// </summary>
        public async Task<(bool Success, IReadOnlyList<GroupMember> Members, string? Error)> GetGroupMembersAsync(
            string groupName, bool nested = false, CancellationToken ct = default)
        {
            var members = new List<GroupMember>();

            try
            {
                if (nested)
                {
                    // Said rather than silently flattened: SCIM has no transitive-membership query,
                    // and returning direct members while the caller asked for nested ones would
                    // under-report without a word.
                    _logger.LogWarning(
                        "SCIM: nested membership was requested for '{Group}' — SCIM has no transitive query, so direct members are returned",
                        groupName);
                }

                var group = await FindGroupAsync(groupName, ct);
                var groupId = group?["id"]?.ToString();
                if (groupId == null)
                    return (false, Array.Empty<GroupMember>(), $"Group '{groupName}' was not found.");

                var startIndex = 1;
                const int pageSize = 200;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var filter = Uri.EscapeDataString($"groups eq \"{ScimPayload.EscapeFilterValue(groupId)}\"");
                    using var response = await _http.GetAsync(
                        $"Users?filter={filter}&startIndex={startIndex}&count={pageSize}", ct);

                    if (!response.IsSuccessStatusCode)
                        return (false, Array.Empty<GroupMember>(),
                            $"Reading members of '{groupName}' returned {(int)response.StatusCode}.");

                    var body = await ReadJsonAsync(response, ct);
                    var resources = body?["Resources"]?.AsArray();
                    if (resources == null || resources.Count == 0) break;

                    foreach (var resource in resources)
                    {
                        var account = resource?["userName"]?.ToString();
                        if (string.IsNullOrWhiteSpace(account)) continue;

                        members.Add(new GroupMember(
                            account!,
                            resource?["displayName"]?.ToString(),
                            resource?["id"]?.ToString() ?? account!));
                    }

                    var total = body?["totalResults"]?.GetValue<int>() ?? members.Count;
                    startIndex += resources.Count;
                    if (startIndex > total) break;
                }

                _logger.LogInformation("SCIM: group '{Group}' has {Count} member(s)", groupName, members.Count);
                return (true, members, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SCIM: reading members of '{Group}' failed", groupName);
                return (false, Array.Empty<GroupMember>(), ex.Message);
            }
        }

        // ══════════════════════════════════════
        // MEMBERSHIP QUESTIONS
        // ══════════════════════════════════════

        /// <summary>
        /// Answers <c>true</c> when it cannot tell — matching the AD connector, and correct only
        /// for the exclusion question it was written for. A permission check must use
        /// <see cref="TryIsMemberOfAnyAsync"/>.
        /// </summary>
        public async Task<bool> IsMemberOfAnyAsync(string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            var answer = await TryIsMemberOfAnyAsync(identity, groupNames, ct);
            return answer ?? true;
        }

        public async Task<bool?> TryIsMemberOfAnyAsync(
            string identity, IEnumerable<string> groupNames, CancellationToken ct = default)
        {
            try
            {
                var names = groupNames?.Where(g => !string.IsNullOrWhiteSpace(g)).ToList() ?? new List<string>();
                if (names.Count == 0) return false;

                var user = await FindUserAsync(identity, ct);
                if (user == null) return false;

                var held = (user["groups"]?.AsArray() ?? new JsonArray())
                    .Select(g => g?["display"]?.ToString())
                    .Where(n => n != null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return names.Any(n => held.Contains(n.Trim()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SCIM: could not determine membership for '{Identity}'", identity);
                return null;
            }
        }

        // ══════════════════════════════════════
        // PLUMBING
        // ══════════════════════════════════════

        private static StringContent JsonContent(JsonNode body) =>
            new(body.ToJsonString(), Encoding.UTF8, "application/scim+json");

        private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                return text.Length > 500 ? text[..500] : text;
            }
            catch
            {
                return "(no body)";
            }
        }

        /// <summary>
        /// A failed response, classified.
        ///
        /// The distinction matters to the circuit breaker: 4xx is this record — a bad value, a
        /// duplicate, a schema the mapping does not match — and must not open the breaker on a
        /// healthy service. 5xx and a refused connection are the service.
        /// </summary>
        private async Task<SyncResultModel> FailureAsync(
            HttpResponseMessage response, string what, DateTime started, CancellationToken ct)
        {
            var status = (int)response.StatusCode;
            var body = await SafeBodyAsync(response, ct);

            _logger.LogError("SCIM {What} returned {Status}: {Body}", what, status, body);

            return new SyncResultModel
            {
                Success = false,
                Error = $"SCIM {what} returned {status}: {body}",
                DurationMs = Elapsed(started),
                FailureKind = status is >= 400 and < 500 ? SyncFailureKind.Data : SyncFailureKind.Unknown
            };
        }

        private SyncResultModel Failed(Exception ex, string what, DateTime started)
        {
            _logger.LogError(ex, "SCIM {What} failed", what);
            return new SyncResultModel
            {
                Success = false,
                Error = ex.Message,
                DurationMs = Elapsed(started),
                FailureKind = SyncFailureKind.Unknown
            };
        }

        private static int Elapsed(DateTime started) => (int)(DateTime.UtcNow - started).TotalMilliseconds;

        public void Dispose()
        {
            if (_ownsClient) _http.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
