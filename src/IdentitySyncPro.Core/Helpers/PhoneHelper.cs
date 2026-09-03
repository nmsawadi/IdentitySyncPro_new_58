namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Normalizes Saudi mobile numbers to the gateway format (9665XXXXXXXX),
    /// tolerating the many ways a number may be stored (in AD or a source view):
    /// 05XXXXXXXX, +9665XXXXXXXX, 009665XXXXXXXX, 9665XXXXXXXX, 5XXXXXXXX.
    /// Null-safe: returns an empty string for null/blank input.
    /// </summary>
    public static class PhoneHelper
    {
        public static string NormalizePhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return string.Empty;

            phone = phone.Trim()
                         .Replace(" ", "").Replace("-", "")
                         .Replace("(", "").Replace(")", "");
            if (phone.StartsWith("+")) phone = phone.Substring(1);

            if (phone.StartsWith("00966")) return "966" + phone.Substring(5); // 00966XXXXXXXXX
            if (phone.StartsWith("966")) return phone;                        // already 966XXXXXXXXX
            if (phone.StartsWith("05") && phone.Length == 10) return "966" + phone.Substring(1); // 05XXXXXXXX
            if (phone.StartsWith("5") && phone.Length == 9) return "966" + phone;                // bare 5XXXXXXXX

            return phone; // unknown shape — return cleaned value unchanged
        }

        /// <summary>Masks all but the last 4 digits, e.g. ********1234.</summary>
        public static string MaskPhone(string? phone)
        {
            phone = (phone ?? string.Empty).Trim();
            return phone.Length > 4 ? new string('*', phone.Length - 4) + phone[^4..] : "****";
        }
    }
}
