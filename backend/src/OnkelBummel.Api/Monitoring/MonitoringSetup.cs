namespace OnkelBummel.Api.Monitoring;

public static class MonitoringSetup
{
    private const string UserAgent = "OnkelBummel (+https://github.com/Marcel-B/onkel-bummel)";

    public static IServiceCollection AddMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MonitoringOptions>().Bind(configuration.GetSection(MonitoringOptions.SectionName));
        services.AddHostedService<InventoryMetricsWorker>();

        if (configuration[$"{MonitoringOptions.SectionName}:Loki:Url"] is { Length: > 0 } lokiUrl)
        {
            services.AddSingleton<LokiLogQueue>();
            services.AddSingleton<ILoggerProvider, LokiLoggerProvider>();
            services.AddHttpClient(LokiShipper.HttpClientName, http =>
                {
                    http.BaseAddress = new Uri(lokiUrl);
                    http.Timeout = TimeSpan.FromSeconds(10);
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                })
                .RemoveAllLoggers();
            services.AddHostedService<LokiShipper>();
        }

        return services;
    }
}
