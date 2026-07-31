using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HS2.CrystalOverlay.Core;

namespace HS2.NeteasePlaybackBridge;

internal static class Program
{
    internal const string PipeName =
        "HS2.NeteasePlaybackBridge";

    private static readonly NeteasePlaybackMemoryReader Playback = new();
    private static IReadOnlySet<int> cachedProcessIds =
        new HashSet<int>();
    private static DateTimeOffset processIdsReadAt =
        DateTimeOffset.MinValue;

    private static async Task Main()
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            @"Global\HS2.NeteasePlaybackBridge",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        while (true)
        {
            try
            {
                await ServeOneClientAsync();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static async Task ServeOneClientAsync()
    {
        await using var pipe = CreateServerPipe();
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        while (pipe.IsConnected)
        {
            var request = await reader.ReadLineAsync();
            if (request is null)
            {
                return;
            }

            if (!string.Equals(
                    request,
                    "read",
                    StringComparison.Ordinal))
            {
                await writer.WriteLineAsync(
                    NeteasePlaybackBridgeProtocol.Serialize(
                        null,
                        DateTimeOffset.UtcNow));
                continue;
            }

            var processIds = ReadCloudMusicProcessIds();
            var sample = processIds.Count == 0
                ? null
                : Playback.Read(processIds);
            await writer.WriteLineAsync(
                NeteasePlaybackBridgeProtocol.Serialize(
                    sample,
                    DateTimeOffset.UtcNow));
        }
    }

    private static NamedPipeServerStream CreateServerPipe()
    {
        var userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The bridge requires an interactive Windows user.");
        var security = new PipeSecurity();
        security.SetOwner(userSid);
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security,
            HandleInheritability.None);
    }

    private static IReadOnlySet<int> ReadCloudMusicProcessIds()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - processIdsReadAt < TimeSpan.FromSeconds(2))
        {
            return cachedProcessIds;
        }

        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("cloudmusic"))
        {
            using (process)
            {
                processIds.Add(process.Id);
            }
        }

        cachedProcessIds = processIds;
        processIdsReadAt = now;
        return cachedProcessIds;
    }
}
