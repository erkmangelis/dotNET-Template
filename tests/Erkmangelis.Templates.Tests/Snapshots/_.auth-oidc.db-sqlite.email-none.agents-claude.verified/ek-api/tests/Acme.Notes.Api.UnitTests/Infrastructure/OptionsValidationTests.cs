namespace Acme.Notes.Api.UnitTests.Infrastructure;

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

    [Theory]
    [InlineData(new[] { "en", "tr" }, "en", true)]
    [InlineData(new[] { "en", "tr" }, "tr", true)]
    [InlineData(new[] { "en" }, "tr", false)]
    [InlineData(new[] { "en", "not-a-language" }, "en", false)]
    public void Localization_needs_real_languages_including_the_default(string[] languages, string defaultLanguage, bool valid) =>
        Assert.Equal(valid, LocalizationSettings.AreValid(new LocalizationSettings { Languages = languages, DefaultLanguage = defaultLanguage }));
}
