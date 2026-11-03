using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Monitoring;

namespace OnkelBummel.Api.Tests;

public sealed class JwtTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    public static TheoryData<string, object> Roles => new()
    {
        { "role as string", "onkel-bummel" },
        { "role in an array", new[] { "andere", "onkel-bummel" } },
        { "admin", "admin" },
        { "admin in an array", new[] { "admin" } },
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Lets_users_with_the_role_or_admin_in(string _, object roles)
    {
        var response = await GetTopicsAsync(_factory.Identity.CreateToken(roles: roles));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Turns_users_without_the_role_away()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await GetTopicsAsync(_factory.Identity.CreateToken(roles: "slipways"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetTopicsAsync(_factory.Identity.CreateToken(roles: null))).StatusCode);
    }

    [Fact]
    public async Task Rejects_tokens_for_another_audience_issuer_or_key_and_expired_ones()
    {
        var identity = _factory.Identity;
        var otherKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = identity.Key.KeyId };

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetTopicsAsync(identity.CreateToken(audience: "slipways-api"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetTopicsAsync(identity.CreateToken(issuer: "https://evil.example/"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetTopicsAsync(identity.CreateToken(expires: DateTime.UtcNow.AddMinutes(-10)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetTopicsAsync(identity.CreateToken(key: otherKey))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetTopicsAsync(null)).StatusCode);
    }

    [Fact]
    public async Task Answers_rejections_with_problem_json()
    {
        var response = await GetTopicsAsync(null);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Signed_in_users_may_also_send_to_any_topic()
    {
        var response = await _factory.CreateUserClient().PostAsJsonAsync("/api/v1/messages",
            new { topic = "irgendwas", message = "von der App" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetTopicsAsync(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/api/v1/topics", TestContext.Current.CancellationToken);
    }
}

public sealed class PublishTokenTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Tokens_send_only_to_their_topics()
    {
        var garage = await _factory.CreateSenderClientAsync("garage", "kamera");
        var all = await _factory.CreateSenderClientAsync("*");
        var forbiddenBefore = OnkelBummelMetrics.MessagesReceived.WithLabels("api", "forbidden").Value;

        var allowed = await SendAsync(garage, "kamera");
        var forbidden = await SendAsync(garage, "alarme");
        var wildcard = await SendAsync(all, "alarme");

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Created, wildcard.StatusCode);
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("api", "forbidden").Value >= forbiddenBefore + 1);
    }

    [Fact]
    public async Task Unknown_or_missing_tokens_get_401_and_are_counted()
    {
        var before = OnkelBummelMetrics.MessagesReceived.WithLabels("api", "unauthorized").Value;
        var unknown = _factory.CreateClient();
        unknown.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ob_gibtsnicht");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(unknown, "garage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(_factory.CreateClient(), "garage")).StatusCode);
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("api", "unauthorized").Value >= before + 2);
    }

    [Fact]
    public async Task Tokens_cannot_read()
    {
        var sender = await _factory.CreateSenderClientAsync("*");

        var response = await sender.GetAsync("/api/v1/messages", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Notes_when_a_token_was_last_used()
    {
        var sender = await _factory.CreateSenderClientAsync("garage");

        await SendAsync(sender, "garage");

        await using var db = _factory.CreateDbContext();
        Assert.NotNull((await db.PublishTokens.SingleAsync(TestContext.Current.CancellationToken)).LastUsedAt);
    }

    [Fact]
    public async Task Users_create_list_and_delete_tokens_and_see_the_key_only_once()
    {
        var user = _factory.CreateUserClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        var response = await user.PostAsJsonAsync("/api/v1/publish-tokens", new { name = "Grafana", topics = new[] { "alarme" } }, cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<CreatedPublishToken>(cancellationToken);
        var listed = await user.GetFromJsonAsync<List<PublishTokenInfo>>("/api/v1/publish-tokens", cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(created);
        Assert.StartsWith("ob_", created.Token);
        Assert.True(created.Token.Length > 40);
        var token = Assert.Single(listed!);
        Assert.Equal("Grafana", token.Name);
        Assert.Equal(["alarme"], token.Topics);
        await using (var db = _factory.CreateDbContext())
            Assert.Equal(PublishTokens.Hash(created.Token), (await db.PublishTokens.SingleAsync(cancellationToken)).Hash);

        var sender = _factory.CreateClient();
        sender.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(sender, "alarme")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/v1/publish-tokens/{created.Id}", cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.DeleteAsync($"/api/v1/publish-tokens/{created.Id}", cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(sender, "alarme")).StatusCode);
    }

    [Theory]
    [InlineData(null, new[] { "garage" })]
    [InlineData("Skript", new string[0])]
    [InlineData("Skript", new[] { "Garage" })]
    public async Task Rejects_tokens_without_name_or_valid_topics(string? name, string[] topics)
    {
        var response = await _factory.CreateUserClient().PostAsJsonAsync("/api/v1/publish-tokens", new { name, topics }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_command_line_creates_lists_and_deletes_tokens()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var created = await TokenCommands.RunAsync(["--create-token", "grafana", "alarme,garage"], _factory.Services, output, error);
        var token = output.ToString().Split('\n').Single(l => l.StartsWith("ob_", StringComparison.Ordinal)).Trim();
        var sender = _factory.CreateClient();
        sender.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var sent = await SendAsync(sender, "garage");

        await using var db = _factory.CreateDbContext();
        var id = (await db.PublishTokens.SingleAsync(TestContext.Current.CancellationToken)).Id;
        output.GetStringBuilder().Clear();
        var listed = await TokenCommands.RunAsync(["--list-tokens"], _factory.Services, output, error);
        var list = output.ToString();
        var deleted = await TokenCommands.RunAsync(["--delete-token", id], _factory.Services, output, error);
        var invalid = await TokenCommands.RunAsync(["--create-token", "x", "API"], _factory.Services, output, error);

        Assert.Equal((0, 0, 0, 1), (created, listed, deleted, invalid));
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        Assert.Contains("grafana  [alarme, garage]", list);
        Assert.Equal(0, await db.PublishTokens.CountAsync(TestContext.Current.CancellationToken));
        Assert.True(TokenCommands.IsCommand(["--create-token", "a", "b"]));
        Assert.False(TokenCommands.IsCommand(["--healthcheck"]));
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string topic) =>
        client.PostAsJsonAsync("/api/v1/messages", new { topic, message = "Test" }, TestContext.Current.CancellationToken);
}
