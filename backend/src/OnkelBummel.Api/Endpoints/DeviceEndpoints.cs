using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OnkelBummel.Api.Auth;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Push;

namespace OnkelBummel.Api.Endpoints;

/// <param name="Id">The id the app got last time; lets it keep its device when the APNs token changes.</param>
/// <param name="Environment">"sandbox" for development builds, "production" for TestFlight and App Store.</param>
/// <param name="Topics">Subscriptions; a new device gets ["*"] without them, a known one keeps its own.</param>
public sealed record RegisterDeviceRequest(string? Id, string? Name, string? ApnsToken, string? Environment, List<string>? Topics);

/// <param name="Topics">Topic names, or ["*"] for all.</param>
public sealed record SubscriptionsRequest(List<string>? Topics);

public sealed record DeviceDto(string Id, string Name, string Environment, List<string> Topics, DateTime CreatedAt, DateTime LastSeenAt);

public static partial class DeviceEndpoints
{
    public const int MaxNameLength = 100;
    public const int MaxSubscriptions = 200;

    public static RouteGroupBuilder MapDeviceEndpoints(this RouteGroupBuilder api)
    {
        var devices = api.MapGroup("/devices").RequireAuthorization(Policies.User);

        devices.MapGet("/", ListAsync).WithSummary("The signed-in user's devices with their subscriptions");
        devices.MapPost("/", RegisterAsync).WithSummary("Register a device or update its APNs token");
        devices.MapPut("/{id}/subscriptions", SetSubscriptionsAsync).WithSummary("Replace the subscriptions of a device");
        devices.MapDelete("/{id}", DeleteAsync).WithSummary("Sign a device off; it gets no more pushes");

        return api;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, OnkelBummelDbContext db, CancellationToken cancellationToken)
    {
        var owner = Owner(user);
        var devices = await db.Devices.AsNoTracking().Include(d => d.Subscriptions)
            .Where(d => d.OwnerSub == owner)
            .OrderByDescending(d => d.LastSeenAt)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(devices.Select(ToDto).ToList());
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDeviceRequest request, ClaimsPrincipal user, OnkelBummelDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var token = request.ApnsToken?.Trim().ToLowerInvariant();
        if (token is null || !ApnsToken().IsMatch(token))
            return Problems.BadRequest("„apnsToken“ ist das Geräte-Token von APNs in Hex.");
        if (request.Environment is not (ApnsClient.SandboxEnvironment or ApnsClient.ProductionEnvironment))
            return Problems.BadRequest($"„environment“ ist „{ApnsClient.SandboxEnvironment}“ oder „{ApnsClient.ProductionEnvironment}“.");
        var name = string.IsNullOrWhiteSpace(request.Name) ? "iPhone" : request.Name.Trim();
        if (name.Length > MaxNameLength)
            return Problems.BadRequest($"„name“ hat höchstens {MaxNameLength} Zeichen.");
        List<string>? topics = null;
        if (request.Topics is not null && ValidateTopics(request.Topics, out topics) is { } error)
            return Problems.BadRequest(error);

        var owner = Owner(user);
        var now = time.GetUtcNow().UtcDateTime;
        var byToken = await db.Devices.Include(d => d.Subscriptions).SingleOrDefaultAsync(d => d.ApnsToken == token, cancellationToken);
        var device = request.Id is { Length: > 0 } id
            ? await db.Devices.Include(d => d.Subscriptions).SingleOrDefaultAsync(d => d.Id == id && d.OwnerSub == owner, cancellationToken)
            : null;
        if (device is not null && byToken is not null && byToken != device)
        {
            // The token moved to the known device, e.g. after a reinstall that kept its id; the old entry goes.
            db.Devices.Remove(byToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        device ??= byToken;

        var created = device is null;
        if (device is null)
        {
            device = new Device
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                OwnerSub = owner,
                Name = name,
                ApnsToken = token,
                Environment = request.Environment,
                CreatedAt = now,
            };
            device.Subscriptions.Add(new Subscription { DeviceId = device.Id, Topic = TopicNames.All });
            db.Devices.Add(device);
        }

        // A token is one installation; whoever signs in there now owns it.
        device.OwnerSub = owner;
        device.Name = name;
        device.ApnsToken = token;
        device.Environment = request.Environment;
        device.LastSeenAt = now;
        if (topics is not null)
            Replace(device, topics);
        await db.SaveChangesAsync(cancellationToken);

        var dto = ToDto(device);
        return created ? TypedResults.Created($"/api/v1/devices/{device.Id}", dto) : TypedResults.Ok(dto);
    }

    private static async Task<IResult> SetSubscriptionsAsync(
        string id, SubscriptionsRequest request, ClaimsPrincipal user, OnkelBummelDbContext db, CancellationToken cancellationToken)
    {
        if (ValidateTopics(request.Topics ?? [], out var topics) is { } error)
            return Problems.BadRequest(error);
        var owner = Owner(user);
        var device = await db.Devices.Include(d => d.Subscriptions).SingleOrDefaultAsync(d => d.Id == id && d.OwnerSub == owner, cancellationToken);
        if (device is null)
            return Problems.NotFound($"Gerät {id} gibt es nicht.");

        Replace(device, topics!);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToDto(device));
    }

    private static async Task<IResult> DeleteAsync(string id, ClaimsPrincipal user, OnkelBummelDbContext db, CancellationToken cancellationToken)
    {
        var owner = Owner(user);
        return await db.Devices.Where(d => d.Id == id && d.OwnerSub == owner).ExecuteDeleteAsync(cancellationToken) > 0
            ? TypedResults.NoContent()
            : Problems.NotFound($"Gerät {id} gibt es nicht.");
    }

    /// <summary>Topic names or "*"; topics need not exist yet, they may come with their first message.</summary>
    private static string? ValidateTopics(List<string> requested, out List<string>? topics)
    {
        topics = requested.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (topics.Count > MaxSubscriptions)
            return $"Höchstens {MaxSubscriptions} Abos pro Gerät.";
        if (topics.FirstOrDefault(t => t != TopicNames.All && !TopicNames.IsValid(t)) is { } invalid)
            return $"„{invalid}“ ist kein gültiges Topic.";
        // "*" covers everything else.
        if (topics.Contains(TopicNames.All))
            topics = [TopicNames.All];
        return null;
    }

    private static void Replace(Device device, List<string> topics)
    {
        device.Subscriptions.RemoveAll(s => !topics.Contains(s.Topic));
        foreach (var topic in topics.Where(t => device.Subscriptions.All(s => s.Topic != t)))
            device.Subscriptions.Add(new Subscription { DeviceId = device.Id, Topic = topic });
    }

    private static string Owner(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("Signed-in user without sub.");

    private static DeviceDto ToDto(Device device) => new(
        device.Id, device.Name, device.Environment,
        device.Subscriptions.Select(s => s.Topic).Order(StringComparer.Ordinal).ToList(),
        device.CreatedAt, device.LastSeenAt);

    // Apple's tokens are 32 bytes today, but Apple says they may grow.
    [GeneratedRegex("^[0-9a-f]{64,200}$")]
    private static partial Regex ApnsToken();
}
