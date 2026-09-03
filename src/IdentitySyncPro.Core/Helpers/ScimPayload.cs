using System.Text.Json;
using System.Text.Json.Nodes;

namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Building and reading SCIM 2.0 bodies.
    ///
    /// Kept apart from the HTTP client so the part that decides what is sent can be tested without
    /// a server — and that is the part that fails quietly. <b>A SCIM server ignores attributes it
    /// does not recognise.</b> It answers 201 Created, returns a resource, and simply leaves out
    /// what it did not understand. A mapping typed as an Active Directory attribute name goes to a
    /// SCIM service, is dropped in silence, and the sync reports success on an account that is
    /// missing half its data.
    ///
    /// So this builds dotted paths into real nested JSON, and offers
    /// <see cref="AttributesDroppedBy"/> to compare what came back against what was sent.
    /// </summary>
    public static class ScimPayload
    {
        public const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
        public const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
        public const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

        /// <summary>
        /// Turns flat mapped attributes into a SCIM user resource.
        ///
        /// The mapping screen writes the target attribute name, and for a SCIM tenant that name is
        /// the SCIM path: <c>name.givenName</c>, <c>emails[0].value</c>, <c>active</c>. No
        /// translation table converts Active Directory names on the way — a hidden conversion would
        /// mean the value that arrives is not the one the mapping shows, and the screen would stop
        /// being the truth about what is sent.
        /// </summary>
        public static JsonObject BuildUser(string userName, IReadOnlyDictionary<string, string> attributes)
        {
            var root = new JsonObject
            {
                ["schemas"] = new JsonArray(UserSchema),
                ["userName"] = userName
            };

            foreach (var (path, value) in attributes)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                SetPath(root, path.Trim(), value);
            }

            return root;
        }

        /// <summary>
        /// Writes a dotted (and optionally indexed) path into the object, creating what it needs.
        ///
        /// <c>emails[0].value</c> becomes an array holding an object, not a property literally named
        /// "emails[0].value" — which is what a flat assignment would produce, and which every SCIM
        /// server would accept and ignore.
        /// </summary>
        public static void SetPath(JsonObject root, string path, string value)
        {
            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            JsonObject current = root;

            for (var i = 0; i < segments.Length; i++)
            {
                var (name, index) = SplitIndex(segments[i]);
                var last = i == segments.Length - 1;

                if (index == null)
                {
                    if (last) { current[name] = Typed(value); return; }
                    if (current[name] is not JsonObject next) current[name] = next = new JsonObject();
                    current = next;
                    continue;
                }

                if (current[name] is not JsonArray array) current[name] = array = new JsonArray();
                while (array.Count <= index.Value) array.Add(new JsonObject());

                if (last)
                {
                    // A bare indexed leaf ("members[0]") means the element itself, not a property on it.
                    array[index.Value] = Typed(value);
                    return;
                }

                if (array[index.Value] is not JsonObject element)
                {
                    element = new JsonObject();
                    array[index.Value] = element;
                }
                current = element;
            }
        }

        /// <summary>
        /// SCIM is typed where JSON is typed. <c>active</c> is a boolean in the schema, and a server
        /// given the string "false" either rejects it or — worse — reads it as a non-empty value and
        /// leaves the account enabled.
        /// </summary>
        private static JsonNode? Typed(string value)
        {
            if (bool.TryParse(value, out var b)) return JsonValue.Create(b);
            return JsonValue.Create(value);
        }

        private static (string Name, int? Index) SplitIndex(string segment)
        {
            var open = segment.IndexOf('[');
            if (open <= 0 || !segment.EndsWith("]")) return (segment, null);

            var inner = segment[(open + 1)..^1];
            return int.TryParse(inner, out var i) && i >= 0
                ? (segment[..open], i)
                : (segment, null);
        }

        /// <summary>A PATCH body replacing one path — the shape every SCIM 2.0 server accepts.</summary>
        public static JsonObject BuildPatch(IReadOnlyDictionary<string, string> attributes)
        {
            var operations = new JsonArray();
            foreach (var (path, value) in attributes)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                operations.Add(new JsonObject
                {
                    ["op"] = "replace",
                    ["path"] = path.Trim(),
                    ["value"] = Typed(value)
                });
            }

            return new JsonObject { ["schemas"] = new JsonArray(PatchSchema), ["Operations"] = operations };
        }

        /// <summary>A PATCH adding or removing one member on a group.</summary>
        public static JsonObject BuildMemberPatch(string memberId, bool add) =>
            new()
            {
                ["schemas"] = new JsonArray(PatchSchema),
                ["Operations"] = new JsonArray(new JsonObject
                {
                    ["op"] = add ? "add" : "remove",
                    ["path"] = add ? "members" : $"members[value eq \"{memberId}\"]",
                    ["value"] = add
                        ? new JsonArray(new JsonObject { ["value"] = memberId })
                        : null
                })
            };

        /// <summary>
        /// The attributes that were sent and did not come back.
        ///
        /// This is the guard against SCIM's defining silence. The server answers 201, the resource
        /// looks fine, and an attribute it did not recognise is simply absent from the reply — no
        /// error anywhere. Comparing the two is the only way to notice, and a caller that reports
        /// this is the difference between "created" and "created, missing four fields".
        /// </summary>
        public static IReadOnlyList<string> AttributesDroppedBy(
            IReadOnlyDictionary<string, string> sent, JsonNode? returned)
        {
            if (returned is not JsonObject resource)
                return sent.Keys.Where(k => !NeverReturned(k)).ToList();

            var dropped = new List<string>();
            foreach (var path in sent.Keys)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (NeverReturned(path)) continue;
                if (ReadPath(resource, path.Trim()) == null) dropped.Add(path);
            }
            return dropped;
        }

        /// <summary>
        /// Attributes a SCIM service is never expected to echo, whatever it did with them.
        ///
        /// A password is write-only by design — RFC 7643 marks it so, and no service returns it.
        /// Comparing it against the reply therefore reported it as discarded on every single
        /// create, and a warning that fires every time is one an operator learns to scroll past.
        /// The check is only worth having while everything it names is worth reading.
        ///
        /// Found by running the connector against a real service rather than a stub built to agree
        /// with it.
        /// </summary>
        public static bool NeverReturned(string path) =>
            path.Trim().Equals("password", StringComparison.OrdinalIgnoreCase);

        /// <summary>Reads a dotted/indexed path back out, or null when the server did not return it.</summary>
        public static string? ReadPath(JsonNode? node, string path)
        {
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node == null) return null;
                var (name, index) = SplitIndex(segment);

                node = node is JsonObject obj ? obj[name] : null;
                if (index != null) node = node is JsonArray arr && index < arr.Count ? arr[index.Value] : null;
            }

            return node switch
            {
                null => null,
                JsonValue value => value.ToString(),
                _ => node.ToJsonString()
            };
        }

        /// <summary>
        /// Escapes a value for a SCIM filter, which is a query language and injectable like any
        /// other: a quote in an account name would otherwise end the literal and change the filter.
        /// </summary>
        public static string EscapeFilterValue(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        public static JsonSerializerOptions SerializerOptions { get; } = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }
}
