using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Push;

/// <summary>
/// Short-lived links to thumbnails for the Notification Service Extension: it fetches the picture without a login,
/// so it never touches the app's tokens. The link carries its expiry and an HMAC over attachment and expiry.
/// </summary>
public sealed class ThumbnailUrlSigner(IOptions<StorageOptions> storage, IOptions<ServerOptions> server, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly Lazy<byte[]> _key = new(() => LoadOrCreateKey(Path.Combine(storage.Value.DataPath, "url-signing.key")));

    /// <summary>Absolute when <c>Server:PublicUrl</c> is set; otherwise relative, and the app puts its server in front.</summary>
    public string Sign(string attachmentId)
    {
        var expires = (time.GetUtcNow() + Lifetime).ToUnixTimeSeconds();
        var path = $"/api/v1/attachments/{Uri.EscapeDataString(attachmentId)}/push-thumb" +
                   $"?expires={expires.ToString(CultureInfo.InvariantCulture)}&signature={Signature(attachmentId, expires)}";
        return server.Value.PublicUrl is { } publicUrl ? new Uri(publicUrl, path).ToString() : path;
    }

    public bool IsValid(string attachmentId, long expires, string? signature)
    {
        if (string.IsNullOrEmpty(signature) || expires < time.GetUtcNow().ToUnixTimeSeconds())
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Signature(attachmentId, expires)));
    }

    private string Signature(string attachmentId, long expires) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(_key.Value, Encoding.UTF8.GetBytes($"thumb\n{attachmentId}\n{expires}")));

    /// <summary>The key stays in the data folder, so links survive a restart; if it is lost, only open links break.</summary>
    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = RandomNumberGenerator.GetBytes(32);
        var temporary = path + ".new";
        File.WriteAllBytes(temporary, key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            File.Move(temporary, path);
            return key;
        }
        catch (IOException)
        {
            // Another instance was faster; its key wins.
            File.Delete(temporary);
            return File.ReadAllBytes(path);
        }
    }
}
