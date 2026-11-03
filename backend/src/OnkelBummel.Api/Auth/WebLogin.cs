using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OnkelBummel.Api.Endpoints;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Auth;

/// <param name="Name">User name at the identity server, for the header of the web interface.</param>
public sealed record WebUserDto(string Sub, string Name);

/// <summary>
/// Sign-in of the web interface: the server runs OpenID Connect with the identity server (code with PKCE) and
/// gives the browser only a session cookie. No token reaches the browser, and none needs refreshing, because the
/// server calls no other API with it.
/// </summary>
public static class WebLogin
{
    /// <summary>The web interface sends it with every change; another site cannot, without a CORS preflight the server refuses.</summary>
    public const string RequestedWithHeader = "X-Requested-With";

    public const string CallbackPath = "/auth/callback";

    public const string SignedOutCallbackPath = "/auth/signed-out";

    private const string IdTokenName = "id_token";

    /// <summary>Requests from the web interface carry the cookie and no Authorization header.</summary>
    public static bool UsesCookie(HttpRequest request) =>
        !request.Headers.ContainsKey("Authorization") && request.Cookies.ContainsKey(AuthSetup.CookieName);

    public static bool IsConfigured(OidcOptions oidc) => oidc.Issuer is not null && !string.IsNullOrEmpty(oidc.WebClientSecret);

    public static IServiceCollection AddWebLogin(this IServiceCollection services)
    {
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme).Configure(options =>
        {
            options.Cookie.Name = AuthSetup.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            // The API answers with a status, the web interface decides where to go from there.
            options.Events.OnRedirectToLogin = context => SetStatus(context.Response, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => SetStatus(context.Response, StatusCodes.Status403Forbidden);
        });

        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<OidcOptions>>((options, oidc) =>
            {
                options.Authority = oidc.Value.Issuer?.ToString();
                options.RequireHttpsMetadata = oidc.Value.Issuer?.Scheme == Uri.UriSchemeHttps;
                // Without an identity server the handler would fail every request, the API's included; /auth/login
                // answers 503 then.
                if (oidc.Value.Issuer is null)
                    options.Configuration = new OpenIdConnectConfiguration();
                options.ClientId = oidc.Value.WebClientId;
                options.ClientSecret = oidc.Value.WebClientSecret;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = OpenIdConnectResponseType.Code;
                // A GET back to the callback is a top-level navigation, so the SameSite=Lax cookies of the login come along.
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.UsePkce = true;
                options.CallbackPath = CallbackPath;
                options.SignedOutCallbackPath = SignedOutCallbackPath;
                options.Scope.Clear();
                // "roles" puts the roles into the ID token, which is all the server needs to know about the user.
                options.Scope.Add(OpenIdConnectScope.OpenId);
                options.Scope.Add("profile");
                options.Scope.Add("roles");
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "role";
                // Only the ID token is kept, as id_token_hint for signing out; the cookie stays small.
                options.SaveTokens = false;
                options.Events.OnTokenValidated = context =>
                {
                    if (context.TokenEndpointResponse?.IdToken is { } idToken)
                        context.Properties!.StoreTokens([new AuthenticationToken { Name = IdTokenName, Value = idToken }]);
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebLogin))
                        .LogWarning(context.Failure, "Anmeldung über den Identity-Server gescheitert");
                    context.Response.Redirect("/?login=failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });

        return services;
    }

    /// <summary>Changes through the API with the cookie need <see cref="RequestedWithHeader"/>; reading does not.</summary>
    public static IApplicationBuilder UseCookieRequestCheck(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
            && request.Path.StartsWithSegments("/api") && UsesCookie(request) && !request.Headers.ContainsKey(RequestedWithHeader))
        {
            await Problems.Forbidden($"Änderungen mit dem Cookie brauchen den Header {RequestedWithHeader}.").ExecuteAsync(context);
            return;
        }
        await next(context);
    });

    public static RouteGroupBuilder MapWebLoginEndpoints(this RouteGroupBuilder auth)
    {
        auth.MapGet("/login", (string? returnUrl, IOptions<OidcOptions> oidc) =>
                IsConfigured(oidc.Value)
                    ? Results.Challenge(new AuthenticationProperties { RedirectUri = LocalOrRoot(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme])
                    : TypedResults.Problem("Oidc:Issuer und Oidc:WebClientSecret fehlen.", statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Anmeldung nicht eingerichtet"))
            .WithSummary("Sign in at the identity server, then back to returnUrl");

        auth.MapGet("/me", (ClaimsPrincipal user) =>
                TypedResults.Ok(new WebUserDto(
                    user.FindFirstValue("sub")!,
                    user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? user.FindFirstValue("sub")!)))
            .RequireAuthorization(Policies.User)
            .WithSummary("The signed-in user; 401 without login, 403 without the role");

        // A form post from the web interface: signing out ends with redirects to the identity server and back.
        auth.MapPost("/logout", (IOptions<OidcOptions> oidc) =>
                Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                    IsConfigured(oidc.Value)
                        ? [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
                        : [CookieAuthenticationDefaults.AuthenticationScheme]))
            .WithSummary("Sign out here and at the identity server");

        return auth;
    }

    /// <summary>Only paths of this server, so the login cannot be used to send someone elsewhere.</summary>
    internal static string LocalOrRoot(string? returnUrl) =>
        returnUrl is ['/', not '/' and not '\\', ..] or "/" ? returnUrl : "/";

    private static Task SetStatus(HttpResponse response, int status)
    {
        response.StatusCode = status;
        return Task.CompletedTask;
    }
}
