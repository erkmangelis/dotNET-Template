using System.Net.Http.Headers;
using System.Net.Http.Json;
using SkiaSharp;

namespace Acme.Notes.Api.IntegrationTests.Files;

internal static class TestFiles
{
    /// <summary>A real PNG of the given size, drawn with SkiaSharp.</summary>
    public static byte[] Png(int width = 640, int height = 480)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>The start of a Windows executable: no allowed type has this signature, whatever the name says.</summary>
    public static byte[] Executable() => [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00];

    public static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] content, string fileName, string contentType = "application/octet-stream", bool temporary = false)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        return await client.PostAsync(temporary ? "/v1/files?temporary=true" : "/v1/files", form, TestContext.Current.CancellationToken);
    }

    public static async Task<FileResponse> UploadOkAsync(HttpClient client, byte[] content, string fileName, bool temporary = false)
    {
        using var response = await UploadAsync(client, content, fileName, temporary: temporary);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FileResponse>(TestContext.Current.CancellationToken))!;
    }
}
