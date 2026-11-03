using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OnkelBummel.Api.Data;

namespace OnkelBummel.Api.Tests;

/// <summary>Runs the server against its own data folder with a fresh SQLite database, deleted afterwards.</summary>
public sealed class OnkelBummelApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "onkel-bummel-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:InitializeOnStartup", "false");
        builder.UseSetting("Storage:DataPath", DataPath);
        builder.UseSetting("Monitoring:InventoryEnabled", "false");
        builder.UseSetting("Oidc:Issuer", "https://identity.example.ts.net");
    }

    public OnkelBummelDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<OnkelBummelDbContext>();

    public async ValueTask InitializeAsync() => await Services.InitializeDatabaseAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        // SQLite keeps pooled connections open; they hold the file until cleared.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataPath))
            Directory.Delete(DataPath, recursive: true);
    }
}
