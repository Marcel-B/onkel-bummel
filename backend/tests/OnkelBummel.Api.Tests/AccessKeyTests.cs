using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OnkelBummel.Api.Auth;

namespace OnkelBummel.Api.Tests;

/// <summary>A server of one's own without identity server: in with the access key.</summary>
public sealed class AccessKeyTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();
    private WebApplicationFactory<Program> _server = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _server = _factory.WithWebHostBuilder(b => b.UseSetting("Oidc:Issuer", null));
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Makes_a_key_on_the_first_start_and_keeps_only_its_hash()
    {
        _server.CreateClient();
        var keys = _server.Services.GetRequiredService<AccessKeyStore>();
        var file = Path.Combine(_factory.DataPath, "access-key.sha256");

        Assert.True(keys.IsActive);
        Assert.True(File.Exists(file));
        Assert.Matches("^[0-9a-f]{64}$", await File.ReadAllTextAsync(file, CancellationToken));
    }

    [Fact]
    public async Task Tells_the_clients_to_ask_for_the_key()
    {
        var config = await _server.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/config", CancellationToken);
        var withIdentityServer = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/config", CancellationToken);

        Assert.Equal("key", config.GetProperty("login").GetString());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("issuer").ValueKind);
        Assert.Equal("oidc", withIdentityServer.GetProperty("login").GetString());
    }

    [Fact]
    public async Task Signs_the_web_interface_in_with_the_key()
    {
        var key = NewKey();
        var browser = CreateBrowser();

        var before = await browser.GetAsync("/auth/me", CancellationToken);
        var login = await browser.GetAsync("/auth/login?returnUrl=/topics/garage", CancellationToken);
        var wrong = await browser.PostAsJsonAsync("/auth/key", new { key = "obk_falsch" }, CancellationToken);
        var right = await browser.PostAsJsonAsync("/auth/key", new { key }, CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/topics/garage", login.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.Contains(right.Headers.GetValues("Set-Cookie"), c => c.StartsWith("onkel-bummel=", StringComparison.Ordinal) && c.Contains("httponly"));
        Assert.Equal(new WebUserDto("admin", "Admin"), await browser.GetFromJsonAsync<WebUserDto>("/auth/me", CancellationToken));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/topics", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/publish-tokens", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Takes_the_key_as_bearer_for_reading_and_sending()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", NewKey());

        var sent = await client.PostAsJsonAsync("/api/v1/messages", new { topic = "garage", message = "Tor offen" }, CancellationToken);
        var read = await client.GetFromJsonAsync<JsonElement>("/api/v1/messages", CancellationToken);

        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Equal(1, read.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Ends_sessions_and_old_keys_with_a_new_key()
    {
        var old = NewKey();
        var browser = CreateBrowser();
        await browser.PostAsJsonAsync("/auth/key", new { key = old }, CancellationToken);
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", old);

        NewKey();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/topics", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Ignores_the_key_on_a_server_with_identity_server()
    {
        // A key left over from before the identity server was set up.
        var key = NewKey();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var browser = _factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });

        var login = await browser.PostAsJsonAsync("/auth/key", new { key }, CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/topics", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Makes_a_new_key_on_the_command_line()
    {
        var old = NewKey();
        var output = new StringWriter();

        var exit = await TokenCommands.RunAsync([TokenCommands.NewAccessKey], _server.Services, output, TextWriter.Null);
        var key = output.ToString().Split('\n').Single(l => l.StartsWith(AccessKeyStore.Prefix, StringComparison.Ordinal)).Trim();
        var keys = _server.Services.GetRequiredService<AccessKeyStore>();

        Assert.Equal(0, exit);
        Assert.True(keys.Matches(key));
        Assert.False(keys.Matches(old));
        Assert.Equal(1, await TokenCommands.RunAsync([TokenCommands.NewAccessKey], _factory.Services, TextWriter.Null, TextWriter.Null));
    }

    private string NewKey() => _server.Services.GetRequiredService<AccessKeyStore>().Create();

    private HttpClient CreateBrowser() => _server.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
}
