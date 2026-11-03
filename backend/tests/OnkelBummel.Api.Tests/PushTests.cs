using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Endpoints;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;
using OnkelBummel.Api.Push;

namespace OnkelBummel.Api.Tests;

public sealed class DeviceTests : IAsyncLifetime
{
    private static readonly string Token = new('a', 64);

    private readonly OnkelBummelApiFactory _factory = new();
    private HttpClient _user = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _user = _factory.CreateUserClient();
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Registers_a_device_for_all_topics_and_knows_it_again_by_its_token()
    {
        var first = await _user.PostAsJsonAsync("/api/v1/devices", new { name = "Marcels iPhone", apnsToken = Token.ToUpperInvariant(), environment = "sandbox" }, CancellationToken);
        var again = await _user.PostAsJsonAsync("/api/v1/devices", new { name = "iPhone 17", apnsToken = Token, environment = "production" }, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<DeviceDto>(CancellationToken))!;
        var updated = (await again.Content.ReadFromJsonAsync<DeviceDto>(CancellationToken))!;
        Assert.Equal(["*"], created.Topics);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(("iPhone 17", "production"), (updated.Name, updated.Environment));
        await using var db = _factory.CreateDbContext();
        var device = await db.Devices.SingleAsync(CancellationToken);
        Assert.Equal(Token, device.ApnsToken);
    }

    [Fact]
    public async Task Keeps_the_device_and_its_subscriptions_when_the_token_changes()
    {
        var device = await RegisterAsync(Token, topics: ["garage"]);
        var newToken = new string('b', 64);

        var response = await _user.PostAsJsonAsync("/api/v1/devices", new { id = device.Id, apnsToken = newToken, environment = "sandbox" }, CancellationToken);

        var updated = (await response.Content.ReadFromJsonAsync<DeviceDto>(CancellationToken))!;
        Assert.Equal(device.Id, updated.Id);
        Assert.Equal(["garage"], updated.Topics);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(newToken, (await db.Devices.SingleAsync(CancellationToken)).ApnsToken);
    }

    [Fact]
    public async Task Replaces_subscriptions_and_lets_a_star_stand_for_everything()
    {
        var device = await RegisterAsync(Token);

        var some = await _user.PutAsJsonAsync($"/api/v1/devices/{device.Id}/subscriptions", new { topics = new[] { "garage", "filomat", "garage" } }, CancellationToken);
        var all = await _user.PutAsJsonAsync($"/api/v1/devices/{device.Id}/subscriptions", new { topics = new[] { "garage", "*" } }, CancellationToken);
        var invalid = await _user.PutAsJsonAsync($"/api/v1/devices/{device.Id}/subscriptions", new { topics = new[] { "Garage!" } }, CancellationToken);

        Assert.Equal(["filomat", "garage"], (await some.Content.ReadFromJsonAsync<DeviceDto>(CancellationToken))!.Topics);
        Assert.Equal(["*"], (await all.Content.ReadFromJsonAsync<DeviceDto>(CancellationToken))!.Topics);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var listed = await _user.GetFromJsonAsync<List<DeviceDto>>("/api/v1/devices", CancellationToken);
        Assert.Equal(["*"], Assert.Single(listed!).Topics);
    }

