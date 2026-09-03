using IdentitySyncPro.Core.Models.Connectors;
using IdentitySyncPro.Core.Enums;
using IdentitySyncPro.Infrastructure.Connectors;
using System.DirectoryServices.Protocols;
using System.Net;
using ClosedXML.Excel;
using IdentitySyncPro.Core.Helpers;
using IdentitySyncPro.Core.Models.AccountStatus;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Service for searching AD users and toggling account enable/disable status.
    /// Completely independent from the sync engine — uses direct LDAP operations.
    /// </summary>
    public class AccountStatusService
    {
        private readonly ILogger<AccountStatusService> _logger;

        public AccountStatusService(ILogger<AccountStatusService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Search for a user in AD and return their attributes.
        /// Returns null if user not found.
        /// </summary>
        /// <param name="phoneAttribute">
        /// The domain's configured AD attribute holding the mobile number. When set it is read
        /// FIRST; the common attributes remain as a fallback so nothing breaks if it is empty.
        /// </param>
        public AdUserInfo? SearchUser(string server, int port, string baseDN,
            string? username, string? password, string samAccountName, string? phoneAttribute = null)
        {
            try
            {
                using var connection = CreateConnection(server, port, username, password);
                connection.Bind();

                var attributes = new List<string>
                {
                    "sAMAccountName", "description", "userAccountControl",
                    "mobile", "telephoneNumber", "displayName", "mail",
                    "distinguishedName", "department", "title",
                    "extensionAttribute13", "extensionAttribute14"
                };
                // Request the configured attribute too (it may not be one of the defaults).
                if (!string.IsNullOrWhiteSpace(phoneAttribute))
                    attributes.Add(phoneAttribute.Trim());

                var searchRequest = new SearchRequest(
                    baseDN,
                    $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(samAccountName)})",
                    SearchScope.Subtree,
                    attributes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                );

                var response = (SearchResponse)connection.SendRequest(searchRequest);

                if (response.Entries.Count == 0)
                {
                    _logger.LogInformation("User {SamAccountName} not found in {Server}/{BaseDN}",
                        samAccountName, server, baseDN);
                    return null;
                }

                var entry = response.Entries[0];
                var uac = GetAttribute(entry, "userAccountControl");
                var isDisabled = false;

                if (int.TryParse(uac, out var uacValue))
                {
                    isDisabled = (uacValue & 0x0002) != 0; // ACCOUNTDISABLE flag
                }

                // The domain's configured attribute wins; otherwise fall back to the common ones.
                var phone = (!string.IsNullOrWhiteSpace(phoneAttribute) ? GetAttribute(entry, phoneAttribute.Trim()) : null)
                    ?? GetAttribute(entry, "mobile")
                    ?? GetAttribute(entry, "telephoneNumber")
                    ?? GetAttribute(entry, "extensionAttribute13")
                    ?? GetAttribute(entry, "extensionAttribute14");

                return new AdUserInfo
                {
                    SamAccountName = GetAttribute(entry, "sAMAccountName") ?? samAccountName,
                    DisplayName = GetAttribute(entry, "description") ?? GetAttribute(entry, "displayName") ?? "",
                    Email = GetAttribute(entry, "mail") ?? "",
                    PhoneNumber = phone ?? "",
                    Department = GetAttribute(entry, "department") ?? "",
                    Title = GetAttribute(entry, "title") ?? "",
                    DistinguishedName = entry.DistinguishedName,
                    IsDisabled = isDisabled,
                    UserAccountControl = uacValue
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching for user {SamAccountName} in {Server}",
                    samAccountName, server);
                throw;
            }
        }

        /// <summary>
        /// Enable or disable an AD account by modifying userAccountControl.
        /// </summary>
        public bool ToggleAccountStatus(string server, int port, string baseDN,
            string? username, string? password, string samAccountName, bool enable)
        {
            try
            {
                using var connection = CreateConnection(server, port, username, password);
                connection.Bind();

                // First find the user's DN
                var searchRequest = new SearchRequest(
                    baseDN,
                    $"(sAMAccountName={LdapSanitizer.EscapeFilterValue(samAccountName)})",
                    SearchScope.Subtree,
                    "distinguishedName", "userAccountControl"
                );

                var response = (SearchResponse)connection.SendRequest(searchRequest);
                if (response.Entries.Count == 0)
                {
                    _logger.LogWarning("User {SamAccountName} not found for toggle in {Server}",
                        samAccountName, server);
                    return false;
                }

                var entry = response.Entries[0];
                var dn = entry.DistinguishedName;
                var currentUac = GetAttribute(entry, "userAccountControl");

                if (!int.TryParse(currentUac, out var uacValue))
                {
                    _logger.LogWarning("Cannot parse userAccountControl for {SamAccountName}: {Value}",
                        samAccountName, currentUac);
                    return false;
                }

                int newUac;
                if (enable)
                {
                    // Remove ACCOUNTDISABLE flag (bit 1)
                    newUac = uacValue & ~0x0002;
                }
                else
                {
                    // Set ACCOUNTDISABLE flag (bit 1)
                    newUac = uacValue | 0x0002;
                }

                // Apply the modification
                var mod = new DirectoryAttributeModification
                {
                    Name = "userAccountControl",
                    Operation = DirectoryAttributeOperation.Replace
                };
                mod.Add(newUac.ToString());

                var modifyRequest = new ModifyRequest(dn, mod);
                connection.SendRequest(modifyRequest);

                _logger.LogInformation("{Action} account {SamAccountName} in {Server} — UAC: {OldUac} → {NewUac}",
                    enable ? "Enabled" : "Disabled", samAccountName, server, uacValue, newUac);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to {Action} account {SamAccountName} in {Server}",
                    enable ? "enable" : "disable", samAccountName, server);
                throw;
            }
        }

        /// <summary>
        /// Export account status logs to an Excel file.
        /// Returns the byte array of the XLSX file.
        /// </summary>
        public byte[] ExportToExcel(List<AccountStatusLog> logs, bool isArabic = true)
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add(isArabic ? "سجل العمليات" : "Operations Log");

            // Headers
            var headers = isArabic
                ? new[] { "#", "اسم المستخدم", "الاسم", "الدومين", "العملية", "السبب", "الحالة السابقة", "الحالة الجديدة", "SMS", "رقم الهاتف", "المنفذ", "التاريخ" }
                : new[] { "#", "Username", "Name", "Domain", "Action", "Reason", "Previous Status", "New Status", "SMS", "Phone", "Performed By", "Date" };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#6366f1");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // Data rows
            for (int row = 0; row < logs.Count; row++)
            {
                var log = logs[row];
                var r = row + 2;
                worksheet.Cell(r, 1).Value = row + 1;
                worksheet.Cell(r, 2).Value = log.SamAccountName;
                worksheet.Cell(r, 3).Value = log.DisplayName;
                worksheet.Cell(r, 4).Value = log.Domain;
                worksheet.Cell(r, 5).Value = log.Action == "Disable"
                    ? (isArabic ? "تعطيل" : "Disable")
                    : (isArabic ? "تفعيل" : "Enable");
                worksheet.Cell(r, 6).Value = log.Reason;
                worksheet.Cell(r, 7).Value = log.PreviousStatus;
                worksheet.Cell(r, 8).Value = log.NewStatus;
                worksheet.Cell(r, 9).Value = log.SmsSent
                    ? (isArabic ? "نعم" : "Yes")
                    : (isArabic ? "لا" : "No");
                worksheet.Cell(r, 10).Value = log.PhoneNumber ?? "";
                worksheet.Cell(r, 11).Value = log.PerformedBy;
                worksheet.Cell(r, 12).Value = log.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

                // Alternate row coloring
                if (row % 2 == 1)
                {
                    for (int c = 1; c <= headers.Length; c++)
                        worksheet.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#f8f9fa");
                }
            }

            // Auto-fit columns
            worksheet.Columns().AdjustToContents();

            // Set RTL for Arabic
            if (isArabic)
            {
                worksheet.RightToLeft = true;
            }

            using var ms = new MemoryStream();
            workbook.SaveAs(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Test LDAP connection by binding to the server.
        /// </summary>
        public (bool Success, string Message) TestConnection(string server, int port, string? username, string? password)
        {
            try
            {
                using var connection = CreateConnection(server, port, username, password);
                connection.Bind();
                return (true, "Connection successful");
            }
            catch (LdapException ex)
            {
                _logger.LogWarning(ex, "LDAP connection test failed to {Server}:{Port}", server, port);
                return (false, $"LDAP error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection test failed to {Server}:{Port}", server, port);
                return (false, ex.Message);
            }
        }

        // === Private Helpers ===

        /// <summary>
        /// Account-status domains carry no SSL flag, so the channel is inferred from the port
        /// (<see cref="LdapSecurityMode.Auto"/>): 636/3269 → LDAPS, otherwise Kerberos sign &amp;
        /// seal. Previously this connection was left completely unencrypted.
        /// </summary>
        private static LdapConnection CreateConnection(string server, int port, string? username, string? password)
            => LdapConnectionFactory.Create(new LdapConnectionOptions
            {
                Server = server,
                Port = port,
                Username = username,
                Password = password,
                SecurityMode = LdapSecurityMode.Auto
            });

        private static string? GetAttribute(SearchResultEntry entry, string attributeName)
        {
            if (entry.Attributes.Contains(attributeName))
            {
                var attr = entry.Attributes[attributeName];
                if (attr.Count > 0)
                    return attr[0]?.ToString();
            }
            return null;
        }
    }

    /// <summary>
    /// Represents AD user information returned from a search.
    /// </summary>
    public class AdUserInfo
    {
        public string SamAccountName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string DistinguishedName { get; set; } = string.Empty;
        public bool IsDisabled { get; set; }
        public int UserAccountControl { get; set; }
    }
}
