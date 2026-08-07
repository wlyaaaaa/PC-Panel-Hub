using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal interface IPhoneBatteryProbe
{
    Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed partial class XiaomiHyperConnectBatteryProbe :
    IPhoneBatteryProbe
{
    private const int TailBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ChargingTrendAge =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogBatteryMaximumAge =
        TimeSpan.FromHours(12);
    private static readonly TimeSpan LogConnectionMaximumAge =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LogReadInterval =
        TimeSpan.FromSeconds(10);

    private readonly string logPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "MI",
        "AIoT",
        "Log",
        "smart_share_log.txt");
    private XiaomiDeviceLogState? cachedLogState;
    private DateTimeOffset lastLogRead = DateTimeOffset.MinValue;

    public Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.Run(() => Read(now, cancellationToken), cancellationToken);

    private PhoneBatteryReading? Read(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process
            .GetProcessesByName("MiSmartShare")
            .OrderByDescending(candidate => candidate.StartTime)
            .FirstOrDefault();
        if (process is null)
        {
            throw new InvalidOperationException(
                "Xiaomi source process is unavailable.");
        }

        var currentUiReading =
            TryReadConnectedPercentage(process.Id);
        if (currentUiReading is not null)
        {
            XiaomiBatteryLogSnapshot? chargingTrend = null;
            try
            {
                chargingTrend = TryReadLog(now)?.Battery;
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                // The live UI remains authoritative for percentage and
                // connection. A missing log only makes charging unknown.
            }

            return PhoneBatteryProbeEvidence.CreateXiaomiLiveUiReading(
                currentUiReading.Percentage,
                currentUiReading.ObservedAt,
                chargingTrend,
                now,
                ChargingTrendAge);
        }

        var logState = TryReadLog(now);
        if (logState?.Connection?.IsConnected == false)
        {
            // A recent Xiaomi transport record explicitly reports disconnect.
            return null;
        }

        if (logState?.Connection?.IsConnected != true)
        {
            throw new InvalidDataException(
                "Xiaomi source has no current connection evidence.");
        }

        var logBattery = logState?.Battery;
        if (logBattery is null ||
            !IsWithinAge(
                now,
                logBattery.ObservedAt,
                LogBatteryMaximumAge))
        {
            throw new InvalidDataException(
                "Xiaomi source has no current battery evidence.");
        }

        return new PhoneBatteryReading(
            PhoneBatteryProvider.XiaomiHyperConnect,
            logBattery.Percentage,
            IsWithinAge(
                now,
                logBattery.ObservedAt,
                ChargingTrendAge)
                ? logBattery.IsCharging
                : null,
            true,
            logBattery.ObservedAt,
            "xiaomi-log");
    }

    private static XiaomiUiBatteryReading? TryReadConnectedPercentage(
        int processId)
    {
        try
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(
                    AutomationElement.ProcessIdProperty,
                    processId));
            foreach (AutomationElement window in windows)
            {
                var disconnect = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        "DisconnectButton"));
                if (disconnect is null)
                {
                    continue;
                }

                var textElements = window.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Text));
                foreach (AutomationElement element in textElements)
                {
                    var match = PercentText().Match(element.Current.Name);
                    if (match.Success &&
                        int.TryParse(
                            match.Groups["value"].Value,
                            out var percentage) &&
                        percentage is >= 0 and <= 100)
                    {
                        return new XiaomiUiBatteryReading(
                            percentage,
                            DateTimeOffset.Now);
                    }
                }
            }
        }
        catch (ElementNotAvailableException exception)
        {
            throw new InvalidOperationException(
                "Xiaomi rebuilt its visual tree while it was being sampled.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "Xiaomi visual tree could not be read.",
                exception);
        }

        return null;
    }

    private XiaomiDeviceLogState? TryReadLog(DateTimeOffset now)
    {
        if (now - lastLogRead < LogReadInterval)
        {
            return cachedLogState;
        }

        using var stream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(TailBytes, stream.Length);
        if (length <= 0)
        {
            throw new InvalidDataException(
                "Xiaomi smart-share log is empty.");
        }

        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            throw new IOException("Xiaomi smart-share log could not be read.");
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var battery = XiaomiBatteryLogParser.Parse(
            text,
            now,
            ChargingTrendAge);
        var connection = XiaomiConnectionLogParser.Parse(
            text,
            now,
            LogConnectionMaximumAge);
        cachedLogState = new XiaomiDeviceLogState(
            battery,
            connection);
        lastLogRead = now;
        return cachedLogState;
    }

    private static bool IsWithinAge(
        DateTimeOffset now,
        DateTimeOffset observedAt,
        TimeSpan maximumAge)
    {
        var age = now - observedAt;
        return age >= TimeSpan.FromMinutes(-1) && age <= maximumAge;
    }

    [GeneratedRegex(@"^\s*(?<value>\d{1,3})%\s*$")]
    private static partial Regex PercentText();

    private sealed record XiaomiDeviceLogState(
        XiaomiBatteryLogSnapshot? Battery,
        XiaomiConnectionLogSnapshot? Connection);

    private sealed record XiaomiUiBatteryReading(
        int Percentage,
        DateTimeOffset ObservedAt);
}

