using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Acme.Notes.Api.Features.Notes;

/// <summary>A row the import could not use, numbered as a spreadsheet numbers it (the header is row 1).</summary>
public sealed record ImportRowError(int Row, string Message);

public sealed record FileImportResponse(int Created, int Skipped, IReadOnlyList<ImportRowError> Errors);

/// <summary>Notes as documents: CSV and Excel export and import, and a PDF report.</summary>
internal static class NoteDocuments
{
    public const int MaxImportRows = 1_000;
    public const int MaxReportRows = 5_000;

    private static readonly string[] _headers = ["Title", "Content", "CreatedAt", "UpdatedAt"];

    public static RouteGroupBuilder MapNoteDocumentEndpoints(this RouteGroupBuilder notes)
    {
        notes.MapGet("/export/csv", ExportCsvAsync).RequireAuthorization(NotePolicies.Read).WithName("ExportNotesCsv").WithSummary("Download the matching notes as CSV");
        notes.MapGet("/export/xlsx", ExportXlsxAsync).RequireAuthorization(NotePolicies.Read).WithName("ExportNotesXlsx").WithSummary("Download the matching notes as an Excel workbook");
        notes.MapGet("/report", ReportAsync).RequireAuthorization(NotePolicies.Read).WithName("NotesReport").WithSummary("Download the matching notes as a PDF report");
        notes.MapPost("/imports/file", ImportFileAsync).RequireAuthorization(NotePolicies.Write).DisableAntiforgery().WithName("ImportNotesFile").WithSummary("Create notes from a CSV or Excel file with Title and Content columns");
        return notes;
    }

    /// <summary>Streams the CSV as the notes are read, so a large export never sits in memory whole.</summary>
    private static async Task ExportCsvAsync(
        [AsParameters] NoteFilter filter,
        INoteStore store,
        HttpContext http,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        http.Response.ContentType = $"{Spreadsheets.CsvContentType}; charset=utf-8";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"notes.csv\"";
        var notes = store.StreamAsync(user.GetUserId(), filter);
        await Spreadsheets.WriteCsvAsync(http.Response.Body, _headers, notes.Select(Row), cancellationToken);
    }

    private static async Task ExportXlsxAsync(
        [AsParameters] NoteFilter filter,
        INoteStore store,
        HttpContext http,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var notes = await store.StreamAsync(user.GetUserId(), filter).Select(Row).ToListAsync(cancellationToken);
        http.Response.ContentType = Spreadsheets.XlsxContentType;
        http.Response.Headers.ContentDisposition = "attachment; filename=\"notes.xlsx\"";
        await Spreadsheets.WriteXlsxAsync(http.Response.Body, _headers, notes, cancellationToken);
    }

    private static async Task<FileContentHttpResult> ReportAsync(
        [AsParameters] NoteFilter filter,
        INoteStore store,
        TimeProvider time,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var notes = await store.StreamAsync(user.GetUserId(), filter).Take(MaxReportRows + 1).ToListAsync(cancellationToken);
        var shown = notes.Take(MaxReportRows).ToList();
        var subtitle = $"{shown.Count} notes{(notes.Count > MaxReportRows ? $" (the first {MaxReportRows}; narrow the filter for the rest)" : "")}, generated {time.GetUtcNow():yyyy-MM-dd HH:mm} UTC";
        var pdf = PdfReports.Table(
            "Notes",
            subtitle,
            [new PdfColumn("Title", 3), new PdfColumn("Content", 5), new PdfColumn("Created (UTC)", 2)],
            shown.Select(note => new[] { note.Title, note.Content, note.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) }));
        return TypedResults.File(pdf, PdfReports.ContentType, "notes.pdf");
    }

    /// <summary>
    /// Creates a note per row, each checked like a single create. Rows with errors and titles the owner
    /// already uses are skipped; the rest are created. The request body limit (1 MB) bounds the file.
    /// </summary>
    private static async Task<Results<Ok<FileImportResponse>, ProblemHttpResult>> ImportFileAsync(
        IFormFile file,
        INoteStore store,
        TimeProvider time,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (Spreadsheets.FormatOf(file.FileName, file.ContentType) is not { } format)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported file", detail: "Send a .csv or .xlsx file.");
        }

        List<IReadOnlyDictionary<string, string?>> rows;
        try
        {
            await using var content = file.OpenReadStream();
            rows = [.. Spreadsheets.Read(content, format).Take(MaxImportRows + 1)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Unreadable file", detail: $"The file is not a valid {format.ToString().ToUpperInvariant()} file.");
        }

        if (rows.Count > MaxImportRows)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Too many rows", detail: $"Import at most {MaxImportRows} rows at a time.");
        }

        var created = 0;
        var skipped = 0;
        var errors = new List<ImportRowError>();
        for (var index = 0; index < rows.Count; index++)
        {
            var request = new CreateNoteRequest
            {
                Title = (rows[index].GetValueOrDefault("Title") ?? "").Trim(),
                Content = rows[index].GetValueOrDefault("Content"),
            };
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
            {
                errors.Add(new ImportRowError(index + 2, string.Join(" ", results.Select(result => result.ErrorMessage))));
                continue;
            }

            var note = new Note
            {
                Id = Guid.CreateVersion7(time.GetUtcNow()),
                OwnerId = user.GetUserId(),
                Title = request.Title,
                Content = request.Content,
            };
            if (await store.TryAddAsync(note, cancellationToken))
            {
                created++;
            }
            else
            {
                skipped++;
            }
        }

        return TypedResults.Ok(new FileImportResponse(created, skipped, errors));
    }

    private static string?[] Row(Note note) =>
    [
        note.Title,
        note.Content,
        note.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
        note.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
    ];
}
