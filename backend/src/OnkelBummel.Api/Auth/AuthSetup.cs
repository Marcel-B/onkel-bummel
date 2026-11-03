using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Auth;

public static class Policies
{
    /// <summary>Sending messages: a publish token, or a signed-in user.</summary>
    public const string Publish = "publish";

    /// <summary>Reading and managing: a signed-in user with the required role or <c>admin</c>.</summary>
    public const string User = "user";
}

/// <summary>Marks an endpoint that receives messages, so rejected logins count in <c>onkelbummel_messages_received_total</c>.</summary>
public sealed record MessageSourceMetadata(string Source);

public static class AuthSetup
{
    /// <summary>Picks the publish token handler for <c>ob_</c> keys and JWT bearer for everything else.</summary>
    public const string ApiScheme = "Api";

    public const string AdminRole = "admin";

    public static IServiceCollection AddOnkelBummelAuth(this IServiceCollection services)
    {
        services.AddAuthentication(ApiScheme)
            .AddPolicyScheme(ApiScheme, "Publish token or access token", options =>
                options.ForwardDefaultSelector = context => PublishTokenHandler.ReadToken(context.Request) is not null
                    ? PublishTokenHandler.SchemeName
                    : JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, PublishTokenHandler>(PublishTokenHandler.SchemeName, null)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<OidcOptions>>((options, oidc) =>
            {
                // Discovery and JWKS come from the identity server; the issuer of its discovery document is the valid one.
                options.Authority = oidc.Value.Issuer?.ToString();
                options.Audience = oidc.Value.Audience;
                options.RequireHttpsMetadata = oidc.Value.Issuer?.Scheme == Uri.UriSchemeHttps;
                // Keep the claim names of the token ("sub", "role") instead of the long .NET ones.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "sub";
                // A "role" claim holding an array becomes one claim per role, so both forms work.
                options.TokenValidationParameters.RoleClaimType = "role";
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.User, policy => policy.RequireAuthenticatedUser().AddRequirements(new AccessRequirement(AllowPublishTokens: false)))
            .AddPolicy(Policies.Publish, policy => policy.RequireAuthenticatedUser().AddRequirements(new AccessRequirement(AllowPublishTokens: true)));
        services.AddSingleton<IAuthorizationHandler, AccessHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, CountingAuthorizationResultHandler>();
        return services;
    }

    /// <summary>
    /// May the caller send to the topic? Users may send anywhere, publish tokens only to their topics.
    /// </summary>
    public static bool MaySendTo(this ClaimsPrincipal user, string topic) =>
        !user.HasClaim(c => c.Type == PublishTokenHandler.TokenIdClaim)
        || user.HasClaim(PublishTokenHandler.TopicClaim, Data.TopicNames.All)
        || user.HasClaim(PublishTokenHandler.TopicClaim, topic);

    /// <summary>Users need the role from <c>Oidc:RequiredRole</c> or <c>admin</c>; publish tokens only count where allowed.</summary>
    private sealed record AccessRequirement(bool AllowPublishTokens) : IAuthorizationRequirement;

    private sealed class AccessHandler(IOptions<OidcOptions> oidc) : AuthorizationHandler<AccessRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessRequirement requirement)
        {
            var user = context.User;
            var isPublishToken = user.HasClaim(c => c.Type == PublishTokenHandler.TokenIdClaim);
            if (isPublishToken ? requirement.AllowPublishTokens : user.IsInRole(oidc.Value.RequiredRole) || user.IsInRole(AdminRole))
                context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    /// <summary>Counts senders turned away before the endpoint ran: without a valid token (401) or without the right (403).</summary>
    private sealed class CountingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<MessageSourceMetadata>() is { } source)
            {
                if (authorizeResult.Challenged)
                    OnkelBummelMetrics.MessagesReceived.WithLabels(source.Source, "unauthorized").Inc();
                else if (authorizeResult.Forbidden)
                    OnkelBummelMetrics.MessagesReceived.WithLabels(source.Source, "forbidden").Inc();
            }
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
