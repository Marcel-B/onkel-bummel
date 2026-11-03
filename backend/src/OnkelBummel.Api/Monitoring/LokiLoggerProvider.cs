using Microsoft.Extensions.Logging.Abstractions;

namespace OnkelBummel.Api.Monitoring;

/// <summary>
/// Hands log lines to <see cref="LokiLogQueue"/>. Filters apply as for any provider, under the alias "Loki"
/// (e.g. <c>Logging:Loki:LogLevel</c>). The console log stays as it is.
/// </summary>
[ProviderAlias("Loki")]
public sealed class LokiLoggerProvider(LokiLogQueue queue, TimeProvider time) : ILoggerProvider
{
    // The shipper reports its own trouble locally only; shipping that would add lines exactly when Loki is down.
    public ILogger CreateLogger(string categoryName) =>
        categoryName == typeof(LokiShipper).FullName ? NullLogger.Instance : new LokiLogger(categoryName, Source(categoryName), queue, time);

    public void Dispose()
    {
    }

    /// <summary>Part of the app for the <c>source</c> label; a handful of fixed values.</summary>
    internal static string Source(string category) => category switch
    {
        _ when category.StartsWith("OnkelBummel.Api.Push", StringComparison.Ordinal) => "push",
        _ when category.StartsWith("OnkelBummel.Api.Actions", StringComparison.Ordinal) => "actions",
        _ when category.StartsWith("OnkelBummel.Api.Auth", StringComparison.Ordinal) => "auth",
        _ when category.StartsWith("OnkelBummel.Api.Jobs", StringComparison.Ordinal) => "jobs",
        _ when category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) => "db",
        _ when category.StartsWith("System.Net.Http", StringComparison.Ordinal) => "http-client",
        _ when category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) => "http",
        _ => "api",
    };

    internal static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warning",
        _ => "error",
    };

    private sealed class LokiLogger(string category, string source, LokiLogQueue queue, TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var line = exception is null ? $"{category}: {message}" : $"{category}: {message}\n{exception}";
            var timestamp = (time.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks * 100;
            queue.Add(new LokiEntry(timestamp, Level(logLevel), source, line));
        }
    }
}
