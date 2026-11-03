using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Actions;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Endpoints;
using OnkelBummel.Api.Jobs;
using OnkelBummel.Api.Messages;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;
using OnkelBummel.Api.Publishing;
using OnkelBummel.Api.Push;
using OnkelBummel.Api.Web;
using Prometheus;

// Docker's health check: the aspnet image has neither curl nor wget, so the app asks itself.
if (args.Contains("--healthcheck"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        using var response = await http.GetAsync("http://127.0.0.1:8080/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

// UseHttpMetrics already measures the requests; the built-in ASP.NET Core meters would only repeat that under other names.
Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions { SuppressMeters = true });
OnkelBummelMetrics.Register();

// "--create-token" and friends run against the database and exit; their arguments are no settings.
var tokenCommand = TokenCommands.IsCommand(args);
var builder = WebApplication.CreateBuilder(tokenCommand ? [] : args);

builder.Services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.SectionName);
builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
builder.Services.AddOptions<LimitsOptions>().BindConfiguration(LimitsOptions.SectionName);
builder.Services.AddOptions<OidcOptions>().BindConfiguration(OidcOptions.SectionName);
builder.Services.AddOptions<ApnsOptions>().BindConfiguration(ApnsOptions.SectionName);
builder.Services.AddOptions<BackupOptions>().BindConfiguration(BackupOptions.SectionName);
builder.Services.AddOptions<JobsOptions>().BindConfiguration(JobsOptions.SectionName);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDatabase();
builder.Services.AddMonitoring(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOnkelBummelAuth();
// Keys for protected data (the login cookie of the web interface) belong to the data folder; inside the
// container they would be gone with every update.
builder.Services.AddDataProtection();
builder.Services.AddOptions<KeyManagementOptions>().Configure<IOptions<StorageOptions>, ILoggerFactory>((options, storage, loggers) =>
    options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.Combine(storage.Value.DataPath, "keys")), loggers));

builder.Services.AddSingleton<MessageEvents>();
builder.Services.AddSingleton<AttachmentStore>();
builder.Services.AddScoped<MessagePublisher>();
builder.Services.AddScoped<ActionRunner>();
builder.Services.AddSingleton<PushQueue>();
builder.Services.AddSingleton<ApnsTokenProvider>();
builder.Services.AddSingleton<ApnsClient>();
builder.Services.AddSingleton<ThumbnailUrlSigner>();
builder.Services.AddHostedService<PushWorker>();
builder.Services.AddScoped<RetentionJob>();
builder.Services.AddScoped<CleanupJob>();
builder.Services.AddScoped<BackupJob>();
builder.Services.AddHostedService<JobScheduler>();

const string userAgent = "OnkelBummel (+https://github.com/Marcel-B/onkel-bummel)";
builder.Services.AddHttpClient(AttachmentStore.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddHttpClient(ActionRunner.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(15);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddHttpClient(ApnsClient.HttpClientName, http =>
    {
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
    })
    // One connection to Apple stays open and carries all pushes, as Apple asks.
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(30),
        KeepAlivePingDelay = TimeSpan.FromMinutes(5),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
    });
builder.Services.AddHttpClient(BackupJob.HttpClientName, http =>
{
    // The archive holds all attachments, which may take a while to upload.
    http.Timeout = TimeSpan.FromMinutes(30);
    http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddHealthChecks().AddDbContextCheck<OnkelBummelDbContext>();

var useForwardedHeaders = builder.Configuration.GetValue<bool>($"{ServerOptions.SectionName}:{nameof(ServerOptions.UseForwardedHeaders)}");
if (useForwardedHeaders)
{
    // tailscale serve runs in the same network namespace and is the only way in.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (tokenCommand)
{
    await app.Services.InitializeDatabaseAsync();
    return await TokenCommands.RunAsync(args, app.Services, Console.Out, Console.Error);
}

if (useForwardedHeaders)
    app.UseForwardedHeaders();

// The metrics port answers nothing but /metrics, and the other ports do not answer /metrics. It goes by the port
// the connection came in on, not by the Host header, which a client could set.
var metricsPort = app.Configuration.GetValue("Monitoring:MetricsPort", new MonitoringOptions().MetricsPort);
app.Use(async (context, next) =>
{
    if ((context.Connection.LocalPort == metricsPort) != (context.Request.Path == "/metrics"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});

app.UseWebInterface();

// Metrics come first, so they see the status codes the exception handler and status code pages set.
app.UseRouting();
app.UseHttpMetrics();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseCookieRequestCheck();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapMetrics();
app.MapGroup("/api/v1")
    .MapConfigEndpoints()
    .MapPublishEndpoints()
    .MapMessageEndpoints()
    .MapTopicEndpoints()
    .MapAttachmentEndpoints()
    .MapStreamEndpoints()
    .MapPublishTokenEndpoints()
    .MapDeviceEndpoints();
app.MapGroup("/auth").MapWebLoginEndpoints();
app.MapWebInterface();

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
    await app.Services.InitializeDatabaseAsync();

await app.RunAsync();
return 0;

public partial class Program;
