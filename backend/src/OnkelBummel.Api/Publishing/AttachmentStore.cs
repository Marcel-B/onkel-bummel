using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Data;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Publishing;

/// <summary>
/// Writes attachments to the data folder: checks size and type, computes the hash and makes thumbnails of images.
/// Files land under <c>attachments/yyyy/MM/</c>, named by the attachment id.
/// </summary>
public sealed class AttachmentStore(
    IOptions<StorageOptions> storage,
    IOptions<LimitsOptions> limits,
    IHttpClientFactory httpClients,
    TimeProvider time,
    ILogger<AttachmentStore> logger)
{
    /// <summary>Client for images given by URL.</summary>
    public const string HttpClientName = "Attachments";

    private const string PartialSuffix = ".part";

    private long MaxBytes => limits.Value.MaxFileBytes;

    public Task<Attachment> SaveAsync(IncomingFile file, CancellationToken cancellationToken) => file switch
    {
        UploadedFile upload => SaveUploadAsync(upload.File, cancellationToken),
        InlineFile inline => SaveInlineAsync(inline, cancellationToken),
        RemoteFile remote => DownloadAsync(remote, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    };

    /// <summary>Removes the files of an attachment; what fails here, the cleanup job catches later.</summary>
    public void Delete(Attachment attachment)
    {
        foreach (var path in new[] { attachment.Path, attachment.ThumbnailPath })
        {
            if (path is null)
                continue;
            try
            {
                File.Delete(FullPath(path));
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete attachment file {Path}", path);
            }
        }
    }

    public string FullPath(string relativePath) => Path.Combine(storage.Value.AttachmentsPath, relativePath);

    private async Task<Attachment> SaveUploadAsync(IFormFile upload, CancellationToken cancellationToken)
    {
        if (upload.Length > MaxBytes)
            throw TooLarge(upload.FileName);
        await using var stream = upload.OpenReadStream();
        return await StoreAsync(stream, upload.FileName, upload.ContentType, cancellationToken);
    }

    private async Task<Attachment> SaveInlineAsync(InlineFile inline, CancellationToken cancellationToken)
    {
        // Four Base64 characters carry three bytes; checked before decoding, so nothing too large lands in memory.
        if ((long)inline.Base64.Length / 4 * 3 > MaxBytes + 3)
            throw TooLarge(inline.FileName);
        var data = inline.Base64;
        // Also takes data URLs as browsers produce them: "data:image/png;base64,...."
        if (data.StartsWith("data:", StringComparison.Ordinal) && data.IndexOf(',', StringComparison.Ordinal) is > 0 and var comma)
            data = data[(comma + 1)..];
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            throw PublishException.Invalid($"„data“ von {inline.FileName ?? "einem Bild"} ist kein gültiges Base64.");
        }
        await using var stream = new MemoryStream(bytes, writable: false);
        return await StoreAsync(stream, inline.FileName, null, cancellationToken);
    }

    private async Task<Attachment> DownloadAsync(RemoteFile remote, CancellationToken cancellationToken)
    {
        var http = httpClients.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(remote.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw PublishException.Invalid($"{remote.Url} ist nicht erreichbar: {ex.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw PublishException.Invalid($"{remote.Url} antwortet mit {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw TooLarge(remote.Url.ToString());
            var fileName = remote.FileName
                ?? FileNameOf(response.Content.Headers.ContentDisposition)
                ?? Path.GetFileName(remote.Url.AbsolutePath);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await StoreAsync(stream, fileName, response.Content.Headers.ContentType?.MediaType, cancellationToken);
        }
    }

    private async Task<Attachment> StoreAsync(Stream content, string? fileName, string? declaredType, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7().ToString("N");
        var now = time.GetUtcNow();
        var folder = Path.Combine(now.ToString("yyyy"), now.ToString("MM"));
        Directory.CreateDirectory(FullPath(folder));
        var partial = FullPath(Path.Combine(folder, id + PartialSuffix));

        var created = new List<string> { partial };
        try
        {
            var (size, sha256, head) = await CopyAsync(content, partial, fileName, cancellationToken);
            var type = ContentTypes.Detect(head, fileName, declaredType)
                ?? throw PublishException.UnsupportedType(
                    $"{DisplayName(fileName)} ist weder ein Bild (JPEG, PNG, GIF, WebP, HEIC) noch PDF oder Text.");

            var path = Path.Combine(folder, id + type.Extension);
            File.Move(partial, FullPath(path));
            created.Add(FullPath(path));

            var attachment = new Attachment
            {
                Id = id,
                FileName = CleanFileName(fileName, type.Extension),
                ContentType = type.ContentType,
                Size = size,
                Sha256 = sha256,
                Path = path,
            };

            if (type.IsImage)
            {
                var thumbnail = Path.Combine(folder, id + ".thumb.jpg");
                created.Add(FullPath(thumbnail));
                var imageSize = Thumbnails.Create(FullPath(path), FullPath(thumbnail));
                if (imageSize is not null)
                {
                    attachment.ThumbnailPath = thumbnail;
                    attachment.Width = imageSize.Width;
                    attachment.Height = imageSize.Height;
                }
                // HEIC needs the codecs of the platform, which the server lacks; iOS shows the original itself.
                else if (type.ContentType != "image/heic")
                {
                    throw PublishException.Invalid($"{DisplayName(fileName)} lässt sich nicht als Bild lesen.");
                }
            }

            return attachment;
        }
        catch
        {
            foreach (var path in created)
                File.Delete(path);
            throw;
        }
    }

    /// <summary>Copies to the file while counting and hashing; stops as soon as it gets too large.</summary>
    private async Task<(long Size, string Sha256, byte[] Head)> CopyAsync(
        Stream content, string path, string? fileName, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        var head = new MemoryStream();
        long size = 0;
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, useAsync: true);
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                size += read;
                if (size > MaxBytes)
                    throw TooLarge(fileName);
                hash.AppendData(buffer, 0, read);
                if (head.Length < ContentTypes.HeadLength)
                    head.Write(buffer, 0, (int)Math.Min(read, ContentTypes.HeadLength - head.Length));
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (size == 0)
            throw PublishException.Invalid($"{DisplayName(fileName)} ist leer.");
        return (size, Convert.ToHexStringLower(hash.GetHashAndReset()), head.ToArray());
    }

    private PublishException TooLarge(string? fileName) =>
        PublishException.TooLarge($"{DisplayName(fileName)} ist größer als {MaxBytes / (1024 * 1024.0):0.#} MB.");

    private static string DisplayName(string? fileName) => string.IsNullOrWhiteSpace(fileName) ? "Die Datei" : $"„{fileName}“";

    private static string? FileNameOf(ContentDispositionHeaderValue? disposition) =>
        (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');

    /// <summary>A name that is safe to show and to offer for download, with the extension matching the content.</summary>
    internal static string CleanFileName(string? fileName, string extension)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c) && c is not '"' and not '/').ToArray()).Trim();
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0)
            stem = extension == ".txt" ? "text" : extension == ".pdf" ? "dokument" : "bild";
        if (stem.Length > 100)
            stem = stem[..100];
        var currentExtension = Path.GetExtension(name).ToLowerInvariant();
        // ".jpeg" stays as the sender wrote it, a ".png" holding a JPEG does not.
        var keep = currentExtension == extension || (extension == ".jpg" && currentExtension == ".jpeg")
            || (extension == ".txt" && currentExtension is ".log" or ".csv" or ".json" or ".md");
        return stem + (keep ? Path.GetExtension(name) : extension);
    }
}
