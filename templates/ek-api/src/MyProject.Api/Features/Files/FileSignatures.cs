using System.Text;

namespace MyProject.Api.Features.Files;

/// <summary>
/// Tells a file's type from its first bytes, its signature, never from the name or the type the
/// client sent: a renamed executable stays an executable. Add a signature here to accept a new type,
/// then list the type in <c>Files:AllowedTypes</c>.
/// </summary>
internal static class FileSignatures
{
    /// <summary>How many bytes <see cref="Detect"/> looks at.</summary>
    public const int HeaderLength = 4096;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";
    public const string Pdf = "application/pdf";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Zip = "application/zip";
    public const string Csv = "text/csv";
    public const string Text = "text/plain";

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool IsImage(string contentType) => contentType is Png or Jpeg or Gif or Webp;

    /// <summary>The content type <paramref name="header"/> starts, or null when it is none this app knows.</summary>
    /// <param name="header">The file's first bytes, up to <see cref="HeaderLength"/>.</param>
    /// <param name="fileName">Only tells apart types that share a signature: a spreadsheet from another zip, CSV from plain text.</param>
    public static string? Detect(ReadOnlySpan<byte> header, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return header switch
        {
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Png,
            [0xFF, 0xD8, 0xFF, ..] => Jpeg,
            [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => Gif,
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => Webp,
            [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => Pdf,
            [0x50, 0x4B, 0x03, 0x04, ..] => extension == ".xlsx" ? Xlsx : Zip,
            _ when IsText(header) => extension == ".csv" ? Csv : Text,
            _ => null,
        };
    }

    /// <summary>UTF-8 without control characters (DEL included) other than tab and line breaks. A sequence cut off at the end of the header still counts.</summary>
    private static bool IsText(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        foreach (var value in header)
        {
            if ((value < 0x20 && value is not ((byte)'\t' or (byte)'\n' or (byte)'\r')) || value == 0x7F)
            {
                return false;
            }
        }

        // Drop a multi-byte character the header may have cut in two.
        var end = header.Length;
        for (var back = 1; back <= 3 && back <= header.Length; back++)
        {
            var value = header[^back];
            if ((value & 0xC0) == 0xC0)
            {
                end = header.Length - back;
                break;
            }

            if ((value & 0x80) == 0)
            {
                break;
            }
        }

        try
        {
            _strictUtf8.GetCharCount(header[..end]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
