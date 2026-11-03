using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;
using OnkelBummel.Api.Publishing;

namespace OnkelBummel.Api.Endpoints;

public static class PublishEndpoints
{
    public const string ApiSource = "api";

    public const string IdempotencyHeader = "Idempotency-Key";

    public static RouteGroupBuilder MapPublishEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/messages", PublishAsync)
            .RequireAuthorization(Policies.Publish)
            .WithMetadata(new MessageSourceMetadata(ApiSource))
            .WithSummary("Send a message, as JSON or multipart/form-data");

        api.MapPost("/webhooks/grafana", GrafanaAsync)
            .RequireAuthorization(Policies.Publish)
            .WithMetadata(new MessageSourceMetadata(GrafanaWebhook.Source))
            .WithSummary("Take the payload of a Grafana webhook contact point");

        return api;
    }

    private static async Task<IResult> PublishAsync(
        HttpContext http, MessagePublisher publisher, IOptions<LimitsOptions> limits, IOptions<JsonOptions> json, CancellationToken cancellationToken)
    {
        try
        {
            LimitBody(http, limits.Value);
            var (request, files) = await ReadAsync(http.Request, limits.Value, json.Value.SerializerOptions, cancellationToken);
            var result = await publisher.PublishAsync(http.User, ApiSource, request, files, IdempotencyKey(http), cancellationToken);
            return Created(http, result, ApiSource);
        }
        catch (Exception ex) when (AsPublishException(ex) is { } problem)
        {
            return Problem(problem, ApiSource);
        }
    }

    private static async Task<IResult> GrafanaAsync(
        HttpContext http, string? topic, MessagePublisher publisher, IOptions<LimitsOptions> limits, IOptions<JsonOptions> json,
        CancellationToken cancellationToken)
    {
        try
        {
            LimitBody(http, limits.Value);
            if (!http.Request.HasJsonContentType())
                throw PublishException.UnsupportedType("Grafana schickt JSON.");
            var payload = await ReadJsonAsync<GrafanaPayload>(http.Request, json.Value.SerializerOptions, cancellationToken);
            var request = GrafanaWebhook.ToRequest(payload, topic);
            var result = await publisher.PublishAsync(http.User, GrafanaWebhook.Source, request, [], IdempotencyKey(http), cancellationToken);
            return Created(http, result, GrafanaWebhook.Source);
        }
        catch (Exception ex) when (AsPublishException(ex) is { } problem)
        {
            return Problem(problem, GrafanaWebhook.Source);
        }
    }

    private static async Task<(PublishRequest Request, IReadOnlyList<IncomingFile> Files)> ReadAsync(
        HttpRequest http, LimitsOptions limits, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        if (http.HasJsonContentType())
            return (await ReadJsonAsync<PublishRequest>(http, json, cancellationToken), []);
        if (!http.HasFormContentType)
            throw PublishException.UnsupportedType("Nachrichten kommen als application/json oder multipart/form-data.");

        var form = await http.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = limits.MaxRequestBytes }, cancellationToken);
        var request = new PublishRequest
        {
            Topic = form["topic"],
            Title = form["title"],
            Message = form["message"],
            Tags = form["tags"].SelectMany(t => (t ?? "").Split(',')).ToList(),
            Click = form["click"],
        };
        if (form["priority"] is { Count: > 0 } priority)
        {
            request.Priority = int.TryParse(priority, out var value)
                ? value
                : throw PublishException.Invalid($"„{priority}“ ist keine Priorität von 1 bis 5.");
        }
        if (form["actions"] is { Count: > 0 } actions && !string.IsNullOrWhiteSpace(actions))
        {
            try
            {
                request.Actions = JsonSerializer.Deserialize<List<ActionRequest>>(actions.ToString(), json);
            }
            catch (JsonException ex)
            {
                throw PublishException.Invalid($"„actions“ ist kein gültiges JSON-Array: {ex.Message}");
            }
        }
        return (request, form.Files.Select(f => (IncomingFile)new UploadedFile(f)).ToList());
    }

    private static async Task<T> ReadJsonAsync<T>(HttpRequest http, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        try
        {
            return await http.ReadFromJsonAsync<T>(json, cancellationToken)
                ?? throw PublishException.Invalid("Der Inhalt ist leer.");
        }
        catch (JsonException ex)
        {
            throw PublishException.Invalid($"Kein gültiges JSON: {ex.Message}");
        }
    }

    /// <summary>Kestrel's default of 30 MB fits neither several images nor Base64; the limit here is configurable.</summary>
    private static void LimitBody(HttpContext http, LimitsOptions limits)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limits.MaxRequestBytes;
    }

    private static string? IdempotencyKey(HttpContext http) =>
        http.Request.Headers[IdempotencyHeader].FirstOrDefault()?.Trim() is { Length: > 0 } key ? key : null;

    /// <summary>Errors of the request that the sender has to fix, including bodies over the limit.</summary>
    private static PublishException? AsPublishException(Exception ex) => ex switch
    {
        PublishException publish => publish,
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            PublishException.TooLarge("Die Anfrage ist größer als erlaubt (Limits:MaxRequestBytes)."),
        // The form reader reports its own limits this way.
        InvalidDataException => PublishException.TooLarge("Die Anfrage ist größer als erlaubt (Limits:MaxRequestBytes)."),
        BadHttpRequestException bad => PublishException.Invalid(bad.Message),
        _ => null,
    };

    private static IResult Created(HttpContext http, PublishResult result, string source)
    {
        OnkelBummelMetrics.MessagesReceived.WithLabels(source, result.Duplicate ? "duplicate" : "stored").Inc();
        if (result.Duplicate)
            http.Response.Headers["Idempotent-Replayed"] = "true";
        return TypedResults.Created($"/api/v1/messages/{result.Message.Id}", result.Message);
    }

    private static IResult Problem(PublishException problem, string source)
    {
        OnkelBummelMetrics.MessagesReceived.WithLabels(source, problem.Result).Inc();
        return TypedResults.Problem(problem.Detail, statusCode: problem.Status, title: problem.Title);
    }
}
