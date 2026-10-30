using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Features.Files;

internal static class FileEndpoints
{
    public static IServiceCollection AddFiles(this IServiceCollection services)
    {
        services.AddOptions<FilesOptions>()
            .BindConfiguration(FilesOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IVirusScanner>(provider =>
            provider.GetRequiredService<IOptions<FilesOptions>>().Value.Scanning.Enabled
                ? ActivatorUtilities.CreateInstance<ClamAvScanner>(provider)
                : new NoVirusScanner());
        services.AddScoped<FileProcessor>();
        services.AddScoped<FileJanitor>();
        services.AddHostedService<FileCleanupLoop>();
        services.AddTusUploads();
        return services;
    }

    public static RouteGroupBuilder MapFileEndpoints(this IEndpointRouteBuilder routes)
    {
        var files = routes.MapGroup("/files").WithTags("Files");
        var read = files;
        var write = files;
        var maxSize = routes.ServiceProvider.GetRequiredService<IOptions<FilesOptions>>().Value.MaxSizeBytes;

        // The upload is the one request allowed past the 1 MB body limit, up to the largest file plus the form around it.
        write.MapPost("/", UploadAsync)
            .WithMetadata(new RequestSizeLimitAttribute(maxSize + 64 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: maxSize + 64 * 1024)
            .DisableAntiforgery()
            .WithName("UploadFile")
            .WithSummary("Upload a file through the API (multipart form, field 'file')");
        write.MapPost("/uploads", CreateUploadAsync).WithName("CreateUpload").WithSummary("Get a signed link to upload a file straight to storage");
        write.MapPost("/{id:guid}/complete", CompleteUploadAsync).WithName("CompleteUpload").WithSummary("Check a file uploaded through a signed link and make it available");
        read.MapGet("/", ListAsync).WithName("ListFiles").WithSummary("List your files, newest first");
        read.MapGet("/{id:guid}", GetAsync).WithName("GetFile").WithSummary("Get a file's record");
        read.MapGet("/{id:guid}/content", DownloadAsync).WithName("DownloadFile").WithSummary("Download a file through the API");
        read.MapGet("/{id:guid}/link", CreateDownloadLinkAsync).WithName("CreateDownloadLink").WithSummary("Get a short-lived signed link to download a file");
        read.MapGet("/{id:guid}/thumbnail", ThumbnailAsync).WithName("GetThumbnail").WithSummary("Get an image's thumbnail (WebP)");
        write.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteFile").WithSummary("Delete a file and its content");
        write.MapTusUploads(maxSize);

        return files;
    }

    private static async Task<Results<Created<FileResponse>, ProblemHttpResult>> UploadAsync(
        IFormFile file,
        AppDbContext db,
        FileProcessor processor,
        IOptions<FilesOptions> options,
        TimeProvider time,
        HttpContext http,
        CancellationToken cancellationToken,
        bool temporary = false)
    {
        if (file.Length > options.Value.MaxSizeBytes)
        {
            return Refused(Rejection.TooLarge, $"The file is larger than {options.Value.MaxSizeBytes} bytes.");
        }

        var record = new StoredFile
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            FileName = SafeFileName(file.FileName),
            ContentType = file.ContentType,
            Status = FileStatus.Available,
            IsTemporary = temporary,
        };

        await using var content = file.OpenReadStream();
        var rejection = await processor.ProcessAsync(record, content, alreadyStored: false, cancellationToken);
        if (rejection != Rejection.None)
        {
            return Refused(rejection, record.RejectionReason!);
        }

        db.Files.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created(ApiVersions.PathFor(http, $"files/{record.Id}"), FileResponse.From(record));
    }

    private static async Task<Results<Created<UploadTicketResponse>, ProblemHttpResult>> CreateUploadAsync(
        CreateUploadRequest request,
        AppDbContext db,
        IObjectStorage storage,
        IOptions<FilesOptions> options,
        TimeProvider time,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (request.Size > settings.MaxSizeBytes)
        {
            return Refused(Rejection.TooLarge, $"The file is larger than {settings.MaxSizeBytes} bytes.");
        }

        if (!settings.AllowedTypes.Contains(request.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return Refused(Rejection.TypeNotAllowed, $"Files of this type are not accepted. Accepted: {string.Join(", ", settings.AllowedTypes)}.");
        }

        var now = time.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(settings.UploadLinkMinutes);
        var record = new StoredFile
        {
            Id = Guid.CreateVersion7(now),
            FileName = SafeFileName(request.FileName),
            ContentType = request.ContentType,
            Size = request.Size,
            Status = FileStatus.Pending,
            IsTemporary = request.Temporary,
            // Until it is completed; completing sets the temporary file's own lifetime.
            ExpiresAt = now.Add(lifetime),
        };
        db.Files.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        var url = await storage.CreateUploadUrlAsync(record.ObjectKey, record.ContentType, lifetime);
        var ticket = new UploadTicketResponse(
            FileResponse.From(record),
            url,
            HttpMethods.Put,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = record.ContentType },
            now.Add(lifetime));
        return TypedResults.Created(ApiVersions.PathFor(http, $"files/{record.Id}"), ticket);
    }

    private static async Task<Results<Ok<FileResponse>, NotFound, ProblemHttpResult>> CompleteUploadAsync(
        Guid id,
        AppDbContext db,
        IObjectStorage storage,
        FileProcessor processor,
        IOptions<FilesOptions> options,
        CancellationToken cancellationToken)
    {
        var file = await db.Files.FirstOrDefaultAsync(file => file.Id == id, cancellationToken);
        if (file is null)
        {
            return TypedResults.NotFound();
        }

        if (file.Status != FileStatus.Pending)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not pending", detail: "The file was already completed or refused.");
        }

        var size = await storage.GetSizeAsync(file.ObjectKey, cancellationToken);
        if (size is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Upload missing", detail: "Nothing was uploaded to the link yet.");
        }

        Rejection rejection;
        if (size > options.Value.MaxSizeBytes)
        {
            // Never download something this large: refuse it on its size alone.
            rejection = await processor.RefuseAsync(file, Rejection.TooLarge, $"The file is larger than {options.Value.MaxSizeBytes} bytes.", alreadyStored: true, cancellationToken);
        }
        else
        {
            await using var content = await TempFiles.DownloadAsync(storage, file.ObjectKey, cancellationToken);
            rejection = await processor.ProcessAsync(file, content, alreadyStored: true, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return rejection == Rejection.None ? TypedResults.Ok(FileResponse.From(file)) : Refused(rejection, file.RejectionReason!);
    }

    private static async Task<Ok<PagedResult<FileResponse>>> ListAsync(
        AppDbContext db,
        CancellationToken cancellationToken,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
        var files = db.Files.AsQueryable();
        var total = await files.LongCountAsync(cancellationToken);
        var items = await files
            .OrderByDescending(file => file.CreatedAt)
            .ThenByDescending(file => file.Id)
            .Skip(Paging.Skip(page, pageSize))
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new PagedResult<FileResponse>([.. items.Select(FileResponse.From)], page, pageSize, total));
    }

    private static async Task<Results<Ok<FileResponse>, NotFound>> GetAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var file = await FindAsync(db, id, cancellationToken);
        return file is null ? TypedResults.NotFound() : TypedResults.Ok(FileResponse.From(file));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> DownloadAsync(
        Guid id,
        AppDbContext db,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        var file = await FindAsync(db, id, cancellationToken);
        if (file is not { Status: FileStatus.Available } || await storage.OpenReadAsync(file.ObjectKey, cancellationToken) is not { } content)
        {
            return TypedResults.NotFound();
        }

        // An attachment, never shown inline, so an uploaded HTML or SVG file cannot run script on the API's origin.
        return TypedResults.Stream(content, file.ContentType, fileDownloadName: file.FileName);
    }

    private static async Task<Results<Ok<DownloadLinkResponse>, NotFound>> CreateDownloadLinkAsync(
        Guid id,
        AppDbContext db,
        IObjectStorage storage,
        IOptions<FilesOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var file = await FindAsync(db, id, cancellationToken);
        if (file is not { Status: FileStatus.Available })
        {
            return TypedResults.NotFound();
        }

        var lifetime = TimeSpan.FromMinutes(options.Value.DownloadLinkMinutes);
        var url = await storage.CreateDownloadUrlAsync(file.ObjectKey, file.FileName, lifetime);
        return TypedResults.Ok(new DownloadLinkResponse(url, time.GetUtcNow().Add(lifetime)));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> ThumbnailAsync(
        Guid id,
        AppDbContext db,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        var file = await FindAsync(db, id, cancellationToken);
        if (file is not { Status: FileStatus.Available, HasThumbnail: true } || await storage.OpenReadAsync(file.ThumbnailKey, cancellationToken) is not { } content)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Stream(content, ImageThumbnails.ContentType);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id,
        AppDbContext db,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        var file = await db.Files.FirstOrDefaultAsync(file => file.Id == id, cancellationToken);
        if (file is null)
        {
            return TypedResults.NotFound();
        }

        // The content first: a row without content is cleaned up, content without a row would be an orphan until cleanup finds it.
        await storage.DeleteAsync(file.ObjectKey, cancellationToken);
        await storage.DeleteAsync(file.ThumbnailKey, cancellationToken);
        db.Files.Remove(file);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Task<StoredFile?> FindAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        db.Files.AsNoTracking().FirstOrDefaultAsync(file => file.Id == id, cancellationToken);

    /// <summary>Keeps the name's last segment only and drops control characters; the name is metadata, never a path.</summary>
    internal static string SafeFileName(string? name)
    {
        var last = Path.GetFileName((name ?? "").Replace('\\', '/'));
        var clean = new string([.. last.Where(character => !char.IsControl(character))]).Trim();
        clean = clean.Length > StoredFile.FileNameMaxLength ? clean[..StoredFile.FileNameMaxLength] : clean;
        return clean.Length == 0 ? "file" : clean;
    }

    private static ProblemHttpResult Refused(Rejection rejection, string detail) => rejection == Rejection.TooLarge
        ? TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large", detail: detail)
        : TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "File refused", detail: detail);
}
