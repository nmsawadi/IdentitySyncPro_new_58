using IdentitySyncPro.Core.Models.Settings;

namespace IdentitySyncPro.Core.Interfaces
{
    /// <summary>
    /// Builds source/target connectors from a specific tenant's connection settings.
    /// Enables multiple tenants (e.g. identities from one view, employees from another)
    /// to sync with their own connections, mappings, and rules.
    /// Falls back to the appsettings-configured default connectors when a tenant
    /// has no connection settings of its own (legacy single-tenant behavior).
    /// </summary>
    public interface ITenantConnectorFactory
    {
        ISourceConnector CreateSourceConnector(TenantSettings tenant);
        ITargetConnector CreateTargetConnector(TenantSettings tenant);
    }
}
