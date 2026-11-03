using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Push;

/// <summary>
/// The provider token APNs wants in the authorization header: an ES256 JWT signed with the .p8 key. Apple takes a
/// token for up to an hour and turns away new ones more often than every 20 minutes, so it is renewed every 50.
/// </summary>
public sealed class ApnsTokenProvider(IOptions<ApnsOptions> options, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(50);

    private readonly Lock _lock = new();
    private ECDsa? _key;
    private string? _token;
    private DateTimeOffset _issuedAt;

    public string GetToken()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (_token is null || now - _issuedAt >= Lifetime)
            {
                _token = Create(now);
                _issuedAt = now;
            }
            return _token;
        }
    }

    /// <summary>Drops the token after APNs called it expired or invalid; the next push signs a new one.</summary>
    public void Invalidate(string token)
    {
        lock (_lock)
        {
            if (_token == token)
                _token = null;
        }
    }

    private string Create(DateTimeOffset now)
    {
        var apns = options.Value;
        if (_key is null)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(apns.KeyPath!));
            _key = key;
        }

        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = apns.KeyId }));
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { iss = apns.TeamId, iat = now.ToUnixTimeSeconds() }));
        var signingInput = $"{header}.{claims}";
        // JWS wants r and s side by side (IEEE P1363), which is what SignData gives by default.
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }
}
