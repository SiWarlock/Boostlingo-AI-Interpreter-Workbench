using AiInterpreter.Api.Common;

namespace AiInterpreter.Tests;

public class ClockAndResultTests
{
    private sealed class FakeClock(DateTimeOffset fixedNow) : IClock
    {
        public DateTimeOffset UtcNow => fixedNow;
    }

    [Fact]
    public void system_clock_returns_utc_now()
    {
        IClock clock = new SystemClock();

        var before = DateTimeOffset.UtcNow;
        var now = clock.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.True(now >= before && now <= after, "SystemClock.UtcNow should be the real current instant");
    }

    [Fact]
    public void fake_clock_returns_fixed_time()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 28, 15, 30, 0, TimeSpan.Zero);
        IClock clock = new FakeClock(fixedTime);

        Assert.Equal(fixedTime, clock.UtcNow);
        Assert.Equal(fixedTime, clock.UtcNow); // stable across reads (deterministic for tests)
    }

    [Fact]
    public void result_success_carries_value()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void result_failure_carries_error_and_blocks_value()
    {
        var result = Result<int>.Failure("boom");

        Assert.False(result.IsSuccess);
        Assert.Equal("boom", result.Error);
        var ex = Assert.Throws<InvalidOperationException>(() => _ = result.Value);
        Assert.Contains("failed Result", ex.Message);
    }

    [Fact]
    public void nongeneric_result_success_and_failure()
    {
        Assert.True(Result.Success().IsSuccess);

        var failure = Result.Failure("nope");
        Assert.False(failure.IsSuccess);
        Assert.Equal("nope", failure.Error);
    }
}
