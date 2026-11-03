using System.Security.Cryptography;
using System.Text;

namespace OnkelBummel.Api.Jobs;

/// <summary>
/// AES-256-CBC in the format of <c>openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt</c>, as the backups of
/// Identity and fil-o-mat use it, so an archive can be opened without this server:
/// <c>openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in onkel-bummel-mon.zip.enc -out onkel-bummel-mon.zip</c>
/// </summary>
public static class OpenSslEncryption
{
    public const int Iterations = 600_000;

    private static ReadOnlySpan<byte> Magic => "Salted__"u8;

    private const int SaltLength = 8;

    public static async Task EncryptAsync(Stream input, Stream output, string passphrase, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        using var aes = CreateAes(passphrase, salt);
        output.Write(Magic);
        output.Write(salt);
        await using var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        await input.CopyToAsync(crypto, cancellationToken);
    }

    public static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken cancellationToken)
    {
        var header = new byte[Magic.Length + SaltLength];
        await input.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("Not an openssl enc file with salt.");
        using var aes = CreateAes(passphrase, header[Magic.Length..]);
        await using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true);
        await crypto.CopyToAsync(output, cancellationToken);
    }

    // openssl takes key and IV together from one PBKDF2 run with SHA-256.
    private static Aes CreateAes(string passphrase, byte[] salt)
    {
        var keyAndIv = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, 32 + 16);
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keyAndIv[..32];
        aes.IV = keyAndIv[32..];
        return aes;
    }
}
