using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HS2.CrystalOverlay.Core;

namespace HS2.CrystalOverlay.Core;

public sealed record NeteasePlaybackMemorySample(
    TimeSpan Position,
    int ProcessId);

public sealed class NeteasePlaybackMemoryReader
{
    private const string SupportedCloudMusicDllSha256 =
        "B64D67CE73AE15EEFFAC8F9CA344FF13E408C82F03F68368573BFF710AF31309";
    private const long PlaybackEngineRva = 0x1DE0F90;
    private const int CurrentPlayerOffset = 0x30;
    private const int AudioFormatOffset = 0xC0;

    private string? fingerprintPath;
    private long fingerprintLength;
    private DateTime fingerprintWriteTimeUtc;
    private bool fingerprintSupported;
    private int preferredProcessId;
    private long preferredEngineAddress;

    public NeteasePlaybackMemorySample? Read(
        IReadOnlySet<int> processIds)
    {
        if (IntPtr.Size != sizeof(long))
        {
            return null;
        }

        if (preferredProcessId != 0 &&
            preferredEngineAddress != 0 &&
            processIds.Contains(preferredProcessId) &&
            TryReadPosition(
                preferredProcessId,
                preferredEngineAddress,
                out var preferredPosition))
        {
            return new NeteasePlaybackMemorySample(
                preferredPosition,
                preferredProcessId);
        }

        if (preferredProcessId != 0 &&
            !processIds.Contains(preferredProcessId))
        {
            preferredProcessId = 0;
            preferredEngineAddress = 0;
        }

        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var module = process.Modules
                    .Cast<ProcessModule>()
                    .FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.ModuleName,
                            "cloudmusic.dll",
                            StringComparison.OrdinalIgnoreCase));
                if (module is null ||
                    !IsSupportedBuild(module.FileName))
                {
                    continue;
                }

                var engine = checked(
                    module.BaseAddress.ToInt64() +
                    PlaybackEngineRva);
                if (!TryReadPosition(
                        processId,
                        engine,
                        out var position))
                {
                    continue;
                }

                preferredProcessId = processId;
                preferredEngineAddress = engine;
                return new NeteasePlaybackMemorySample(
                    position,
                    processId);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (CryptographicException)
            {
            }
        }

        return null;
    }

    private static bool TryReadPosition(
        int processId,
        long engine,
        out TimeSpan position)
    {
        position = default;
        var handle = OpenProcess(
            ProcessVmRead | ProcessQueryLimitedInformation,
            false,
            processId);
        if (handle == 0)
        {
            return false;
        }

        try
        {
            if (!TryReadInt64(
                    handle,
                    engine + CurrentPlayerOffset,
                    out var player) ||
                !IsPlausiblePointer(player))
            {
                return false;
            }

            var snapshot = new byte[
                NeteasePlaybackPositionDecoder.SnapshotSize];
            return TryRead(
                       handle,
                       player + AudioFormatOffset,
                       snapshot) &&
                   TryReadInt64(
                       handle,
                       engine + CurrentPlayerOffset,
                       out var confirmedPlayer) &&
                   confirmedPlayer == player &&
                   NeteasePlaybackPositionDecoder.TryDecode(
                       snapshot,
                       out position);
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private bool IsSupportedBuild(string path)
    {
        var file = new FileInfo(path);
        if (string.Equals(
                fingerprintPath,
                file.FullName,
                StringComparison.OrdinalIgnoreCase) &&
            fingerprintLength == file.Length &&
            fingerprintWriteTimeUtc == file.LastWriteTimeUtc)
        {
            return fingerprintSupported;
        }

        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(stream));
        fingerprintPath = file.FullName;
        fingerprintLength = file.Length;
        fingerprintWriteTimeUtc = file.LastWriteTimeUtc;
        fingerprintSupported = string.Equals(
            fingerprint,
            SupportedCloudMusicDllSha256,
            StringComparison.Ordinal);
        return fingerprintSupported;
    }

    private static bool TryReadInt64(
        nint process,
        long address,
        out long value)
    {
        var bytes = new byte[sizeof(long)];
        if (!TryRead(process, address, bytes))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToInt64(bytes);
        return true;
    }

    private static bool TryRead(
        nint process,
        long address,
        byte[] destination) =>
        ReadProcessMemory(
            process,
            (nint)address,
            destination,
            (nuint)destination.Length,
            out var bytesRead) &&
        bytesRead == (nuint)destination.Length;

    private static bool IsPlausiblePointer(long value) =>
        value is > 0x10_000 and < 0x0000_8000_0000_0000;

    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        nint process,
        nint baseAddress,
        [Out] byte[] buffer,
        nuint size,
        out nuint bytesRead);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
