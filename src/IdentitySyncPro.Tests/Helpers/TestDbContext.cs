using IdentitySyncPro.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace IdentitySyncPro.Tests.Helpers
{
    /// <summary>
    /// Creates an in-memory AppDbContext for unit testing.
    /// Each test gets a fresh database instance.
    /// </summary>
    public static class TestDbContext
    {
        public static AppDbContext Create(string? dbName = null)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: dbName ?? Guid.NewGuid().ToString())
                .Options;

            var context = new AppDbContext(options);
            context.Database.EnsureCreated();
            return context;
        }
    }
}
