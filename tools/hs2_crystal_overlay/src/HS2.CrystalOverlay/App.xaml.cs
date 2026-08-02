using HS2.CrystalOverlay.Core;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace HS2_CrystalOverlay;

public partial class App : Application
{
    private const int TargetDisplayAttempts = 30;
    private static readonly TimeSpan TargetDisplayRetryInterval =
        TimeSpan.FromSeconds(2);

    private MainWindow? window;
    private OverlayController? controller;
    private PhoneBatterySourceCoordinator? phoneBattery;
    private MediaSessionSource? media;
    private HardwareAlertSourceCoordinator? hardwareAlerts;
    private GlanceSourceCoordinator? glance;
    private SteamGameSourceCoordinator? steamGames;
    private AudioOperationSourceCoordinator? audioOperations;
    private DeviceNetworkSourceCoordinator? deviceNetwork;
    private PhoneNotificationSourceCoordinator? phoneNotifications;
    private ImportantTaskSourceCoordinator? importantTasks;

    public App()
    {
        InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
        UnhandledException += (_, args) =>
        {
            RuntimeLog.Write($"Unhandled: {args.Exception}");
        };
    }

    protected override async void OnLaunched(
        LaunchActivatedEventArgs args)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var target = await WaitForTargetDisplayAsync();
        if (target is null)
        {
            RuntimeLog.Write(
                "Target display did not appear within 60 seconds; " +
                "refusing to place overlay on the primary display.");
            Exit();
            return;
        }

        RuntimeLog.Write(
            $"Target {target.DeviceName} {target.Width}x{target.Height} at {target.X},{target.Y}.");
        var placement = OverlayLayoutPlanner.Plan(target);
        window = new MainWindow();
        window.Activate();
        window.Configure(placement);
        controller = new OverlayController(
            window,
            new CrystalCardWindow(),
            new DirectOverlayWindow(),
            placement);

        var activationArguments = string.IsNullOrWhiteSpace(args.Arguments)
            ? []
            : args.Arguments.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        var demoArgument = Environment.GetCommandLineArgs()
            .Skip(1)
            .Concat(activationArguments)
            .FirstOrDefault(argument =>
                string.Equals(
                    argument,
                    "--demo",
                    StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith(
                    "--demo=",
                    StringComparison.OrdinalIgnoreCase));
        glance = new GlanceSourceCoordinator(controller);
        if (demoArgument is not null)
        {
            var separator = demoArgument.IndexOf('=');
            var scenario = separator < 0
                ? "full"
                : demoArgument[(separator + 1)..];
            DemoSource.Publish(controller, scenario);
        }
        else
        {
            phoneBattery = new PhoneBatterySourceCoordinator(controller);
            media = new MediaSessionSource(controller);
            hardwareAlerts =
                new HardwareAlertSourceCoordinator(controller);
            steamGames = new SteamGameSourceCoordinator(controller);
            audioOperations =
                new AudioOperationSourceCoordinator(controller);
            deviceNetwork =
                new DeviceNetworkSourceCoordinator(controller);
            phoneNotifications =
                new PhoneNotificationSourceCoordinator(controller);
            importantTasks =
                new ImportantTaskSourceCoordinator(controller);
        }

        if (foreground != 0)
        {
            _ = NativeMethods.SetForegroundWindow(foreground);
        }
    }

    private static async Task<DisplayGeometry?>
        WaitForTargetDisplayAsync()
    {
        for (var attempt = 1;
             attempt <= TargetDisplayAttempts;
             attempt++)
        {
            try
            {
                var target = DisplayTargetSelector.Select(
                    DisplayDiscovery.Enumerate(),
                    @"\\.\DISPLAY20",
                    2288,
                    1048);
                if (target is not null)
                {
                    return target;
                }
            }
            catch (Exception exception)
            {
                RuntimeLog.Write(
                    "Target display probe failed: " +
                    exception.GetType().Name);
            }

            if (attempt == 1)
            {
                RuntimeLog.Write(
                    "Target display is not ready; waiting up to 60 seconds.");
            }

            if (attempt < TargetDisplayAttempts)
            {
                await Task.Delay(TargetDisplayRetryInterval);
            }
        }

        return null;
    }
}

internal static class RuntimeLog
{
    private static readonly object Sync = new();

    internal static void Write(string message)
    {
        try
        {
            var folder = ApplicationData.Current.LocalFolder.Path;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "overlay.log");
            lock (Sync)
            {
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception)
        {
            try
            {
                var folder = Path.Combine(
                    Path.GetTempPath(),
                    "HS2.CrystalOverlay");
                Directory.CreateDirectory(folder);
                File.AppendAllText(
                    Path.Combine(folder, "overlay.log"),
                    $"{DateTimeOffset.Now:O} " +
                    $"Primary log unavailable: {exception.GetType().Name}; " +
                    $"{message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never stop the overlay.
            }
        }
    }
}
