namespace MyProject.Api.IntegrationTests;

/// <summary>Waits for work that finishes outside the request, such as a background job.</summary>
internal static class Eventually
{
    /// <summary>Calls <paramref name="read"/> until <paramref name="done"/> holds for its result, or fails after 30 seconds.</summary>
    public static async Task<T> HoldsAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"Still not done after 30 seconds. Last value: {value}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
