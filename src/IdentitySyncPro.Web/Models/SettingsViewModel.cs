using IdentitySyncPro.Core.Models.Settings;

namespace IdentitySyncPro.Web.Models
{
    public class SettingsViewModel
    {
        public List<TenantSettings> Tenants { get; set; } = new();
        public TenantSettings? CurrentTenant { get; set; }
        public string CurrentLanguage { get; set; } = "ar";
        public bool IsEditing { get; set; } = false;
    }
}
