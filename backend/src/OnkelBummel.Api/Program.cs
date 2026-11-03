using Microsoft.AspNetCore.HttpOverrides;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Endpoints;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;
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

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.SectionName);
builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
builder.Services.AddOptions<LimitsOptions>().BindConfiguration(LimitsOptions.SectionName);
builder.Services.AddOptions<OidcOptions>().BindConfiguration(OidcOptions.SectionName);
builder.Services.AddOptions<ApnsOptions>().BindConfiguration(ApnsOptions.SectionName);
builder.Services.AddOptions<BackupOptions>().BindConfiguration(BackupOptions.SectionName);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDatabase();
builder.Services.AddMonitoring(builder.Configuration);
builder.Services.AddProblemDetails();
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

// Metrics come first, so they see the status codes the exception handler and status code pages set.
app.UseRouting();
app.UseHttpMetrics();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health");
app.MapMetrics();
app.MapGroup("/api/v1").MapConfigEndpoints();

if (app.Configuration.GetValue("Database:InitializeOnStartup", true))
    await app.Services.InitializeDatabaseAsync();

await app.RunAsync();
return 0;

public partial class Program;
