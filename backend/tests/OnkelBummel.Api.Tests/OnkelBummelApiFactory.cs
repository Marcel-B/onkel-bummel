using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OnkelBummel.Api.Actions;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Jobs;
using OnkelBummel.Api.Publishing;

namespace OnkelBummel.Api.Tests;

/// <summary>Runs the server against its own data folder with a fresh SQLite database, deleted afterwards.</summary>
public sealed class OnkelBummelApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DataPath { get; } = Path.Combine(Path.GetTempPath(), "onkel-bummel-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Signs access tokens and serves discovery and JWKS in place of the identity server.</summary>
    public TestIdentity Identity { get; } = new();

    /// <summary>Stands in for everything the server calls: action targets, image URLs and Nextcloud.</summary>
    public StubHttp Remote { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:InitializeOnStartup", "false");
        builder.UseSetting("Storage:DataPath", DataPath);
        builder.UseSetting("Monitoring:InventoryEnabled", "false");
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Oidc:Issuer", "https://identity.example.ts.net");
        builder.ConfigureTestServices(services =>
        {
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = Identity.Backchannel);
            foreach (var name in new[] { ActionRunner.HttpClientName, AttachmentStore.HttpClientName, BackupJob.HttpClientName })
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Remote);
        });
    }

    public OnkelBummelDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<OnkelBummelDbContext>();

    /// <summary>A client signed in as the user, with the role "onkel-bummel" unless said otherwise.</summary>
    public HttpClient CreateUserClient(object? roles = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Identity.CreateToken(roles: roles ?? "onkel-bummel"));
        return client;
    }

    /// <summary>A client with a fresh publish token for the topics.</summary>
    public async Task<HttpClient> CreateSenderClientAsync(params string[] topics)
    {
        await using var db = CreateDbContext();
        var token = await PublishTokens.CreateAsync(db, "test", [.. topics], TimeProvider.System, TestContext.Current.CancellationToken);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

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
