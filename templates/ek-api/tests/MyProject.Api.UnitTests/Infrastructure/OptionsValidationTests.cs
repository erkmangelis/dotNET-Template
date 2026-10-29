namespace MyProject.Api.UnitTests.Infrastructure;

public sealed class OptionsValidationTests
{
    [Theory]
    [InlineData("https://app.example.com", true)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("*", false)]
    [InlineData("https://app.example.com/path", false)]
    [InlineData("app.example.com", false)]
    [InlineData("ftp://example.com", false)]
    public void Cors_origins_must_be_explicit_http_origins(string origin, bool valid) =>
        Assert.Equal(valid, CorsSettings.AreValid(new CorsSettings { AllowedOrigins = [origin] }));

    [Theory]
    [InlineData("172.16.0.0/12", "10.0.0.5", true)]
    [InlineData("not-a-network", "10.0.0.5", false)]
    [InlineData("172.16.0.0/12", "not-an-address", false)]
    public void Forwarded_headers_need_valid_networks_and_proxies(string network, string proxy, bool valid) =>
        Assert.Equal(valid, ForwardedHeadersSettings.AreValid(new ForwardedHeadersSettings
        {
            KnownNetworks = [network],
            KnownProxies = [proxy],
        }));
}
