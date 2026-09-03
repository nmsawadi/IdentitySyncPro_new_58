namespace IdentitySyncPro.Core.Models.Settings
{
    /// <summary>
    /// The kinds of directory this system can provision into.
    ///
    /// Kept as constants beside a resolver rather than as an enum, matching how
    /// <see cref="TenantSettings.SourceProvider"/> already names Oracle and SqlServer: the value is
    /// stored as text, read by a factory, and shown in a dropdown, and an enum would only add a
    /// mapping between those three without removing any of them.
    /// </summary>
    public static class TargetProviders
    {
        public const string ActiveDirectory = "ActiveDirectory";
        public const string Scim = "Scim";

        public static readonly string[] All = { ActiveDirectory, Scim };

        /// <summary>
        /// Normalises a stored value, treating blank as Active Directory.
        ///
        /// Blank means the row predates the column, and every such row is an Active Directory
        /// tenant — defaulting anywhere else would repoint a working installation on upgrade.
        /// </summary>
        public static string Normalise(string? stored) =>
            string.IsNullOrWhiteSpace(stored)
                ? ActiveDirectory
                : All.FirstOrDefault(p => p.Equals(stored.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? stored.Trim();

        public static bool IsKnown(string? stored) =>
            All.Contains(Normalise(stored), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this target has a place-in-a-tree concept.
        ///
        /// Active Directory has organisational units; SCIM has nothing of the kind — no path, no
        /// container, no move. The difference matters because the sync engine and the lifecycle
        /// rules both move accounts between OUs, and on a SCIM tenant that instruction cannot be
        /// carried out. It has to be refused where it is issued, not absorbed into a false success.
        /// </summary>
        public static bool SupportsOrganisationalUnits(string? provider) =>
            Normalise(provider) == ActiveDirectory;

        /// <summary>
        /// Whether this target needs a placeholder in place of an empty attribute value.
        ///
        /// Active Directory refuses a write that sets an attribute to an empty string, so a
        /// placeholder — a dot, a dash, "N/A" — is the standard way to keep the write legal. No
        /// other target has that constraint: SCIM is perfectly happy for an attribute to be absent,
        /// and sending the placeholder writes nonsense into it as though it were data.
        ///
        /// <para>Deliberately a separate question from <see cref="SupportsOrganisationalUnits"/>,
        /// even though both answer "is this Active Directory" today. They are two different facts
        /// about a target, and a third provider could easily answer them differently — reusing one
        /// for the other is how a rename or a new connector quietly changes unrelated behaviour.</para>
        /// </summary>
        public static bool UsesEmptyAttributePlaceholder(string? provider) =>
            Normalise(provider) == ActiveDirectory;

        /// <summary>Display name for the settings screen.</summary>
        public static string Label(string? provider, bool isArabic) => Normalise(provider) switch
        {
            Scim => isArabic ? "خدمة SCIM 2.0" : "SCIM 2.0 service",
            ActiveDirectory => isArabic ? "Active Directory" : "Active Directory",
            var other => other
        };
    }
}
