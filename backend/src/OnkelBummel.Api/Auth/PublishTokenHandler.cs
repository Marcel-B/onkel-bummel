using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Data;

namespace OnkelBummel.Api.Auth;

/// <summary>Signs senders in with an <c>ob_</c> key from the Authorization header.</summary>
public sealed class PublishTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OnkelBummelDbContext db,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PublishToken";

    /// <summary>Claim with a topic the token may send to; one per topic, "*" for all.</summary>
    public const string TopicClaim = "ob_topic";

    public const string TokenIdClaim = "ob_token";

    // Writing the time of every use would turn each message into two writes; a minute is precise enough.
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ReadToken(Request) is not { } token)
            return AuthenticateResult.NoResult();

        var hash = PublishTokens.Hash(token);
        var entity = await db.PublishTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Hash == hash, Context.RequestAborted);
        if (entity is null)
        {
            Logger.LogInformation("Unknown publish token from {RemoteIp}", Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("Unknown publish token");
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (entity.LastUsedAt is null || now - entity.LastUsedAt > LastUsedPrecision)
        {
            await db.PublishTokens.Where(t => t.Id == entity.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
        }

        var claims = new List<Claim> { new(TokenIdClaim, entity.Id), new(ClaimTypes.Name, entity.Name) };
        claims.AddRange(entity.Topics.Select(t => new Claim(TopicClaim, t)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }

    /// <summary>The key from <c>Authorization: Bearer ob_…</c>, or null when the header holds something else.</summary>
    public static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = header[bearer.Length..].Trim();
        return token.StartsWith(PublishTokens.Prefix, StringComparison.Ordinal) ? token : null;
    }
}
