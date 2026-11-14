using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Slskarr.Data;

/// <summary>Lets <c>dotnet ef</c> build the context without booting the host (which validates options).</summary>
public sealed class SlskarrDbDesignTimeFactory : IDesignTimeDbContextFactory<SlskarrDb>
{
    public SlskarrDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SlskarrDb>().UseSqlite("Data Source=design-time.db").Options);
}
