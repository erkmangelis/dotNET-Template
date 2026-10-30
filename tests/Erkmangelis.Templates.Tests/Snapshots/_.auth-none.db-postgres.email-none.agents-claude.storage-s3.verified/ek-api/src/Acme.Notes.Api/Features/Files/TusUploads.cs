using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using tusdotnet;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;

namespace Acme.Notes.Api.Features.Files;

/// <summary>
/// Resumable uploads over the tus protocol (tus.io) with tusdotnet (MIT): a client sends a large file
/// in pieces and picks up where it stopped after a dropped connection. Each upload gets a pending file
/// record when it starts, owned by whoever started it; only they may continue it. When the last piece
/// arrives, the file takes the same checks as any other upload and moves to object storage.
/// </summary>
internal static class TusUploads
{
    /// <summary>The metadata key (in Upload-Metadata) that carries the file name; tus-js-client sends it as <c>filename</c>.</summary>
    public const string FileNameKey = "filename";

    public static IServiceCollection AddTusUploads(this IServiceCollection services)
    {
        services.AddSingleton<TusUploadStore>();
        return services;
    }

    public static void MapTusUploads(this IEndpointRouteBuilder routes, long maxSize)
    {
        if (!routes.ServiceProvider.GetRequiredService<IOptions<FilesOptions>>().Value.Tus.Enabled)
        {
            return;
        }

        // Each piece is its own request, so a piece may be as large as the whole file.
        routes.MapTus("/tus", Configure)
            .WithMetadata(new RequestSizeLimitAttribute(maxSize))
            .ExcludeFromDescription();
    }

    private static Task<DefaultTusConfiguration> Configure(HttpContext http)
    {
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptions<FilesOptions>>().Value;
        return Task.FromResult(new DefaultTusConfiguration
        {
            Store = services.GetRequiredService<TusUploadStore>().Store,
            MaxAllowedUploadSizeInBytesLong = options.MaxSizeBytes,
            Expiration = new SlidingExpiration(TimeSpan.FromHours(options.Tus.ExpirationHours)),
            Events = new Events
            {
                OnAuthorizeAsync = AuthorizeAsync,
                OnBeforeCreateAsync = BeforeCreateAsync,
                OnCreateCompleteAsync = CreateCompleteAsync,
                OnFileCompleteAsync = FileCompleteAsync,
            },
        });
    }

    private static Task BeforeCreateAsync(BeforeCreateContext context)
    {
        if (context.UploadLengthIsDeferred)
        {
            context.FailRequest(HttpStatusCode.BadRequest, "Send Upload-Length: the size must be known up front.");
        }
        else if (context.FileConcatenation is not null)
        {
            context.FailRequest(HttpStatusCode.BadRequest, "Concatenated uploads are not supported.");
        }

        return Task.CompletedTask;
    }

    /// <summary>Starting an upload needs a caller the endpoint lets in; every later request must come from the user who started it.</summary>
    private static async Task AuthorizeAsync(AuthorizeContext context)
    {
        if (context.Intent is IntentType.CreateFile or IntentType.GetOptions)
        {
            return;
        }

        if (!Guid.TryParseExact(context.FileId, "N", out var id) || await FindPendingAsync(context.HttpContext, id, context.CancellationToken) is null)
        {
            // The same answer whether the upload is someone else's or does not exist.
            context.FailRequest(HttpStatusCode.NotFound);
        }
    }

    private static async Task CreateCompleteAsync(CreateCompleteContext context)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptions<FilesOptions>>().Value;
        var time = services.GetRequiredService<TimeProvider>();
        var db = services.GetRequiredService<AppDbContext>();
        var fileName = context.Metadata.TryGetValue(FileNameKey, out var name) ? name.GetString(Encoding.UTF8) : null;

        db.Files.Add(new StoredFile
        {
            Id = Guid.ParseExact(context.FileId, "N"),
            FileName = FileEndpoints.SafeFileName(fileName),
            ContentType = "application/octet-stream",
            Size = context.UploadLength,
            Status = FileStatus.Pending,
            IsTemporary = context.Metadata.TryGetValue("temporary", out var temporary) && temporary.GetString(Encoding.UTF8) == "true",
            ExpiresAt = time.GetUtcNow().AddHours(options.Tus.ExpirationHours),
        });
        await db.SaveChangesAsync(context.CancellationToken);
    }

    /// <summary>Checks the finished upload, moves it to object storage and removes the local copy, whatever the outcome.</summary>
    private static async Task FileCompleteAsync(FileCompleteContext context)
    {
        var services = context.HttpContext.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.ParseExact(context.FileId, "N");
        var file = await db.Files.FirstAsync(file => file.Id == id, context.CancellationToken);

        var upload = await context.GetFileAsync();
        await using (var content = await upload.GetContentAsync(context.CancellationToken))
        {
            await services.GetRequiredService<FileProcessor>().ProcessAsync(file, content, alreadyStored: false, context.CancellationToken);
        }

        await db.SaveChangesAsync(context.CancellationToken);
        await services.GetRequiredService<TusUploadStore>().Store.DeleteFileAsync(context.FileId, context.CancellationToken);
    }

    private static Task<StoredFile?> FindPendingAsync(HttpContext http, Guid id, CancellationToken cancellationToken)
    {
        var files = http.RequestServices.GetRequiredService<AppDbContext>().Files.Where(file => file.Id == id && file.Status == FileStatus.Pending);
        return files.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>Where unfinished tus uploads wait, on local disk. File ids are version 7 GUIDs, so they double as file record ids.</summary>
internal sealed class TusUploadStore
{
    public TusUploadStore(IOptions<FilesOptions> options)
    {
        var directory = options.Value.Tus.Directory is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "Acme.Notes-tus");
        Directory.CreateDirectory(directory);
        Store = new TusDiskStore(directory, deletePartialFilesOnConcat: true, TusDiskBufferSize.Default, new Version7FileIds());
    }

    public TusDiskStore Store { get; }

    public Task<int> RemoveExpiredAsync(CancellationToken cancellationToken) => Store.RemoveExpiredFilesAsync(cancellationToken);

    private sealed class Version7FileIds : ITusFileIdProvider
    {
        public Task<string> CreateId(string metadata) => Task.FromResult(Guid.CreateVersion7().ToString("N"));

        public Task<bool> ValidateId(string fileId) => Task.FromResult(Guid.TryParseExact(fileId, "N", out _));
    }
}
