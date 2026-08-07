using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class PhoneBatteryProbeSingleFlightTests
{
    [Fact]
    public async Task PermanentlyHungSourceDoesNotPreventIndependentSourceFromReturning()
    {
        using var hung = new PhoneBatteryProbeSingleFlight<int>();
        using var healthy = new PhoneBatteryProbeSingleFlight<int>();
        var never = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var hungStarts = 0;
        var healthyStarts = 0;

        var hungAttempt = hung.ObserveAsync(
            _ =>
            {
                hungStarts++;
                return never.Task;
            },
            TimeSpan.FromMilliseconds(30),
            CancellationToken.None);
        var healthyAttempt = await healthy.ObserveAsync(
            _ =>
            {
                healthyStarts++;
                return Task.FromResult(97);
            },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        var timedOut = await hungAttempt;

        Assert.Equal(PhoneBatteryProbeAttemptStatus.TimedOut, timedOut.Status);
        Assert.Equal(PhoneBatteryProbeAttemptStatus.Succeeded, healthyAttempt.Status);
        Assert.Equal(97, healthyAttempt.Value);
        Assert.Equal(1, hungStarts);
        Assert.Equal(1, healthyStarts);

        never.TrySetResult(0);
        await WaitUntilAsync(
            () => !hung.IsInFlight,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TimedOutFlightIsNotStartedAgainUntilItsOriginalTaskFinishes()
    {
        using var flight = new PhoneBatteryProbeSingleFlight<int>();
        var never = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;

        var first = await flight.ObserveAsync(
            _ =>
            {
                starts++;
                return never.Task;
            },
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);
        var second = await flight.ObserveAsync(
            _ =>
            {
                starts++;
                return Task.FromResult(2);
            },
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.Equal(PhoneBatteryProbeAttemptStatus.TimedOut, first.Status);
        Assert.Equal(PhoneBatteryProbeAttemptStatus.TimedOut, second.Status);
        Assert.Equal(1, starts);

        never.TrySetResult(1);
        await WaitUntilAsync(
            () => !flight.IsInFlight,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TimedOutFlightCanRecoverWithFreshProbeAfterOriginalCompletes()
    {
        using var flight = new PhoneBatteryProbeSingleFlight<int>();
        var delayed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;

        var timedOut = await flight.ObserveAsync(
            _ =>
            {
                starts++;
                return delayed.Task;
            },
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);
        delayed.TrySetResult(41);
        await delayed.Task;
        await WaitUntilAsync(
            () => !flight.IsInFlight,
            TimeSpan.FromSeconds(1));

        var recovered = await flight.ObserveAsync(
            _ =>
            {
                starts++;
                return Task.FromResult(42);
            },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(PhoneBatteryProbeAttemptStatus.TimedOut, timedOut.Status);
        Assert.Equal(PhoneBatteryProbeAttemptStatus.Succeeded, recovered.Status);
        Assert.Equal(42, recovered.Value);
        Assert.Equal(2, starts);
        Assert.NotNull(recovered.CompletedAt);
    }

    [Fact]
    public async Task CompletionTimeIsCapturedWhenReadingFinishesRatherThanWhenObserved()
    {
        using var flight = new PhoneBatteryProbeSingleFlight<int>();
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = DateTimeOffset.UtcNow;
        var attemptTask = flight.ObserveAsync(
            _ => completion.Task,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await Task.Delay(25);
        completion.TrySetResult(75);
        var attempt = await attemptTask;

        Assert.Equal(PhoneBatteryProbeAttemptStatus.Succeeded, attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
        Assert.True(attempt.CompletedAt >= started.AddMilliseconds(10));
    }

    [Fact]
    public async Task StopWaitsForResidualProbeBeforeReportingTermination()
    {
        using var flight = new PhoneBatteryProbeSingleFlight<int>();
        var completion = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observing = flight.ObserveAsync(
            _ => completion.Task,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        var stopped = flight.StopAsync();
        await Task.Delay(20);
        Assert.False(stopped.IsCompleted);

        completion.TrySetResult(80);
        var attempt = await observing;
        await stopped.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(PhoneBatteryProbeAttemptStatus.Succeeded, attempt.Status);
        Assert.False(flight.IsInFlight);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition did not become true.");
            }

            await Task.Delay(10);
        }
    }
}
