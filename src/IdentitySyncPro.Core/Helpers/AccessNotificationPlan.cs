namespace IdentitySyncPro.Core.Helpers
{
    /// <summary>
    /// Who a governance email goes to, decided before anything is sent.
    ///
    /// Split out from the sending so the choice can be tested: an approval request delivered to
    /// nobody is the failure this module is most exposed to. The request is raised, the row says
    /// "Pending", the screen shows a queue — and the person who could clear it never learned it
    /// existed. There is no error anywhere in that sequence.
    ///
    /// So "nobody to notify" is a result this type carries explicitly, rather than an empty list
    /// the caller may or may not look at.
    /// </summary>
    public static class AccessNotificationPlan
    {
        /// <param name="Recipients">Addresses to send to, de-duplicated. Empty when nobody could be found.</param>
        /// <param name="Unreachable">
        /// Approvers who were named but whose address could not be resolved. Recorded so a partial
        /// delivery is visible as partial, instead of reading as a complete one.
        /// </param>
        public sealed record Plan(IReadOnlyList<string> Recipients, IReadOnlyList<string> Unreachable)
        {
            public bool HasRecipients => Recipients.Count > 0;
        }

        /// <summary>
        /// Builds the approver recipient list.
        ///
        /// The configured notification mailbox is preferred because it is the one address an
        /// administrator chose deliberately — usually the approver group's own distribution list.
        /// Individual addresses resolved from the directory are the fallback for installations that
        /// have not set one.
        /// </summary>
        /// <param name="notificationEmail">The catalog item's approver mailbox, if configured.</param>
        /// <param name="approverUsers">Named console approvers.</param>
        /// <param name="resolvedAddresses">
        /// Address found in the directory for each named approver, keyed by username. A username
        /// absent from this map, or mapped to a blank, could not be reached.
        /// </param>
        public static Plan ForApprovers(
            string? notificationEmail,
            IEnumerable<string> approverUsers,
            IReadOnlyDictionary<string, string?> resolvedAddresses)
        {
            var recipients = new List<string>();
            var unreachable = new List<string>();

            foreach (var address in SplitAddresses(notificationEmail))
                recipients.Add(address);

            foreach (var user in approverUsers)
            {
                var address = resolvedAddresses.TryGetValue(user, out var found) ? found : null;
                if (string.IsNullOrWhiteSpace(address)) unreachable.Add(user);
                else recipients.Add(address!.Trim());
            }

            return new Plan(Distinct(recipients), unreachable);
        }

        /// <summary>One person's address, or an empty plan naming them as unreachable.</summary>
        public static Plan ForPerson(string username, string? address) =>
            string.IsNullOrWhiteSpace(address)
                ? new Plan(Array.Empty<string>(), new[] { username })
                : new Plan(new[] { address.Trim() }, Array.Empty<string>());

        /// <summary>Splits a configured recipient field on the separators people actually type.</summary>
        public static IReadOnlyList<string> SplitAddresses(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Where(a => a.Contains('@'))
                       .ToList();

        private static IReadOnlyList<string> Distinct(IEnumerable<string> addresses) =>
            addresses.Where(a => !string.IsNullOrWhiteSpace(a))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }
}
