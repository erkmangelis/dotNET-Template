using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Acme.Notes.Api.Infrastructure.Documents;

/// <summary>One column of a <see cref="PdfReports.Table"/>: its heading and its share of the page width.</summary>
internal sealed record PdfColumn(string Heading, double Width);

/// <summary>
/// PDF reports with PdfPig (Apache-2.0). PDFsharp 6.5 and later are AGPL and QuestPDF needs a paid
/// licence above a revenue limit, so neither is used. Text is set in Liberation Sans (SIL Open Font
/// License, see Fonts/OFL.txt), embedded in each PDF, so every language the font covers prints the
/// same on every machine.
/// </summary>
internal static class PdfReports
{
    public const string ContentType = "application/pdf";

    // A4 in points.
    private const double PageWidth = 595;
    private const double PageHeight = 842;
    private const double Margin = 40;
    private const double FontSize = 9;
    private const double LineHeight = 14;
    private static readonly byte[] _regular = Font("LiberationSans-Regular.ttf");
    private static readonly byte[] _bold = Font("LiberationSans-Bold.ttf");

    /// <summary>
    /// A table across as many A4 pages as it needs, headings repeated on each, with the title on the
    /// first page and page numbers at the foot. A cell too long for its column is cut with an ellipsis.
    /// </summary>
    public static byte[] Table(string title, string subtitle, IReadOnlyList<PdfColumn> columns, IEnumerable<string?[]> rows)
    {
        var builder = new PdfDocumentBuilder { DocumentInformation = { Title = title, Producer = "PdfPig" } };
        var regular = builder.AddTrueTypeFont(_regular);
        var bold = builder.AddTrueTypeFont(_bold);
        var usable = PageWidth - (2 * Margin);
        var total = columns.Sum(column => column.Width);
        var widths = columns.Select(column => usable * column.Width / total).ToArray();

        PdfPageBuilder? page = null;
        var y = 0d;
        var pages = new List<PdfPageBuilder>();

        void NewPage()
        {
            page = builder.AddPage(PageSize.A4);
            pages.Add(page);
            y = PageHeight - Margin;
            if (pages.Count == 1)
            {
                page.AddText(title, 16, new PdfPoint(Margin, y - 16), bold);
                page.AddText(subtitle, FontSize, new PdfPoint(Margin, y - 32), regular);
                y -= 52;
            }

            WriteRow(page, columns.Select(column => column.Heading).ToArray(), bold);
            page.DrawLine(new PdfPoint(Margin, y + 4), new PdfPoint(PageWidth - Margin, y + 4), 0.5);
        }

        void WriteRow(PdfPageBuilder target, string?[] cells, PdfDocumentBuilder.AddedFont font)
        {
            y -= LineHeight;
            var x = Margin;
            for (var column = 0; column < widths.Length; column++)
            {
                var text = Fit(target, column < cells.Length ? cells[column] : null, widths[column] - 6, font);
                if (text.Length > 0)
                {
                    target.AddText(text, FontSize, new PdfPoint(x, y), font);
                }

                x += widths[column];
            }
        }

        NewPage();
        foreach (var row in rows)
        {
            if (y - LineHeight < Margin + LineHeight)
            {
                NewPage();
            }

            WriteRow(page!, row, regular);
        }

        for (var number = 0; number < pages.Count; number++)
        {
            pages[number].AddText($"{number + 1} / {pages.Count}", 8, new PdfPoint(PageWidth - Margin - 30, Margin / 2), regular);
        }

        return builder.Build();
    }

    /// <summary>The longest start of <paramref name="text"/> on one line that fits <paramref name="width"/>, with an ellipsis when it was cut.</summary>
    private static string Fit(PdfPageBuilder page, string? text, double width, PdfDocumentBuilder.AddedFont font)
    {
        text = string.Join(' ', (text ?? "").Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (text.Length == 0 || Measure(page, text, font) <= width)
        {
            return text;
        }

        int low = 0, high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Measure(page, text[..middle] + "…", font) <= width)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return text[..low].TrimEnd() + "…";
    }

    private static double Measure(PdfPageBuilder page, string text, PdfDocumentBuilder.AddedFont font)
    {
        var letters = page.MeasureText(text, FontSize, new PdfPoint(0, 0), font);
        return letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
    }

    private static byte[] Font(string fileName)
    {
        using var resource = typeof(PdfReports).Assembly.GetManifestResourceStream($"Fonts.{fileName}")
            ?? throw new InvalidOperationException($"The font {fileName} is not embedded in the assembly.");
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    }
}
