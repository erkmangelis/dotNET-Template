using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests.Files;

public sealed class FileTests(ApiFactory api) : IAsyncLifetime
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
    public async Task An_uploaded_image_is_available_with_its_detected_type_and_a_thumbnail()
    {
        var png = TestFiles.Png();

        // The client claims a type; the signature decides.
        using var response = await TestFiles.UploadAsync(_client, png, "photo.png", "application/x-anything");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var file = await response.Content.ReadFromJsonAsync<FileResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(file);
        Assert.Equal("available", file.Status);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(png.Length, file.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(png)), file.Sha256);
        Assert.True(file.HasThumbnail);

        using var thumbnail = await _client.GetAsync($"/v1/files/{file.Id}/thumbnail", TestContext.Current.CancellationToken);
        Assert.Equal("image/webp", thumbnail.Content.Headers.ContentType?.MediaType);
        using var decoded = SkiaSharp.SKBitmap.Decode(await thumbnail.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(256, Math.Max(decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task A_download_returns_the_same_bytes_as_an_attachment()
    {
        var file = await TestFiles.UploadOkAsync(_client, "a,b\n1,2\n"u8.ToArray(), "../../etc/data.csv");

        using var download = await _client.GetAsync($"/v1/files/{file.Id}/content", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("a,b\n1,2\n"u8.ToArray(), await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/csv", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        // The path is dropped: the name is only ever metadata.
        Assert.Equal("data.csv", file.FileName);
    }

    [Fact]
    public async Task A_file_whose_signature_is_not_allowed_is_refused_whatever_its_name()
    {
        using var response = await TestFiles.UploadAsync(_client, TestFiles.Executable(), "invoice.pdf", "application/pdf");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var page = await _client.GetFromJsonAsync<PagedResult<FileResponse>>("/v1/files", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(page!.Items, file => file.FileName == "invoice.pdf");
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        using var response = await TestFiles.UploadAsync(_client, [], "empty.txt");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_deleted_file_is_gone_with_its_content()
    {
        var file = await TestFiles.UploadOkAsync(_client, TestFiles.Png(), "gone.png");

        using var deleted = await _client.DeleteAsync($"/v1/files/{file.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var read = await _client.GetAsync($"/v1/files/{file.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        var storage = api.Services.GetRequiredService<IObjectStorage>();
        Assert.Null(await storage.GetSizeAsync(ObjectKeys.Content(file.Id), TestContext.Current.CancellationToken));
        Assert.Null(await storage.GetSizeAsync(ObjectKeys.Thumbnail(file.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_temporary_file_expires()
    {
        var file = await TestFiles.UploadOkAsync(_client, TestFiles.Png(), "scratch.png", temporary: true);

        Assert.NotNull(file.ExpiresAt);
        Assert.InRange(file.ExpiresAt.Value, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
    }
}
