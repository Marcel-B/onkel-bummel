using SkiaSharp;

namespace OnkelBummel.Api.Publishing;

/// <summary>Size of the image as it is meant to be seen, i.e. after applying the EXIF orientation.</summary>
public sealed record ImageSize(int Width, int Height);

/// <summary>Scales images down for lists, galleries and pushes.</summary>
public static class Thumbnails
{
    /// <summary>Longest side of a thumbnail.</summary>
    public const int MaxSide = 600;

    private const int JpegQuality = 80;

    /// <summary>
    /// Writes a JPEG of at most <see cref="MaxSide"/> pixels on the longest side, turned the way the EXIF orientation
    /// says (phones store photos sideways and only note how to turn them). JPEG, because notification attachments
    /// on iOS take no WebP; transparent areas become white.
    /// </summary>
    /// <returns>The size of the original, or null when the file cannot be decoded.</returns>
    public static ImageSize? Create(string sourcePath, string thumbnailPath)
    {
        using var codec = SKCodec.Create(sourcePath);
        if (codec is null)
            return null;

        var origin = codec.EncodedOrigin;
        var swapsSides = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0)
            return null;

        var scale = Math.Min(1f, MaxSide / (float)Math.Max(width, height));
        using var decoded = Decode(codec, scale);
        if (decoded is null)
            return null;

        // Size before turning, then the canvas is set up so drawing into it turns the picture.
        var drawWidth = Math.Max(1, (int)Math.Round(width * scale));
        var drawHeight = Math.Max(1, (int)Math.Round(height * scale));
        var (outWidth, outHeight) = swapsSides ? (drawHeight, drawWidth) : (drawWidth, drawHeight);

        using var surface = SKSurface.Create(new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        Orient(canvas, origin, drawWidth, drawHeight);
        using (var image = SKImage.FromBitmap(decoded))
            canvas.DrawImage(image, new SKRect(0, 0, drawWidth, drawHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        using (var file = File.Create(thumbnailPath))
            data.SaveTo(file);

        return swapsSides ? new ImageSize(height, width) : new ImageSize(width, height);
    }

    // JPEG decodes straight to 1/2, 1/4 or 1/8 of the size, which saves memory and time with large photos;
    // other formats decode at full size.
    private static SKBitmap? Decode(SKCodec codec, float scale)
    {
        var size = codec.GetScaledDimensions(scale);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return bitmap;
        bitmap.Dispose();

        if (size.Width == codec.Info.Width && size.Height == codec.Info.Height)
            return null;
        info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap = new SKBitmap(info);
        result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return bitmap;
        bitmap.Dispose();
        return null;
    }

    /// <summary>Sets up the canvas so a picture drawn at (0, 0, width, height) comes out turned as the EXIF origin says.</summary>
    private static void Orient(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        // Canvas calls apply in reverse order to what is drawn: the last call acts first.
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: // mirrored
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight: // upside down
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft: // upside down, mirrored
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop: // transposed
                canvas.Scale(-1, 1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightTop: // turned 90° clockwise
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom: // transverse
                canvas.Translate(height, width);
                canvas.Scale(1, -1);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom: // turned 90° counter-clockwise
                canvas.Translate(0, width);
                canvas.RotateDegrees(-90);
                break;
        }
    }
}
