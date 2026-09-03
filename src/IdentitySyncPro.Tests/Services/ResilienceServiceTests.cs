using IdentitySyncPro.Infrastructure.Services;
using IdentitySyncPro.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IdentitySyncPro.Tests.Services
{
    public class ResilienceServiceTests
    {
        private readonly Mock<ILogger<ResilienceService>> _logger = new();

        private ResilienceService CreateService()
        {
            var services = new ServiceCollection();
            services.AddDbContext<Infrastructure.Data.AppDbContext>(opt =>
                opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            return new ResilienceService(scopeFactory, _logger.Object);
        }

        [Fact]
        public void CircuitBreaker_InitiallyClosed()
        {
            var service = CreateService();

            Assert.False(service.IsCircuitOpen("Oracle"));
        }

        [Fact]
        public void CircuitBreaker_OpensAfterThreshold()
        {
            var service = CreateService();

            // Record 3 consecutive failures (threshold)
            service.RecordFailure("Oracle", "Connection failed");
            service.RecordFailure("Oracle", "Connection failed");
            service.RecordFailure("Oracle", "Connection failed");

            Assert.True(service.IsCircuitOpen("Oracle"));
        }

        [Fact]
        public void CircuitBreaker_SuccessResetsCounter()
        {
            var service = CreateService();

            service.RecordFailure("Oracle", "Error 1");
            service.RecordFailure("Oracle", "Error 2");
            service.RecordSuccess("Oracle"); // Reset
            service.RecordFailure("Oracle", "Error 3");

            // Should still be closed — only 1 consecutive failure after reset
            Assert.False(service.IsCircuitOpen("Oracle"));
        }

        [Fact]
        public void CircuitBreaker_IndependentPerComponent()
        {
            var service = CreateService();

            service.RecordFailure("Oracle", "Error");
            service.RecordFailure("Oracle", "Error");
            service.RecordFailure("Oracle", "Error");

            Assert.True(service.IsCircuitOpen("Oracle"));
            Assert.False(service.IsCircuitOpen("ActiveDirectory")); // Separate component
        }

        [Fact]
        public void GetComponentsHealth_ReturnsAllTrackedComponents()
        {
            var service = CreateService();

            service.RecordSuccess("Oracle");
            service.RecordFailure("AD", "Timeout");

            var health = service.GetComponentsHealth();

            Assert.Equal(2, health.Count);
            Assert.Contains(health, h => h.Name == "Oracle" && h.Status == "Healthy");
            Assert.Contains(health, h => h.Name == "AD" && h.Status == "Degraded");
        }

        [Fact]
        public async Task ExecuteWithRetry_SucceedsOnFirstTry()
        {
            var service = CreateService();
            var callCount = 0;

            var result = await service.ExecuteWithRetryAsync("Test.Operation", async () =>
            {
                callCount++;
                return "success";
            });

            Assert.Equal("success", result);
            Assert.Equal(1, callCount);
        }

        [Fact]
        public async Task ExecuteWithRetry_RetriesOnFailure()
        {
            var service = CreateService();
            var callCount = 0;

            var result = await service.ExecuteWithRetryAsync("Test.Operation", async () =>
            {
                callCount++;
                if (callCount < 3)
                    throw new Exception("Transient error");
                return "recovered";
            });

            Assert.Equal("recovered", result);
            Assert.Equal(3, callCount);
        }

        [Fact]
        public async Task ExecuteWithRetry_ThrowsAfterMaxRetries()
        {
            var service = CreateService();

            await Assert.ThrowsAsync<Exception>(() =>
                service.ExecuteWithRetryAsync<string>("Test.Operation", () =>
                    throw new Exception("Persistent error")));
        }

        [Fact]
        public async Task ExecuteWithRetry_CircuitOpen_ThrowsImmediately()
        {
            var service = CreateService();

            // Open the circuit
            service.RecordFailure("Test", "err");
            service.RecordFailure("Test", "err");
            service.RecordFailure("Test", "err");

            await Assert.ThrowsAsync<CircuitBreakerOpenException>(() =>
                service.ExecuteWithRetryAsync<string>("Test.Operation", () =>
                    Task.FromResult("should not execute")));
        }
    }
}
