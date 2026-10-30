using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Notes.Api.IntegrationTests.Files;

/// <summary>With scanning on, a file the scanner flags is refused. The ClamAV protocol itself is unit tested.</summary>
public sealed class VirusScanningTests(ApiFactory api)
{
    // The EICAR test file: every virus scanner flags it, and it is harmless.
    private static readonly byte[] _eicar = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"u8.ToArray();

    [Fact]
    public async Task A_file_the_scanner_flags_is_refused()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        using var scanned = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IVirusScanner, EicarScanner>()));
        using var client = scanned.CreateClient();
        client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;
        user.Client.Dispose();

        using var flagged = await TestFiles.UploadAsync(client, _eicar, "eicar.txt");
        using var clean = await TestFiles.UploadAsync(client, "clean"u8.ToArray(), "clean.txt");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, flagged.StatusCode);
        Assert.Equal(HttpStatusCode.Created, clean.StatusCode);
    }

    private sealed class EicarScanner : IVirusScanner
    {
        public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            return copy.ToArray().AsSpan().IndexOf("EICAR-STANDARD-ANTIVIRUS-TEST-FILE"u8) >= 0 ? new ScanResult("Eicar-Test-Signature") : ScanResult.Clean;
        }
    }
}
