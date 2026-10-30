using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using UglyToad.PdfPig;

namespace Acme.Notes.Api.IntegrationTests;

public sealed class NoteDocumentTests(ApiFactory api) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _client = api.CreateClient();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Notes_export_as_csv_with_formulas_defused()
    {
        var title = Unique("=1+1 ");
        await _client.PostAsJsonAsync("/v1/notes", new { title, content = "Süt, ekmek" }, TestContext.Current.CancellationToken);

        using var response = await _client.GetAsync($"/v1/notes/export/csv?search={Uri.EscapeDataString(title)}", TestContext.Current.CancellationToken);

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith("\uFEFFTitle,Content,CreatedAt,UpdatedAt", csv, StringComparison.Ordinal);
        Assert.Contains($"'{title},\"Süt, ekmek\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_excel_export_imports_back()
    {
        var title = Unique("Round trip");
        await _client.PostAsJsonAsync("/v1/notes", new { title, content = "Body" }, TestContext.Current.CancellationToken);
        using var export = await _client.GetAsync($"/v1/notes/export/xlsx?search={Uri.EscapeDataString(title)}", TestContext.Current.CancellationToken);
        var workbook = await export.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        await DeleteAsync(title);

        var result = await ImportAsync(workbook, "notes.xlsx");

        Assert.Equal(1, result.Created);
        var page = await _client.GetFromJsonAsync<PagedResult<NoteResponse>>($"/v1/notes?search={Uri.EscapeDataString(title)}", TestContext.Current.CancellationToken);
        Assert.Equal("Body", Assert.Single(page!.Items).Content);
    }

    [Fact]
    public async Task A_csv_import_creates_the_good_rows_and_reports_the_rest()
    {
        var good = Unique("Imported");
        var csv = $"Title,Content\n{good},Fine\n,No title\n{good},Duplicate\n{new string('x', Note.TitleMaxLength + 1)},Too long\n";

        var result = await ImportAsync(Encoding.UTF8.GetBytes(csv), "notes.csv");

        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Skipped);
        Assert.Equal([3, 5], result.Errors.Select(error => error.Row));
    }

    [Fact]
    public async Task Another_format_is_refused()
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent("{}"u8.ToArray()), "file", "notes.json" } };

        using var response = await _client.PostAsync("/v1/notes/imports/file", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task The_pdf_report_lists_the_notes()
    {
        // Short enough for its column, which cuts longer text.
        var title = $"Rapor İçin {Guid.NewGuid():N}"[..19];
        await _client.PostAsJsonAsync("/v1/notes", new { title }, TestContext.Current.CancellationToken);

        using var response = await _client.GetAsync($"/v1/notes/report?search={Uri.EscapeDataString(title)}", TestContext.Current.CancellationToken);

        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        using var pdf = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Contains(title, pdf.GetPage(1).Text, StringComparison.Ordinal);
    }

    private async Task<FileImportResponse> ImportAsync(byte[] content, string fileName)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var response = await _client.PostAsync("/v1/notes/imports/file", form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileImportResponse>(TestContext.Current.CancellationToken))!;
    }

    private async Task DeleteAsync(string title)
    {
        var page = await _client.GetFromJsonAsync<PagedResult<NoteResponse>>($"/v1/notes?search={Uri.EscapeDataString(title)}", TestContext.Current.CancellationToken);
        foreach (var note in page!.Items)
        {
            await _client.DeleteAsync($"/v1/notes/{note.Id}", TestContext.Current.CancellationToken);
        }
    }

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}";
}
