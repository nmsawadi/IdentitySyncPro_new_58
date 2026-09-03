namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Decides whether a configured API key is a real secret or a shipped placeholder.
    ///
    /// Why this exists: <c>appsettings.json</c> ships with <c>"CHANGE-THIS-API-KEY"</c> and the
    /// production template with <c>"GENERATE-A-STRONG-API-KEY-HERE"</c>. An install that never
    /// replaced them exposed the SCIM endpoint behind a key printed in the source tree — the
    /// key was *present*, so every "is it configured?" check passed. The gap was invisible.
    ///
    /// Deliberately narrow: only empty values and placeholder markers are rejected. A short but
    /// genuinely chosen key still works (it only earns a startup warning) — blocking on length
    /// would silently break an existing integration, which is a worse failure than the one
    /// being fixed.
    /// </summary>
    public static class ApiKeyGuard
    {
        /// <summary>Keys this long or longer do not raise the weak-key warning.</summary>
        public const int RecommendedMinimumLength = 32;

        /// <summary>
        /// Markers found in every placeholder we ship. Matched case-insensitively anywhere in
        /// the value, so "CHANGE-THIS-API-KEY", "GENERATE-A-STRONG-HANGFIRE-KEY-HERE" and
        /// "YOUR_API_KEY" are all caught without hardcoding each literal.
        /// </summary>
        private static readonly string[] PlaceholderMarkers =
        {
            "CHANGE-THIS", "CHANGE_THIS", "CHANGETHIS",
            "GENERATE-A-", "GENERATE_A_",
            "YOUR-", "YOUR_",
            "REPLACE-ME", "REPLACE_ME",
            "PLACEHOLDER", "EXAMPLE-KEY", "XXXXX"
        };

        /// <summary>
        /// True when the key is missing, blank, or still one of the shipped placeholders.
        /// Callers must treat this exactly like "no key configured" — fail closed.
        /// </summary>
        public static bool IsPlaceholderOrMissing(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return true;
            foreach (var marker in PlaceholderMarkers)
            {
                if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>True when the key is usable for authentication.</summary>
        public static bool IsUsable(string? key) => !IsPlaceholderOrMissing(key);

        /// <summary>
        /// True when the key works but is shorter than recommended. Warning only — never blocks.
        /// </summary>
        public static bool IsWeak(string? key) =>
            IsUsable(key) && key!.Trim().Length < RecommendedMinimumLength;
    }
}
