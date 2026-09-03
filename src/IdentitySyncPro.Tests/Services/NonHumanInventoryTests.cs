using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Infrastructure.Services;
using Xunit;

namespace IdentitySyncPro.Tests.Services
{
    /// <summary>
    /// Guards the non-human account inventory.
    ///
    /// Everything covered here is a failure that completes successfully: a classifier that matches
    /// nothing, a wildcard that stopped being a wildcard, an "All" that behaves like "Any". None of
    /// them throws at runtime, all of them produce a report with a plausible number on it, and the
    /// number is the entire product.
    /// </summary>
    public class NonHumanInventoryTests
    {
        private static NonHumanClassifier.Signals Signals(
            string? names = null, string? ous = null, string? groups = null, string? attrRules = null,
            bool noKey = false, bool pwdNeverExpires = false, bool spn = false,
            bool gmsa = false, string mode = NonHumanClassifier.ModeAny) =>
            new(NonHumanClassifier.SplitList(names),
                NonHumanClassifier.SplitList(ous),
                NonHumanClassifier.SplitList(groups),
                NonHumanClassifier.ParseAttributeRules(attrRules),
                noKey, pwdNeverExpires, spn, gmsa, mode);

        // ══════════════════════════════════════
        // THE EMPTY-CLASSIFIER GUARD
        // ══════════════════════════════════════

        /// <summary>
        /// The whole feature's failure mode: no rules, "Any" mode, no gMSA — the LDAP searches
        /// never run, the report is empty, and it reads as "this domain has no service accounts".
        /// </summary>
        [Fact]
        public void NoRulesUnderAny_IsRejected()
        {
            var problem = NonHumanClassifier.Validate(Signals());
            Assert.NotNull(problem);
            Assert.Contains("no non-human accounts", problem!);
        }

        /// <summary>The mirror image: an "All" intersection over zero conditions matches everything.</summary>
        [Fact]
        public void NoRulesUnderAll_IsRejected()
        {
            var problem = NonHumanClassifier.Validate(Signals(mode: NonHumanClassifier.ModeAll));
            Assert.NotNull(problem);
            Assert.Contains("match every account", problem!);
        }

        /// <summary>gMSA/MSA alone is a real configuration: those accounts need no local rule to be identified.</summary>
        [Fact]
        public void ManagedServiceAccountsAlone_IsAllowed()
        {
            Assert.Null(NonHumanClassifier.Validate(Signals(gmsa: true)));
        }

        /// <summary>But not under "All": there the gMSA union is additive, so the intersection is still empty of conditions.</summary>
        [Fact]
        public void ManagedServiceAccountsAlone_UnderAll_IsRejected()
        {
            Assert.NotNull(NonHumanClassifier.Validate(Signals(gmsa: true, mode: NonHumanClassifier.ModeAll)));
        }

        [Theory]
        [InlineData("svc_*", null, null, null, false, false, false)]
        [InlineData(null, "OU=Svc,DC=x,DC=y", null, null, false, false, false)]
        [InlineData(null, null, "Service Accounts", null, false, false, false)]
        [InlineData(null, null, null, "employeeType=service", false, false, false)]
        [InlineData(null, null, null, null, true, false, false)]
        [InlineData(null, null, null, null, false, true, false)]
        [InlineData(null, null, null, null, false, false, true)]
        public void AnySingleRule_IsEnough(string? names, string? ous, string? groups, string? rules,
            bool noKey, bool pwd, bool spn)
        {
            Assert.Null(NonHumanClassifier.Validate(Signals(names, ous, groups, rules, noKey, pwd, spn)));
        }

        // ══════════════════════════════════════
        // WILDCARD ESCAPING
        // ══════════════════════════════════════

        /// <summary>
        /// The pattern must survive escaping as a pattern. Escaping '*' the way a value is escaped
        /// turns "svc_*" into a search for the single account literally named "svc_*" — a filter
        /// AD accepts, answers with nothing, and never complains about.
        /// </summary>
        [Fact]
        public void Wildcard_SurvivesEscaping()
        {
            Assert.Equal("svc_*", NonHumanClassifier.EscapePreservingWildcards("svc_*"));
            Assert.Equal("*_bot", NonHumanClassifier.EscapePreservingWildcards("*_bot"));
            Assert.Equal("*svc*", NonHumanClassifier.EscapePreservingWildcards("*svc*"));
        }

        /// <summary>Everything that is not the wildcard is still escaped, so a pattern cannot break out of the filter.</summary>
        [Fact]
        public void Injection_IsStillEscaped()
        {
            var escaped = NonHumanClassifier.EscapePreservingWildcards("svc*)(objectClass=*");
            Assert.DoesNotContain("(", escaped);
            Assert.DoesNotContain(")", escaped);
            Assert.Contains("*", escaped);          // the intended wildcards survive
            Assert.Contains(@"\28", escaped);       // the injected parenthesis does not
            Assert.Contains(@"\29", escaped);
        }

