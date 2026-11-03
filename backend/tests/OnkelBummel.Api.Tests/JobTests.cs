using System.IO.Compression;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Jobs;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OnkelBummel.Api.Tests;

public sealed class MaintenanceJobTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Retention_deletes_old_messages_of_topics_with_a_limit()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Topics.AddRange(new Topic { Name = "garage", RetentionDays = 7 }, new Topic { Name = "kamera" });
            var old = Message("garage", "uralt Tor", days: 10);
            old.Attachments.Add(new Attachment { Id = "a1", FileName = "x.jpg", ContentType = "image/jpeg", Sha256 = "00", Path = "x.jpg" });
            old.Actions.Add(new Data.MessageAction { Index = 0, Label = "A", Url = "http://a" });
            db.Messages.AddRange(old, Message("garage", "neu", days: 1), Message("kamera", "uralt Kamera", days: 400));
            await db.SaveChangesAsync(CancellationToken);
        }

        var deleted = await RunAsync<RetentionJob, int>(j => j.RunAsync(CancellationToken));

        Assert.Equal(1, deleted);
        await using var check = _factory.CreateDbContext();
        Assert.Equal(["neu", "uralt Kamera"], await check.Messages.OrderBy(m => m.Id).Select(m => m.Body).ToListAsync(CancellationToken));
        Assert.Equal(0, await check.Attachments.CountAsync(CancellationToken));
        Assert.Equal(0, await check.MessageActions.CountAsync(CancellationToken));
        var hits = await check.Database.SqlQuery<long>($"""SELECT rowid AS "Value" FROM "MessagesFts" WHERE "MessagesFts" MATCH {MessageSearch.ToQuery("tor")}""")
            .ToListAsync(CancellationToken);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Cleanup_deletes_old_files_without_a_message_and_frees_old_idempotency_keys()
    {
        var root = Path.Combine(_factory.DataPath, "attachments");
        Directory.CreateDirectory(Path.Combine(root, "2026", "10"));
        Directory.CreateDirectory(Path.Combine(root, "2025", "01"));
        var known = Path.Combine(root, "2026", "10", "bekannt.jpg");
        var orphan = Path.Combine(root, "2025", "01", "verwaist.jpg");
        var fresh = Path.Combine(root, "2026", "10", "gerade.part");
        foreach (var file in new[] { known, orphan, fresh })
            await File.WriteAllBytesAsync(file, [1, 2, 3], CancellationToken);
        File.SetLastWriteTimeUtc(known, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));
        await using (var db = _factory.CreateDbContext())
        {
            db.Topics.Add(new Topic { Name = "kamera" });
            var message = Message("kamera", "x", days: 2);
            message.IdempotencyKey = "alt";
            message.Attachments.Add(new Attachment { Id = "a1", FileName = "b.jpg", ContentType = "image/jpeg", Sha256 = "00", Path = Path.Combine("2026", "10", "bekannt.jpg") });
            db.Messages.AddRange(message, new Message { Topic = "kamera", Body = "neu", Source = "api", IdempotencyKey = "neu", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(CancellationToken);
        }

        var deleted = await RunAsync<CleanupJob, int>(j => j.RunAsync(CancellationToken));

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(known));
        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(orphan));
        Assert.False(Directory.Exists(Path.Combine(root, "2025")));
        await using var check = _factory.CreateDbContext();
        Assert.Equal(["neu"], await check.Messages.Where(m => m.IdempotencyKey != null).Select(m => m.IdempotencyKey!).ToListAsync(CancellationToken));
    }

    private async Task<TResult> RunAsync<TJob, TResult>(Func<TJob, Task<TResult>> run) where TJob : notnull
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await run(scope.ServiceProvider.GetRequiredService<TJob>());
    }

    internal static Message Message(string topic, string body, int days) =>
        new() { Topic = topic, Body = body, Source = "api", CreatedAt = DateTime.UtcNow.AddDays(-days) };
}

public sealed class BackupJobTests : IAsyncLifetime
{
    private const string Folder = "https://cloud.example.de/remote.php/dav/files/marcel/Backups/onkel-bummel";

