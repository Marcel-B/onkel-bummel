using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Web;

/// <summary>
/// Serves the web interface (Vue app in <c>backend/web</c>, built into <c>wwwroot</c>). Every path that is not
/// the API, a login path or a file gets <c>index.html</c>, so the app's own routes work on reload.
/// </summary>
public static class WebInterface
{
    public static IApplicationBuilder UseWebInterface(this WebApplication app)
    {
        var staticFiles = StaticFileOptions(app.Services.GetRequiredService<IOptions<OidcOptions>>().Value);
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers.XFrameOptions = "DENY";
            await next(context);
        });
        // "/" itself is index.html; the fallback below does not match the empty path.
        app.UseDefaultFiles();
        app.UseStaticFiles(staticFiles);
        return app;
    }

    public static void MapWebInterface(this WebApplication app)
    {
        // Unknown API paths stay 404 instead of getting the app. A fallback endpoint for them would also win over
        // real endpoints that turn a request down for its content type, so the app's route leaves them out instead.
        app.MapFallbackToFile("{*path:nonfile:regex(^(?!api/))}", "index.html",
            StaticFileOptions(app.Services.GetRequiredService<IOptions<OidcOptions>>().Value));
    }

    private static StaticFileOptions StaticFileOptions(OidcOptions oidc)
    {
        // Everything from this server; images also as data: (QR code) and blob: URLs. Signing out is a form post
        // that ends at the identity server, which counts for form-action too.
        var identity = oidc.Issuer is null ? "" : " " + oidc.Issuer.GetLeftPart(UriPartial.Authority);
        var policy = "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self'; " +
            $"object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'{identity}";

        return new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                // Vite puts a hash into every name under /assets; index.html names the current ones.
                if (context.Context.Request.Path.StartsWithSegments("/assets"))
                {
                    headers.CacheControl = "public, max-age=31536000, immutable";
                    return;
                }
                headers.CacheControl = "no-cache";
                if (context.File.Name.EndsWith(".html", StringComparison.Ordinal))
                    headers[HeaderNames.ContentSecurityPolicy] = policy;
            },
        };
    }
}