internal sealed partial class PhoneLinkBatteryProbe :
    IPhoneBatteryProbe
{
    private const int MaximumCompanionBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan CompanionMaximumAge =
        TimeSpan.FromMinutes(5);

    private readonly string companionPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        "Microsoft.YourPhone_8wekyb3d8bbwe",
        "LocalState",
        "StartMenu",
        "StartMenuCompanion.json");

    public Task<PhoneBatteryReading?> ReadAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.Run(() => Read(now, cancellationToken), cancellationToken);

    private PhoneBatteryReading? Read(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PhoneLinkCompanionReadResult? companion = null;
        Exception? companionFailure = null;
        try
        {
            companion = TryReadCompanion(now);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                FormatException or
                InvalidOperationException)
        {
            companionFailure = exception;
        }

        using var process = Process
            .GetProcessesByName("PhoneExperienceHost")
            .OrderByDescending(candidate => candidate.StartTime)
            .FirstOrDefault();
        if (process is null)
        {
            var resolution = PhoneBatteryProbeEvidence.ResolvePhoneLink(
                liveUi: null,
                companion?.Reading,
                companion?.IsExplicitDisconnect == true);
            if (resolution.Reading is not null)
            {
                return resolution.Reading;
            }

            if (resolution.IsExplicitDisconnect)
            {
                return null;
            }

            if (companionFailure is not null)
            {
                throw new InvalidOperationException(
                    "Phone Link companion source could not be read.",
                    companionFailure);
            }

            throw new InvalidOperationException(
                "Phone Link source process is unavailable.");
        }

        try
        {
            var windows = AutomationElement.RootElement.FindAll(
                TreeScope.Children,
                new PropertyCondition(
                    AutomationElement.ProcessIdProperty,
                    process.Id));
            foreach (AutomationElement window in windows)
            {
                var textElements = window.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Text));
                int? percentage = null;
                var charging = false;
                foreach (AutomationElement element in textElements)
                {
                    var name = element.Current.Name;
                    var match = PercentAnywhere().Match(name);
                    if (match.Success &&
                        int.TryParse(
                            match.Groups["value"].Value,
                            out var value) &&
                        value is >= 0 and <= 100)
                    {
                        percentage = value;
                    }

                    charging |= ChargingText().IsMatch(name);
                }

                if (percentage is not null)
                {
                    return new PhoneBatteryReading(
                        PhoneBatteryProvider.PhoneLink,
                        percentage.Value,
                        charging ? true : null,
                        true,
                        DateTimeOffset.Now,
                        "phone-link-ui");
                }
            }
        }
        catch (ElementNotAvailableException exception)
        {
            var resolution = PhoneBatteryProbeEvidence.ResolvePhoneLink(
                liveUi: null,
                companion?.Reading,
                companion?.IsExplicitDisconnect == true);
            if (resolution.Reading is not null)
            {
                return resolution.Reading;
            }

            if (resolution.IsExplicitDisconnect)
            {
                return null;
            }

            throw new InvalidOperationException(
                "Phone Link rebuilt its visual tree while it was being sampled.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            var resolution = PhoneBatteryProbeEvidence.ResolvePhoneLink(
                liveUi: null,
                companion?.Reading,
                companion?.IsExplicitDisconnect == true);
            if (resolution.Reading is not null)
            {
                return resolution.Reading;
            }

            if (resolution.IsExplicitDisconnect)
            {
                return null;
            }

            throw new InvalidOperationException(
                "Phone Link visual tree could not be read.",
                exception);
        }

        var finalResolution = PhoneBatteryProbeEvidence.ResolvePhoneLink(
            liveUi: null,
            companion?.Reading,
            companion?.IsExplicitDisconnect == true);
        if (finalResolution.Reading is not null)
        {
            return finalResolution.Reading;
        }

        if (finalResolution.IsExplicitDisconnect)
        {
            return null;
        }

        if (companionFailure is not null)
        {
            throw new InvalidOperationException(
                "Phone Link companion source could not be read.",
                companionFailure);
        }

        throw new InvalidDataException(
            "Phone Link source has no current battery evidence.");
    }

    private PhoneLinkCompanionReadResult? TryReadCompanion(
        DateTimeOffset now)
    {
        try
        {
            _ = File.GetAttributes(companionPath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        var info = new FileInfo(companionPath);
        if (info.Length <= 0)
        {
            throw new InvalidDataException(
                "Phone Link companion file is empty.");
        }

        if (info.Length > MaximumCompanionBytes)
        {
            throw new InvalidDataException(
                "Phone Link companion file exceeds the size limit.");
        }

        var observedAt = new DateTimeOffset(
            info.LastWriteTimeUtc,
            TimeSpan.Zero);
        var age = now.ToUniversalTime() - observedAt;
        if (age < TimeSpan.FromMinutes(-1) ||
            age > CompanionMaximumAge)
        {
            throw new InvalidOperationException(
                "Phone Link companion file is stale.");
        }

        using var stream = new FileStream(
            companionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var snapshot = PhoneLinkCompanionParser.Parse(reader.ReadToEnd());
        if (snapshot is null)
        {
            throw new FormatException(
                "Phone Link companion file has no valid battery payload.");
        }

        if (snapshot.IsConnected == false)
        {
            return new PhoneLinkCompanionReadResult(
                Reading: null,
                IsExplicitDisconnect: true);
        }

        if (snapshot.IsConnected != true)
        {
            throw new InvalidDataException(
                "Phone Link companion connection state is unknown.");
        }

        return new PhoneLinkCompanionReadResult(
            new PhoneBatteryReading(
                PhoneBatteryProvider.PhoneLink,
                snapshot.Percentage,
                snapshot.IsCharging,
                true,
                observedAt,
                "phone-link-companion"),
            IsExplicitDisconnect: false);
    }

    [GeneratedRegex(@"\b(?<value>\d{1,3})%\b")]
    private static partial Regex PercentAnywhere();

    [GeneratedRegex(@"charging|充电", RegexOptions.IgnoreCase)]
    private static partial Regex ChargingText();

    private sealed record PhoneLinkCompanionReadResult(
        PhoneBatteryReading? Reading,
        bool IsExplicitDisconnect);
}

internal sealed class PhoneBatterySourceCoordinator : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SourceTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumReadingAge =
        TimeSpan.FromSeconds(15);

    private readonly IOverlayPublisher publisher;
    private readonly LifetimePublicationGate publicationGate = new();
    private readonly IPhoneBatteryProbe xiaomi;
    private readonly IPhoneBatteryProbe phoneLink;
    private readonly PhoneBatteryProbeSingleFlight<
        PhoneBatteryReading?> xiaomiFlight = new();
    private readonly PhoneBatteryProbeSingleFlight<
        PhoneBatteryReading?> phoneLinkFlight = new();
    private readonly PhoneBatteryProbeReadingCache xiaomiReadingCache =
        new();
    private readonly PhoneBatteryProbeReadingCache phoneLinkReadingCache =
        new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly PeriodicTimer timer;
    private readonly Task loop;
    private PhoneBatteryReading? published;
    private string? lastSourceState;
    private bool baselineCaptured;

    internal PhoneBatterySourceCoordinator(IOverlayPublisher publisher)
        : this(
            publisher,
            new XiaomiHyperConnectBatteryProbe(),
            new PhoneLinkBatteryProbe())
    {
    }

    internal PhoneBatterySourceCoordinator(
        IOverlayPublisher publisher,
        IPhoneBatteryProbe xiaomi,
        IPhoneBatteryProbe phoneLink)
    {
        this.publisher = publisher;
        this.xiaomi = xiaomi;
        this.phoneLink = phoneLink;
        timer = new PeriodicTimer(PollInterval);
        loop = Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        try
        {
            await PollOnceAsync();
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                await PollOnceAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RuntimeLog.Write(
                $"Phone battery poll loop failed: " +
                $"{exception.GetType().Name}");
        }
    }

    private async Task PollOnceAsync()
    {
        if (publicationGate.IsClosed)
        {
            return;
        }

        var token = cancellation.Token;
        // Start both probes before observing either one. Each source owns its
        // own timeout and single-flight, so a stuck Xiaomi/Phone Link call
        // cannot block a healthy fallback forever or spawn one worker per tick.
        var xiaomiAttemptTask = xiaomiFlight.ObserveAsync(
            sourceToken => xiaomi.ReadAsync(
                DateTimeOffset.Now,
                sourceToken),
            SourceTimeout,
            token);
        var phoneLinkAttemptTask = phoneLinkFlight.ObserveAsync(
            sourceToken => phoneLink.ReadAsync(
                DateTimeOffset.Now,
                sourceToken),
            SourceTimeout,
            token);

        var xiaomiAttempt = await xiaomiAttemptTask;
        var phoneLinkAttempt = await phoneLinkAttemptTask;
        if (publicationGate.IsClosed)
        {
            return;
        }

        // The cache preserves each source's own evidence timestamp. It retains
        // a recent confirmed value through timeout/fault jitter and only clears
        // it when a completed successful probe explicitly returns no phone.
        var xiaomiReading = xiaomiReadingCache.Apply(xiaomiAttempt);
        var phoneLinkReading = phoneLinkReadingCache.Apply(phoneLinkAttempt);
        var now = DateTimeOffset.Now;
        var selected = PhoneBatteryArbitration.Select(
            xiaomiReading,
            phoneLinkReading,
            now,
            MaximumReadingAge);
        LogSourceState(
            xiaomiAttempt,
            phoneLinkAttempt,
            xiaomiReading,
            phoneLinkReading,
            selected);
        PublishSelection(selected);
    }

    private void LogSourceState(
        PhoneBatteryProbeAttempt<PhoneBatteryReading?> xiaomiAttempt,
        PhoneBatteryProbeAttempt<PhoneBatteryReading?> phoneLinkAttempt,
        PhoneBatteryReading? xiaomiReading,
        PhoneBatteryReading? phoneLinkReading,
        PhoneBatteryReading? selected)
    {
        var state =
            $"xiaomi={DescribeAttempt(xiaomiAttempt, xiaomiReading)}; " +
            $"phone-link={DescribeAttempt(phoneLinkAttempt, phoneLinkReading)}; " +
            $"selected={selected?.Provider.ToString() ?? "none"}";
        if (string.Equals(
                state,
                lastSourceState,
                StringComparison.Ordinal))
        {
            return;
        }

        lastSourceState = state;
        RuntimeLog.Write($"Phone battery sources: {state}.");
    }

    private static string DescribeAttempt(
        PhoneBatteryProbeAttempt<PhoneBatteryReading?> attempt,
        PhoneBatteryReading? reading) => attempt.Status switch
    {
        PhoneBatteryProbeAttemptStatus.Succeeded =>
            DescribeReading(reading),
        PhoneBatteryProbeAttemptStatus.TimedOut => "timeout",
        PhoneBatteryProbeAttemptStatus.Faulted =>
            $"failed:{attempt.Error?.GetType().Name ?? "unknown"}",
        PhoneBatteryProbeAttemptStatus.Canceled => "canceled",
        PhoneBatteryProbeAttemptStatus.Stopped => "stopped",
        _ => "unknown",
    };

    private static string DescribeReading(PhoneBatteryReading? reading) =>
        reading is null
            ? "none"
            : $"{reading.Percentage}%/" +
              $"{reading.Evidence ?? "unknown"}/" +
              $"charging={reading.IsCharging?.ToString() ?? "unknown"}";

    private void PublishSelection(PhoneBatteryReading? selected)
    {
        if (selected is null)
        {
            if (published is not null)
            {
                var previous = published;
                _ = PublishIfActive(OverlayRequest.End(
                    "phone-battery",
                    OverlayKind.PhoneBattery,
                    SourceFor(previous.Provider)));
                if (baselineCaptured)
                {
                    PublishConnection(previous.Provider, connected: false);
                }

                published = null;
            }

            baselineCaptured = true;
            return;
        }

        if (published is not null &&
            published.Provider == selected.Provider &&
            published.Percentage == selected.Percentage &&
            published.IsCharging == selected.IsCharging)
        {
            baselineCaptured = true;
            return;
        }

        if (published is null && baselineCaptured)
        {
            PublishConnection(selected.Provider, connected: true);
        }

        _ = PublishIfActive(OverlayRequest.Active(
            "phone-battery",
            OverlayKind.PhoneBattery,
            SourceFor(selected.Provider),
            $"{selected.Percentage}%",
            visual: new OverlayVisualData(
                IsCharging: selected.IsCharging)));
        published = selected;
        baselineCaptured = true;
    }

    private void PublishConnection(
        PhoneBatteryProvider provider,
        bool connected)
    {
        var sourceName = provider switch
        {
            PhoneBatteryProvider.XiaomiHyperConnect => "小米妙享",
            PhoneBatteryProvider.PhoneLink => "手机连接",
            _ => "手机",
        };
        _ = PublishIfActive(OverlayRequest.Timed(
            connected ? "phone-connected" : "phone-disconnected",
            OverlayKind.PhoneConnection,
            SourceFor(provider),
            connected ? "手机已连接" : "手机已断开",
            sourceName,
            dedupKey:
                $"{provider}:{(connected ? "connected" : "disconnected")}",
            visual: new OverlayVisualData(
                Eyebrow: connected
                    ? "连接成功 / CONNECTED"
                    : "连接断开 / DISCONNECTED",
                AccentHex: connected ? "#8EF2C8" : "#FFD08A")));
    }

    private bool PublishIfActive(OverlayRequest request) =>
        publicationGate.TryPublish(() => publisher.Publish(request));

    private static OverlaySource SourceFor(
        PhoneBatteryProvider provider) => provider switch
        {
            PhoneBatteryProvider.XiaomiHyperConnect =>
                OverlaySource.XiaomiHyperConnect,
            PhoneBatteryProvider.PhoneLink => OverlaySource.PhoneLink,
            _ => OverlaySource.System,
        };

    public void Dispose()
    {
        if (!publicationGate.Close())
        {
            return;
        }

        cancellation.Cancel();
        timer.Dispose();
        var termination = WaitForTerminationAsync(
            xiaomiFlight.StopAsync(),
            phoneLinkFlight.StopAsync());
        var completed = false;
        try
        {
            completed = termination.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        if (completed)
        {
            cancellation.Dispose();
            return;
        }

        _ = termination.ContinueWith(
            completedTermination =>
            {
                _ = completedTermination.Exception;
                cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForTerminationAsync(
        Task xiaomiStop,
        Task phoneLinkStop)
    {
        try
        {
            await loop;
        }
        catch
        {
        }

        try
        {
            await xiaomiStop;
        }
        catch
        {
        }

        try
        {
            await phoneLinkStop;
        }
        catch
        {
        }
    }
}
