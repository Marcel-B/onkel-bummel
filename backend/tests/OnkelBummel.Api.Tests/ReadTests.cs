using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Endpoints;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Monitoring;

namespace OnkelBummel.Api.Tests;

public sealed class MessageReadTests : IAsyncLifetime
{
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
    public async Task Pages_newest_first_with_before_and_forward_with_after()
    {
        var ids = new List<long>();
        for (var i = 1; i <= 5; i++)
            ids.Add(await SendAsync("garage", $"Nachricht {i}"));

        var first = await PageAsync("limit=2");
        var second = await PageAsync($"limit=2&before={first.Messages[^1].Id}");
        var last = await PageAsync($"limit=2&before={second.Messages[^1].Id}");
        var catchUp = await PageAsync($"limit=3&after={ids[1]}");

        Assert.Equal([ids[4], ids[3]], first.Messages.Select(m => m.Id));
        Assert.True(first.HasMore);
        Assert.Equal([ids[2], ids[1]], second.Messages.Select(m => m.Id));
        Assert.Equal([ids[0]], last.Messages.Select(m => m.Id));
        Assert.False(last.HasMore);
        Assert.Equal([ids[2], ids[3], ids[4]], catchUp.Messages.Select(m => m.Id));
        Assert.False(catchUp.HasMore);
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.GetAsync("/api/v1/messages?limit=500", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Filters_by_topics_search_unread_and_images()
    {
        var printed = await SendAsync("filomat", "Benchy ist fertig", title: "Druck fertig");
        var door = await SendAsync("garage", "Die Tür ist offen");
        var picture = await SendAsync("kamera", "Bewegung", image: true);
        var other = await SendAsync("alarme", "Platte voll");
        await _user.PostAsync($"/api/v1/messages/{other}/read", null, CancellationToken);

        Assert.Equal([picture, door], await IdsAsync("topics=garage,kamera"));
        Assert.Equal([printed], await IdsAsync("q=druck%20fert"));
        Assert.Equal([door], await IdsAsync("q=tur&topics=garage"));
        Assert.Equal([picture, door, printed], await IdsAsync("unread=1"));
        Assert.Equal([picture], await IdsAsync("has_image=true"));
        Assert.Equal([other, picture, door, printed], await IdsAsync("q=%22%20*"));
    }

    [Fact]
    public async Task Filters_by_priority_tags_and_time()
    {
        async Task<long> Send(int priority, string[] tags)
        {
            var response = await _sender.PostAsJsonAsync("/api/v1/messages", new { topic = "garage", message = "x", priority, tags }, CancellationToken);
            return (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!.Id;
        }
        var low = await Send(2, ["tor"]);
        var urgent = await Send(5, ["tor", "warning"]);
        var high = await Send(4, ["licht"]);
        await using (var db = _factory.CreateDbContext())
            await db.Messages.Where(m => m.Id == low).ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc)), CancellationToken);

        Assert.Equal([high, urgent], await IdsAsync("min_priority=4"));
        Assert.Equal([urgent, low], await IdsAsync("tags=tor"));
        Assert.Equal([urgent], await IdsAsync("tags=tor,warning"));
        Assert.Equal([low], await IdsAsync("since=2026-01-10T00:00:00Z&until=2026-01-11T00:00:00Z"));
        Assert.Equal([high, urgent], await IdsAsync("since=2026-01-11T00:00:00%2B01:00"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.GetAsync("/api/v1/messages?min_priority=6", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Gets_and_deletes_a_message_with_its_files()
    {
        var id = await SendAsync("kamera", "Bewegung", image: true);
        var message = await _user.GetFromJsonAsync<MessageDto>($"/api/v1/messages/{id}", CancellationToken);
        var files = Directory.EnumerateFiles(Path.Combine(_factory.DataPath, "attachments"), "*", SearchOption.AllDirectories).ToList();

        var deleted = await _user.DeleteAsync($"/api/v1/messages/{id}", CancellationToken);

        Assert.Equal("Bewegung", message!.Message);
        Assert.Equal(2, files.Count);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.All(files, f => Assert.False(File.Exists(f)));
        Assert.Equal(HttpStatusCode.NotFound, (await _user.GetAsync($"/api/v1/messages/{id}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.DeleteAsync($"/api/v1/messages/{id}", CancellationToken)).StatusCode);
        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Attachments.CountAsync(CancellationToken));
    }

    [Fact]
    public async Task Deletes_several_messages_at_once_with_their_files()
    {
        var picture = await SendAsync("kamera", "Bewegung", image: true);
        var door = await SendAsync("garage", "Tür offen");
        var kept = await SendAsync("garage", "Tür zu");

        var response = await _user.PostAsJsonAsync("/api/v1/messages/delete", new { ids = new[] { picture, door, door, 999_999 } }, CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<DeleteMessagesResult>(CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, result!.Count);
        Assert.Equal([kept], await IdsAsync(""));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_factory.DataPath, "attachments"), "*", SearchOption.AllDirectories));
        await using (var db = _factory.CreateDbContext())
            Assert.Equal(0, await db.Attachments.CountAsync(CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.PostAsJsonAsync("/api/v1/messages/delete", new { ids = Array.Empty<long>() }, CancellationToken)).StatusCode);
        var tooMany = Enumerable.Range(1, MessageEndpoints.MaxDeleteIds + 1).Select(i => (long)i).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.PostAsJsonAsync("/api/v1/messages/delete", new { ids = tooMany }, CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Marks_one_a_topic_or_everything_as_read()
    {
        var a = await SendAsync("garage", "a");
        await SendAsync("garage", "b");
        await SendAsync("kamera", "c");

        var single = await _user.PostAsync($"/api/v1/messages/{a}/read", null, CancellationToken);
        var topic = await _user.PostAsJsonAsync("/api/v1/messages/read", new { topic = "garage" }, CancellationToken);
        var late = await SendAsync("kamera", "d");
        var all = await _user.PostAsJsonAsync("/api/v1/messages/read", new { upToId = late - 1 }, CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, single.StatusCode);
        Assert.Equal(1, (await topic.Content.ReadFromJsonAsync<MarkReadResult>(CancellationToken))!.Count);
        Assert.Equal(1, (await all.Content.ReadFromJsonAsync<MarkReadResult>(CancellationToken))!.Count);
        Assert.Equal([late], await IdsAsync("unread=1"));
        Assert.Equal(HttpStatusCode.NotFound, (await _user.PostAsync("/api/v1/messages/999/read", null, CancellationToken)).StatusCode);

        var rest = await _user.PostAsync("/api/v1/messages/read", null, CancellationToken);
        Assert.Equal(1, (await rest.Content.ReadFromJsonAsync<MarkReadResult>(CancellationToken))!.Count);
    }

    [Fact]
    public async Task Lists_topics_with_counts_and_changes_their_settings()
    {
        await SendAsync("garage", "Tor offen");
        var last = await SendAsync("garage", "Tor zu", title: "Garage");
        await SendAsync("kamera", "Bewegung");
        await _user.PostAsync($"/api/v1/messages/{last}/read", null, CancellationToken);

        var topics = await _user.GetFromJsonAsync<List<TopicDto>>("/api/v1/topics", CancellationToken);
        var patched = await _user.PatchAsync("/api/v1/topics/garage", JsonContent.Create(new
        {
            displayName = "Garage",
            icon = "🚗",
            color = "#3B82F6",
            muted = true,
            retentionDays = 30,
        }), CancellationToken);
        var cleared = await _user.PatchAsync("/api/v1/topics/garage", JsonContent.Create(new { retentionDays = (int?)null }), CancellationToken);

        Assert.NotNull(topics);
        Assert.Equal(["kamera", "garage"], topics.Select(t => t.Name));
        var garage = topics[1];
        Assert.Equal((2, 1), (garage.Total, garage.Unread));
        Assert.Equal((last, "Garage", "Tor zu"), (garage.LastMessage!.Id, garage.LastMessage.Title, garage.LastMessage.Message));
        var changed = (await patched.Content.ReadFromJsonAsync<TopicDto>(CancellationToken))!;
        Assert.Equal(("Garage", "🚗", "#3b82f6", true, 30), (changed.DisplayName, changed.Icon, changed.Color, changed.Muted, changed.RetentionDays!.Value));
        var after = (await cleared.Content.ReadFromJsonAsync<TopicDto>(CancellationToken))!;
        Assert.Null(after.RetentionDays);
        Assert.Equal(("Garage", true), (after.DisplayName, after.Muted));
    }

    [Theory]
    [InlineData("""{"color":"blau"}""")]
    [InlineData("""{"retentionDays":0}""")]
    [InlineData("""{"muted":"ja"}""")]
    [InlineData("""{"name":"anders"}""")]
    public async Task Rejects_invalid_topic_settings(string body)
    {
        await SendAsync("garage", "x");

        var response = await _user.PatchAsync("/api/v1/topics/garage", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.PatchAsync("/api/v1/topics/gibtsnicht", JsonContent.Create(new { muted = true }), CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Serves_originals_and_thumbnails()
    {
        var jpeg = TestImages.Jpeg(1000, 500);
        var message = await _user.GetFromJsonAsync<MessageDto>($"/api/v1/messages/{await SendAsync("kamera", "x", image: true, imageBytes: jpeg)}", CancellationToken);
        var attachment = Assert.Single(message!.Attachments);

        var original = await _user.GetAsync(attachment.Url, CancellationToken);
        var download = await _user.GetAsync(attachment.Url + "?download=true", CancellationToken);
        var thumbnail = await _user.GetAsync(attachment.ThumbnailUrl, CancellationToken);
        var anonymous = await _factory.CreateClient().GetAsync(attachment.Url, CancellationToken);

        Assert.Equal(jpeg, await original.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal("image/jpeg", original.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", original.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.NotNull(original.Headers.ETag);
        using var thumb = TestImages.Decode(await thumbnail.Content.ReadAsByteArrayAsync(CancellationToken));
        Assert.Equal((600, 300), (thumb.Width, thumb.Height));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.GetAsync("/api/v1/attachments/gibtsnicht/thumb", CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Streams_new_messages_read_changes_and_deletions_of_the_chosen_topics()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var before = OnkelBummelMetrics.SseConnections.Value;
        using var response = await _user.GetAsync("/api/v1/stream?topics=garage", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));

        var ping = await ReadEventAsync(reader, timeout.Token);
        Assert.True(OnkelBummelMetrics.SseConnections.Value >= before + 1);
        await SendAsync("kamera", "anderes Topic");
        var id = await SendAsync("garage", "Tor offen");
        var message = await ReadEventAsync(reader, timeout.Token);
        await _user.PostAsync($"/api/v1/messages/{id}/read", null, timeout.Token);
        var read = await ReadEventAsync(reader, timeout.Token);
        await _user.PostAsJsonAsync("/api/v1/messages/read", new { }, timeout.Token);
        await _user.DeleteAsync($"/api/v1/messages/{id}", timeout.Token);
        var all = await ReadEventAsync(reader, timeout.Token);
        var deleted = await ReadEventAsync(reader, timeout.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ping", ping.Type);
        Assert.Equal("message", message.Type);
        Assert.Equal(("garage", "Tor offen"), (message.Data.GetProperty("topic").GetString(), message.Data.GetProperty("message").GetString()));
        Assert.Equal(id, message.Data.GetProperty("id").GetInt64());
        Assert.Equal(("read", id), (read.Type, read.Data.GetProperty("ids")[0].GetInt64()));
        // Reading everything goes to every connection; the message in "kamera" was unread.
        Assert.Equal("read", all.Type);
        Assert.Equal(JsonValueKind.Null, all.Data.GetProperty("topic").ValueKind);
        Assert.Equal(("deleted", id), (deleted.Type, deleted.Data.GetProperty("id").GetInt64()));
    }

    private static async Task<(string Type, JsonElement Data)> ReadEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? type = null;
        var data = "";
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0 && type is not null)
                return (type, JsonDocument.Parse(data).RootElement);
            if (line.StartsWith("event: ", StringComparison.Ordinal))
                type = line["event: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data += line["data: ".Length..];
        }
        throw new EndOfStreamException();
    }

    private async Task<long> SendAsync(string topic, string text, string? title = null, bool image = false, byte[]? imageBytes = null)
    {
        var images = image ? new[] { new { data = Convert.ToBase64String(imageBytes ?? TestImages.Png(10, 10)) } } : null;
        var response = await _sender.PostAsJsonAsync("/api/v1/messages", new { topic, title, message = text, images }, CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MessageDto>(CancellationToken))!.Id;
    }

    private async Task<MessagePage> PageAsync(string query) =>
        (await _user.GetFromJsonAsync<MessagePage>($"/api/v1/messages?{query}", CancellationToken))!;

    private async Task<List<long>> IdsAsync(string query) => (await PageAsync(query)).Messages.Select(m => m.Id).ToList();
}
