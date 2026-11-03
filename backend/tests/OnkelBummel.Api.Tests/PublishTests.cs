using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Publishing;

namespace OnkelBummel.Api.Tests;

public sealed class PublishTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();
    private HttpClient _sender = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        _sender = await _factory.CreateSenderClientAsync("*");
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Stores_a_json_message_and_answers_201_with_it()
    {
        var before = OnkelBummelMetrics.MessagesReceived.WithLabels("api", "stored").Value;

        var response = await _sender.PostAsJsonAsync("/api/v1/messages", new
        {
            topic = "filomat",
            title = "Druck fertig",
            message = "Benchy ist fertig (1 h 12 min).",
            priority = 4,
            tags = new[] { "white_check_mark", "drucker" },
            click = "https://fil-o-mat.example.ts.net/jobs/42",
            actions = new[]
            {
                new { label = "Nächster Druck", url = "http://fil-o-mat:8080/api/jobs/next", method = "post", clear = true },
            },
        }, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!;
        Assert.Equal($"/api/v1/messages/{message.Id}", response.Headers.Location?.ToString());
        Assert.Equal(("filomat", "Druck fertig", 4, "api"), (message.Topic, message.Title, message.Priority, message.Source));
        Assert.Equal(["white_check_mark", "drucker"], message.Tags);
        var action = Assert.Single(message.Actions);
        Assert.Equal((0, "POST", true), (action.Index, action.Method, action.Clear));
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("api", "stored").Value >= before + 1);

        var json = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.Contains("\"createdAt\":\"", json);
        Assert.Matches("\"createdAt\":\"[^\"]+Z\"", json);

        await using var db = _factory.CreateDbContext();
        var topic = await db.Topics.SingleAsync(CancellationToken);
        Assert.Equal("filomat", topic.Name);
    }

    [Theory]
    [InlineData("""{"topic":"Garage","message":"x"}""", "kein gültiges Topic")]
    [InlineData("""{"topic":"api","message":"x"}""", "kein gültiges Topic")]
    [InlineData("""{"topic":"garage","message":"x","priority":9}""", "Priorität")]
    [InlineData("""{"topic":"garage"}""", "Text")]
    [InlineData("""{"topic":"garage","message":"x","click":"javascript:alert(1)"}""", "Klick")]
    [InlineData("""{"topic":"garage","message":"x","actions":[{"label":"A","url":"ftp://x"}]}""", "http(s)")]
    [InlineData("""{"topic":"garage","message":"x","actions":[{"label":"A","url":"http://x","method":"TRACE"}]}""", "Methode")]
    [InlineData("""{"topic":"garage","message":"x","images":[{"url":"http://a","data":"AA=="}]}""", "genau eins")]
    [InlineData("""{"topic":"garage","message":"x",""", "JSON")]
    public async Task Answers_invalid_messages_with_400_problem_json(string body, string detail)
    {
        var before = OnkelBummelMetrics.MessagesReceived.WithLabels("api", "invalid").Value;

        var response = await _sender.PostAsync("/api/v1/messages", new StringContent(body, Encoding.UTF8, "application/json"), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken)).RootElement;
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Contains(detail, problem.GetProperty("detail").GetString());
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("api", "invalid").Value >= before + 1);
    }

    [Fact]
    public async Task Turns_away_other_content_types_with_415()
    {
        var response = await _sender.PostAsync("/api/v1/messages", new StringContent("Hallo", Encoding.UTF8, "text/plain"), CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Takes_a_form_with_files_tags_and_actions()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("kamera"), "topic" },
            { new StringContent("Bewegung an der Einfahrt"), "message" },
            { new StringContent("5"), "priority" },
            { new StringContent("kamera, rotating_light"), "tags" },
            { new StringContent("""[{"label":"Licht an","url":"http://haus/licht","method":"PUT","headers":{"X-Key":"1"},"body":"{\"on\":true}"}]"""), "actions" },
            { Image(TestImages.Jpeg(1200, 800), "image/jpeg"), "file", "snap.jpg" },
            { Image(TestImages.Png(40, 30), "image/png"), "file", "zweites.png" },
        };

        var response = await _sender.PostAsync("/api/v1/messages", form, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!;
        Assert.Equal(("kamera", 5), (message.Topic, message.Priority));
        Assert.Equal(["kamera", "rotating_light"], message.Tags);
        Assert.Equal("PUT", Assert.Single(message.Actions).Method);
        Assert.Equal(2, message.Attachments.Count);
        var jpeg = message.Attachments.Single(a => a.FileName == "snap.jpg");
        Assert.Equal(("image/jpeg", 1200, 800), (jpeg.ContentType, jpeg.Width, jpeg.Height));
        Assert.Equal($"/api/v1/attachments/{jpeg.Id}/thumb", jpeg.ThumbnailUrl);

        await using var db = _factory.CreateDbContext();
        var stored = await db.MessageActions.SingleAsync(CancellationToken);
        Assert.Equal("1", stored.Headers["X-Key"]);
        Assert.Equal("""{"on":true}""", stored.Body);
    }

    [Fact]
    public async Task Rejects_a_form_with_a_priority_that_is_no_number()
    {
        using var form = new MultipartFormDataContent { { new StringContent("garage"), "topic" }, { new StringContent("x"), "message" }, { new StringContent("hoch"), "priority" } };

        var response = await _sender.PostAsync("/api/v1/messages", form, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Takes_images_as_base64_and_as_data_url()
    {
        var png = Convert.ToBase64String(TestImages.Png(20, 10));

        var response = await _sender.PostAsJsonAsync("/api/v1/messages", new
        {
            topic = "garage",
            message = "Bilder",
            images = new object[] { new { data = png, filename = "a.png" }, new { data = "data:image/png;base64," + png } },
        }, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!;
        Assert.Equal(["a.png", "bild.png"], message.Attachments.Select(a => a.FileName).Order());
        Assert.All(message.Attachments, a => Assert.Equal((20, 10), (a.Width!.Value, a.Height!.Value)));
    }

    [Fact]
    public async Task Loads_images_from_a_url()
    {
        var jpeg = TestImages.Jpeg(64, 48);
        _factory.Remote.Respond = request => request.RequestUri!.AbsolutePath == "/api/printers/1/thumbnail"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = Image(jpeg, "image/jpeg") }
            : new HttpResponseMessage(HttpStatusCode.NotFound);

        var response = await _sender.PostAsJsonAsync("/api/v1/messages", new
        {
            topic = "filomat",
            message = "Druck fertig",
            images = new[] { new { url = "http://fil-o-mat:8080/api/printers/1/thumbnail" } },
        }, CancellationToken);
        var missing = await _sender.PostAsJsonAsync("/api/v1/messages", new
        {
            topic = "filomat",
            message = "Druck fertig",
            images = new[] { new { url = "http://fil-o-mat:8080/fehlt.jpg" } },
        }, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var attachment = Assert.Single((await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!.Attachments);
        Assert.Equal(("thumbnail.jpg", jpeg.Length, 64), (attachment.FileName, attachment.Size, attachment.Width!.Value));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("404", await missing.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task Turns_files_that_are_no_image_pdf_or_text_away_with_415()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("garage"), "topic" },
            { new StringContent("x"), "message" },
            { Image([0x4D, 0x5A, 0x90, 0x00, 0x03], "image/jpeg"), "file", "bild.jpg" },
        };

        var response = await _sender.PostAsync("/api/v1/messages", form, CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Messages.CountAsync(CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_factory.DataPath, "attachments"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Keeps_text_and_pdf_without_thumbnail()
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("garage"), "topic" },
            { new StringContent("Protokoll"), "message" },
            { Image("Zeile 1\nZeile 2 – äöü"u8.ToArray(), "text/plain"), "file", "log.txt" },
            { Image("%PDF-1.7\n..."u8.ToArray(), "application/pdf"), "file", "rechnung.pdf" },
        };

        var response = await _sender.PostAsync("/api/v1/messages", form, CancellationToken);

        var message = (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!;
        Assert.Equal(["application/pdf", "text/plain; charset=utf-8"], message.Attachments.Select(a => a.ContentType).Order());
        Assert.All(message.Attachments, a => Assert.Null(a.ThumbnailUrl));
    }

    [Fact]
    public async Task Turns_files_over_the_limit_away_with_413()
    {
        await using var small = _factory.WithWebHostBuilder(b => b.UseSetting("Limits:MaxFileBytes", "1000"));
        var sender = small.CreateClient();
        sender.DefaultRequestHeaders.Authorization = _sender.DefaultRequestHeaders.Authorization;
        var before = OnkelBummelMetrics.MessagesReceived.WithLabels("api", "too_large").Value;
        using var form = new MultipartFormDataContent
        {
            { new StringContent("garage"), "topic" },
            { Image(TestImages.Jpeg(400, 400), "image/jpeg"), "file", "gross.jpg" },
        };

        var upload = await sender.PostAsync("/api/v1/messages", form, CancellationToken);
        var base64 = await sender.PostAsJsonAsync("/api/v1/messages", new
        {
            topic = "garage",
            images = new[] { new { data = Convert.ToBase64String(new byte[2000]) } },
        }, CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, upload.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, base64.StatusCode);
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("api", "too_large").Value >= before + 2);
    }

    [Fact]
    public async Task Turns_images_into_thumbnails_of_600_pixels_turned_as_the_exif_says()
    {
        // Stored sideways, with orientation 6: turn 90° clockwise to view. 900×300 becomes 300×900.
        var response = await _sender.PostAsJsonAsync("/api/v1/messages", new
        {
            topic = "kamera",
            message = "hochkant",
            images = new[] { new { data = Convert.ToBase64String(TestImages.Jpeg(900, 300, exifOrientation: 6)), filename = "hoch.jpg" } },
        }, CancellationToken);

        var attachment = Assert.Single((await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!.Attachments);
        Assert.Equal((300, 900), (attachment.Width!.Value, attachment.Height!.Value));
        await using var db = _factory.CreateDbContext();
        var stored = await db.Attachments.SingleAsync(CancellationToken);
        using var thumbnail = TestImages.Decode(await File.ReadAllBytesAsync(Path.Combine(_factory.DataPath, "attachments", stored.ThumbnailPath!), CancellationToken));
        Assert.Equal((200, 600), (thumbnail.Width, thumbnail.Height));
        // The red left half of the stored picture is on top after turning clockwise.
        Assert.True(thumbnail.GetPixel(100, 50).Red > 200 && thumbnail.GetPixel(100, 50).Blue < 80);
        Assert.True(thumbnail.GetPixel(100, 550).Blue > 200 && thumbnail.GetPixel(100, 550).Red < 80);
    }

    [Fact]
    public async Task Small_images_keep_their_size_in_the_thumbnail()
    {
        var path = Path.Combine(_factory.DataPath, "klein.png");
        Directory.CreateDirectory(_factory.DataPath);
        await File.WriteAllBytesAsync(path, TestImages.Png(120, 80), CancellationToken);

        var size = Thumbnails.Create(path, path + ".thumb.jpg");

        Assert.Equal(new ImageSize(120, 80), size);
        using var thumbnail = TestImages.Decode(await File.ReadAllBytesAsync(path + ".thumb.jpg", CancellationToken));
        Assert.Equal((120, 80), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public async Task A_repeat_with_the_same_idempotency_key_returns_the_first_message()
    {
        var before = OnkelBummelMetrics.MessagesReceived.WithLabels("api", "duplicate").Value;

        var first = await SendWithKeyAsync("slipways-42", "erste");
        var repeat = await SendWithKeyAsync("slipways-42", "zweite");
        var other = await SendWithKeyAsync("slipways-43", "dritte");

        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        var (a, b, c) = (await IdAsync(first), await IdAsync(repeat), await IdAsync(other));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal("true", repeat.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("api", "duplicate").Value >= before + 1);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(2, await db.Messages.CountAsync(CancellationToken));
    }

    [Fact]
    public async Task An_idempotency_key_older_than_24_hours_makes_a_new_message()
    {
        var first = await IdAsync(await SendWithKeyAsync("taeglich", "gestern"));
        await using (var db = _factory.CreateDbContext())
        {
            await db.Messages.ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, DateTime.UtcNow.AddHours(-25)), CancellationToken);
        }

        var second = await IdAsync(await SendWithKeyAsync("taeglich", "heute"));

        Assert.NotEqual(first, second);
        await using var check = _factory.CreateDbContext();
        Assert.Null((await check.Messages.SingleAsync(m => m.Id == first, CancellationToken)).IdempotencyKey);
        Assert.Equal("taeglich", (await check.Messages.SingleAsync(m => m.Id == second, CancellationToken)).IdempotencyKey);
    }

    private async Task<HttpResponseMessage> SendWithKeyAsync(string key, string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages")
        {
            Content = JsonContent.Create(new { topic = "slipways", message = text }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await _sender.SendAsync(request, CancellationToken);
    }

    private static async Task<long> IdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!.Id;

    internal static ByteArrayContent Image(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }
}

public sealed class GrafanaWebhookTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Turns_a_firing_alert_into_priority_4()
    {
        var sender = await _factory.CreateSenderClientAsync("alarme");
        var before = OnkelBummelMetrics.MessagesReceived.WithLabels("grafana", "stored").Value;

        var response = await sender.PostAsync("/api/v1/webhooks/grafana?topic=alarme", Payload("firing", ("firing", "DiskFull", "Platte auf proxmox zu 95 % voll")), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var message = (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!;
        Assert.Equal(("alarme", "Alarm: DiskFull", "Platte auf proxmox zu 95 % voll", 4), (message.Topic, message.Title, message.Message, message.Priority));
        Assert.Equal(["grafana"], message.Tags);
        Assert.Equal("https://grafana.example.ts.net/", message.Click);
        Assert.Equal("grafana", message.Source);
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("grafana", "stored").Value >= before + 1);
    }

    [Fact]
    public async Task Turns_a_resolved_alert_into_priority_2()
    {
        var sender = await _factory.CreateSenderClientAsync("alarme");

        var response = await sender.PostAsync("/api/v1/webhooks/grafana?topic=alarme", Payload("resolved", ("resolved", "DiskFull", "Platte wieder frei")), CancellationToken);

        var message = (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!;
        Assert.Equal(("Wieder gut: DiskFull", "Platte wieder frei", 2), (message.Title, message.Message, message.Priority));
    }

    [Fact]
    public async Task Lists_the_alerts_of_a_group_and_mentions_the_ones_that_ended()
    {
        var json = await Payload("firing", ("firing", "HighLoad", "Last auf a"), ("firing", "DiskFull", "Platte b"), ("resolved", "HighLoad", "Last auf c"))
            .ReadAsStringAsync(CancellationToken);
        var payload = JsonSerializer.Deserialize<GrafanaPayload>(json, JsonSerializerOptions.Web)!;

        var request = GrafanaWebhook.ToRequest(payload, "alarme");

        Assert.Equal("Alarm: HighLoad (+1)", request.Title);
        Assert.Equal("• Last auf a\n• Platte b\n• Wieder gut: Last auf c", request.Message);
    }

    [Fact]
    public async Task Needs_a_token_for_the_topic()
    {
        var sender = await _factory.CreateSenderClientAsync("garage");
        var before = OnkelBummelMetrics.MessagesReceived.WithLabels("grafana", "forbidden").Value;

        var forbidden = await sender.PostAsync("/api/v1/webhooks/grafana?topic=alarme", Payload("firing", ("firing", "X", "y")), CancellationToken);
        var anonymous = await _factory.CreateClient().PostAsync("/api/v1/webhooks/grafana?topic=alarme", Payload("firing", ("firing", "X", "y")), CancellationToken);
        var noTopic = await sender.PostAsync("/api/v1/webhooks/grafana", Payload("firing", ("firing", "X", "y")), CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noTopic.StatusCode);
        Assert.True(OnkelBummelMetrics.MessagesReceived.WithLabels("grafana", "forbidden").Value >= before + 1);
    }

    /// <summary>Shaped like the payload of Grafana 11's webhook contact point.</summary>
    private static StringContent Payload(string status, params (string Status, string Name, string Summary)[] alerts) =>
        new(JsonSerializer.Serialize(new
        {
            receiver = "onkel-bummel",
            status,
            orgId = 1,
            alerts = alerts.Select(a => new
            {
                status = a.Status,
                labels = new Dictionary<string, string> { ["alertname"] = a.Name, ["grafana_folder"] = "Homelab" },
                annotations = new Dictionary<string, string> { ["summary"] = a.Summary },
                startsAt = "2026-11-03T08:00:00Z",
                endsAt = "0001-01-01T00:00:00Z",
                generatorURL = "https://grafana.example.ts.net/alerting/grafana/abc/view",
                fingerprint = "c6eadffa33fcdf37",
            }),
            groupLabels = new Dictionary<string, string> { ["grafana_folder"] = "Homelab" },
            commonLabels = new Dictionary<string, string> { ["grafana_folder"] = "Homelab" },
            externalURL = "https://grafana.example.ts.net/",
            version = "1",
            groupKey = "{}:{grafana_folder=\"Homelab\"}",
            truncatedAlerts = 0,
            title = "[FIRING:1] Homelab",
            state = "alerting",
            message = "**Firing**",
        }), Encoding.UTF8, "application/json");
}
