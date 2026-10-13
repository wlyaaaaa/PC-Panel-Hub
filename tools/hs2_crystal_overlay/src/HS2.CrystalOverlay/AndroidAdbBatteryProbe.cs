using System.Diagnostics;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class AndroidAdbBatteryProbe(string adbPath, string serial) :
    IPhoneBatteryProbe
{
    internal static AndroidAdbBatteryProbe? FromUserEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(
            "PC_PANEL_ADB_PATH", EnvironmentVariableTarget.User)?.Trim();
        var target = Environment.GetEnvironmentVariable(
            "PC_PANEL_PHONE_SERIAL", EnvironmentVariableTarget.User)?.Trim();
        return !string.IsNullOrWhiteSpace(path) &&
               Path.IsPathFullyQualified(path) && File.Exists(path) &&
               !string.IsNullOrWhiteSpace(target) &&
               !target.Any(char.IsWhiteSpace)
            ? new AndroidAdbBatteryProbe(path, target)
            : null;
    }

    public async Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var devices = await RunAsync(["devices"], cancellationToken);
        var transport = AndroidAdbBatteryParser.SelectTransport(devices, serial);
        if (transport is null)
            return null;
        var battery = await RunAsync(
            ["-s", transport, "shell", "dumpsys", "battery"], cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return AndroidAdbBatteryParser.Parse(battery, DateTimeOffset.Now);
    }

    private async Task<string> RunAsync(
        string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(adbPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("Could not start adb client.");
        // Terminate only this client; a paired adb server must stay running.
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdout, stderr,
            process.WaitForExitAsync(cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"adb client exited {process.ExitCode}.");
        return await stdout;
    }
}
