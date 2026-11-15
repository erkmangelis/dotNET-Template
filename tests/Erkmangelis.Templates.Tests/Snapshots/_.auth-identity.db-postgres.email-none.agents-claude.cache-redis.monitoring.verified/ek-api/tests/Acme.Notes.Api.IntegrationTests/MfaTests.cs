using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity.Data;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>Authenticator-app two-factor sign-in through Identity's /manage/2fa endpoint.</summary>
public sealed class MfaTests(ApiFactory api)
{
    [Fact]
    public async Task With_an_authenticator_app_a_password_alone_is_not_enough()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        await EnableAuthenticatorAsync(user);
        using var client = api.CreateClient();

        var withoutCode = await client.PostAsJsonAsync("/v1/auth/login", new LoginRequest { Email = user.Email, Password = user.Password }, TestContext.Current.CancellationToken);
        var withCode = await client.PostAsJsonAsync(
            "/v1/auth/login",
            new LoginRequest { Email = user.Email, Password = user.Password, TwoFactorCode = Totp(await SharedKeyAsync(user)) },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutCode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withCode.StatusCode);
        user.Client.Dispose();
    }

    [Fact]
    public async Task A_recovery_code_works_once()
    {
        var user = await TestUsers.SignInNewUserAsync(api);
        var recoveryCode = (await EnableAuthenticatorAsync(user))[0];
        using var client = api.CreateClient();
        var login = new LoginRequest { Email = user.Email, Password = user.Password, TwoFactorRecoveryCode = recoveryCode };

        var first = await client.PostAsJsonAsync("/v1/auth/login", login, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync("/v1/auth/login", login, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        user.Client.Dispose();
    }

    private static async Task<string> SharedKeyAsync(TestUser user)
    {
        var response = await user.Client.PostAsJsonAsync("/v1/auth/manage/2fa", new TwoFactorRequest(), TestContext.Current.CancellationToken);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("sharedKey").GetString()!;
    }

    private static async Task<string[]> EnableAuthenticatorAsync(TestUser user)
    {
        var response = await user.Client.PostAsJsonAsync(
            "/v1/auth/manage/2fa",
            new TwoFactorRequest { Enable = true, TwoFactorCode = Totp(await SharedKeyAsync(user)) },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()!)];
    }

    /// <summary>The 6-digit code an authenticator app shows now (RFC 6238, 30-second steps, HMAC-SHA1).</summary>
    private static string Totp(string base32Key)
    {
        var step = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(step);
        }

#pragma warning disable CA5350 // RFC 6238 authenticator codes are defined with HMAC-SHA1.
        var hash = HMACSHA1.HashData(Base32Decode(base32Key), step);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (code % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var character in input.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(character, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
            }
        }

        return [.. bytes];
    }
}