        [Fact]
        public void NameFilter_IsAnOrOverPatterns()
        {
            Assert.Null(NonHumanClassifier.BuildNameFilter(Array.Empty<string>()));
            Assert.Equal("(sAMAccountName=svc_*)", NonHumanClassifier.BuildNameFilter(new[] { "svc_*" }));
            Assert.Equal("(|(sAMAccountName=svc_*)(sAMAccountName=sa-*))",
                NonHumanClassifier.BuildNameFilter(new[] { "svc_*", "sa-*" }));
        }

        // ══════════════════════════════════════
        // ANY / ALL
        // ══════════════════════════════════════

        private static IReadOnlyCollection<string>[] Sets(params string[][] sets) =>
            sets.Select(s => (IReadOnlyCollection<string>)s).ToArray();

        [Fact]
        public void Any_Unions()
        {
            var r = NonHumanClassifier.Combine(Sets(new[] { "a", "b" }, new[] { "b", "c" }), requireAll: false);
            Assert.Equal(new[] { "a", "b", "c" }, r.OrderBy(x => x));
        }

        [Fact]
        public void All_Intersects()
        {
            var r = NonHumanClassifier.Combine(Sets(new[] { "a", "b" }, new[] { "b", "c" }), requireAll: true);
            Assert.Equal(new[] { "b" }, r);
        }

        /// <summary>
        /// The second route to "everything is a service account": an "All" over no sets at all.
        /// <see cref="NonHumanClassifier.Validate"/> already blocks this, and the fold must not
        /// reopen it if a future caller forgets to validate.
        /// </summary>
        [Fact]
        public void All_OverNothing_IsEmptyNotEverything()
        {
            Assert.Empty(NonHumanClassifier.Combine(Sets(), requireAll: true));
        }

        /// <summary>DNs differ in case between LDAP responses and hand-typed settings; the same account must not be counted twice.</summary>
        [Fact]
        public void Combine_IsCaseInsensitive()
        {
            var r = NonHumanClassifier.Combine(
                Sets(new[] { "CN=Svc,DC=x" }, new[] { "cn=svc,dc=x" }), requireAll: true);
            Assert.Single(r);
        }

        // ══════════════════════════════════════
        // ATTRIBUTE RULES
        // ══════════════════════════════════════

        [Fact]
        public void AttributeRules_Parse()
        {
            var rules = NonHumanClassifier.ParseAttributeRules("employeeType=service, extensionAttribute5=svc");
            Assert.Equal(2, rules.Length);
            Assert.Equal(("employeeType", "service"), rules[0]);
            Assert.Equal(("extensionAttribute5", "svc"), rules[1]);
        }

        /// <summary>
        /// A malformed rule must throw, not be skipped. A skipped rule narrows the inventory, and a
        /// narrower inventory is indistinguishable from a cleaner directory.
        /// </summary>
        [Theory]
        [InlineData("employeeType")]           // no '='
        [InlineData("=service")]               // no attribute
        [InlineData("employeeType=")]          // no value
        [InlineData("employee Type=service")]  // not a valid attribute name
        [InlineData("(objectClass=*)=x")]      // injection attempt in the attribute position
        public void MalformedAttributeRule_Throws(string rule)
        {
            Assert.Throws<InvalidOperationException>(() => NonHumanClassifier.ParseAttributeRules(rule));
        }

