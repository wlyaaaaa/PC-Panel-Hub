using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Tests;

public sealed class GlobalHotkeyGestureStateTests
{
    [Fact]
    public void PhysicalWinF1_TriggersOnceAndSuppressesThroughKeyUp()
    {
        var state = new GlobalHotkeyGestureState();

        Assert.Equal(
            new GlobalHotkeyDecision(true, true),
            state.HandleF1(
                isKeyDown: true,
                isInjected: false,
                isWinDown: true,
                hasConflictingModifier: false));
        Assert.Equal(
            new GlobalHotkeyDecision(true, false),
            state.HandleF1(
                isKeyDown: true,
                isInjected: false,
                isWinDown: true,
                hasConflictingModifier: false));
        Assert.Equal(
            new GlobalHotkeyDecision(true, false),
            state.HandleF1(
                isKeyDown: false,
                isInjected: false,
                isWinDown: false,
                hasConflictingModifier: false));
    }

    [Fact]
    public void NewPhysicalPress_AfterKeyUpTriggersAgain()
    {
        var state = new GlobalHotkeyGestureState();

        _ = state.HandleF1(true, false, true, false);
        _ = state.HandleF1(false, false, false, false);

        Assert.Equal(
            new GlobalHotkeyDecision(true, true),
            state.HandleF1(true, false, true, false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void NonMatchingF1_IsPassedThrough(
        bool isWinDown,
        bool isInjected,
        bool hasConflictingModifier)
    {
        var state = new GlobalHotkeyGestureState();

        Assert.Equal(
            GlobalHotkeyDecision.PassThrough,
            state.HandleF1(
                isKeyDown: true,
                isInjected,
                isWinDown,
                hasConflictingModifier));
        Assert.Equal(
            GlobalHotkeyDecision.PassThrough,
            state.HandleF1(
                isKeyDown: false,
                isInjected,
                isWinDown,
                hasConflictingModifier));
    }

    [Fact]
    public void InjectedKeyUp_DoesNotTerminateAnActivePhysicalChord()
    {
        var state = new GlobalHotkeyGestureState();

        _ = state.HandleF1(true, false, true, false);

        Assert.Equal(
            GlobalHotkeyDecision.PassThrough,
            state.HandleF1(false, true, false, false));
        Assert.Equal(
            new GlobalHotkeyDecision(true, false),
            state.HandleF1(false, false, false, false));
    }
}
