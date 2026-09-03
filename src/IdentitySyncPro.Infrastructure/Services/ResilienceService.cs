using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IdentitySyncPro.Core.Models.Resilience;
using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IdentitySyncPro.Infrastructure.Services
{
    /// <summary>
    /// Resilience service providing Circuit Breaker, Retry, Quarantine, and Dead Letter Queue.
    /// 
    /// Circuit Breaker: After N consecutive failures, opens the circuit and stops attempts.
    /// Retry: Exponential backoff with configurable max retries.
    /// Quarantine: Isolates identities that fail repeatedly.
    /// Dead Letter: Saves failed operations for manual replay.
    /// </summary>
    public class ResilienceService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ResilienceService> _logger;

        // Circuit Breaker state per component
        private readonly ConcurrentDictionary<string, CircuitState> _circuits = new();

        // Configuration
        private const int CircuitBreakerThreshold = 3;
        private const int CircuitBreakerCooldownSec = 60;
        private const int MaxRetries = 3;
        private const int QuarantineThreshold = 5;

        public ResilienceService(IServiceScopeFactory scopeFactory, ILogger<ResilienceService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        // ═══════════════════════════════════════
        // CIRCUIT BREAKER
        // ═══════════════════════════════════════

        /// <summary>Check if a circuit is open (should not attempt operations).</summary>
        public bool IsCircuitOpen(string component)
        {
            if (!_circuits.TryGetValue(component, out var state)) return false;

            if (state.IsOpen && DateTime.UtcNow > state.OpenedAt.AddSeconds(CircuitBreakerCooldownSec))
            {
                state.IsOpen = false;
                state.ConsecutiveFailures = 0;
                _logger.LogInformation("Circuit breaker CLOSED for {Component} (cooldown expired)", component);
            }

            return state.IsOpen;
        }

        /// <summary>
        /// The failure that most recently counted against a component, or null.
        ///
        /// Exists so the message shown when a run stops can name the actual cause. "Repeated
        /// connection failure — check the domain controller is reachable" is the right advice for
        /// an outage and the wrong advice for a service account missing its delegation, and both
        /// open the same breaker.
        /// </summary>
        public string? GetLastError(string component)
            => _circuits.TryGetValue(component, out var state) ? state.LastError : null;

        /// <summary>Record a successful operation (resets failure counter).</summary>
        public void RecordSuccess(string component)
        {
            var state = _circuits.GetOrAdd(component, _ => new CircuitState());
            state.ConsecutiveFailures = 0;
            state.IsOpen = false;
            state.LastSuccess = DateTime.UtcNow;
        }

        /// <summary>Record a failed operation (may open the circuit).</summary>
        public void RecordFailure(string component, string? error = null)
        {
            var state = _circuits.GetOrAdd(component, _ => new CircuitState());
            state.ConsecutiveFailures++;
            state.LastFailure = DateTime.UtcNow;
            state.LastError = error;

            if (state.ConsecutiveFailures >= CircuitBreakerThreshold && !state.IsOpen)
            {
                state.IsOpen = true;
                state.OpenedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "Circuit breaker OPEN for {Component} after {Failures} consecutive failures. Cooldown: {Cooldown}s",
                    component, state.ConsecutiveFailures, CircuitBreakerCooldownSec);
            }
        }

        /// <summary>Get the health status of all components.</summary>
        public List<ComponentHealth> GetComponentsHealth()
        {
            return _circuits.Select(kv => new ComponentHealth
            {
                Name = kv.Key,
                Status = kv.Value.IsOpen ? "Open" : kv.Value.ConsecutiveFailures > 0 ? "Degraded" : "Healthy",
                ConsecutiveFailures = kv.Value.ConsecutiveFailures,
                CircuitOpen = kv.Value.IsOpen,
                Details = kv.Value.LastError,
                LastChecked = kv.Value.LastFailure > kv.Value.LastSuccess ? kv.Value.LastFailure : kv.Value.LastSuccess
            }).ToList();
        }

        // ═══════════════════════════════════════
        // RETRY WITH EXPONENTIAL BACKOFF
        // ═══════════════════════════════════════

        /// <summary>Execute an action with retry logic and exponential backoff.</summary>
        public async Task<T> ExecuteWithRetryAsync<T>(
            string operationName,
            Func<Task<T>> action,
            CancellationToken ct = default)
        {
            var component = operationName.Split('.').FirstOrDefault() ?? operationName;

            if (IsCircuitOpen(component))
                throw new CircuitBreakerOpenException($"Circuit breaker is OPEN for {component}");

            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    var result = await action();
                    RecordSuccess(component);
                    return result;
                }
                catch (Exception ex) when (attempt < MaxRetries)
                {
                    lastException = ex;
                    var delay = (int)Math.Pow(2, attempt) * 500; // 1s, 2s, 4s
                    _logger.LogWarning(
                        "Retry {Attempt}/{Max} for {Operation}: {Error}. Waiting {Delay}ms",
                        attempt, MaxRetries, operationName, ex.Message, delay);

                    await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    RecordFailure(component, ex.Message);
                }
            }

            throw lastException ?? new Exception($"Operation {operationName} failed after {MaxRetries} retries");
        }

        // ═══════════════════════════════════════
        // QUARANTINE
        // ═══════════════════════════════════════

        /// <summary>Check if a identity should be quarantined and quarantine if threshold exceeded.</summary>
        public async Task<bool> CheckAndQuarantineAsync(int identityId, string error, string operation)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existing = await db.QuarantinedIdentities
                .FirstOrDefaultAsync(q => q.IdentityId == identityId && !q.IsResolved);

            if (existing != null)
            {
                existing.FailureCount++;
                existing.LastError = error;
                existing.FailedOperation = operation;
                await db.SaveChangesAsync();
                return true;
            }

            // Count recent failures for this identity
            var recentFailures = await db.SyncOperations
                .CountAsync(o => o.IdentityId == identityId
                    && o.Status == Core.Enums.SyncOperationStatus.Failed
                    && o.Timestamp >= DateTime.UtcNow.AddDays(-7));

            if (recentFailures >= QuarantineThreshold)
            {
                db.QuarantinedIdentities.Add(new QuarantinedIdentity
                {
                    IdentityId = identityId,
                    Reason = $"Exceeded {QuarantineThreshold} failures in 7 days",
                    FailureCount = recentFailures,
                    LastError = error,
                    FailedOperation = operation
                });
                await db.SaveChangesAsync();

                _logger.LogWarning("Identity {IdentityId} QUARANTINED: {Count} failures", identityId, recentFailures);
                return true;
            }

            return false;
        }

        /// <summary>Check if identity is quarantined.</summary>
        public async Task<bool> IsQuarantinedAsync(int identityId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.QuarantinedIdentities.AnyAsync(q => q.IdentityId == identityId && !q.IsResolved);
        }

        // ═══════════════════════════════════════
        // DEAD LETTER QUEUE
        // ═══════════════════════════════════════

        /// <summary>Add a failed operation to the dead letter queue.</summary>
        public async Task AddToDeadLetterAsync(int identityId, string operationType, string error, object? payload = null)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.DeadLetterEntries.Add(new DeadLetterEntry
            {
                IdentityId = identityId,
                OperationType = operationType,
                ErrorMessage = error,
                Payload = payload != null ? JsonSerializer.Serialize(payload) : null,
                RetryCount = MaxRetries
            });

            await db.SaveChangesAsync();
            _logger.LogInformation("Added identity {IdentityId} ({Operation}) to dead letter queue", identityId, operationType);
        }

        /// <summary>Get dead letter queue statistics.</summary>
        public async Task<(int Pending, int Replayed)> GetDeadLetterStatsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pending = await db.DeadLetterEntries.CountAsync(d => !d.IsReplayed);
            var replayed = await db.DeadLetterEntries.CountAsync(d => d.IsReplayed);
            return (pending, replayed);
        }

        // ═══════════════════════════════════════
        // INTERNAL STATE
        // ═══════════════════════════════════════

        private class CircuitState
        {
            public int ConsecutiveFailures { get; set; }
            public bool IsOpen { get; set; }
            public DateTime OpenedAt { get; set; }
            public DateTime LastSuccess { get; set; } = DateTime.UtcNow;
            public DateTime LastFailure { get; set; }
            public string? LastError { get; set; }
        }
    }

    public class CircuitBreakerOpenException : Exception
    {
        public CircuitBreakerOpenException(string message) : base(message) { }
    }
}
