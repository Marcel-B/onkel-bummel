using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkiaSharp;

namespace OnkelBummel.Api.Tests;

/// <summary>An identity server of its own: a signing key, discovery and JWKS, and tokens as OpenIddict issues them.</summary>
public sealed class TestIdentity
{
    public const string Issuer = "https://identity.example.ts.net/";

    public const string Audience = "onkel-bummel-api";

    public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "test-signing-key" };

    public StubHttp Backchannel { get; } = new();

    public TestIdentity()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(Key.Rsa.ExportParameters(false)) { KeyId = Key.KeyId });
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        Backchannel.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/.well-known/openid-configuration" => StubHttp.Json(new { issuer = Issuer, jwks_uri = Issuer + ".well-known/jwks" }),
            "/.well-known/jwks" => StubHttp.Json(new
            {
                keys = new[] { new { kty = jwk.Kty, use = jwk.Use, alg = jwk.Alg, kid = jwk.Kid, n = jwk.N, e = jwk.E } },
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    /// <param name="roles">A string, an array of strings, or null for no role claim.</param>
    public string CreateToken(
        object? roles = null, string audience = Audience, string issuer = Issuer, DateTime? expires = null, SecurityKey? key = null,
        string subject = "user-1")
    {
        var expiresAt = expires ?? DateTime.UtcNow.AddMinutes(30);
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (roles is not null)
            claims["role"] = roles;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = expiresAt.AddHours(-1),
            NotBefore = expiresAt.AddHours(-1),
            Expires = expiresAt,
            TokenType = "at+jwt",
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }
}

/// <param name="Body">The body as text; for binary bodies see <see cref="Bytes"/>.</param>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? ContentType, byte[] Bytes)
{
    public string Body => Encoding.UTF8.GetString(Bytes);
}

/// <summary>Answers HTTP calls of the server from the test and keeps them for checking.</summary>
public sealed class StubHttp : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.ToString(), bytes));
        return Respond(request);
    }

    // The client factory disposes handlers it rotates out; this one lives as long as the test.
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>Test pictures, made with SkiaSharp.</summary>
public static class TestImages
{
    /// <summary>A JPEG with the left half red and the right half blue.</summary>
    public static byte[] Jpeg(int width, int height, int exifOrientation = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var red = new SKPaint { Color = SKColors.Red };
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, width / 2f, height, red);
            canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        var jpeg = data.ToArray();
        return exifOrientation == 1 ? jpeg : WithOrientation(jpeg, exifOrientation);
    }

    public static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Green);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static SKBitmap Decode(byte[] image) => SKBitmap.Decode(image);

    /// <summary>Puts an EXIF segment with just the orientation tag right after the start of the JPEG.</summary>
    private static byte[] WithOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big endian, first IFD at 8
            0x00, 0x01,                                                 // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,             // Orientation, SHORT, count 1
            0x00, (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                                     // no next IFD
        ];
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = payload.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }
}