    private readonly OnkelBummelApiFactory _factory = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _factory.InitializeAsync();
        await using var db = _factory.CreateDbContext();
        db.Topics.Add(new Topic { Name = "kamera" });
        var message = MaintenanceJobTests.Message("kamera", "Bewegung", days: 0);
        message.Attachments.Add(new Attachment { Id = "a1", FileName = "snap.jpg", ContentType = "image/jpeg", Sha256 = "00", Path = Path.Combine("2026", "11", "a1.jpg") });
        db.Messages.Add(message);
        await db.SaveChangesAsync(CancellationToken);
        Directory.CreateDirectory(Path.Combine(_factory.DataPath, "attachments", "2026", "11"));
        await File.WriteAllBytesAsync(Path.Combine(_factory.DataPath, "attachments", "2026", "11", "a1.jpg"), TestImages.Jpeg(20, 20), CancellationToken);
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Uploads_the_database_and_the_attachments_as_zip_per_weekday_and_month()
    {
        _factory.Remote.Respond = request => new HttpResponseMessage(request.Method.Method == "MKCOL" ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.Created);

        var result = await Job(passphrase: null).RunAsync(CancellationToken);

        Assert.NotNull(result);
        var requests = _factory.Remote.Requests.ToList();
        Assert.Equal(["MKCOL", "PUT", "PUT"], requests.Select(r => r.Method.Method));
        Assert.Equal(Folder + "/", requests[0].Uri.ToString());
        Assert.Equal("Basic bWFyY2VsOmFwcC1wYXNzd29ydA==", requests[0].Headers["Authorization"]); // marcel:app-passwort
        Assert.Matches(@"/onkel-bummel-(mon|tue|wed|thu|fri|sat|sun)\.zip$", requests[1].Uri.AbsolutePath);
        Assert.Matches(@"/onkel-bummel-\d{4}-\d{2}\.zip$", requests[2].Uri.AbsolutePath);
        Assert.Equal(requests[1].Bytes, requests[2].Bytes);
        await AssertArchiveAsync(requests[1].Bytes);
        Assert.True(OnkelBummelMetrics.BackupLastSuccess.Value > 0);
        Assert.False(Directory.EnumerateFileSystemEntries(Path.Combine(_factory.DataPath, "backup-tmp")).Any());
    }

    [Fact]
    public async Task Encrypts_the_archive_like_openssl_with_a_passphrase()
    {
        _factory.Remote.Respond = _ => new HttpResponseMessage(HttpStatusCode.Created);

        var result = await Job(passphrase: "lange geheime Passphrase").RunAsync(CancellationToken);

        Assert.All(result!.Files, f => Assert.EndsWith(".zip.enc", f));
        var encrypted = _factory.Remote.Requests.Last().Bytes;
        Assert.Equal("Salted__"u8.ToArray(), encrypted[..8]);
        using var zip = new MemoryStream();
        await OpenSslEncryption.DecryptAsync(new MemoryStream(encrypted), zip, "lange geheime Passphrase", CancellationToken);
        await AssertArchiveAsync(zip.ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Conflict, "409")]
    public async Task Fails_with_a_clear_reason_when_nextcloud_refuses(HttpStatusCode status, string reason)
    {
        _factory.Remote.Respond = _ => new HttpResponseMessage(status);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Job(passphrase: null).RunAsync(CancellationToken));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public async Task Is_off_without_nextcloud_access()
    {
        var job = new BackupJob(MsOptions.Create(new BackupOptions()), MsOptions.Create(new StorageOptions { DataPath = _factory.DataPath }),
            _factory.Services.GetRequiredService<IHttpClientFactory>(), TimeProvider.System, NullLogger<BackupJob>.Instance);

        Assert.False(job.IsConfigured);
        Assert.Null(await job.RunAsync(CancellationToken));
        Assert.Empty(_factory.Remote.Requests);
    }

    private BackupJob Job(string? passphrase) => new(
        MsOptions.Create(new BackupOptions
        {
            NextcloudWebDavUrl = new Uri(Folder + "/"),
            NextcloudUserName = "marcel",
            NextcloudAppPassword = "app-passwort",
            Passphrase = passphrase,
        }),
        MsOptions.Create(new StorageOptions { DataPath = _factory.DataPath }),
        _factory.Services.GetRequiredService<IHttpClientFactory>(),
        TimeProvider.System,
        NullLogger<BackupJob>.Instance);

    private async Task AssertArchiveAsync(byte[] bytes)
    {
        await using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(["attachments/2026/11/a1.jpg", "onkel-bummel.db"], zip.Entries.Select(e => e.FullName).Order());

        var copy = Path.Combine(_factory.DataPath, "restored.db");
        await using (var entry = await zip.GetEntry("onkel-bummel.db")!.OpenAsync(CancellationToken))
        await using (var file = File.Create(copy))
            await entry.CopyToAsync(file, CancellationToken);
        await using var connection = new SqliteConnection($"Data Source={copy};Pooling=False");
        await connection.OpenAsync(CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Body" FROM "Messages" """;
        Assert.Equal("Bewegung", await command.ExecuteScalarAsync(CancellationToken));
    }
}

public class JobScheduleTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    [Theory]
    [InlineData("2026-11-03T02:00:00Z", "03:30", 30)] // 03:00 in Berlin
    [InlineData("2026-11-03T02:31:00Z", "03:30", 23 * 60 + 59)]
    [InlineData("2027-03-28T00:30:00Z", "02:30", 60)] // the clocks skip 02:00–03:00; 03:30 summer time is 01:30 UTC
    [InlineData("2026-10-24T23:00:00Z", "03:30", 3 * 60 + 30)] // the clocks go back that night
    public void Waits_until_the_next_time_of_day_in_the_local_zone(string now, string at, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), JobScheduler.UntilNext(TimeOnly.Parse(at), DateTimeOffset.Parse(now), Berlin));
}
