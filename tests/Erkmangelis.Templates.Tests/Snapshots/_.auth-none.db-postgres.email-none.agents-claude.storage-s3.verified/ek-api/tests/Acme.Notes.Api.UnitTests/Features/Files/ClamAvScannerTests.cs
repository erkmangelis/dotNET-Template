using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.UnitTests.Features.Files;

public sealed class ClamAvScannerTests
{
    [Theory]
    [InlineData("stream: OK\0", null)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND\0", "Win.Test.EICAR_HDB-1")]
    public void Replies_are_read(string reply, string? threat)
    {
        Assert.Equal(threat, ClamAvScanner.Parse(reply).Threat);
    }

    [Fact]
    public void An_error_reply_throws_so_the_upload_fails_instead_of_skipping_the_scan()
    {
        Assert.Throws<InvalidOperationException>(() => ClamAvScanner.Parse("INSTREAM size limit exceeded. ERROR\0"));
    }

    [Fact]
    public async Task The_file_is_streamed_in_length_prefixed_chunks()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var received = AcceptOneAsync(listener, "stream: OK\0");
        var options = Options.Create(new FilesOptions { Scanning = { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port } });
        var content = new byte[150_000];
        Random.Shared.NextBytes(content);

        var result = await new ClamAvScanner(options).ScanAsync(new MemoryStream(content), TestContext.Current.CancellationToken);

        Assert.True(result.IsClean);
        var (command, payload) = await received;
        Assert.Equal("zINSTREAM\0", command);
        Assert.Equal(content, payload);
    }

    /// <summary>Plays clamd: reads the command and the chunks, then answers <paramref name="reply"/>.</summary>
    private static async Task<(string Command, byte[] Payload)> AcceptOneAsync(TcpListener listener, string reply)
    {
        using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await using var stream = client.GetStream();
        var command = new byte[10];
        await stream.ReadExactlyAsync(command, TestContext.Current.CancellationToken);
        using var payload = new MemoryStream();
        var length = new byte[4];
        while (true)
        {
            await stream.ReadExactlyAsync(length, TestContext.Current.CancellationToken);
            var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(length);
            if (size == 0)
            {
                break;
            }

            var chunk = new byte[size];
            await stream.ReadExactlyAsync(chunk, TestContext.Current.CancellationToken);
            payload.Write(chunk);
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), TestContext.Current.CancellationToken);
        client.Client.Shutdown(SocketShutdown.Send);
        return (Encoding.ASCII.GetString(command), payload.ToArray());
    }
}
