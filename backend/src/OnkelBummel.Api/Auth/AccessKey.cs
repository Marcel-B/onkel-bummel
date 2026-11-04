using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Auth;

/// <summary>
/// The access key of a server without identity server: one <c>obk_</c> key with all rights for the web interface,
/// the API and the app. The server makes it on its first start and writes it to the log once; the data folder keeps
/// only its SHA-256 hash. <c>--new-access-key</c> replaces it and so signs every browser out.
/// </summary>
public sealed class AccessKeyStore(IOptions<StorageOptions> storage, IOptions<OidcOptions> oidc)
{
    public const string Prefix = "obk_";

    /// <summary>Without an identity server the access key is the way in; with one, it does not count.</summary>
    public bool IsActive => oidc.Value.Issuer is null;

    private string HashPath => Path.Combine(storage.Value.DataPath, "access-key.sha256");

    /// <summary>Hash of the current key, or null while there is none. Read each time, so a new key counts at once.</summary>
    public string? CurrentHash() => File.Exists(HashPath) ? File.ReadAllText(HashPath).Trim() : null;

    public bool Exists => CurrentHash() is not null;

    /// <summary>Makes a new key, replaces the old one and returns it; only the hash is kept.</summary>
    public string Create()
    {
        var key = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(storage.Value.DataPath);
        File.WriteAllText(HashPath, Hash(key));
        return key;
    }

    /// <summary>Is it the current key? Compared in fixed time.</summary>
    public bool Matches(string? key) =>
        IsActive && key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) && CurrentHash() is { } current
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(key)), Encoding.ASCII.GetBytes(current));

    /// <summary>Still the key the session was made with? A new key ends all sessions.</summary>
    public bool IsCurrent(string? hash) =>
        IsActive && hash is not null && CurrentHash() is { } current
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(current));

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>The user behind the key: one administrator, as with the role <c>admin</c> at the identity server.</summary>
    public ClaimsPrincipal Principal(string authenticationType) => new(new ClaimsIdentity(
        [
            new Claim("sub", "admin"),
            new Claim("name", "Admin"),
            new Claim("role", AuthSetup.AdminRole),
            new Claim(KeyHashClaim, CurrentHash() ?? ""),
        ],
        authenticationType, "name", "role"));

    /// <summary>Hash of the key a session was made with, so a new key ends it.</summary>
    public const string KeyHashClaim = "ob_key";

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Signs in with <c>Authorization: Bearer obk_…</c>, for the app and scripts that read.</summary>
public sealed class AccessKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccessKeyStore keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AccessKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ReadKey(Request) is not { } key)
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!keys.Matches(key))
        {
            Logger.LogInformation("Wrong access key from {RemoteIp}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Wrong access key"));
        }
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(keys.Principal(SchemeName), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }

    /// <summary>The key from <c>Authorization: Bearer obk_…</c>, or null when the header holds something else.</summary>
    public static string? ReadKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return null;
        var key = header[bearer.Length..].Trim();
        return key.StartsWith(AccessKeyStore.Prefix, StringComparison.Ordinal) ? key : null;
    }
}

/// <summary>Makes the access key on the first start without identity server and shows it once in the log.</summary>
public sealed class AccessKeyStartup(AccessKeyStore keys, ILogger<AccessKeyStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (keys.IsActive && !keys.Exists)
        {
            var key = keys.Create();
            logger.LogWarning(
                "Kein Identity-Server eingetragen, angemeldet wird mit dem Zugangsschlüssel. Er wird nur dieses eine Mal angezeigt: {AccessKey}  "
                + "Einen neuen gibt es mit --new-access-key.", key);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
