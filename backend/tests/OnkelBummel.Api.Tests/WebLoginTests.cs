using System.Net;
using System.Net.Http.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using OnkelBummel.Api.Auth;

namespace OnkelBummel.Api.Tests;

public sealed class WebLoginTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Signs_in_at_the_identity_server_and_keeps_only_a_cookie()
    {
        var browser = CreateBrowser();

        var login = await browser.GetAsync("/auth/login?returnUrl=/topics/garage", CancellationToken);
        var authorize = login.Headers.Location!;
        var query = HttpUtility.ParseQueryString(authorize.Query);
        var callback = await CompleteLoginAsync(browser, authorize);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("https://identity.example.ts.net/connect/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal("onkel-bummel-web", query["client_id"]);
        Assert.Equal("https://localhost/auth/callback", query["redirect_uri"]);
        // Query is the default for the code flow; form_post would lose the SameSite=Lax cookies.
        Assert.Null(query["response_mode"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("openid profile roles", query["scope"]);
        Assert.Equal("/topics/garage", callback.Headers.Location?.OriginalString);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith("onkel-bummel=", StringComparison.Ordinal) && c.Contains("httponly") && c.Contains("samesite=lax"));

        var me = await browser.GetFromJsonAsync<WebUserDto>("/auth/me", CancellationToken);
        Assert.Equal(new WebUserDto("user-1", "marcel"), me);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/v1/topics", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Lets_cookie_changes_through_only_with_the_header_of_the_web_interface()
    {
        var browser = CreateBrowser();
        await SignInAsync(browser);

        var withoutHeader = await browser.PostAsync("/api/v1/messages/read", null, CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages/read");
        request.Headers.Add(WebLogin.RequestedWithHeader, "XMLHttpRequest");
        var withHeader = await browser.SendAsync(request, CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        Assert.Equal("application/problem+json", withoutHeader.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
    }

    [Fact]
    public async Task Answers_without_redirects_when_the_cookie_is_missing_or_the_role_is()
    {
        var anonymous = CreateBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/auth/me", CancellationToken)).StatusCode);

        _factory.Identity.WebLoginRoles = "slipways";
        var stranger = CreateBrowser();
        await SignInAsync(stranger);

        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/auth/me", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/v1/topics", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Signs_out_here_and_at_the_identity_server()
    {
        var browser = CreateBrowser();
        await SignInAsync(browser);

        var logout = await browser.PostAsync("/auth/logout", null, CancellationToken);
        var endSession = HttpUtility.ParseQueryString(logout.Headers.Location!.Query);

        Assert.Equal("https://identity.example.ts.net/connect/endsession", logout.Headers.Location.GetLeftPart(UriPartial.Path));
        Assert.Equal("https://localhost/auth/signed-out", endSession["post_logout_redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(endSession["id_token_hint"]));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Keeps_the_api_working_without_an_identity_server()
    {
        var sender = await _factory.CreateSenderClientAsync("*");
        var client = _factory.WithWebHostBuilder(b => b.UseSetting("Oidc:Issuer", null)).CreateClient();
        client.DefaultRequestHeaders.Authorization = sender.DefaultRequestHeaders.Authorization;

        var sent = await client.PostAsJsonAsync("/api/v1/messages", new { topic = "garage", message = "Tor offen" }, CancellationToken);
        var login = await client.GetAsync("/auth/login", CancellationToken);

        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
    }

    [Theory]
    [InlineData("/topics/garage", "/topics/garage")]
    [InlineData("/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData(null, "/")]
    public void Returns_only_to_paths_of_this_server(string? returnUrl, string expected) =>
        Assert.Equal(expected, WebLogin.LocalOrRoot(returnUrl));

    [Fact]
    public async Task Serves_the_app_for_its_own_routes_but_not_for_unknown_api_paths()
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot-test");
        Directory.CreateDirectory(wwwroot);
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "index.html"), "<!doctype html><title>Onkel Bummel</title>", CancellationToken);
        await using var factory = new OnkelBummelApiFactory();
        await factory.InitializeAsync();
        var client = factory.WithWebHostBuilder(b => b.UseSetting("webroot", wwwroot)).CreateClient();

        var api = await client.GetAsync("/api/v1/gibt-es-nicht", CancellationToken);

        foreach (var path in new[] { "/", "/topics/garage/galerie" })
        {
            var page = await client.GetAsync(path, CancellationToken);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
            Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());
        }
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
    }

    // https as behind "tailscale serve": the correlation cookie of the login is always Secure.
    private HttpClient CreateBrowser() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });

    private async Task SignInAsync(HttpClient browser)
    {
        var login = await browser.GetAsync("/auth/login", CancellationToken);
        var callback = await CompleteLoginAsync(browser, login.Headers.Location!);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
    }

    /// <summary>Does what the identity server does after the user signed in: back to the callback with a code.</summary>
    private async Task<HttpResponseMessage> CompleteLoginAsync(HttpClient browser, Uri authorize)
    {
        var query = HttpUtility.ParseQueryString(authorize.Query);
        _factory.Identity.WebLoginNonce = query["nonce"];
        var state = Uri.EscapeDataString(query["state"]!);
        return await browser.GetAsync($"/auth/callback?code=test-code&state={state}", CancellationToken);
    }
}
