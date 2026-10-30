using UglyToad.PdfPig;

namespace MyProject.Api.UnitTests.Infrastructure.Documents;

public sealed class PdfReportsTests
{
    private static readonly PdfColumn[] _columns = [new("Title", 1), new("Content", 2)];

    [Fact]
    public void The_report_holds_the_title_headings_and_rows_in_any_language()
    {
        var pdf = PdfReports.Table("Notlar", "2 notes", _columns, [["Alışveriş listesi", "Süt, ekmek, peynir"], ["İş", null]]);

        using var document = PdfDocument.Open(pdf);
        var text = document.GetPage(1).Text;
        Assert.Contains("Notlar", text, StringComparison.Ordinal);
        Assert.Contains("Content", text, StringComparison.Ordinal);
        Assert.Contains("Alışveriş listesi", text, StringComparison.Ordinal);
        Assert.Contains("İş", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_tables_continue_on_numbered_pages()
    {
        var rows = Enumerable.Range(1, 200).Select(number => new string?[] { $"Note {number}", new string('x', 500) });

        var pdf = PdfReports.Table("Notes", "200 notes", _columns, rows);

        using var document = PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages > 1);
        var last = document.GetPage(document.NumberOfPages).Text;
        Assert.Contains("Note 200", last, StringComparison.Ordinal);
        Assert.Contains($"{document.NumberOfPages} / {document.NumberOfPages}", last, StringComparison.Ordinal);
        // A cell longer than its column is cut, not spilled into the next one.
        Assert.Contains("…", document.GetPage(1).Text, StringComparison.Ordinal);
    }
}
