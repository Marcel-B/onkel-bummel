using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Tests;

public sealed class MetricsEndpointTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task The_app_port_does_not_answer_metrics()
    {
        var client = _factory.CreateClient();

        var metrics = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task The_metrics_port_answers_metrics_and_nothing_else()
    {
        // The test server reports local port 0, so that is the metrics port here.
        var client = _factory.WithWebHostBuilder(b => b.UseSetting("Monitoring:MetricsPort", "0")).CreateClient();

        var metrics = await client.GetStringAsync("/metrics", TestContext.Current.CancellationToken);
        var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        // Present from the start; the values are shared with tests running at the same time.
        Assert.Matches("""(?m)^onkelbummel_messages_received_total\{source="grafana",result="duplicate"\} \d""", metrics);
        Assert.Matches("""(?m)^onkelbummel_push_sent_total\{result="invalid_token"\} \d""", metrics);
        Assert.Matches("""(?m)^onkelbummel_job_runs_total\{task="backup",result="failure"\} \d""", metrics);
        Assert.Matches("""(?m)^onkelbummel_loki_lines_dropped_total\{reason="push_failed"\} \d""", metrics);
        Assert.Contains("process_resident_memory_bytes", metrics);
        // Missing until the first backup, so a server without Nextcloud access does not trip the backup alert.
        Assert.DoesNotMatch("(?m)^onkelbummel_backup_last_success_timestamp_seconds ", metrics);
        Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
    }
}

public sealed class InventoryMetricsTests : IAsyncLifetime
{
    private readonly OnkelBummelApiFactory _factory = new();

    public ValueTask InitializeAsync() => _factory.InitializeAsync();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Counts_messages_devices_tokens_and_storage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = _factory.CreateDbContext();
        db.Topics.Add(new Topic { Name = "garage" });
        db.Messages.Add(new Message { Topic = "garage", Body = "Tor offen", Source = "api", CreatedAt = DateTime.UtcNow });
        db.Messages.Add(new Message { Topic = "garage", Body = "Tor zu", Source = "api", CreatedAt = DateTime.UtcNow, ReadAt = DateTime.UtcNow });
        db.PublishTokens.Add(new PublishToken { Id = "t1", Name = "Skript", Hash = "abc", Topics = ["garage"], CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        var storage = new StorageOptions { DataPath = _factory.DataPath };
        await File.WriteAllBytesAsync(Path.Combine(storage.AttachmentsPath, "bild.jpg"), new byte[1234], cancellationToken);

        await InventoryMetricsWorker.UpdateAsync(db, storage, cancellationToken);

        Assert.Equal(2, OnkelBummelMetrics.Messages.WithLabels("total").Value);
        Assert.Equal(1, OnkelBummelMetrics.Messages.WithLabels("unread").Value);
        Assert.Equal(0, OnkelBummelMetrics.Devices.Value);
        Assert.Equal(1, OnkelBummelMetrics.PublishTokens.Value);
        Assert.True(OnkelBummelMetrics.DatabaseSize.Value > 0);
        Assert.Equal(1234, OnkelBummelMetrics.AttachmentsSize.Value);
    }
}

public class JobMetricsTests
{
    private static readonly TimeProvider Time = TimeProvider.System;

    [Fact]
    public async Task Records_successful_runs()
    {
        var before = OnkelBummelMetrics.JobRuns.WithLabels("test-success", "success").Value;

        var result = await OnkelBummelMetrics.MeasureJobAsync("test-success", Time, () => Task.FromResult(42), TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(before + 1, OnkelBummelMetrics.JobRuns.WithLabels("test-success", "success").Value);
        Assert.True(OnkelBummelMetrics.JobLastSuccess.WithLabels("test-success").Value > 0);
    }

    [Fact]
    public async Task Records_failed_runs_and_passes_the_error_on()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => OnkelBummelMetrics.MeasureJobAsync<int>(
            "test-failure", Time, () => throw new HttpRequestException("down"), TestContext.Current.CancellationToken));

        Assert.Equal(1, OnkelBummelMetrics.JobRuns.WithLabels("test-failure", "failure").Value);
        Assert.Equal(0, OnkelBummelMetrics.JobRuns.WithLabels("test-failure", "success").Value);
    }

    [Fact]
    public async Task Does_not_count_runs_cut_short_by_shutdown()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => OnkelBummelMetrics.MeasureJobAsync(
            "test-shutdown", Time, () => Task.FromCanceled<int>(stopping.Token), stopping.Token));

        Assert.Equal(0, OnkelBummelMetrics.JobRuns.WithLabels("test-shutdown", "failure").Value);
    }
}

