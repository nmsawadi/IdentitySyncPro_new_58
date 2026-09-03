using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Infrastructure.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the SCIM target against a stub service.
    ///
    /// The behaviours worth pinning are the ones a real server would let pass in silence: a body
    /// built flat instead of nested, attributes the target discarded, a member list that stopped at
    /// the first page, and a move instruction that has no meaning here at all. Each of those
    /// produces a successful-looking run and a wrong directory.
    /// </summary>
    public class ScimConnectorTests
    {
        // ══════════════════════════════════════
        // A STUB SCIM SERVICE
        // ══════════════════════════════════════

        private sealed class StubScim : HttpMessageHandler
        {
            public List<(string Method, string Path, string? Body)> Calls = new();
            public Func<string, string, string?, HttpResponseMessage>? Respond;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
                var path = request.RequestUri!.PathAndQuery;
                Calls.Add((request.Method.Method, path, body));

                return Respond?.Invoke(request.Method.Method, path, body)
                       ?? Json(HttpStatusCode.OK, """{"Resources":[],"totalResults":0}""");
            }

            public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
                new(status) { Content = new StringContent(json, Encoding.UTF8, "application/scim+json") };
        }

        private static (ScimConnector Connector, StubScim Stub) Build(
            Func<string, string, string?, HttpResponseMessage>? respond = null)
        {
            var stub = new StubScim { Respond = respond };
            var http = new HttpClient(stub) { BaseAddress = new Uri("https://idp.example.edu/scim/v2/") };

            var connector = new ScimConnector(
                new ScimConnectionSettings { BaseUrl = "https://idp.example.edu/scim/v2", TimeoutSeconds = 30 },
                NullLogger<ScimConnector>.Instance, http);

            return (connector, stub);
        }

        private const string OneUser = """
            {"Resources":[{"id":"u1","userName":"ahmed.s","displayName":"أحمد"}],"totalResults":1}
            """;

        // ══════════════════════════════════════
        // CREATING
        // ══════════════════════════════════════

        [Fact]
        public async Task CreatingAUser_SendsNestedJsonNotFlatKeys()
        {
            var (connector, stub) = Build((method, path, _) =>
                method == "POST"
                    ? StubScim.Json(HttpStatusCode.Created,
                        """{"id":"u1","userName":"ahmed.s","name":{"givenName":"أحمد"},"title":"مهندس"}""")
                    : StubScim.Json(HttpStatusCode.OK, """{"Resources":[],"totalResults":0}"""));

            var result = await connector.CreateDynamicAsync("ahmed.s",
                new Dictionary<string, string> { ["name.givenName"] = "أحمد", ["title"] = "مهندس" },
                targetOU: "", groups: Array.Empty<string>(), password: null);

            Assert.True(result.Success, result.Error);

            var post = stub.Calls.Single(c => c.Method == "POST");
            var sent = JsonNode.Parse(post.Body!)!;
            Assert.Equal("أحمد", sent["name"]!["givenName"]!.ToString());
            Assert.Null(sent["name.givenName"]);
        }

        /// <summary>
        /// The silence, end to end. The service answers 201 with a resource that looks right and
        /// simply omits what it did not understand — and without this the run reports a clean
        /// creation of an account missing the attribute an operator configured.
        /// </summary>
        [Fact]
        public async Task WhenTheTargetDiscardsAnAttribute_TheResultSaysSo()
        {
            var (connector, _) = Build((method, _, _) =>
                method == "POST"
                    ? StubScim.Json(HttpStatusCode.Created, """{"id":"u1","userName":"ahmed.s","title":"مهندس"}""")
                    : StubScim.Json(HttpStatusCode.OK, """{"Resources":[],"totalResults":0}"""));

            var result = await connector.CreateDynamicAsync("ahmed.s",
                new Dictionary<string, string> { ["title"] = "مهندس", ["extensionAttribute2"] = "440000001" },
                targetOU: "", groups: Array.Empty<string>(), password: null);

            // The write did succeed — the account exists — but the report is not clean.
            Assert.True(result.Success);
            Assert.Contains("not stored", result.ChangedFields!);
            Assert.Contains("extensionAttribute2", result.ChangedFields!);
        }

        [Fact]
        public async Task WhenNothingIsDiscarded_TheResultIsQuiet()
        {
            var (connector, _) = Build((method, _, _) =>
                method == "POST"
                    ? StubScim.Json(HttpStatusCode.Created, """{"id":"u1","userName":"ahmed.s","title":"مهندس"}""")
                    : StubScim.Json(HttpStatusCode.OK, """{"Resources":[],"totalResults":0}"""));

            var result = await connector.CreateDynamicAsync("ahmed.s",
                new Dictionary<string, string> { ["title"] = "مهندس" },
                targetOU: "", groups: Array.Empty<string>(), password: null);

            Assert.DoesNotContain("not stored", result.ChangedFields ?? "");
        }

        /// <summary>
        /// A rejected record must not open the circuit breaker on a service that is answering
        /// perfectly well — the fault is in this row, not in the target.
        /// </summary>
        [Theory]
        [InlineData(HttpStatusCode.BadRequest, SyncFailureKind.Data)]
        [InlineData(HttpStatusCode.Conflict, SyncFailureKind.Data)]
        [InlineData(HttpStatusCode.InternalServerError, SyncFailureKind.Unknown)]
        [InlineData(HttpStatusCode.ServiceUnavailable, SyncFailureKind.Unknown)]
        public async Task AFailedWriteIsClassifiedByWhoseFaultItIs(HttpStatusCode status, SyncFailureKind expected)
        {
            var (connector, _) = Build((method, _, _) =>
                method == "POST" ? StubScim.Json(status, """{"detail":"nope"}""")
                                 : StubScim.Json(HttpStatusCode.OK, """{"Resources":[],"totalResults":0}"""));

            var result = await connector.CreateDynamicAsync("ahmed.s", new Dictionary<string, string>(),
                targetOU: "", groups: Array.Empty<string>(), password: null);

            Assert.False(result.Success);
            Assert.Equal(expected, result.FailureKind);
        }

        // ══════════════════════════════════════
        // WHAT SCIM CANNOT DO
        // ══════════════════════════════════════

        /// <summary>
        /// SCIM has no containers. Answering true would let a run record a placement that never
        /// happened, and every report afterwards would agree with it.
        /// </summary>
        [Fact]
        public async Task MovingBetweenOrganisationalUnits_IsRefusedNotFaked()
        {
            var (connector, stub) = Build();

            Assert.False(await connector.MoveToOUAsync("ahmed.s", "OU=Staff,DC=x"));
            Assert.Null(await connector.GetCurrentOUAsync("ahmed.s"));
            Assert.Empty(stub.Calls);   // and it does not pretend by calling anything
        }

        // ══════════════════════════════════════
        // FINDING
        // ══════════════════════════════════════

        [Fact]
        public async Task AUserIsFoundByAFilterOnItsName()
        {
            var (connector, stub) = Build((_, path, _) =>
                path.Contains("Users?filter=") ? StubScim.Json(HttpStatusCode.OK, OneUser)
                                               : StubScim.Json(HttpStatusCode.OK, """{"Resources":[]}"""));

            Assert.True(await connector.ExistsAsync("ahmed.s"));
            Assert.Contains(stub.Calls, c => c.Path.Contains("userName%20eq"));
        }

        /// <summary>
        /// Two users answering to one userName is a fault in the target. Picking one would write a
        /// second person's identity onto the first person's account on every sync from then on.
        /// </summary>
        [Fact]
        public async Task AnAmbiguousUserName_IsRefusedRatherThanGuessed()
        {
            var (connector, _) = Build((_, _, _) => StubScim.Json(HttpStatusCode.OK, """
                {"Resources":[{"id":"u1","userName":"ahmed.s"},{"id":"u2","userName":"ahmed.s"}],"totalResults":2}
                """));

            Assert.False(await connector.ExistsAsync("ahmed.s"));
        }

        /// <summary>
        /// A SCIM filter is a query language. The invariant is not that the injected words vanish —
        /// they stay, as data inside the literal — but that the only unescaped quotes in the filter
        /// are the two delimiting it. One more would end the literal early and turn the rest of the
        /// account name into filter syntax.
        /// </summary>
        [Fact]
        public async Task AFilterValueCannotBreakOutOfTheQuery()
        {
            var (connector, stub) = Build();
            await connector.ExistsAsync("ahmed\" or userName pr");

            var query = Uri.UnescapeDataString(stub.Calls.Single().Path);
            var value = query[(query.IndexOf("filter=", StringComparison.Ordinal) + 7)..];

            var unescapedQuotes = 0;
            for (var i = 0; i < value.Length; i++)
                if (value[i] == '"' && (i == 0 || value[i - 1] != '\\')) unescapedQuotes++;

            Assert.Equal(2, unescapedQuotes);
            Assert.Contains("\\\"", value);   // the injected quote survived as escaped data
        }

        // ══════════════════════════════════════
        // GROUPS
        // ══════════════════════════════════════

        [Fact]
        public async Task AddingToAGroup_PatchesTheGroupWithTheMember()
        {
            var (connector, stub) = Build((method, path, _) =>
                method == "GET" && path.Contains("Users?filter") ? StubScim.Json(HttpStatusCode.OK, OneUser)
                : method == "GET" && path.Contains("Groups?filter")
                    ? StubScim.Json(HttpStatusCode.OK, """{"Resources":[{"id":"g1","displayName":"DB-Admin"}]}""")
                : StubScim.Json(HttpStatusCode.OK, "{}"));

            var (success, added, _) = await connector.AddToGroupsAsync("ahmed.s", new[] { "DB-Admin" });

            Assert.True(success);
            Assert.Equal(1, added);

            var patch = stub.Calls.Single(c => c.Method == "PATCH");
            Assert.Contains("Groups/g1", patch.Path);
            Assert.Contains("u1", patch.Body!);
        }

        /// <summary>A group that does not exist is a failure, not a quiet no-op on a "successful" run.</summary>
        [Fact]
        public async Task AMissingGroup_IsAFailure()
        {
            var (connector, _) = Build((method, path, _) =>
                method == "GET" && path.Contains("Users?filter") ? StubScim.Json(HttpStatusCode.OK, OneUser)
                : StubScim.Json(HttpStatusCode.OK, """{"Resources":[]}"""));

            var (success, added, _) = await connector.AddToGroupsAsync("ahmed.s", new[] { "Nope" });

            Assert.False(success);
            Assert.Equal(0, added);
        }

        /// <summary>Partial is not success — a caller told "success" stops looking.</summary>
        [Fact]
        public async Task AddingToTwoGroupsWhenOnlyOneExists_IsNotSuccess()
        {
            var (connector, _) = Build((method, path, _) =>
                method == "GET" && path.Contains("Users?filter") ? StubScim.Json(HttpStatusCode.OK, OneUser)
                : method == "GET" && path.Contains("displayName%20eq%20%22DB-Admin%22")
                    ? StubScim.Json(HttpStatusCode.OK, """{"Resources":[{"id":"g1"}]}""")
                : method == "GET" ? StubScim.Json(HttpStatusCode.OK, """{"Resources":[]}""")
                : StubScim.Json(HttpStatusCode.OK, "{}"));

            var (success, added, _) = await connector.AddToGroupsAsync("ahmed.s", new[] { "DB-Admin", "Missing" });

            Assert.False(success);
            Assert.Equal(1, added);
        }

        // ══════════════════════════════════════
        // PAGED MEMBERSHIP
        // ══════════════════════════════════════

        /// <summary>
        /// A caller that reads only the first response gets the first page and no sign there was
        /// another. For a certification campaign that is the difference between reviewing a group
        /// and reviewing the beginning of one.
        /// </summary>
        [Fact]
        public async Task ReadingAGroupFollowsEveryPage()
        {
            var (connector, _) = Build((method, path, _) =>
            {
                if (path.Contains("Groups?filter"))
                    return StubScim.Json(HttpStatusCode.OK, """{"Resources":[{"id":"g1"}]}""");

                if (path.Contains("startIndex=1"))
                    return StubScim.Json(HttpStatusCode.OK, """
                        {"totalResults":3,"Resources":[
                          {"id":"u1","userName":"a1","displayName":"A"},
                          {"id":"u2","userName":"a2","displayName":"B"}]}
                        """);

                return StubScim.Json(HttpStatusCode.OK, """
                    {"totalResults":3,"Resources":[{"id":"u3","userName":"a3","displayName":"C"}]}
                    """);
            });

            var (success, members, error) = await connector.GetGroupMembersAsync("DB-Admin");

            Assert.True(success, error);
            Assert.Equal(3, members.Count);
            Assert.Equal(new[] { "a1", "a2", "a3" }, members.Select(m => m.Account));
        }

        /// <summary>A page that cannot be read fails the call — never returns a shorter answer.</summary>
        [Fact]
        public async Task APageThatFails_FailsTheWholeRead()
        {
            var (connector, _) = Build((method, path, _) =>
            {
                if (path.Contains("Groups?filter"))
                    return StubScim.Json(HttpStatusCode.OK, """{"Resources":[{"id":"g1"}]}""");
                if (path.Contains("startIndex=1"))
                    return StubScim.Json(HttpStatusCode.OK, """
                        {"totalResults":3,"Resources":[{"id":"u1","userName":"a1"},{"id":"u2","userName":"a2"}]}
                        """);
                return StubScim.Json(HttpStatusCode.InternalServerError, "{}");
            });

            var (success, members, error) = await connector.GetGroupMembersAsync("DB-Admin");

            Assert.False(success);
            Assert.Empty(members);
            Assert.NotNull(error);
        }

        [Fact]
        public async Task AGroupThatDoesNotExist_IsAFailureNotAnEmptyGroup()
        {
            var (connector, _) = Build((_, _, _) => StubScim.Json(HttpStatusCode.OK, """{"Resources":[]}"""));

            var (success, members, error) = await connector.GetGroupMembersAsync("Nope");

            Assert.False(success);
            Assert.Empty(members);
            Assert.Contains("not found", error!);
        }

        // ══════════════════════════════════════
        // MEMBERSHIP QUESTIONS
        // ══════════════════════════════════════

        [Fact]
        public async Task MembershipIsReadFromTheUsersOwnGroups()
        {
            var (connector, _) = Build((_, _, _) => StubScim.Json(HttpStatusCode.OK, """
                {"Resources":[{"id":"u1","userName":"ahmed.s","groups":[{"display":"DB-Admin"}]}],"totalResults":1}
                """));

            Assert.True(await connector.TryIsMemberOfAnyAsync("ahmed.s", new[] { "DB-Admin" }));
            Assert.False(await connector.TryIsMemberOfAnyAsync("ahmed.s", new[] { "Other" }));
        }

        /// <summary>
        /// The three-valued answer the governance module depends on: asked "is this person an
        /// approver?", an optimistic guess during an outage would grant the right to whoever asked.
        /// </summary>
        [Fact]
        public async Task WhenTheServiceCannotBeReached_MembershipIsUnknownNotFalse()
        {
            var (connector, _) = Build((_, _, _) => throw new HttpRequestException("connection refused"));

            Assert.Null(await connector.TryIsMemberOfAnyAsync("ahmed.s", new[] { "DB-Admin" }));

            // And the exclusion-shaped question still errs towards denial, as its AD twin does.
            Assert.True(await connector.IsMemberOfAnyAsync("ahmed.s", new[] { "DB-Admin" }));
        }

        // ══════════════════════════════════════
        // STATE
        // ══════════════════════════════════════

        /// <summary>
        /// A server handed the string "false" either rejects it or reads a non-empty value and
        /// leaves the account enabled — a disable that reports success and disables nothing.
        /// </summary>
        [Fact]
        public async Task DisablingSendsABooleanNotTheWordFalse()
        {
            var (connector, stub) = Build((method, path, _) =>
                method == "GET" ? StubScim.Json(HttpStatusCode.OK, OneUser)
                                : StubScim.Json(HttpStatusCode.OK, """{"id":"u1","active":false}"""));

            Assert.True(await connector.DisableAccountAsync("ahmed.s"));

            var patch = stub.Calls.Single(c => c.Method == "PATCH");
            var operation = JsonNode.Parse(patch.Body!)!["Operations"]!.AsArray()[0]!;
            Assert.False(operation["value"]!.GetValue<bool>());
        }

        [Fact]
        public async Task UpdatingAnAccountThatDoesNotExist_IsADataFault()
        {
            var (connector, _) = Build((_, _, _) => StubScim.Json(HttpStatusCode.OK, """{"Resources":[]}"""));

            var result = await connector.UpdateDynamicAsync("ghost", new Dictionary<string, string> { ["title"] = "x" });

            Assert.False(result.Success);
            Assert.Equal(SyncFailureKind.Data, result.FailureKind);
        }

        /// <summary>
        /// A 204 carries no body, so there is nothing to compare — and claiming everything landed
        /// would be exactly the assumption the comparison exists to avoid.
        /// </summary>
        [Fact]
        public async Task AnEmptyPatchReply_ClaimsNothingAboutWhatWasStored()
        {
            var (connector, _) = Build((method, _, _) =>
                method == "GET" ? StubScim.Json(HttpStatusCode.OK, OneUser)
                                : new HttpResponseMessage(HttpStatusCode.NoContent));

            var result = await connector.UpdateDynamicAsync("ahmed.s",
                new Dictionary<string, string> { ["title"] = "مهندس" });

            Assert.True(result.Success);
            Assert.DoesNotContain("not stored", result.ChangedFields ?? "");
        }

        [Fact]
        public async Task ReadingAttributes_ReturnsThePathsThatWereAskedFor()
        {
            var (connector, _) = Build((_, _, _) => StubScim.Json(HttpStatusCode.OK, """
                {"Resources":[{"id":"u1","userName":"ahmed.s","name":{"givenName":"أحمد"}}],"totalResults":1}
                """));

            var attributes = await connector.GetAttributesAsync("ahmed.s", new[] { "name.givenName", "title" });

            Assert.NotNull(attributes);
            Assert.Equal("أحمد", attributes!["name.givenName"]);
            Assert.False(attributes.ContainsKey("title"));   // absent, not blank
            Assert.Equal("u1", attributes["id"]);
        }

        [Fact]
        public async Task AConnectionTestProvesTheTokenIsAccepted()
        {
            var (ok, _) = Build((_, _, _) => StubScim.Json(HttpStatusCode.OK, """{"Resources":[],"totalResults":0}"""));
            Assert.True(await ok.TestConnectionAsync());

            var (denied, _) = Build((_, _, _) => StubScim.Json(HttpStatusCode.Unauthorized, "{}"));
            Assert.False(await denied.TestConnectionAsync());
        }
    }
}
