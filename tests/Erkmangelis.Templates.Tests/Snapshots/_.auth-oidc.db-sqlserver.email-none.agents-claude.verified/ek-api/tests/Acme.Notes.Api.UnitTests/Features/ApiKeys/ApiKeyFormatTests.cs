namespace Acme.Notes.Api.UnitTests.Features.ApiKeys;

public sealed class ApiKeyFormatTests
{
    [Fact]
    public void A_generated_key_parses_back_and_matches_its_hash()
    {
        var (key, lookupId, hash) = ApiKeyFormat.Generate();

        Assert.StartsWith("ak_", key, StringComparison.Ordinal);
        Assert.True(ApiKeyFormat.TryParse(key, out var parsedLookupId, out var secret));
        Assert.Equal(lookupId, parsedLookupId);
        Assert.True(ApiKeyFormat.Matches(secret, hash));
        Assert.False(ApiKeyFormat.Matches(secret + "x", hash));
    }

    [Fact]
    public void Two_keys_never_share_a_lookup_id_or_secret()
    {
        var first = ApiKeyFormat.Generate();
        var second = ApiKeyFormat.Generate();

        Assert.NotEqual(first.LookupId, second.LookupId);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ak_")]
    [InlineData("xx_0123456789ab_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ak_short_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ak_0123456789ab_tooshort")]
    public void Malformed_keys_do_not_parse(string key) =>
        Assert.False(ApiKeyFormat.TryParse(key, out _, out _));

    [Fact]
    public void The_hint_never_contains_the_secret()
    {
        var (key, lookupId, _) = ApiKeyFormat.Generate();
        Assert.True(ApiKeyFormat.TryParse(key, out _, out var secret));

        Assert.DoesNotContain(secret, ApiKeyFormat.Hint(lookupId), StringComparison.Ordinal);
    }
}