public class LokiShipperTests
{
    private readonly StubHandler _loki = new();

    [Fact]
    public async Task Pushes_lines_grouped_into_streams_with_fixed_labels()
    {
        var (queue, shipper, provider) = Create();
        provider.CreateLogger("OnkelBummel.Api.Push.ApnsClient").LogInformation("Push sent to {Count} devices", 2);
        provider.CreateLogger("OnkelBummel.Api.Push.PushQueue").LogInformation("second line");
        provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel").LogWarning(new InvalidOperationException("boom"), "Request failed");

        await shipper.PushAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(_loki.Bodies);
        Assert.Equal("/loki/api/v1/push", _loki.Paths.Single());
        var streams = JsonDocument.Parse(request).RootElement.GetProperty("streams").EnumerateArray().ToList();
        Assert.Equal(2, streams.Count);

        var push = streams.Single(s => s.GetProperty("stream").GetProperty("source").GetString() == "push");
        Assert.Equal(
            new Dictionary<string, string> { ["app"] = "onkelbummel", ["host"] = "test-host", ["level"] = "info", ["source"] = "push" },
            push.GetProperty("stream").Deserialize<Dictionary<string, string>>());
        var values = push.GetProperty("values").EnumerateArray().ToList();
        Assert.Equal("1792490400000000000", values[0][0].GetString());
        Assert.Equal("OnkelBummel.Api.Push.ApnsClient: Push sent to 2 devices", values[0][1].GetString());
        Assert.Equal(2, values.Count);

        var http = streams.Single(s => s.GetProperty("stream").GetProperty("source").GetString() == "http");
        Assert.Equal("warning", http.GetProperty("stream").GetProperty("level").GetString());
        Assert.Contains("System.InvalidOperationException: boom", http.GetProperty("values")[0][1].GetString());
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Drops_lines_when_loki_is_down_without_throwing()
    {
        _loki.Status = HttpStatusCode.ServiceUnavailable;
        var (_, shipper, provider) = Create();
        var before = OnkelBummelMetrics.LokiLinesDropped.WithLabels("push_failed").Value;
        provider.CreateLogger("OnkelBummel.Api.Jobs.RetentionJob").LogInformation("one");
        provider.CreateLogger("OnkelBummel.Api.Jobs.RetentionJob").LogInformation("two");

        await shipper.PushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(before + 2, OnkelBummelMetrics.LokiLinesDropped.WithLabels("push_failed").Value);
    }

    [Fact]
    public void The_shipper_does_not_ship_its_own_lines()
    {
        var (queue, _, provider) = Create();

        provider.CreateLogger(typeof(LokiShipper).FullName!).LogWarning("Loki is unreachable");

        Assert.False(queue.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("OnkelBummel.Api.Push.ApnsClient", "push")]
    [InlineData("OnkelBummel.Api.Actions.ActionRunner", "actions")]
    [InlineData("OnkelBummel.Api.Auth.PublishTokenHandler", "auth")]
    [InlineData("OnkelBummel.Api.Jobs.BackupJob", "jobs")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", "db")]
    [InlineData("System.Net.Http.HttpClient.Loki", "http-client")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "http")]
    [InlineData("OnkelBummel.Api.Endpoints.MessageEndpoints", "api")]
    [InlineData("OnkelBummel.Api.Publishing.MessagePublisher", "api")]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler", "auth")]
    [InlineData("Microsoft.AspNetCore.Authorization.DefaultAuthorizationService", "auth")]
    [InlineData("System.Net.Http.HttpClient.Actions.ClientHandler", "actions")]
    [InlineData("System.Net.Http.HttpClient.Backup.LogicalHandler", "jobs")]
    [InlineData("System.Net.Http.HttpClient.Attachments.ClientHandler", "http-client")]
    public void Sorts_categories_into_a_few_sources(string category, string source) =>
        Assert.Equal(source, LokiLoggerProvider.Source(category));

    private (LokiLogQueue Queue, LokiShipper Shipper, LokiLoggerProvider Provider) Create()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MonitoringOptions
        {
            Loki = new LokiOptions { Url = "http://loki.test", Host = "test-host" },
        });
        var time = new FixedTime(DateTimeOffset.FromUnixTimeSeconds(1792490400));
        var queue = new LokiLogQueue(options);
        var shipper = new LokiShipper(queue, new StubClientFactory(_loki), options, time, NullLogger<LokiShipper>.Instance);
        return (queue, shipper, new LokiLoggerProvider(queue, time));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://loki.test") };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        public List<string> Bodies { get; } = [];

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status);
        }
    }
}
