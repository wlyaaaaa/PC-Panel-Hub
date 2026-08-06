namespace HS2.CrystalOverlay.Core;

public readonly record struct GlobalHotkeyDecision(
    bool Suppress,
    bool TriggerClear)
{
    public static GlobalHotkeyDecision PassThrough { get; } =
        new(false, false);
}

public sealed class GlobalHotkeyGestureState
{
    private bool suppressUntilKeyUp;

    public GlobalHotkeyDecision HandleF1(
        bool isKeyDown,
        bool isInjected,
        bool isWinDown,
        bool hasConflictingModifier)
    {
        if (isInjected)
        {
            return GlobalHotkeyDecision.PassThrough;
        }

        if (!isKeyDown)
        {
            if (!suppressUntilKeyUp)
            {
                return GlobalHotkeyDecision.PassThrough;
            }

            suppressUntilKeyUp = false;
            return new GlobalHotkeyDecision(true, false);
        }

        if (suppressUntilKeyUp)
        {
            return new GlobalHotkeyDecision(true, false);
        }

        if (!isWinDown || hasConflictingModifier)
        {
            return GlobalHotkeyDecision.PassThrough;
        }

        suppressUntilKeyUp = true;
        return new GlobalHotkeyDecision(true, true);
    }
}
