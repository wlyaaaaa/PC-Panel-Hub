using HS2.CrystalOverlay.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace HS2_CrystalOverlay;

public sealed partial class MainWindow : Window
{
    private readonly nint hwnd;
    private readonly AppWindow appWindow;

    public MainWindow()
    {
        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;
    }

    internal void Configure(OverlayPlacement placement)
    {
        appWindow.MoveAndResize(new RectInt32(
            placement.FrontRegion.X,
            placement.FrontRegion.Y,
            1,
            1));

        var exStyle = NativeMethods.GetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle).ToInt64();
        exStyle |= NativeMethods.WsExTransparent |
                   NativeMethods.WsExToolWindow |
                   NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            new nint(exStyle));
        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
    }
}
