using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Acme.Notes.Api.IntegrationTests.Realtime;

/// <summary>Connected clients on each channel hear about note changes as they happen.</summary>
public sealed class RealtimeTests(ApiFactory api)
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(15);
    private static readonly int[] _knownVersion = [1];
    private static readonly int[] _knownAndFutureVersions = [1, 99];
    private static readonly int[] _futureVersion = [99];

    [Fact]
    public async Task A_signalr_client_hears_about_a_new_note()
    {
        var (client, token) = await CallerAsync();
        var title = UniqueTitle();
        var heard = new TaskCompletionSource<Received>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Hub(token);
        connection.On<Received>(RealtimeHub.EventMethod, message =>
        {
            if (message.Type == AppEventTypes.NoteCreated && message.Data.GetProperty("title").GetString() == title)
            {
                heard.TrySetResult(message);
            }
        });
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var note = await CreateNoteAsync(client, title);

        var message = await heard.Task.WaitAsync(_wait, TestContext.Current.CancellationToken);
        Assert.Equal(note.Id, message.Data.GetProperty("id").GetGuid());
        Assert.NotEqual(default, await connection.InvokeAsync<DateTimeOffset>(nameof(RealtimeHub.Ping), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_event_stream_hears_about_a_new_note()
    {
        var (client, _) = await CallerAsync();
        var title = UniqueTitle();
        using var timeout = Timeout();
        using var response = await client.GetAsync(new Uri("/v1/realtime/events", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var events = SseParser.Create(await response.Content.ReadAsStreamAsync(timeout.Token)).EnumerateAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(RealtimeEndpoints.ConnectedEvent, events.Current.EventType);

        var note = await CreateNoteAsync(client, title);

        var heard = await NextAsync(events, item => item.EventType == AppEventTypes.NoteCreated && Data(item).GetProperty("title").GetString() == title);
        Assert.Equal(note.Id, Data(heard).GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(heard.EventId));
    }

    [Fact]
    public async Task A_websocket_client_agrees_a_version_and_hears_about_a_new_note()
    {
        var (client, token) = await CallerAsync();
        var title = UniqueTitle();
        using var socket = await ConnectAsync(token);

        await SendAsync(socket, new { type = "hello", versions = _knownAndFutureVersions });
        var welcome = await ReceiveAsync(socket);
        Assert.Equal("welcome", welcome.GetProperty("type").GetString());
        Assert.Equal(1, welcome.GetProperty("version").GetInt32());

        await SendAsync(socket, new { type = "ping" });
        Assert.Equal("pong", (await ReceiveAsync(socket, frame => frame.GetProperty("type").GetString() != "event")).GetProperty("type").GetString());

        var note = await CreateNoteAsync(client, title);
        var frame = await ReceiveAsync(socket, frame => frame.GetProperty("type").GetString() != "event" || frame.GetProperty("data").GetProperty("title").GetString() == title);

        Assert.Equal("event", frame.GetProperty("type").GetString());
        Assert.Equal(AppEventTypes.NoteCreated, frame.GetProperty("event").GetString());
        Assert.Equal(note.Id, frame.GetProperty("data").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_websocket_client_with_no_supported_version_is_refused()
    {
        var (_, token) = await CallerAsync();
        using var socket = await ConnectAsync(token);

        await SendAsync(socket, new { type = "hello", versions = _futureVersion });

        var error = await ReceiveAsync(socket);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("unsupported_version", error.GetProperty("code").GetString());
        Assert.Equal([1], error.GetProperty("supported").EnumerateArray().Select(version => version.GetInt32()));
        await ReceiveCloseAsync(socket, WebSocketCloseStatus.PolicyViolation);
    }

    [Fact]
    public async Task A_frame_that_is_not_json_closes_the_websocket()
    {
        var (_, token) = await CallerAsync();
        using var socket = await ConnectAsync(token);

        await socket.SendAsync(Encoding.UTF8.GetBytes("hello?"), WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);

        await ReceiveCloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData);
    }

    [Fact]
    public async Task A_page_on_another_site_cannot_connect()
    {
        var (client, _) = await CallerAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/realtime/events");
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record Received(string Id, string Type, DateTimeOffset OccurredAt, JsonElement Data);

    private Task<(HttpClient Client, string? Token)> CallerAsync() => Task.FromResult((api.CreateClient(), (string?)null));

    private HubConnection Hub(string? token) => new HubConnectionBuilder()
        .WithUrl(new Uri(api.Server.BaseAddress, RealtimeSetup.HubPath), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
            options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var client = api.Server.CreateWebSocketClient();
                return await client.ConnectAsync(context.Uri, cancellationToken);
            };
            if (token is not null)
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            }
        })
        .Build();

    private async Task<WebSocket> ConnectAsync(string? _, string query = "")
    {
        var client = api.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri(api.Server.BaseAddress, $"/v1/realtime/ws{query}"), TestContext.Current.CancellationToken);
    }

    private static Task SendAsync(WebSocket socket, object frame) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, endOfMessage: true, TestContext.Current.CancellationToken);

    /// <summary>The next frame <paramref name="match"/> accepts; other tests' events may come first.</summary>
    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, Func<JsonElement, bool> match)
    {
        while (true)
        {
            var frame = await ReceiveAsync(socket);
            if (match(frame))
            {
                return frame;
            }
        }
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        using var timeout = Timeout();
        var buffer = new byte[64 * 1024];
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
            length += result.Count;
        }
        while (!result.EndOfMessage);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonSerializer.Deserialize<JsonElement>(buffer.AsSpan(0, length));
    }

    private static async Task ReceiveCloseAsync(WebSocket socket, WebSocketCloseStatus expected)
    {
        using var timeout = Timeout();
        var result = await socket.ReceiveAsync(new byte[1024], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(expected, result.CloseStatus);
    }

    private static async Task<SseItem<string>> NextAsync(IAsyncEnumerator<SseItem<string>> events, Func<SseItem<string>, bool> match)
    {
        while (await events.MoveNextAsync())
        {
            if (match(events.Current))
            {
                return events.Current;
            }
        }

        throw new InvalidOperationException("The event stream ended.");
    }

    private static JsonElement Data(SseItem<string> item) => JsonSerializer.Deserialize<JsonElement>(item.Data).GetProperty("data");

    private static async Task<NoteResponse> CreateNoteAsync(HttpClient client, string title)
    {
        var created = await client.PostAsJsonAsync("/v1/notes", new { title, content = "Live" }, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<NoteResponse>(TestContext.Current.CancellationToken))!;
    }

    private static CancellationTokenSource Timeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(_wait);
        return timeout;
    }

    private static string UniqueTitle() => $"Live {Guid.NewGuid():N}";
}
