using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slskarr.Configuration;

namespace Slskarr.Data;

public static class DataServiceCollectionExtensions
{
    public const string DatabaseFileName = "slskarr.db";

    /// <summary>
    /// Registers <see cref="IDbContextFactory{TContext}"/> (for singletons and background services)
    /// and a scoped <see cref="SlskarrDb"/> (for endpoints), both on <c>{DataPath}/slskarr.db</c>.
    /// </summary>
    public static IServiceCollection AddSlskarrData(this IServiceCollection services)
    {
        services.AddDbContextFactory<SlskarrDb>((sp, builder) =>
        {
            var dataPath = sp.GetRequiredService<IOptions<SlskarrOptions>>().Value.DataPath;
            Directory.CreateDirectory(dataPath);
            builder.UseSqlite($"Data Source={Path.Combine(dataPath, DatabaseFileName)}");
        });
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<SlskarrDb>>().CreateDbContext());
        return services;
    }

    public static void MigrateSlskarrDb(this IServiceProvider services)
    {
        using var db = services.GetRequiredService<IDbContextFactory<SlskarrDb>>().CreateDbContext();
        db.Database.Migrate();
    }
}
