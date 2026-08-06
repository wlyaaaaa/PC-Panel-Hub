using System.ComponentModel;
using System.Runtime.InteropServices;
using HS2.CrystalOverlay.Core;
using Microsoft.UI.Dispatching;

namespace HS2_CrystalOverlay;

internal sealed class GlobalHotkeyCoordinator :
    System.Windows.Forms.NativeWindow,
    IDisposable
{
    private const int ClearHotkeyId = 0x4851;
    private const int GlanceHotkeyId = 0x4852;
    private const int WmHotkey = 0x0312;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyF1 = 0x70;
    private const uint KeyF2 = 0x71;
    private const int KeyLeftWin = 0x5B;
    private const int KeyRightWin = 0x5C;
    private const int KeyControl = 0x11;
    private const int KeyMenu = 0x12;

    private readonly DispatcherQueue dispatcher;
    private readonly Action clearDismissible;
    private readonly Action toggleGlance;
    private readonly GlobalHotkeyGestureState fallbackState = new();
    private readonly NativeMethods.LowLevelKeyboardProc hookCallback;
    private bool clearRegistered;
    private bool glanceRegistered;
    private nint keyboardHook;
    private bool disposed;

    internal GlobalHotkeyCoordinator(
        DispatcherQueue dispatcher,
        Action clearDismissible,
        Action toggleGlance)
    {
        this.dispatcher = dispatcher;
        this.clearDismissible = clearDismissible;
        this.toggleGlance = toggleGlance;
        hookCallback = OnLowLevelKeyboard;

        CreateHandle(new System.Windows.Forms.CreateParams
        {
            Caption = "HS2 crystal overlay hotkeys",
        });

        clearRegistered = NativeMethods.RegisterHotKey(
            Handle,
            ClearHotkeyId,
            ModWin | ModNoRepeat,
            KeyF1);
        if (clearRegistered)
        {
            RuntimeLog.Write("Global hotkey Win+F1 registered normally.");
        }
        else
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorHotkeyAlreadyRegistered)
            {
                keyboardHook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WhKeyboardLl,
                    hookCallback,
                    NativeMethods.GetModuleHandle(null),
                    0);
                if (keyboardHook == 0)
                {
                    RuntimeLog.Write(
                        "Win+F1 fallback hook failed: " +
                        new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
                else
                {
                    RuntimeLog.Write(
                        "Win+F1 occupied (1409); narrow fallback hook active.");
                }
            }
            else
            {
                RuntimeLog.Write(
                    $"Global hotkey Win+F1 unavailable: {error}.");
            }
        }

        glanceRegistered = NativeMethods.RegisterHotKey(
            Handle,
            GlanceHotkeyId,
            ModWin | ModNoRepeat,
            KeyF2);
        RuntimeLog.Write(
            glanceRegistered
                ? "Global hotkey Win+F2 registered."
                : $"Global hotkey Win+F2 unavailable: {Marshal.GetLastWin32Error()}.");
    }

    protected override void WndProc(
        ref System.Windows.Forms.Message message)
    {
        if (message.Msg == WmHotkey)
        {
            if (message.WParam.ToInt32() == ClearHotkeyId)
            {
                clearDismissible();
                return;
            }

            if (message.WParam.ToInt32() == GlanceHotkeyId)
            {
                toggleGlance();
                return;
            }
        }

        base.WndProc(ref message);
    }

    private nint OnLowLevelKeyboard(
        int code,
        nuint message,
        nint keyboardData)
    {
        if (code < 0 || disposed)
        {
            return NativeMethods.CallNextHookEx(
                keyboardHook,
                code,
                message,
                keyboardData);
        }

        if (message is not NativeMethods.WmKeyDown and
            not NativeMethods.WmSysKeyDown and
            not NativeMethods.WmKeyUp and
            not NativeMethods.WmSysKeyUp)
        {
            return NativeMethods.CallNextHookEx(
                keyboardHook,
                code,
                message,
                keyboardData);
        }

        var input = Marshal.PtrToStructure<
            NativeMethods.LowLevelKeyboardInput>(keyboardData);
        if (input.VirtualKey != KeyF1)
        {
            return NativeMethods.CallNextHookEx(
                keyboardHook,
                code,
                message,
                keyboardData);
        }

        var injected =
            (input.Flags & (
                NativeMethods.LlkhfInjected |
                NativeMethods.LlkhfLowerIlInjected)) != 0;
        var isKeyDown = message is NativeMethods.WmKeyDown or
            NativeMethods.WmSysKeyDown;
        var winDown = IsDown(KeyLeftWin) || IsDown(KeyRightWin);
        var conflictingModifier =
            IsDown(KeyControl) || IsDown(KeyMenu);
        var decision = fallbackState.HandleF1(
            isKeyDown,
            injected,
            winDown,
            conflictingModifier);

        if (decision.TriggerClear)
        {
            _ = dispatcher.TryEnqueue(() => clearDismissible());
        }

        return decision.Suppress
            ? new nint(1)
            : NativeMethods.CallNextHookEx(
                keyboardHook,
                code,
                message,
                keyboardData);
    }

    private static bool IsDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (clearRegistered)
        {
            _ = NativeMethods.UnregisterHotKey(Handle, ClearHotkeyId);
        }

        if (glanceRegistered)
        {
            _ = NativeMethods.UnregisterHotKey(Handle, GlanceHotkeyId);
        }

        if (keyboardHook != 0)
        {
            _ = NativeMethods.UnhookWindowsHookEx(keyboardHook);
            keyboardHook = 0;
        }

        DestroyHandle();
    }
}
