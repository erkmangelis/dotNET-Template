using System.Globalization;
using System.Text;
using MiniExcelLibs;

namespace MyProject.Api.Infrastructure.Documents;

internal enum SpreadsheetFormat
{
    Csv,
    Xlsx,
}

/// <summary>
/// CSV and Excel in and out. Excel goes through MiniExcel (Apache-2.0), which reads and writes
/// .xlsx without Office and without a commercial licence; CSV is written here, a row at a time.
/// </summary>
internal static class Spreadsheets
{
    public const string CsvContentType = "text/csv";
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The format a file name or content type names, or null when it is neither.</summary>
    public static SpreadsheetFormat? FormatOf(string? fileName, string? contentType) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".csv" => SpreadsheetFormat.Csv,
            ".xlsx" => SpreadsheetFormat.Xlsx,
            _ => contentType switch
            {
                CsvContentType => SpreadsheetFormat.Csv,
                XlsxContentType => SpreadsheetFormat.Xlsx,
                _ => null,
            },
        };

    /// <summary>Writes RFC 4180 CSV (CRLF line ends) in UTF-8 with a byte order mark, which Excel needs to read accents right.</summary>
    public static async Task WriteCsvAsync(Stream output, IReadOnlyList<string> headers, IAsyncEnumerable<string?[]> rows, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(string.Join(',', headers.Select(Field)));
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            await writer.WriteLineAsync(string.Join(',', row.Select(Field)));
        }

        await writer.FlushAsync(cancellationToken);
    }

    /// <summary>Writes one sheet with a header row. The rows are held in memory while the workbook is built.</summary>
    public static async Task WriteXlsxAsync(Stream output, IReadOnlyList<string> headers, IEnumerable<string?[]> rows, CancellationToken cancellationToken)
    {
        var sheet = rows.Select(row =>
        {
            var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var column = 0; column < headers.Count; column++)
            {
                cells[headers[column]] = column < row.Length ? Defused(row[column]) : null;
            }

            return cells;
        });
        // MiniExcel needs a seekable stream; the response body is not.
        using var buffer = new MemoryStream();
        await buffer.SaveAsAsync(sheet, printHeader: true, excelType: ExcelType.XLSX, cancellationToken: cancellationToken);
        buffer.Position = 0;
        await buffer.CopyToAsync(output, cancellationToken);
    }

    /// <summary>The rows under the header row, each as column name to text. Empty rows are skipped. <paramref name="input"/> must be seekable.</summary>
    public static IEnumerable<IReadOnlyDictionary<string, string?>> Read(Stream input, SpreadsheetFormat format)
    {
        var type = format == SpreadsheetFormat.Csv ? ExcelType.CSV : ExcelType.XLSX;
        foreach (IDictionary<string, object?> row in input.Query(useHeaderRow: true, excelType: type))
        {
            var cells = row.ToDictionary(
                cell => cell.Key.Trim(),
                cell => cell.Value is null ? null : Convert.ToString(cell.Value, CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);
            if (cells.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
            {
                yield return cells;
            }
        }
    }

    private static string Field(string? value)
    {
        value = Defused(value);
        if (value is null)
        {
            return "";
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    /// <summary>
    /// A cell starting with = + - @ or a tab is a formula to a spreadsheet, which can run commands or
    /// leak data when the export is opened (CSV injection). A leading apostrophe makes it plain text.
    /// </summary>
    internal static string? Defused(string? value) =>
        value is { Length: > 0 } && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
}
