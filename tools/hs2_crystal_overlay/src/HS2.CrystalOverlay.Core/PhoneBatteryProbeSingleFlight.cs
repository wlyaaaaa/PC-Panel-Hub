namespace HS2.CrystalOverlay.Core;

public enum PhoneBatteryProbeAttemptStatus
{
    Succeeded,
    TimedOut,
    Faulted,
    Canceled,
    Stopped,
}

public sealed record PhoneBatteryProbeAttempt<T>(
    PhoneBatteryProbeAttemptStatus Status,
    T Value,
    DateTimeOffset? CompletedAt = null,
    Exception? Error = null);

/// <summary>
/// Runs at most one probe at a time. A timed-out probe remains owned until its
/// task actually finishes, so an uncooperative provider cannot create a new
/// worker on every polling interval.
/// </summary>
public sealed class PhoneBatteryProbeSingleFlight<T> : IDisposable
{
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private Flight? current;
    private bool stopped;

    public PhoneBatteryProbeSingleFlight(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsInFlight
    {
        get
        {
            lock (sync)
            {
                return current is not null;
            }
        }
    }

    public async Task<PhoneBatteryProbeAttempt<T>> ObserveAsync(
        Func<CancellationToken, Task<T>> start,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Probe timeout must be positive.");
        }

        var flight = GetOrStart(start, cancellationToken);
        if (flight is null)
        {
            return new PhoneBatteryProbeAttempt<T>(
                PhoneBatteryProbeAttemptStatus.Stopped,
                default!);
        }

        if (flight.IsTimedOut && !flight.Completion.IsCompleted)
        {
            return new PhoneBatteryProbeAttempt<T>(
                PhoneBatteryProbeAttemptStatus.TimedOut,
                default!);
        }

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var winner = await Task.WhenAny(
            flight.Completion,
            timeoutTask).ConfigureAwait(false);
        if (winner == flight.Completion || flight.Completion.IsCompleted)
        {
            return await flight.Completion.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (MarkTimedOut(flight))
        {
            flight.Cancel();
        }

        return new PhoneBatteryProbeAttempt<T>(
            PhoneBatteryProbeAttemptStatus.TimedOut,
            default!);
    }

    /// <summary>
    /// Stops accepting new probes and returns only when the in-flight probe has
    /// reached a terminal state. Its linked CTS is disposed by that terminal
    /// continuation, never while provider code may still use it.
    /// </summary>
    public Task StopAsync()
    {
        Flight? flight;
        lock (sync)
        {
            stopped = true;
            flight = current;
        }

        if (flight is null)
        {
            return Task.CompletedTask;
        }

        flight.Cancel();
        return flight.Termination;
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private Flight? GetOrStart(
        Func<CancellationToken, Task<T>> start,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (stopped)
            {
                return null;
            }

            if (current is not null)
            {
                return current;
            }

            var probeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            Task<T> task;
            try
            {
                task = start(probeCancellation.Token) ??
                    Task.FromException<T>(
                        new InvalidOperationException(
                            "Phone battery probe returned no task."));
            }
            catch (Exception exception)
            {
                task = Task.FromException<T>(exception);
            }

            var completion = task.ContinueWith(
                completed => ToAttempt(completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var flight = new Flight(probeCancellation, completion);
            current = flight;
            _ = completion.ContinueWith(
                _ =>
                {
                    try
                    {
                        Release(flight);
                    }
                    finally
                    {
                        flight.MarkTerminated();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return flight;
        }
    }

    private PhoneBatteryProbeAttempt<T> ToAttempt(Task<T> completed)
    {
        var completedAt = timeProvider.GetUtcNow();
        if (completed.IsCanceled)
        {
            return new PhoneBatteryProbeAttempt<T>(
                PhoneBatteryProbeAttemptStatus.Canceled,
                default!,
                completedAt);
        }

        if (completed.IsFaulted)
        {
            return new PhoneBatteryProbeAttempt<T>(
                PhoneBatteryProbeAttemptStatus.Faulted,
                default!,
                completedAt,
                completed.Exception?.GetBaseException());
        }

        return new PhoneBatteryProbeAttempt<T>(
            PhoneBatteryProbeAttemptStatus.Succeeded,
            completed.Result,
            completedAt);
    }

    private bool MarkTimedOut(Flight flight)
    {
        lock (sync)
        {
            if (!ReferenceEquals(current, flight) ||
                flight.Completion.IsCompleted ||
                flight.IsTimedOut)
            {
                return false;
            }

            flight.MarkTimedOut();
            return true;
        }
    }

    private void Release(Flight flight)
    {
        lock (sync)
        {
            if (ReferenceEquals(current, flight))
            {
                current = null;
            }
        }

        flight.DisposeCancellation();
    }

    private sealed class Flight(
        CancellationTokenSource cancellation,
        Task<PhoneBatteryProbeAttempt<T>> completion)
    {
        private readonly TaskCompletionSource termination = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PhoneBatteryProbeAttempt<T>> Completion { get; } =
            completion;

        public Task Termination => termination.Task;

        private int timedOut;

        public bool IsTimedOut => Volatile.Read(ref timedOut) != 0;

        public void MarkTimedOut() => Volatile.Write(ref timedOut, 1);

        public void Cancel()
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The provider completed between status observation and cancel.
            }
        }

        public void DisposeCancellation() => cancellation.Dispose();

        public void MarkTerminated() => termination.TrySetResult();
    }
}
