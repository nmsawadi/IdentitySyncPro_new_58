using System.Collections.Concurrent;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Central static registry that tracks CancellationTokenSource instances
    /// for each running service execution.
    /// Allows the controller to cancel a running service by its ID.
    /// </summary>
    public static class SvcCancellationRegistry
    {
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> _tokens = new();

        /// <summary>
        /// Register a linked CancellationTokenSource for a service.
        /// Links with the Hangfire-provided token so that both manual cancel
        /// and Hangfire shutdown will trigger cancellation.
        /// </summary>
        public static CancellationTokenSource Register(int serviceId, CancellationToken hangfireToken)
        {
            // If there's an existing one (stale), dispose and remove it
            if (_tokens.TryRemove(serviceId, out var existing))
            {
                try { existing.Dispose(); } catch { /* ignore */ }
            }

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(hangfireToken);
            _tokens[serviceId] = linkedCts;
            return linkedCts;
        }

        /// <summary>
        /// Cancel a running service by its ID.
        /// Returns true if the service was found and cancellation was requested.
        /// </summary>
        public static bool Cancel(int serviceId)
        {
            if (_tokens.TryGetValue(serviceId, out var cts))
            {
                try
                {
                    if (!cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                        return true;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed — remove stale entry
                    _tokens.TryRemove(serviceId, out _);
                }
            }
            return false;
        }

        /// <summary>
        /// Remove the registration after execution completes (success, failure, or cancel).
        /// Also disposes the CancellationTokenSource.
        /// </summary>
        public static void Remove(int serviceId)
        {
            if (_tokens.TryRemove(serviceId, out var cts))
            {
                try { cts.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Check if a service is currently registered (i.e., running).
        /// </summary>
        public static bool IsRegistered(int serviceId) => _tokens.ContainsKey(serviceId);
    }
}
