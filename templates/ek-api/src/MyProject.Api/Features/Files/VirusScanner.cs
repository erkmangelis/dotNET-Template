using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Features.Files;

/// <param name="Threat">What the scanner found, or null when the file is clean.</param>
internal sealed record ScanResult(string? Threat)
{
    public static readonly ScanResult Clean = new((string?)null);

    public bool IsClean => Threat is null;
}

internal interface IVirusScanner
{
    /// <summary>Scans <paramref name="content"/> from its current position to the end.</summary>
    Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>Used while <c>Files:Scanning:Enabled</c> is false: every file counts as clean.</summary>
internal sealed class NoVirusScanner : IVirusScanner
{
    public Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken) => Task.FromResult(ScanResult.Clean);
}

/// <summary>
/// Streams the file to ClamAV's clamd with its INSTREAM command, so the file never needs to be on a
/// disk clamd can read. clamd refuses a stream over its StreamMaxLength (100 MB by default): keep it
/// at least <c>Files:MaxSizeBytes</c>.
/// </summary>
internal sealed class ClamAvScanner(IOptions<FilesOptions> options) : IVirusScanner
{
    private const int ChunkSize = 64 * 1024;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        var settings = options.Value.Scanning;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
        await using var clamd = client.GetStream();

        await clamd.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
        var buffer = new byte[4 + ChunkSize];
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(4, ChunkSize), timeout.Token)) > 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, read);
            await clamd.WriteAsync(buffer.AsMemory(0, 4 + read), timeout.Token);
        }

        // A zero-length chunk ends the stream.
        await clamd.WriteAsync(new byte[4], timeout.Token);

        using var reply = new MemoryStream();
        await clamd.CopyToAsync(reply, timeout.Token);
        return Parse(Encoding.ASCII.GetString(reply.ToArray()));
    }

    /// <summary>clamd answers <c>stream: OK</c>, <c>stream: &lt;name&gt; FOUND</c> or <c>... ERROR</c>.</summary>
    internal static ScanResult Parse(string reply)
    {
        reply = reply.TrimEnd('\0', '\n');
        if (reply.EndsWith(": OK", StringComparison.Ordinal))
        {
            return ScanResult.Clean;
        }

        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var start = reply.IndexOf(": ", StringComparison.Ordinal) + 2;
            return new ScanResult(reply[start..^" FOUND".Length]);
        }

        throw new InvalidOperationException($"clamd could not scan the file: {reply}");
    }
}
