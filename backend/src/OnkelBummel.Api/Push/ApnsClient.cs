using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Push;

/// <param name="Status">HTTP status from APNs; 0 when it did not answer.</param>
/// <param name="Reason">Apple's reason for a rejection, such as "BadDeviceToken" or "Unregistered".</param>
public sealed record ApnsResult(int Status, string? Reason)
{
    public bool Ok => Status == 200;

    /// <summary>The device is gone: the app was removed or the token replaced.</summary>
    public bool Unregistered => Status == 410;

    /// <summary>Apple does not take the token: wrong environment, or not a token at all.</summary>
    public bool BadToken => Reason is "BadDeviceToken" or "DeviceTokenNotForTopic";

    /// <summary>The provider token was refused; a freshly signed one may work.</summary>
    public bool ProviderTokenRefused => Status == 403 && Reason is "ExpiredProviderToken" or "InvalidProviderToken";

    /// <summary>Worth another try later: no answer, too many requests or trouble at Apple.</summary>
    public bool Transient => Status is 0 or 429 or >= 500 || ProviderTokenRefused;
}

/// <param name="Priority">10 to show at once, 5 for pushes that may wait (passive ones).</param>
public sealed record ApnsNotification(string DeviceToken, string Environment, byte[] Payload, int Priority, DateTimeOffset Expiration);

/// <summary>Sends pushes to APNs over HTTP/2 with token authentication.</summary>
public sealed class ApnsClient(
    IHttpClientFactory httpClients,
    ApnsTokenProvider tokens,
    IOptions<ApnsOptions> options,
    ILogger<ApnsClient> logger)
{
    public const string HttpClientName = "Apns";

    public const string SandboxEnvironment = "sandbox";
    public const string ProductionEnvironment = "production";

    public async Task<ApnsResult> SendAsync(ApnsNotification notification, CancellationToken cancellationToken)
    {
        var apns = options.Value;
        var baseUrl = notification.Environment == ProductionEnvironment ? apns.ProductionUrl : apns.SandboxUrl;
        var token = tokens.GetToken();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, $"/3/device/{notification.DeviceToken}"))
        {
            // APNs speaks nothing but HTTP/2.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(notification.Payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        request.Headers.Add("apns-topic", apns.BundleId);
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-priority", notification.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("apns-expiration", notification.Expiration.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            var result = new ApnsResult((int)response.StatusCode, response.IsSuccessStatusCode ? null : await ReasonAsync(response, cancellationToken));
            if (result.ProviderTokenRefused)
                tokens.Invalidate(token);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("APNs did not answer: {Error}", ex.Message);
            return new ApnsResult(0, null);
        }
    }

    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return body.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
