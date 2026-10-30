using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Api.IntegrationTests.Files;

/// <summary>Uploads and downloads straight between the client and storage, through links the API signs.</summary>
public sealed class SignedLinkTests(ApiFactory api) : IAsyncLifetime
{
    // Talks to storage directly, as a browser would.
    private readonly HttpClient _storage = new();
    private HttpClient _client = null!;

#if (UseAuth)
    public async ValueTask InitializeAsync() => _client = (await TestUsers.SignInNewUserAsync(api)).Client;
#else
    public ValueTask InitializeAsync()
    {
        _client = api.CreateClient();
        return ValueTask.CompletedTask;
    }
#endif

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _storage.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_file_uploaded_through_a_signed_link_becomes_available_once_completed()
    {
        var png = TestFiles.Png();
        var ticket = await CreateUploadAsync("direct.png", "image/png", png.Length);
        Assert.Equal("pending", ticket.File.Status);

        using var put = await PutAsync(ticket.UploadUrl, png, "image/png");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var completed = await _client.PostAsync($"/v1/files/{ticket.File.Id}/complete", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var file = await completed.Content.ReadFromJsonAsync<FileResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("available", file!.Status);
        Assert.True(file.HasThumbnail);
    }

    [Fact]
    public async Task A_signed_download_link_returns_the_file()
    {
        var file = await TestFiles.UploadOkAsync(_client, "hello"u8.ToArray(), "hello.txt");

        var link = await _client.GetFromJsonAsync<DownloadLinkResponse>($"/v1/files/{file.Id}/link", TestContext.Current.CancellationToken);
        using var download = await _storage.GetAsync(link!.Url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("hello", await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.True(link.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task The_storage_refuses_a_request_without_a_signature()
    {
        var file = await TestFiles.UploadOkAsync(_client, "private"u8.ToArray(), "private.txt");

        using var download = await _storage.GetAsync($"{api.StorageUrl}/files/{ObjectKeys.Content(file.Id)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, download.StatusCode);
    }

    [Fact]
    public async Task Completing_before_the_upload_arrived_answers_409()
    {
        var ticket = await CreateUploadAsync("later.png", "image/png", 100);

        using var completed = await _client.PostAsync($"/v1/files/{ticket.File.Id}/complete", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
    }

    [Fact]
    public async Task An_upload_that_is_not_what_was_declared_is_refused_and_removed()
    {
        var ticket = await CreateUploadAsync("fake.png", "image/png", TestFiles.Executable().Length);
        using var put = await PutAsync(ticket.UploadUrl, TestFiles.Executable(), "image/png");

        using var completed = await _client.PostAsync($"/v1/files/{ticket.File.Id}/complete", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, completed.StatusCode);
        var file = await _client.GetFromJsonAsync<FileResponse>($"/v1/files/{ticket.File.Id}", TestContext.Current.CancellationToken);
        Assert.Equal("rejected", file!.Status);
        var storage = api.Services.GetRequiredService<IObjectStorage>();
        Assert.Null(await storage.GetSizeAsync(ObjectKeys.Content(ticket.File.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_declared_size_over_the_limit_gets_no_link()
    {
        using var response = await _client.PostAsJsonAsync("/v1/files/uploads", new { fileName = "huge.pdf", contentType = "application/pdf", size = 1L << 40 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_type_that_is_not_allowed_gets_no_link()
    {
        using var response = await _client.PostAsJsonAsync("/v1/files/uploads", new { fileName = "page.html", contentType = "text/html", size = 10 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private async Task<UploadTicketResponse> CreateUploadAsync(string fileName, string contentType, long size)
    {
        using var response = await _client.PostAsJsonAsync("/v1/files/uploads", new { fileName, contentType, size }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadTicketResponse>(TestContext.Current.CancellationToken))!;
    }

    private Task<HttpResponseMessage> PutAsync(Uri url, byte[] content, string contentType)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return _storage.PutAsync(url, body, TestContext.Current.CancellationToken);
    }
}