        /// <summary>An attribute name cannot be escaped into safety the way a value can, so it is rejected instead.</summary>
        [Theory]
        [InlineData("lastLogonTimestamp", true)]
        [InlineData("extensionAttribute2", true)]
        [InlineData("ms-DS-Something", true)]
        [InlineData("2bad", false)]
        [InlineData("bad)(x", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void AttributeName_IsValidated(string? name, bool ok)
        {
            if (ok) Assert.Equal(name!.Trim(), NonHumanClassifier.RequireAttributeName(name, "test"));
            else Assert.Throws<InvalidOperationException>(() => NonHumanClassifier.RequireAttributeName(name, "test"));
        }

        [Fact]
        public void SplitList_DropsBlanksAndDuplicates()
        {
            Assert.Empty(NonHumanClassifier.SplitList(null));
            Assert.Empty(NonHumanClassifier.SplitList("   "));
            Assert.Equal(new[] { "svc_*", "sa-*" }, NonHumanClassifier.SplitList(" svc_*, , sa-* , SVC_* "));
        }

        // ══════════════════════════════════════
        // ACTION AND RISK
        // ══════════════════════════════════════

        [Theory]
        [InlineData(true, true, NonHumanClassifier.ActionPrivileged)]
        [InlineData(true, false, NonHumanClassifier.ActionPrivileged)]
        [InlineData(false, true, NonHumanClassifier.ActionOwned)]
        [InlineData(false, false, NonHumanClassifier.ActionUnowned)]
        public void Action_RanksPrivilegeAboveOwnership(bool privileged, bool owned, string expected)
        {
            Assert.Equal(expected, NonHumanClassifier.ChooseAction(privileged, owned));
        }

        private static readonly DateTime Now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

        private static NonHumanClassifier.AccountFacts Account(
            bool enabled = true, bool owner = true, bool privileged = false, bool pwdNeverExpires = false,
            int? pwdAgeDays = 10, int? idleDays = 5, bool expires = true) =>
            new(enabled, owner, privileged, pwdNeverExpires,
                pwdAgeDays == null ? null : Now.AddDays(-pwdAgeDays.Value),
                idleDays == null ? null : Now.AddDays(-idleDays.Value),
                expires ? Now.AddDays(90) : null);

        [Fact]
        public void HealthyOwnedAccount_HasNoRisks()
        {
            Assert.Empty(NonHumanClassifier.EvaluateRisks(Account(), 365, 180, Now));
        }

        [Fact]
        public void StaleCredentialAndDormancy_AreFlaggedWithTheirAge()
        {
            var risks = NonHumanClassifier.EvaluateRisks(Account(pwdAgeDays: 800, idleDays: 400), 365, 180, Now);
            Assert.Contains("credential-800d", risks);
            Assert.Contains("dormant-400d", risks);
        }

        [Fact]
        public void ThresholdIsExclusive_ExactlyAtTheLimitIsNotStale()
        {
            Assert.DoesNotContain("credential-365d",
                NonHumanClassifier.EvaluateRisks(Account(pwdAgeDays: 365), 365, 180, Now));
        }

        [Fact]
        public void ZeroThreshold_DisablesTheCheck()
        {
            var risks = NonHumanClassifier.EvaluateRisks(Account(pwdAgeDays: 5000, idleDays: 5000), 0, 0, Now);
            Assert.DoesNotContain(risks, r => r.StartsWith("credential-"));
            Assert.DoesNotContain(risks, r => r.StartsWith("dormant-"));
        }

        /// <summary>An unreadable pwdLastSet is reported as unknown, never as fresh — silence about a credential is not evidence it is new.</summary>
        [Fact]
        public void UnknownCredentialAge_IsItsOwnFinding()
        {
            Assert.Contains("credential-age-unknown",
                NonHumanClassifier.EvaluateRisks(Account(pwdAgeDays: null), 365, 180, Now));
        }

        /// <summary>
        /// A disabled account cannot be exercised, so its stale credential and its silence are not
        /// live exposures. Ranking them alongside the enabled ones buries the rows that matter.
        /// </summary>
        [Fact]
        public void DisabledAccount_ReportsOnlyStandingFacts()
        {
            var risks = NonHumanClassifier.EvaluateRisks(
                Account(enabled: false, owner: false, privileged: true,
                        pwdNeverExpires: true, pwdAgeDays: 5000, idleDays: 5000, expires: false),
                365, 180, Now);

            Assert.Equal(new[] { "disabled", "privileged", "no-owner" }, risks);
        }

        [Fact]
        public void OwnershipAndPrivilege_AreAlwaysReported()
        {
            var risks = NonHumanClassifier.EvaluateRisks(Account(owner: false, privileged: true), 365, 180, Now);
            Assert.Contains("no-owner", risks);
            Assert.Contains("privileged", risks);
        }

        [Fact]
        public void NoExpiryAndNeverExpiringPassword_AreFlagged()
        {
            var risks = NonHumanClassifier.EvaluateRisks(Account(pwdNeverExpires: true, expires: false), 365, 180, Now);
            Assert.Contains("pwd-never-expires", risks);
            Assert.Contains("no-expiry", risks);
        }

        // ══════════════════════════════════════
        // RUN SUMMARY
        // ══════════════════════════════════════

        /// <summary>
        /// Zero findings on this report is not the good news it is on the others, and the summary
        /// has to say so — the run completes either way, and the audit log is where anyone would
        /// look to find out which one happened.
        /// </summary>
        [Fact]
        public void EmptyInventory_SummaryWarnsAboutTheClassifier()
        {
            var note = SvcAdAuditExecutor.SummaryNote(
                "NonHumanInventory", "DC=x,DC=y", examined: 118364, findingCount: 0);

            Assert.Contains("check the classifier", note);
            Assert.Contains("118364", note);
        }

        /// <summary>The finding count is meaningless without the population it came out of.</summary>
        [Fact]
        public void Inventory_SummaryCarriesTheDenominator()
        {
            var note = SvcAdAuditExecutor.SummaryNote(
                "NonHumanInventory", "DC=x,DC=y", examined: 118364, findingCount: 412);

            Assert.Contains("412 non-human account(s) out of 118364", note);
            Assert.DoesNotContain("check the classifier", note);
        }

        /// <summary>The other reports keep their original wording; this feature must not reword them.</summary>
        [Fact]
        public void OtherReports_KeepTheirSummaryWording()
        {
            var note = SvcAdAuditExecutorSummary("LockedAccounts", 7);
            Assert.Contains("7 account(s) matched the report's filter", note);

            Assert.Contains("No findings — nothing in scope met the criteria",
                SvcAdAuditExecutorSummary("LockedAccounts", 0, findings: 0));
        }

        private static string SvcAdAuditExecutorSummary(string reportType, int examined, int findings = 1) =>
            SvcAdAuditExecutor.SummaryNote(reportType, "DC=x", examined, findings);
    }
}
