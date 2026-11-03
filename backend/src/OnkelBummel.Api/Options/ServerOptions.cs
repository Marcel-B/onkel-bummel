namespace OnkelBummel.Api.Options;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>Address under which clients reach the server, e.g. https://onkel-bummel.tail1234.ts.net; used for links in pushes.</summary>
    public Uri? PublicUrl { get; set; }

    /// <summary>Takes scheme, host and client address from X-Forwarded-* headers, as needed behind "tailscale serve".</summary>
    public bool UseForwardedHeaders { get; set; }
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Folder for the database and the attachments; /data in the container.</summary>
    public string DataPath { get; set; } = "local-data";

    public string DatabasePath => Path.Combine(DataPath, "onkel-bummel.db");

    public string AttachmentsPath => Path.Combine(DataPath, "attachments");
}

public sealed class LimitsOptions
{
    public const string SectionName = "Limits";

    /// <summary>Largest attachment taken, per file.</summary>
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Most attachments on one message.</summary>
    public int MaxFilesPerMessage { get; set; } = 10;

    /// <summary>Largest request body taken when sending a message, all files and Base64 included.</summary>
    public long MaxRequestBytes { get; set; } = 100 * 1024 * 1024;
}

public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>The identity server, e.g. https://identity.tail1234.ts.net; without it, nobody can sign in.</summary>
    public Uri? Issuer { get; set; }

    /// <summary>Resource of the API scope; access tokens must name it in <c>aud</c>.</summary>
    public string Audience { get; set; } = "onkel-bummel-api";

    public string WebClientId { get; set; } = "onkel-bummel-web";

    public string? WebClientSecret { get; set; }

    public string IosClientId { get; set; } = "onkel-bummel-ios";

    /// <summary>Role a user needs to read messages; <c>admin</c> is always enough.</summary>
    public string RequiredRole { get; set; } = "onkel-bummel";
}

public sealed class ApnsOptions
{
    public const string SectionName = "Apns";

    /// <summary>The .p8 key from the Apple developer account; without it, no pushes go out.</summary>
    public string? KeyPath { get; set; }

    public string? KeyId { get; set; }

    public string? TeamId { get; set; }

    public string BundleId { get; set; } = "de.bvelop.onkelbummel";
}

public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>WebDAV folder in Nextcloud, e.g. https://cloud.example.de/remote.php/dav/files/marcel/Backups/onkel-bummel; without it, no backups.</summary>
    public Uri? NextcloudWebDavUrl { get; set; }

    public string? NextcloudUserName { get; set; }

    public string? NextcloudAppPassword { get; set; }

    /// <summary>Encrypts the archives with AES-256 when set.</summary>
    public string? Passphrase { get; set; }

    /// <summary>Time of day for the nightly backup, in the server's time zone.</summary>
    public TimeOnly At { get; set; } = new(3, 30);

    public bool IsConfigured =>
        NextcloudWebDavUrl is not null && !string.IsNullOrEmpty(NextcloudUserName) && !string.IsNullOrEmpty(NextcloudAppPassword);
}

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Turns the background jobs (retention, cleanup, backup) off, e.g. for tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time of day for retention and cleanup, in the server's time zone.</summary>
    public TimeOnly MaintenanceAt { get; set; } = new(4, 15);
}
