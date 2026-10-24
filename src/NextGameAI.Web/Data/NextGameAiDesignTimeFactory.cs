using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NextGameAI.Web.Data;

// EF tooling must never run Program.cs or discover the personal connection when scaffolding migrations.
public sealed class NextGameAiDesignTimeFactory : IDesignTimeDbContextFactory<NextGameAiDbContext>
{
    public NextGameAiDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<NextGameAiDbContext>()
            .UseSqlite("Data Source=:memory:;Foreign Keys=True").Options);
}
