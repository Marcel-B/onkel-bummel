using Microsoft.Extensions.Options;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Endpoints;

/// <param name="Issuer">Identity server the app signs in with; null while it is not configured.</param>
/// <param name="ClientId">Client of the iOS app at the identity server.</param>
/// <param name="Scopes">Scopes the app asks for.</param>
/// <param name="Login"><c>oidc</c> with an identity server, <c>key</c> with the access key of a server without one.</param>
public record ClientConfig(string Name, Uri? Issuer, string ClientId, string[] Scopes, string Login);

public static class ConfigEndpoints
{
    public static RouteGroupBuilder MapConfigEndpoints(this RouteGroupBuilder api)
    {
        // Without login: the app needs it to know where to sign in.
        api.MapGet("/config", (IOptions<OidcOptions> oidc) => new ClientConfig(
                "Onkel Bummel",
                oidc.Value.Issuer,
                oidc.Value.IosClientId,
                ["openid", "profile", "offline_access", "roles", oidc.Value.Audience],
                oidc.Value.Issuer is null ? "key" : "oidc"))
            .WithSummary("Settings for setting up the app");

        return api;
    }
}
