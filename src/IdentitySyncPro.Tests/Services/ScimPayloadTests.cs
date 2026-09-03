using System.Text.Json.Nodes;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.Settings;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards what this system sends to a SCIM service, and what it concludes from the answer.
    ///
    /// SCIM's defining behaviour is silence: a server ignores attributes it does not recognise. It
    /// answers 201 Created, returns a resource, and simply leaves out what it did not understand.
    /// A path built wrongly — flat instead of nested, a literal "emails[0].value" property — is
    /// accepted and dropped, and the sync reports success on an account missing half its data.
    ///
    /// Nothing here throws when it breaks. That is the whole point of testing it.
    /// </summary>
    public class ScimPayloadTests
    {
        private static readonly Dictionary<string, string> NoAttributes = new();

        // ══════════════════════════════════════
        // BUILDING A USER
        // ══════════════════════════════════════

        [Fact]
        public void AUserCarriesItsSchemaAndName()
        {
            var user = ScimPayload.BuildUser("ahmed.s", NoAttributes);

            Assert.Equal("ahmed.s", user["userName"]!.ToString());
            Assert.Equal(ScimPayload.UserSchema, user["schemas"]!.AsArray()[0]!.ToString());
        }

        /// <summary>
        /// The failure that looks like success. Written flat, the body carries a property literally
        /// named "name.givenName" — which every SCIM server accepts and every SCIM server ignores.
        /// </summary>
        [Fact]
        public void ADottedPath_BecomesNestedJsonNotAFlatKey()
        {
            var user = ScimPayload.BuildUser("ahmed.s", new Dictionary<string, string>
            {
                ["name.givenName"] = "أحمد",
                ["name.familyName"] = "السوادي"
            });

            Assert.Null(user["name.givenName"]);                       // not the flat key
            Assert.Equal("أحمد", user["name"]!["givenName"]!.ToString());
            Assert.Equal("السوادي", user["name"]!["familyName"]!.ToString());
        }

        [Fact]
        public void AnIndexedPath_BecomesAnArrayOfObjects()
        {
            var user = ScimPayload.BuildUser("ahmed.s", new Dictionary<string, string>
            {
                ["emails[0].value"] = "a@x.sa",
                ["emails[0].type"] = "work"
            });

            var email = user["emails"]!.AsArray()[0]!;
            Assert.Equal("a@x.sa", email["value"]!.ToString());
            Assert.Equal("work", email["type"]!.ToString());
        }

        /// <summary>Two entries at different indices are two elements, not one overwritten twice.</summary>
        [Fact]
        public void SeparateIndicesAreSeparateElements()
        {
            var user = ScimPayload.BuildUser("ahmed.s", new Dictionary<string, string>
            {
                ["emails[0].value"] = "work@x.sa",
                ["emails[1].value"] = "home@x.sa"
            });

            var emails = user["emails"]!.AsArray();
            Assert.Equal(2, emails.Count);
            Assert.Equal("work@x.sa", emails[0]!["value"]!.ToString());
            Assert.Equal("home@x.sa", emails[1]!["value"]!.ToString());
        }

        /// <summary>
        /// SCIM is typed where JSON is typed. A server handed the string "false" for <c>active</c>
        /// either rejects it, or reads a non-empty value and leaves the account enabled — a disable
        /// that reports success and disables nothing.
        /// </summary>
        [Theory]
        [InlineData("false", false)]
        [InlineData("true", true)]
        public void BooleanAttributes_AreSentAsBooleans(string value, bool expected)
        {
            var user = ScimPayload.BuildUser("ahmed.s", new Dictionary<string, string> { ["active"] = value });

            Assert.Equal(expected, user["active"]!.GetValue<bool>());
        }

        [Fact]
        public void OrdinaryValues_StayStrings()
        {
            var user = ScimPayload.BuildUser("ahmed.s", new Dictionary<string, string> { ["title"] = "مهندس" });
            Assert.Equal("مهندس", user["title"]!.GetValue<string>());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ABlankPath_IsNotSent(string path)
        {
            var user = ScimPayload.BuildUser("ahmed.s", new Dictionary<string, string> { [path] = "x" });
            Assert.Equal(2, user.Count);   // schemas + userName only
        }

        // ══════════════════════════════════════
        // THE SILENT DROP
        // ══════════════════════════════════════

        /// <summary>
        /// The guard this module exists for. Everything sent came back, so nothing was lost.
        /// </summary>
        [Fact]
        public void WhenEverythingComesBack_NothingIsReportedDropped()
        {
            var sent = new Dictionary<string, string>
            {
                ["name.givenName"] = "أحمد",
                ["title"] = "مهندس"
            };
            var returned = JsonNode.Parse("""
                { "id":"1", "userName":"ahmed.s", "name":{"givenName":"أحمد"}, "title":"مهندس" }
                """);

            Assert.Empty(ScimPayload.AttributesDroppedBy(sent, returned));
        }

        /// <summary>
        /// 201 Created, a resource that looks fine, and two attributes the server never understood.
        /// Without this comparison the run reports a clean creation.
        /// </summary>
        [Fact]
        public void AttributesTheServerIgnored_AreNamed()
        {
            var sent = new Dictionary<string, string>
            {
                ["name.givenName"] = "أحمد",
                ["extensionAttribute2"] = "440000001",   // an AD name, meaningless to SCIM
                ["department"] = "الصيدلة"
            };
            var returned = JsonNode.Parse("""
                { "id":"1", "userName":"ahmed.s", "name":{"givenName":"أحمد"} }
                """);

            var dropped = ScimPayload.AttributesDroppedBy(sent, returned);

            Assert.Equal(new[] { "extensionAttribute2", "department" }, dropped);
        }

        /// <summary>A reply that is not a resource at all means nothing was stored, not that everything was.</summary>
        [Fact]
        public void AnUnreadableReply_CountsEverythingAsDropped()
        {
            var sent = new Dictionary<string, string> { ["title"] = "x", ["department"] = "y" };

            Assert.Equal(2, ScimPayload.AttributesDroppedBy(sent, null).Count);
            Assert.Equal(2, ScimPayload.AttributesDroppedBy(sent, JsonNode.Parse("[]")).Count);
        }

        /// <summary>
        /// A password is write-only by design — RFC 7643 marks it so, and no SCIM service echoes
        /// it. Comparing it against the reply reported it as discarded on <b>every single create</b>,
        /// and a warning that fires every time is one an operator learns to scroll past. The guard
        /// is only worth having while everything it names is worth reading.
        ///
        /// Found by running the connector against a real service instead of a stub built to agree
        /// with it.
        /// </summary>
        [Fact]
        public void AWriteOnlyPassword_IsNotReportedAsDiscarded()
        {
            var sent = new Dictionary<string, string> { ["title"] = "مهندس", ["password"] = "P@ssw0rd!" };
            var returned = JsonNode.Parse("""{ "id":"1", "title":"مهندس" }""");

            Assert.Empty(ScimPayload.AttributesDroppedBy(sent, returned));
        }

        /// <summary>And it stays excluded when nothing came back at all, for the same reason.</summary>
        [Fact]
        public void AWriteOnlyPassword_IsExcludedEvenFromAnUnreadableReply()
        {
            var sent = new Dictionary<string, string> { ["title"] = "x", ["password"] = "y" };

            Assert.Equal(new[] { "title" }, ScimPayload.AttributesDroppedBy(sent, null));
        }

        /// <summary>But a real omission is still named — the exclusion is one attribute, not a blanket.</summary>
        [Fact]
        public void AGenuineOmission_IsStillNamedAlongsideAPassword()
        {
            var sent = new Dictionary<string, string>
            {
                ["password"] = "P@ssw0rd!",
                ["extensionAttribute2"] = "440000001"
            };
            var returned = JsonNode.Parse("""{ "id":"1" }""");

            Assert.Equal(new[] { "extensionAttribute2" }, ScimPayload.AttributesDroppedBy(sent, returned));
        }

        [Fact]
        public void ANestedValueIsFoundWhereItWasPut()
        {
            var returned = JsonNode.Parse("""{ "emails":[{"value":"a@x.sa"}] }""");

            Assert.Equal("a@x.sa", ScimPayload.ReadPath(returned, "emails[0].value"));
            Assert.Null(ScimPayload.ReadPath(returned, "emails[1].value"));
            Assert.Null(ScimPayload.ReadPath(returned, "name.givenName"));
        }

        // ══════════════════════════════════════
        // PATCHING
        // ══════════════════════════════════════

        [Fact]
        public void APatchCarriesOneReplacePerAttribute()
        {
            var patch = ScimPayload.BuildPatch(new Dictionary<string, string>
            {
                ["title"] = "مدير",
                ["active"] = "false"
            });

            Assert.Equal(ScimPayload.PatchSchema, patch["schemas"]!.AsArray()[0]!.ToString());
            var ops = patch["Operations"]!.AsArray();
            Assert.Equal(2, ops.Count);
            Assert.All(ops, op => Assert.Equal("replace", op!["op"]!.ToString()));
            // Still typed: a patch that disables an account must send a boolean.
            Assert.False(ops[1]!["value"]!.GetValue<bool>());
        }

        [Fact]
        public void AddingAMember_SendsAnAddOperation()
        {
            var patch = ScimPayload.BuildMemberPatch("user-123", add: true);
            var op = patch["Operations"]!.AsArray()[0]!;

            Assert.Equal("add", op["op"]!.ToString());
            Assert.Equal("members", op["path"]!.ToString());
            Assert.Equal("user-123", op["value"]!.AsArray()[0]!["value"]!.ToString());
        }

        /// <summary>
        /// Removal filters on the member rather than an index. An index would remove whoever happens
        /// to sit in that position, which on a group that changed between read and write is somebody
        /// else entirely.
        /// </summary>
        [Fact]
        public void RemovingAMember_FiltersOnTheMemberNotAPosition()
        {
            var op = ScimPayload.BuildMemberPatch("user-123", add: false)["Operations"]!.AsArray()[0]!;

            Assert.Equal("remove", op["op"]!.ToString());
            Assert.Contains("user-123", op["path"]!.ToString());
            Assert.DoesNotContain("[0]", op["path"]!.ToString());
        }

        // ══════════════════════════════════════
        // FILTER INJECTION
        // ══════════════════════════════════════

        /// <summary>A SCIM filter is a query language, and a quote in an account name would end the literal.</summary>
        [Fact]
        public void AQuoteInAValue_CannotEndTheFilterLiteral()
        {
            var escaped = ScimPayload.EscapeFilterValue("ahmed\" or userName pr or \"x");

            Assert.DoesNotContain("\" or", escaped.Replace("\\\"", ""));
            Assert.Contains("\\\"", escaped);
        }

        [Fact]
        public void ABackslashIsEscapedBeforeTheQuote()
        {
            // Escaping in the wrong order turns \" into \\" and reopens the hole it closed.
            Assert.Equal("a\\\\\\\"b", ScimPayload.EscapeFilterValue("a\\\"b"));
        }

        // ══════════════════════════════════════
        // CHOOSING A TARGET
        // ══════════════════════════════════════

        /// <summary>
        /// A row written before the column existed is an Active Directory tenant. Defaulting
        /// anywhere else would repoint a working installation on upgrade.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnUnsetProvider_MeansActiveDirectory(string? stored) =>
            Assert.Equal(TargetProviders.ActiveDirectory, TargetProviders.Normalise(stored));

        [Theory]
        [InlineData("scim", TargetProviders.Scim)]
        [InlineData("SCIM", TargetProviders.Scim)]
        [InlineData(" ActiveDirectory ", TargetProviders.ActiveDirectory)]
        public void AProviderIsRecognisedWhateverTheCasing(string stored, string expected) =>
            Assert.Equal(expected, TargetProviders.Normalise(stored));

        [Fact]
        public void AnUnknownProvider_IsReportedAsUnknownRatherThanCoerced()
        {
            // Silently reading it as Active Directory would provision a SCIM tenant into a domain.
            Assert.False(TargetProviders.IsKnown("Ldapv4"));
            Assert.Equal("Ldapv4", TargetProviders.Normalise("Ldapv4"));
        }

        /// <summary>
        /// SCIM has no organisational units — no path, no container, no move. The sync engine and
        /// the lifecycle rules both move accounts between OUs, and on a SCIM tenant that instruction
        /// cannot be carried out; it has to be refused, not absorbed into a false success.
        /// </summary>
        [Fact]
        public void OnlyActiveDirectoryHasOrganisationalUnits()
        {
            Assert.True(TargetProviders.SupportsOrganisationalUnits(TargetProviders.ActiveDirectory));
            Assert.True(TargetProviders.SupportsOrganisationalUnits(null));   // a pre-upgrade row
            Assert.False(TargetProviders.SupportsOrganisationalUnits(TargetProviders.Scim));
        }
    }
}
