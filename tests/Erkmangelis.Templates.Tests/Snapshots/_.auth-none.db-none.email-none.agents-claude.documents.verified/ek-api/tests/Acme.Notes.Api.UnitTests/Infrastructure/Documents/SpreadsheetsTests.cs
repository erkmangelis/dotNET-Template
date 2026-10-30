using System.Text;

namespace Acme.Notes.Api.UnitTests.Infrastructure.Documents;

public sealed class SpreadsheetsTests
{
    private static readonly string[] _headers = ["Title", "Content"];

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "'=HYPERLINK(\"http://evil\")")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Plain", "Plain")]
    public void Cells_that_a_spreadsheet_would_run_as_a_formula_are_made_text(string value, string expected)
    {
        Assert.Equal(expected, Spreadsheets.Defused(value));
    }

    [Fact]
    public async Task Csv_quotes_what_needs_quoting_and_reads_back()
    {
        using var output = new MemoryStream();

        await Spreadsheets.WriteCsvAsync(output, _headers, Rows(["Şehir, ülke", "He said \"hi\"\nthen left"]), TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.StartsWith("\uFEFFTitle,Content", text, StringComparison.Ordinal);
        Assert.Contains("\"Şehir, ülke\",\"He said \"\"hi\"\"\nthen left\"", text, StringComparison.Ordinal);
        output.Position = 0;
        var row = Assert.Single(Spreadsheets.Read(output, SpreadsheetFormat.Csv));
        Assert.Equal("Şehir, ülke", row["title"]);
        Assert.Equal("He said \"hi\"\nthen left", row["Content"]?.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Xlsx_round_trips()
    {
        using var output = new MemoryStream();

        await Spreadsheets.WriteXlsxAsync(output, _headers, [["Groceries", "Milk"], ["Empty", null]], TestContext.Current.CancellationToken);

        output.Position = 0;
        var rows = Spreadsheets.Read(output, SpreadsheetFormat.Xlsx).ToList();
        Assert.Equal(["Groceries", "Empty"], rows.Select(row => row["Title"]));
        Assert.Equal("Milk", rows[0]["Content"]);
    }

    [Theory]
    [InlineData("notes.CSV", null, "Csv")]
    [InlineData("notes.xlsx", null, "Xlsx")]
    [InlineData("upload", Spreadsheets.XlsxContentType, "Xlsx")]
    [InlineData("notes.xls", "application/vnd.ms-excel", null)]
    public void The_format_comes_from_the_name_then_the_type(string fileName, string? contentType, string? expected)
    {
        Assert.Equal(expected, Spreadsheets.FormatOf(fileName, contentType)?.ToString());
    }

    private static async IAsyncEnumerable<string?[]> Rows(params string?[][] rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }
}
