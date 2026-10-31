using System.Collections.Concurrent;
using System.Net;

namespace Acme.Notes.Api.IntegrationTests.Webhooks;

/// <summary>A delivery as a subscriber received it.</summary>
public sealed record ReceivedWebhook(Uri Url, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// Every subscriber at once, keyed by URL: records what arrives and answers with the status codes a
/// test queued for that URL (then 200).
/// </summary>
public sealed class WebhookReceiver
{
    private readonly ConcurrentDictionary<Uri, ConcurrentQueue<ReceivedWebhook>> _received = new();
    private readonly ConcurrentDictionary<Uri, ConcurrentQueue<HttpStatusCode>> _answers = new();

    /// <summary>A URL no other test uses, on a public-looking host. Nothing resolves it: deliveries stop here.</summary>
    public static Uri NewUrl() => new($"https://hooks.example.com/{Guid.NewGuid():N}");

    public void Answer(Uri url, params HttpStatusCode[] statuses) => _answers[url] = new ConcurrentQueue<HttpStatusCode>(statuses);

    public IReadOnlyList<ReceivedWebhook> ReceivedAt(Uri url) => _received.TryGetValue(url, out var received) ? [.. received] : [];

    private HttpStatusCode Receive(ReceivedWebhook webhook)
    {
        _received.GetOrAdd(webhook.Url, _ => new()).Enqueue(webhook);
        return _answers.TryGetValue(webhook.Url, out var answers) && answers.TryDequeue(out var status) ? status : HttpStatusCode.OK;
    }

    /// <summary>The primary handler of the webhook client in tests. A new one per handler pipeline; they share the receiver.</summary>
    public sealed class Handler(WebhookReceiver receiver) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(receiver.Receive(new ReceivedWebhook(request.RequestUri!, headers, body)));
        }
    }
}

/// <summary>The incoming webhooks <see cref="RecordingWebhookHandler"/> handled, by the sender's message id.</summary>
public sealed class HandledWebhooks
{
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private readonly ConcurrentDictionary<string, int> _handled = new();

    public int TimesHandled(string messageId) => _handled.GetValueOrDefault(messageId);

    /// <summary>Counts an attempt and says whether it is one of the first <paramref name="failures"/>, which should fail.</summary>
    internal bool ShouldFail(string messageId, int failures) => _attempts.AddOrUpdate(messageId, 1, (_, count) => count + 1) <= failures;

    internal void Handled(string messageId) => _handled.AddOrUpdate(messageId, 1, (_, count) => count + 1);
}

/// <summary>Handles the test source: records each message, and fails the first <c>failTimes</c> attempts when the body asks.</summary>
internal sealed class RecordingWebhookHandler(HandledWebhooks handled) : IIncomingWebhookHandler
{
    public Task HandleAsync(IncomingWebhookMessage message, CancellationToken cancellationToken)
    {
        var failures = message.Payload.TryGetProperty("failTimes", out var failTimes) ? failTimes.GetInt32() : 0;
        if (handled.ShouldFail(message.MessageId, failures))
        {
            throw new InvalidOperationException("Failing on purpose.");
        }

        handled.Handled(message.MessageId);
        return Task.CompletedTask;
    }
}
