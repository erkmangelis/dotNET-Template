using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Acme.Notes.Api.IntegrationTests.Files;

/// <summary>Resumable uploads over the tus protocol, as tus-js-client or Uppy send them.</summary>
public sealed class TusUploadTests(ApiFactory api) : IAsyncLifetime
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
    public async Task An_upload_sent_in_pieces_becomes_an_available_file()
    {
        var png = TestFiles.Png(1200, 900);
        var location = await CreateAsync(_client, png.Length, "big.png");

        var half = png.Length / 2;
        using (var first = await PatchAsync(_client, location, png[..half], offset: 0))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        // A client that lost its connection asks where to resume.
        using var head = new HttpRequestMessage(HttpMethod.Head, location);
        head.Headers.Add("Tus-Resumable", "1.0.0");
        using var resume = await _client.SendAsync(head, TestContext.Current.CancellationToken);
        Assert.Equal(half.ToString(System.Globalization.CultureInfo.InvariantCulture), resume.Headers.GetValues("Upload-Offset").Single());

        using (var second = await PatchAsync(_client, location, png[half..], offset: half))
        {
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        }

        var id = Guid.ParseExact(location.Segments[^1], "N");
        var file = await _client.GetFromJsonAsync<FileResponse>($"/v1/files/{id}", TestContext.Current.CancellationToken);
        Assert.Equal("available", file!.Status);
        Assert.Equal("big.png", file.FileName);
        Assert.Equal("image/png", file.ContentType);
        Assert.True(file.HasThumbnail);
    }

    [Fact]
    public async Task A_finished_upload_of_a_refused_type_is_recorded_as_rejected()
    {
        var content = TestFiles.Executable();
        var location = await CreateAsync(_client, content.Length, "setup.png");

        using var patch = await PatchAsync(_client, location, content, offset: 0);

        var id = Guid.ParseExact(location.Segments[^1], "N");
        var file = await _client.GetFromJsonAsync<FileResponse>($"/v1/files/{id}", TestContext.Current.CancellationToken);
        Assert.Equal("rejected", file!.Status);
        Assert.NotNull(file.RejectionReason);
    }

    private static async Task<Uri> CreateAsync(HttpClient client, long length, string fileName)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/v1/files/tus") { Content = new ByteArrayContent([]) };
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        create.Headers.Add("Upload-Metadata", $"filename {Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName))}");
        using var created = await client.SendAsync(create, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return new Uri(client.BaseAddress!, created.Headers.Location!);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Uri location, byte[] chunk, long offset)
    {
        var content = new ByteArrayContent(chunk);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        using var patch = new HttpRequestMessage(HttpMethod.Patch, location) { Content = content };
        patch.Headers.Add("Tus-Resumable", "1.0.0");
        patch.Headers.Add("Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await client.SendAsync(patch, TestContext.Current.CancellationToken);
    }
}
