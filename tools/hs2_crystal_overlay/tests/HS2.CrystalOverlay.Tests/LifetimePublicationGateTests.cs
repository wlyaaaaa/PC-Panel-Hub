using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class LifetimePublicationGateTests
{
    [Fact]
    public async Task CloseWaitsForInFlightPublishAndRejectsAllLaterPublishes()
    {
        var gate = new LifetimePublicationGate();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = Task.Run(() => gate.TryPublish(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return true;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var close = Task.Run(gate.Close);
        await Task.Delay(30);
        Assert.False(close.IsCompleted);

        release.TrySetResult();
        Assert.True(await publish);
        Assert.True(await close);
        var invokedAfterClose = false;
        Assert.False(gate.TryPublish(() =>
        {
            invokedAfterClose = true;
            return true;
        }));
        Assert.False(invokedAfterClose);
    }
}
