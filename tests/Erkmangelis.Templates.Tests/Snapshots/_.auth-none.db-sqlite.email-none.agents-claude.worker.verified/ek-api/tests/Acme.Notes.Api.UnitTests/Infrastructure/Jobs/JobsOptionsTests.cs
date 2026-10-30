namespace Acme.Notes.Api.UnitTests.Infrastructure.Jobs;

public sealed class JobsOptionsTests
{
    [Theory]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(20, 3600)]
    public void Each_retry_waits_twice_as_long_as_the_one_before_up_to_an_hour(int attempt, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), new JobsOptions { RetryDelaySeconds = 10 }.RetryDelayBefore(attempt));
}