    [Theory]
    [InlineData("nicht-hex", "sandbox")]
    [InlineData("abcd", "sandbox")]
    [InlineData(null, "sandbox")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "development")]
    public async Task Rejects_tokens_that_are_no_apns_tokens_and_unknown_environments(string? token, string environment)
    {
        var response = await _user.PostAsJsonAsync("/api/v1/devices", new { apnsToken = token, environment }, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Signs_devices_off_and_keeps_other_users_away_from_them()
    {
        var device = await RegisterAsync(Token);
        var stranger = _factory.CreateUserClient(subject: "user-2");

        var foreignDelete = await stranger.DeleteAsync($"/api/v1/devices/{device.Id}", CancellationToken);
        var foreignList = await stranger.GetFromJsonAsync<List<DeviceDto>>("/api/v1/devices", CancellationToken);
        var delete = await _user.DeleteAsync($"/api/v1/devices/{device.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        Assert.Empty(foreignList!);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await using var db = _factory.CreateDbContext();
        Assert.False(await db.Devices.AnyAsync(CancellationToken));
        Assert.False(await db.Subscriptions.AnyAsync(CancellationToken));
    }

    [Fact]
    public async Task Needs_a_signed_in_user()
    {
        var sender = await _factory.CreateSenderClientAsync("*");

        var response = await sender.PostAsJsonAsync("/api/v1/devices", new { apnsToken = Token, environment = "sandbox" }, CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<DeviceDto> RegisterAsync(string token, string[]? topics = null)
    {
        var response = await _user.PostAsJsonAsync("/api/v1/devices", new { apnsToken = token, environment = "sandbox", topics }, CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DeviceDto>(CancellationToken))!;
    }
}

public sealed class PushTests : IAsyncLifetime
{
    private static readonly string Token = new('c', 64);

    private readonly OnkelBummelApiFactory _factory = new();
    private HttpClient _sender = null!;
    private HttpClient _user = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _sender = await _factory.CreateSenderClientAsync("*");
        _user = _factory.CreateUserClient();
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Pushes_a_new_message_to_apns_with_a_signed_provider_token()
    {
        var before = OnkelBummelMetrics.PushSent.WithLabels("ok").Value;
        await RegisterAsync(Token, "sandbox");
        await _sender.PostAsJsonAsync("/api/v1/messages", new { topic = "filomat", title = "Druck fertig", message = "Benchy ist fertig.", priority = 4, tags = new[] { "tada", "drucker" } }, CancellationToken);

        var push = Assert.Single(await PushesAsync(1));

        Assert.Equal(HttpMethod.Post, push.Method);
        Assert.Equal($"https://api.sandbox.push.apple.com/3/device/{Token}", push.Uri.ToString());
        Assert.Equal("com.b-velop.onkel-bummel", push.Headers["apns-topic"]);
        Assert.Equal("alert", push.Headers["apns-push-type"]);
        Assert.Equal("10", push.Headers["apns-priority"]);
        Assert.True(long.Parse(push.Headers["apns-expiration"]) > DateTimeOffset.UtcNow.AddHours(23).ToUnixTimeSeconds());
        AssertProviderToken(push.Headers["Authorization"]);

        var aps = Payload(push).GetProperty("aps");
        Assert.Equal("🎉 Druck fertig", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("filomat", aps.GetProperty("alert").GetProperty("subtitle").GetString());
        Assert.Equal("Benchy ist fertig.", aps.GetProperty("alert").GetProperty("body").GetString());
        Assert.Equal("filomat", aps.GetProperty("thread-id").GetString());
        Assert.Equal("time-sensitive", aps.GetProperty("interruption-level").GetString());
        Assert.Equal(1, aps.GetProperty("badge").GetInt32());
        Assert.Equal("message", aps.GetProperty("category").GetString());
        Assert.False(aps.TryGetProperty("mutable-content", out _));
        Assert.True(OnkelBummelMetrics.PushSent.WithLabels("ok").Value >= before + 1);
    }

    [Fact]
    public async Task Pushes_production_devices_to_the_production_service()
    {
        await RegisterAsync(Token, "production");
        await SendAsync("garage", "Tor offen");

        Assert.Equal("api.push.apple.com", Assert.Single(await PushesAsync(1)).Uri.Host);
    }

    [Fact]
    public async Task Gives_images_a_signed_thumbnail_link_and_actions_their_labels()
    {
        await RegisterAsync(Token, "sandbox");
        using var form = new MultipartFormDataContent
        {
            { new StringContent("kamera"), "topic" },
            { new StringContent("2"), "priority" },
            { new StringContent("""[{"label":"Licht an","url":"http://haus/licht"},{"label":"Alarm","url":"http://haus/alarm"}]"""), "actions" },
            { new ByteArrayContent(TestImages.Jpeg(1200, 800)), "file", "snap.jpg" },
        };
        var sent = await _sender.PostAsync("/api/v1/messages", form, CancellationToken);
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);

        var push = Assert.Single(await PushesAsync(1));
        var payload = Payload(push);
        var aps = payload.GetProperty("aps");
        Assert.Equal(1, aps.GetProperty("mutable-content").GetInt32());
        Assert.Equal("passive", aps.GetProperty("interruption-level").GetString());
        Assert.False(aps.TryGetProperty("sound", out _));
        Assert.Equal("5", push.Headers["apns-priority"]);
        Assert.Equal("kamera", aps.GetProperty("alert").GetProperty("title").GetString());
        Assert.Equal("Bild", aps.GetProperty("alert").GetProperty("body").GetString());
        var ob = payload.GetProperty("ob");
        Assert.Equal(["Licht an", "Alarm"], ob.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("label").GetString()));
        Assert.Equal([0, 1], ob.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("index").GetInt32()));

        var link = ob.GetProperty("thumbnail").GetString()!;
        Assert.StartsWith(OnkelBummelApiFactory.PublicUrl + "/api/v1/attachments/", link);
        var anonymous = _factory.CreateClient();
        var thumbnail = await anonymous.GetAsync(new Uri(link).PathAndQuery, CancellationToken);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType?.MediaType);
        var forged = await anonymous.GetAsync(new Uri(link).PathAndQuery.Replace("signature=", "signature=x"), CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        var unsigned = await anonymous.GetAsync(new Uri(link).AbsolutePath, CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
    }

    [Fact]
    public async Task Pushes_only_subscribed_topics_and_nothing_for_muted_ones()
    {
        await RegisterAsync(Token, "sandbox", ["garage", "alarme"]);
        await SendAsync("alarme", "Erste");
        (await _user.PatchAsJsonAsync("/api/v1/topics/alarme", new { muted = true }, CancellationToken)).EnsureSuccessStatusCode();
        await SendAsync("alarme", "Stumm");
        await SendAsync("filomat", "Nicht abonniert");
        await SendAsync("garage", "Letzte");

        // The queue keeps the order, so once the last one is out, the others would have gone before it.
        var pushes = await PushesAsync(2);

        Assert.Equal(["Erste", "Letzte"], pushes.Select(p => Payload(p).GetProperty("aps").GetProperty("alert").GetProperty("body").GetString()));
        Assert.Equal([1, 4], pushes.Select(p => Payload(p).GetProperty("aps").GetProperty("badge").GetInt32()));
    }

    [Fact]
    public async Task Forgets_devices_apns_reports_as_unregistered()
    {
        var before = OnkelBummelMetrics.PushSent.WithLabels("invalid_token").Value;
        _factory.Apns.Respond = _ => new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("""{"reason":"Unregistered","timestamp":1}""") };
        await RegisterAsync(Token, "sandbox");

        await SendAsync("garage", "Tor offen");
        await PushesAsync(1);

        await WaitUntilAsync(async () => !await _factory.CreateDbContext().Devices.AnyAsync(CancellationToken));
        Assert.True(OnkelBummelMetrics.PushSent.WithLabels("invalid_token").Value >= before + 1);
    }

    [Fact]
    public async Task Tries_again_when_apple_is_busy_and_signs_a_new_token_when_it_refuses_the_old_one()
    {
        var answers = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"reason":"ExpiredProviderToken"}""") },
            new HttpResponseMessage(HttpStatusCode.OK),
        ]);
        _factory.Apns.Respond = _ => answers.Dequeue();
        await RegisterAsync(Token, "sandbox");

        await SendAsync("garage", "Tor offen");

        var pushes = await PushesAsync(3);
        Assert.Equal(pushes[0].Bytes, pushes[2].Bytes);
        Assert.Equal(pushes[0].Headers["Authorization"], pushes[1].Headers["Authorization"]);
        Assert.NotEqual(pushes[1].Headers["Authorization"], pushes[2].Headers["Authorization"]);
    }

    [Fact]
    public async Task Gives_up_after_the_last_try()
    {
        var before = OnkelBummelMetrics.PushSent.WithLabels("failed").Value;
        _factory.Apns.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await RegisterAsync(Token, "sandbox");

        await SendAsync("garage", "Tor offen");

        Assert.Equal(4, (await PushesAsync(4)).Count);
        await WaitUntilAsync(() => Task.FromResult(OnkelBummelMetrics.PushSent.WithLabels("failed").Value >= before + 1));
        await Task.Delay(300, CancellationToken);
        Assert.Equal(4, _factory.Apns.Requests.Count);
    }

    private void AssertProviderToken(string authorization)
    {
        Assert.StartsWith("bearer ", authorization);
        var parts = authorization["bearer ".Length..].Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonDocument.Parse(Convert.FromBase64String(Pad(parts[0]))).RootElement;
        var claims = JsonDocument.Parse(Convert.FromBase64String(Pad(parts[1]))).RootElement;
        Assert.Equal(("ES256", "TESTKEY123"), (header.GetProperty("alg").GetString(), header.GetProperty("kid").GetString()));
        Assert.Equal("6GGU8ET2C4", claims.GetProperty("iss").GetString());
        Assert.InRange(claims.GetProperty("iat").GetInt64(), DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
        Assert.True(_factory.ApnsKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Convert.FromBase64String(Pad(parts[2])), HashAlgorithmName.SHA256));
    }

    private static string Pad(string base64Url)
    {
        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        return base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
    }

    private static JsonElement Payload(RecordedRequest push) => JsonDocument.Parse(push.Bytes).RootElement;

    private async Task SendAsync(string topic, string text) =>
        (await _sender.PostAsJsonAsync("/api/v1/messages", new { topic, message = text }, CancellationToken)).EnsureSuccessStatusCode();

    private async Task RegisterAsync(string token, string environment, string[]? topics = null) =>
        (await _user.PostAsJsonAsync("/api/v1/devices", new { apnsToken = token, environment, topics }, CancellationToken)).EnsureSuccessStatusCode();

    private async Task<List<RecordedRequest>> PushesAsync(int count)
    {
        await WaitUntilAsync(() => Task.FromResult(_factory.Apns.Requests.Count >= count));
        return _factory.Apns.Requests.ToList();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition())
            await Task.Delay(20, timeout.Token);
    }
}

public sealed class PushPayloadTests
{
    [Theory]
    [InlineData(1, "passive")]
    [InlineData(2, "passive")]
    [InlineData(3, "active")]
    [InlineData(4, "time-sensitive")]
    [InlineData(5, "time-sensitive")]
    public void Maps_priorities_to_interruption_levels(int priority, string level) =>
        Assert.Equal(level, PushPayload.InterruptionLevel(priority));

    [Fact]
    public void Shortens_long_texts_so_the_payload_stays_under_4_kb()
    {
        var message = new Message
        {
            Id = 7, Topic = "logs", Source = "api", Title = new string('T', 400),
            Body = string.Concat(Enumerable.Repeat("Größe ändert sich 🚀 ", 2000)),
        };

        var payload = PushPayload.Create(message, "Logs", 3, "https://onkel-bummel.example.ts.net/api/v1/attachments/x/push-thumb?expires=1&signature=" + new string('s', 43));

        Assert.InRange(payload.Length, 1000, PushPayload.MaxBytes);
        var alert = JsonDocument.Parse(payload).RootElement.GetProperty("aps").GetProperty("alert");
        Assert.EndsWith("…", alert.GetProperty("body").GetString());
        Assert.Equal(200, alert.GetProperty("title").GetString()!.Length);
    }

    [Fact]
    public void Uses_the_topic_as_title_when_the_message_has_none()
    {
        var message = new Message { Id = 1, Topic = "garage", Source = "api", Body = "Tor offen", Tags = ["warning", "tor"] };

        var alert = JsonDocument.Parse(PushPayload.Create(message, "Garage", 0, null)).RootElement.GetProperty("aps").GetProperty("alert");

        Assert.Equal("⚠️ Garage", alert.GetProperty("title").GetString());
        Assert.False(alert.TryGetProperty("subtitle", out _));
    }
}

public sealed class ThumbnailUrlSignerTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "onkel-bummel-tests", Guid.NewGuid().ToString("N"));
    private readonly MovableTime _time = new(DateTimeOffset.Parse("2026-11-03T12:00:00Z"));

    [Fact]
    public void Accepts_its_own_links_for_15_minutes_and_keeps_the_key_across_restarts()
    {
        var link = Signer(publicUrl: null).Sign("abc123");
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(new Uri("http://x"), link).Query);
        var expires = long.Parse(query["expires"]!);

        Assert.StartsWith("/api/v1/attachments/abc123/push-thumb?", link);
        Assert.True(Signer().IsValid("abc123", expires, query["signature"]));
        Assert.False(Signer().IsValid("abc124", expires, query["signature"]));
        Assert.False(Signer().IsValid("abc123", expires + 1, query["signature"]));
        _time.Now += ThumbnailUrlSigner.Lifetime + TimeSpan.FromSeconds(1);
        Assert.False(Signer().IsValid("abc123", expires, query["signature"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    private ThumbnailUrlSigner Signer(Uri? publicUrl = null) => new(
        Microsoft.Extensions.Options.Options.Create(new StorageOptions { DataPath = _dataPath }),
        Microsoft.Extensions.Options.Options.Create(new ServerOptions { PublicUrl = publicUrl }),
        _time);

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
