using System.ComponentModel.DataAnnotations;

namespace MyProject.Api.Features.Files;

/// <summary>File upload settings, bound from the <c>Files</c> section.</summary>
internal sealed class FilesOptions
{
    public const string SectionName = "Files";

    /// <summary>The largest file accepted, in bytes, however it is uploaded.</summary>
    [Range(1, 5L * 1024 * 1024 * 1024)]
    public long MaxSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>
    /// Content types accepted, as <see cref="FileSignatures"/> detects them from the file's first bytes.
    /// What the client says the type is never counts.
    /// </summary>
    [MinLength(1)]
    public string[] AllowedTypes { get; set; } = [];

    /// <summary>How long a signed upload link works. An upload not completed by then is removed.</summary>
    [Range(1, 24 * 60)]
    public int UploadLinkMinutes { get; set; } = 15;

    /// <summary>How long a signed download link works. Keep it short: anyone holding the link can use it.</summary>
    [Range(1, 24 * 60)]
    public int DownloadLinkMinutes { get; set; } = 5;

    /// <summary>How long a file uploaded with <c>temporary</c> is kept.</summary>
    [Range(1, 24 * 365)]
    public int TemporaryFileHours { get; set; } = 24;

    /// <summary>How long a refused file's record is kept, so the client can read why.</summary>
    [Range(1, 24 * 365)]
    public int RejectedFileHours { get; set; } = 24;

    /// <summary>How old an object with no record must be before cleanup removes it, so uploads in flight are never touched.</summary>
    [Range(1, 24 * 365)]
    public int OrphanGraceHours { get; set; } = 24;

    [Range(1, 24 * 60)]
    public int CleanupIntervalMinutes { get; set; } = 60;

    /// <summary>The longest side of an image's thumbnail, in pixels.</summary>
    [Range(16, 2048)]
    public int ThumbnailSize { get; set; } = 256;

    /// <summary>Images with more pixels are refused, so a small file cannot expand into gigabytes when decoded.</summary>
    [Range(1, 500_000_000)]
    public long MaxImagePixels { get; set; } = 40_000_000;

    public ScanningOptions Scanning { get; set; } = new();

    public TusOptions Tus { get; set; } = new();
}

/// <summary>Virus scanning with ClamAV's clamd. Off by default; compose starts clamd in the <c>scan</c> profile.</summary>
internal sealed class ScanningOptions
{
    /// <summary>Scan every file before it is accepted. When clamd cannot be reached, uploads fail rather than skip the scan.</summary>
    public bool Enabled { get; set; }

    [Required]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 3310;

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Resumable uploads over the tus protocol (tus.io), for large files and flaky connections.</summary>
internal sealed class TusOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Where partial uploads wait until they are complete. Empty means a folder in the system's temp
    /// directory. With more than one API instance, use a shared volume or route a client to one instance.
    /// </summary>
    public string Directory { get; set; } = "";

    /// <summary>How long an unfinished upload is kept after its last chunk.</summary>
    [Range(1, 24 * 30)]
    public int ExpirationHours { get; set; } = 24;
}
