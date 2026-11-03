using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using OnkelBummel.Api.Monitoring;
using OnkelBummel.Api.Options;

namespace OnkelBummel.Api.Jobs;

/// <param name="Files">Names uploaded to the Nextcloud folder.</param>
public sealed record BackupResult(List<string> Files, long Bytes);

/// <summary>
/// Backs up to a Nextcloud folder over WebDAV, like <c>identity-backup</c> in Marcel-B/Identity: a copy of the
/// database through SQLite's online backup (consistent while the server writes) plus all attachments, packed as a
/// ZIP. Each run uploads onkel-bummel-mon.zip … onkel-bummel-sun.zip (the last seven days) and
/// onkel-bummel-2026-10.zip (the last state of each month); nothing is deleted there. With a passphrase, the
/// archives are encrypted with AES-256 (<see cref="OpenSslEncryption"/>) and end in .zip.enc.
/// </summary>
public sealed class BackupJob(
    IOptions<BackupOptions> options,
    IOptions<StorageOptions> storage,
    IHttpClientFactory httpClients,
    TimeProvider time,
    ILogger<BackupJob> logger)
{
    public const string Name = "backup";

    public const string HttpClientName = "Backup";

    private const string ArchiveName = "onkel-bummel";

    private const string DatabaseEntry = "onkel-bummel.db";

    // Already compressed; packing them again costs time and saves nothing.
    private static readonly HashSet<string> Compressed = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".pdf"];

    public bool IsConfigured => options.Value.IsConfigured;

    /// <returns>What was uploaded; null when no Nextcloud access is configured.</returns>
    public async Task<BackupResult?> RunAsync(CancellationToken cancellationToken)
    {
        var backup = options.Value;
        if (!backup.IsConfigured)
            return null;

        var scratch = Path.Combine(storage.Value.DataPath, "backup-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var database = Path.Combine(scratch, DatabaseEntry);
            CopyDatabase(storage.Value.DatabasePath, database);

            var archive = Path.Combine(scratch, ArchiveName + ".zip");
            await PackAsync(archive, database, storage.Value.AttachmentsPath, cancellationToken);
            var extension = "zip";
            if (!string.IsNullOrEmpty(backup.Passphrase))
            {
                await using (var input = File.OpenRead(archive))
                await using (var output = File.Create(archive + ".enc"))
                    await OpenSslEncryption.EncryptAsync(input, output, backup.Passphrase, cancellationToken);
                archive += ".enc";
                extension = "zip.enc";
            }

            var now = time.GetLocalNow();
            List<string> names =
            [
                $"{ArchiveName}-{now.ToString("ddd", CultureInfo.InvariantCulture).ToLowerInvariant()}.{extension}",
                $"{ArchiveName}-{now:yyyy-MM}.{extension}",
            ];
            await UploadAsync(backup, archive, names, cancellationToken);

            var bytes = new FileInfo(archive).Length;
            OnkelBummelMetrics.BackupLastSuccess.Set(time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
            logger.LogInformation("Backup uploaded as {Files} ({Bytes} bytes)", string.Join(", ", names), bytes);
            return new BackupResult(names, bytes);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not remove the backup scratch folder {Folder}", scratch);
            }
        }
    }

    private static void CopyDatabase(string source, string target)
    {
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);

            using var check = to.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (check.ExecuteScalar() as string is not "ok" and var result)
                throw new InvalidOperationException($"The copy of the database is damaged: {result}");

            // One self-contained file; the server switches back to WAL when it opens a restored copy.
            check.CommandText = "PRAGMA journal_mode=DELETE";
            check.ExecuteNonQuery();
        }
    }

    private static async Task PackAsync(string archive, string database, string attachments, CancellationToken cancellationToken)
    {
        await using var file = File.Create(archive);
        await using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        await AddAsync(zip, database, DatabaseEntry, CompressionLevel.SmallestSize, cancellationToken);
        if (!Directory.Exists(attachments))
            return;
        foreach (var path in Directory.EnumerateFiles(attachments, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".part", StringComparison.Ordinal))
                continue;
            var entry = "attachments/" + Path.GetRelativePath(attachments, path).Replace('\\', '/');
            var level = Compressed.Contains(Path.GetExtension(path).ToLowerInvariant()) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            await AddAsync(zip, path, entry, level, cancellationToken);
        }
    }

    private static async Task AddAsync(ZipArchive zip, string path, string entryName, CompressionLevel level, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(entryName, level);
        entry.LastWriteTime = File.GetLastWriteTime(path);
        await using var source = File.OpenRead(path);
        await using var target = await entry.OpenAsync(cancellationToken);
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task UploadAsync(BackupOptions backup, string archive, List<string> names, CancellationToken cancellationToken)
    {
        var http = httpClients.CreateClient(HttpClientName);
        var folder = backup.NextcloudWebDavUrl!.ToString().TrimEnd('/');
        var credentials = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{backup.NextcloudUserName}:{backup.NextcloudAppPassword}")));

        using (var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), folder + "/") { Headers = { Authorization = credentials } })
        using (var response = await http.SendAsync(mkcol, cancellationToken))
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created or HttpStatusCode.MethodNotAllowed: // 405: the folder exists
                    break;
                case HttpStatusCode.Unauthorized:
                    throw new InvalidOperationException("Nextcloud rejects the user or the app password (401).");
                case HttpStatusCode.Conflict:
                    throw new InvalidOperationException($"The folder above the backup folder is missing in Nextcloud (409): {folder}");
                default:
                    throw new InvalidOperationException($"Cannot create the backup folder {folder} (HTTP {(int)response.StatusCode}).");
            }
        }

        foreach (var name in names)
        {
            await using var content = File.OpenRead(archive);
            using var put = new HttpRequestMessage(HttpMethod.Put, $"{folder}/{Uri.EscapeDataString(name)}")
            {
                Headers = { Authorization = credentials },
                Content = new StreamContent(content) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            };
            using var response = await http.SendAsync(put, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent))
            {
                throw new InvalidOperationException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Nextcloud rejects the user or the app password (401).",
                    HttpStatusCode.Locked => $"Nextcloud keeps {name} locked (423).",
                    HttpStatusCode.InsufficientStorage => "Nextcloud is full (507).",
                    _ => $"Cannot upload {name} (HTTP {(int)response.StatusCode}).",
                });
            }
        }
    }
}
