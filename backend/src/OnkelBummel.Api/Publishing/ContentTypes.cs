using System.Text.Unicode;

namespace OnkelBummel.Api.Publishing;

/// <param name="IsImage">Gets a thumbnail.</param>
public sealed record DetectedType(string ContentType, string Extension, bool IsImage);

/// <summary>
/// Tells the type of a file by its first bytes, not by what the sender claims, so nothing else hides behind an
/// image name. Only a few types are taken.
/// </summary>
public static class ContentTypes
{
    /// <summary>Bytes the detection looks at.</summary>
    public const int HeadLength = 4096;

    private static readonly HashSet<string> TextExtensions = [".txt", ".log", ".csv", ".json", ".md"];

    public static DetectedType? Detect(ReadOnlySpan<byte> head, string? fileName, string? declaredType)
    {
        if (head.StartsWith((byte[])[0xFF, 0xD8, 0xFF]))
            return new("image/jpeg", ".jpg", true);
        if (head.StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
            return new("image/png", ".png", true);
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return new("image/gif", ".gif", true);
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return new("image/webp", ".webp", true);
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8) && IsHeif(head[8..12]))
            return new("image/heic", ".heic", true);
        if (head.StartsWith("%PDF-"u8))
            return new("application/pdf", ".pdf", false);
        if (LooksLikeText(head, fileName, declaredType))
            return new("text/plain; charset=utf-8", ".txt", false);
        return null;
    }

    private static bool IsHeif(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("heim"u8);

    // Text only when the sender says so, and when it is UTF-8 without NUL bytes; a multi-byte character cut off
    // at the end of the head is fine.
    private static bool LooksLikeText(ReadOnlySpan<byte> head, string? fileName, string? declaredType)
    {
        var said = declaredType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            || TextExtensions.Contains(Path.GetExtension(fileName ?? "").ToLowerInvariant());
        if (!said || head.Contains((byte)0))
            return false;
        for (var cut = 0; cut < 4 && cut <= head.Length; cut++)
        {
            if (Utf8.IsValid(head[..^cut]))
                return true;
        }
        return false;
    }
}
