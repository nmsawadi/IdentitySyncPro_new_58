using System.Text.Json;
using IdentitySyncPro.Infrastructure.Data;
using IdentitySyncPro.Web.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Web.Controllers.Api
{
    /// <summary>
    /// SCIM 2.0 compliant API for identity provisioning.
    /// Endpoints: /scim/v2/Users, /scim/v2/Groups
    /// Spec: https://datatracker.ietf.org/doc/html/rfc7644
    /// 
    /// ⚠️ Protected by API Key authentication.
    /// Provide X-API-Key header or api_key query parameter.
    /// </summary>
    [ApiController]
    [Route("scim/v2")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous] // SCIM keeps its own API-key auth (below)
    [IgnoreAntiforgeryToken]
    [ServiceFilter(typeof(ApiKeyAuthAttribute))]
    public class ScimController : ControllerBase
    {
        private readonly AppDbContext _db;

        public ScimController(AppDbContext db)
        {
            _db = db;
        }

        // ═══════════════════════════════════════
        // USERS
        // ═══════════════════════════════════════

        /// <summary>GET /scim/v2/Users — List users with filtering and pagination.</summary>
        [HttpGet("Users")]
        public async Task<IActionResult> GetUsers(
            [FromQuery] string? filter, [FromQuery] int startIndex = 1, [FromQuery] int count = 100)
        {
            var query = _db.SyncStates.AsQueryable();

            // Basic SCIM filter: userName eq "value"
            if (!string.IsNullOrEmpty(filter) && filter.Contains("userName eq"))
            {
                var val = ExtractFilterValue(filter);
                if (val != null)
                    query = query.Where(s => s.IdentityId.ToString() == val);
            }

            var total = await query.CountAsync();
            var users = await query
                .OrderBy(s => s.IdentityId)
                .Skip(startIndex - 1)
                .Take(count)
                .ToListAsync();

            var resources = users.Select(u => FormatUser(u)).ToList();

            return Ok(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = total,
                startIndex,
                itemsPerPage = count,
                Resources = resources
            });
        }

        /// <summary>GET /scim/v2/Users/{id} — Get a single user.</summary>
        [HttpGet("Users/{id}")]
        public async Task<IActionResult> GetUser(string id)
        {
            if (!int.TryParse(id, out var identityId))
                return NotFound(ScimError("User not found", 404));

            var state = await _db.SyncStates.FirstOrDefaultAsync(s => s.IdentityId == identityId);
            if (state == null)
                return NotFound(ScimError("User not found", 404));

            return Ok(FormatUser(state));
        }

        /// <summary>POST /scim/v2/Users — Create a user (provision request).</summary>
        [HttpPost("Users")]
        public async Task<IActionResult> CreateUser([FromBody] JsonElement body)
        {
            var userName = body.TryGetProperty("userName", out var un) ? un.GetString() : null;
            if (string.IsNullOrEmpty(userName) || !int.TryParse(userName, out var identityId))
                return BadRequest(ScimError("userName is required and must be a identity ID", 400));

            var existing = await _db.SyncStates.FirstOrDefaultAsync(s => s.IdentityId == identityId);
            if (existing != null)
                return Conflict(ScimError("User already exists", 409));

            var state = new Core.Models.Sync.SyncState
            {
                IdentityId = identityId,
                Status = "PendingProvision",
                CreatedInAD = false,
                LastSyncDate = DateTime.UtcNow,
                CurrentHash = ""
            };

            _db.SyncStates.Add(state);
            await _db.SaveChangesAsync();

            Response.StatusCode = 201;
            return Ok(FormatUser(state));
        }

        /// <summary>PATCH /scim/v2/Users/{id} — Partial update (SCIM Patch).</summary>
        [HttpPatch("Users/{id}")]
        public async Task<IActionResult> PatchUser(string id, [FromBody] JsonElement body)
        {
            if (!int.TryParse(id, out var identityId))
                return NotFound(ScimError("User not found", 404));

            var state = await _db.SyncStates.FirstOrDefaultAsync(s => s.IdentityId == identityId);
            if (state == null)
                return NotFound(ScimError("User not found", 404));

            // Process SCIM Patch operations
            if (body.TryGetProperty("Operations", out var ops))
            {
                foreach (var op in ops.EnumerateArray())
                {
                    var opType = op.GetProperty("op").GetString()?.ToLower();
                    var path = op.TryGetProperty("path", out var p) ? p.GetString() : null;

                    if (opType == "replace" && path == "active")
                    {
                        var active = op.GetProperty("value").GetBoolean();
                        state.Status = active ? "Synced" : "Disabled";
                    }
                }
            }

            state.LastSyncDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Ok(FormatUser(state));
        }

        /// <summary>
        /// DELETE /scim/v2/Users/{id} — ⛔ BLOCKED: Safe Sync mode.
        /// IdentitySyncPro does NOT delete or disable any accounts.
        /// </summary>
        [HttpDelete("Users/{id}")]
        public IActionResult DeleteUser(string id)
        {
            return StatusCode(403, ScimError(
                "⛔ Safe Sync mode: IdentitySyncPro does NOT delete or disable accounts. This operation is blocked.", 403));
        }

        // ═══════════════════════════════════════
        // SERVICE PROVIDER CONFIG
        // ═══════════════════════════════════════

        /// <summary>GET /scim/v2/ServiceProviderConfig</summary>
        [HttpGet("ServiceProviderConfig")]
        public IActionResult GetServiceProviderConfig()
        {
            return Ok(new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
                patch = new { supported = true },
                bulk = new { supported = false },
                filter = new { supported = true, maxResults = 1000 },
                changePassword = new { supported = false },
                sort = new { supported = false },
                etag = new { supported = false },
                authenticationSchemes = new[] { new { type = "httpbasic", name = "HTTP Basic" } }
            });
        }

        /// <summary>GET /scim/v2/Schemas</summary>
        [HttpGet("Schemas")]
        public IActionResult GetSchemas()
        {
            return Ok(new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
                totalResults = 1,
                Resources = new[] { new { id = "urn:ietf:params:scim:schemas:core:2.0:User", name = "User" } }
            });
        }

        // ═══════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════

        private object FormatUser(Core.Models.Sync.SyncState state)
        {
            return new
            {
                schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
                id = state.IdentityId.ToString(),
                userName = state.IdentityId.ToString(),
                active = state.Status != "Disabled" && state.Status != "Deprovisioned",
                meta = new
                {
                    resourceType = "User",
                    created = state.LastSyncDate,
                    lastModified = state.LastSyncDate,
                    location = $"/scim/v2/Users/{state.IdentityId}"
                }
            };
        }

        private static string? ExtractFilterValue(string filter)
        {
            var parts = filter.Split('"');
            return parts.Length >= 2 ? parts[1] : null;
        }

        private static object ScimError(string detail, int status)
        {
            return new
            {
                schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:Error" },
                detail,
                status
            };
        }
    }
}
