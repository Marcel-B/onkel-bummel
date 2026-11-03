using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Data;

public static class DatabaseSetup
{
    public static IServiceCollection AddDatabase(this IServiceCollection services) =>
        services.AddDbContext<OnkelBummelDbContext>((provider, options) =>
            options.UseSqlite($"Data Source={provider.GetRequiredService<IOptions<StorageOptions>>().Value.DatabasePath}"));

    /// <summary>Creates the data folders, applies pending migrations and switches the database to write-ahead logging.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        Directory.CreateDirectory(storage.DataPath);
        Directory.CreateDirectory(storage.AttachmentsPath);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OnkelBummelDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        // Readers (the app, live updates, the backup) then no longer wait for a writer. The mode sticks to the file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }
}
