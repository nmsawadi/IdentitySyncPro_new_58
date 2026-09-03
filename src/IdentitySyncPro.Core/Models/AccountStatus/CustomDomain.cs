namespace IdentitySyncPro.Core.Models.AccountStatus
{
    public class CustomDomain
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public int Port { get; set; } = 389;
        public string BaseDN { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? Password { get; set; }

        /// <summary>
        /// AD attribute holding the user's mobile number for this domain (e.g. mobile,
        /// telephoneNumber, extensionAttribute13). Editable — change it and save to switch
        /// which attribute the search reads. Empty falls back to the common attributes.
        /// </summary>
        public string? PhoneAttribute { get; set; } = "mobile";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
