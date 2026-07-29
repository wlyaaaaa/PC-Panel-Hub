using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace TURZX.SideScreen
{
    public static class SideScreenStreamApp
    {
        private const string DefaultMetricsUrl = "http://127.0.0.1:18765/snapshot";
        private const int DefaultHttpTimeoutMs = 450;
        private const int DefaultPreviewIntervalSeconds = 45;

        public static int Main(string[] args)
        {
            StreamOptions options;
            try
            {
                options = StreamOptions.Parse(args);
                if (options.ShowHelp)
                {
                    PrintUsage();
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                PrintUsage();
                return 2;
            }

            int frame = 0;
            int sent = 0;
            int failed = 0;
            Stopwatch total = Stopwatch.StartNew();
            TurzxHelperSender.DiffSession diffSession = null;
            byte[] previousFrameData = null;
            Snapshot lastSnapshot = null;
            long diffSequence = 0;
            long previousFrameStartTicks = -1;
            int consecutiveSendFailures = 0;
            int lastFullFrame = 0;
            long lastPreviewTicks = -1;

            Console.WriteLine("TURZX stream starting: frames=" + (options.FrameCount == 0 ? "infinite" : options.FrameCount.ToString()) +
                ", intervalMs=" + options.IntervalMs +
                ", previewIntervalSeconds=" + options.PreviewIntervalSeconds +
                ", dryRun=" + options.DryRun +
                ", diff=" + options.UseDiff +
                ", altHelper=" + options.AltHelper +
                ", port=" + options.Port);

            try
            {
                if (options.UseDiff && !options.DryRun)
                {
                    diffSession = new TurzxHelperSender.DiffSession(options.Root, options.Port, options.DevCode);
                }

                while (options.FrameCount == 0 || frame < options.FrameCount)
                {
                    frame++;
                    long frameStartTicks = Stopwatch.GetTimestamp();
                    long periodMs = previousFrameStartTicks < 0
                        ? 0
                        : TicksToMilliseconds(frameStartTicks - previousFrameStartTicks, Stopwatch.Frequency);
                    previousFrameStartTicks = frameStartTicks;
                    Stopwatch frameWatch = Stopwatch.StartNew();
                    string snapshotStatus = "none";
                    long fetchMs = 0;
                    long renderMs = 0;
                    bool sendAttempted = false;
                    string frameStatus = "ok";
                    string frameError = null;
                    try
                    {
                        Stopwatch fetchWatch = Stopwatch.StartNew();
                        Snapshot snapshot = FetchFrameSnapshot(options, frame, ref lastSnapshot, out snapshotStatus);
                        fetchWatch.Stop();
                        fetchMs = fetchWatch.ElapsedMilliseconds;
                        ApplyStreamInterval(snapshot, options.IntervalMs);
                        Stopwatch renderWatch = Stopwatch.StartNew();
                        using (Bitmap bitmap = SideScreenRenderer.Render(snapshot))
                        {
                            renderWatch.Stop();
                            renderMs = renderWatch.ElapsedMilliseconds;
                            long previewTicks = Stopwatch.GetTimestamp();
                            if (!string.IsNullOrWhiteSpace(options.PreviewDir) &&
                                ShouldWritePreview(lastPreviewTicks, previewTicks, Stopwatch.Frequency, options.PreviewIntervalSeconds))
                            {
                                // Preview is diagnostic only. A transient file lock or storage error
                                // must never suppress the primary COM frame send.
                                lastPreviewTicks = previewTicks;
                                try
                                {
                                    Directory.CreateDirectory(options.PreviewDir);
                                    string preview = Path.Combine(options.PreviewDir, "stream-last.png");
                                    SavePreviewAtomically(bitmap, preview);
                                }
                                catch (Exception previewError)
                                {
                                    Console.WriteLine("preview warning: " + DescribeException(previewError));
                                }
                            }

                            if (options.DryRun)
                            {
                                sent++;
                                consecutiveSendFailures = 0;
                                Console.WriteLine("frame " + frame + " rendered in " + frameWatch.ElapsedMilliseconds + "ms");
                            }
                            else if (options.UseDiff)
                            {
                                sendAttempted = true;
                                Stopwatch sendWatch = Stopwatch.StartNew();
                                byte[] currentFrameData = diffSession.Convert(bitmap);
                                if (ShouldSendFullFrame(frame, previousFrameData != null, options.FullResyncEveryFrames))
                                {
                                    if (previousFrameData != null)
                                    {
                                        diffSession.Dispose();
                                        diffSession = new TurzxHelperSender.DiffSession(options.Root, options.Port, options.DevCode);
                                    }
                                    diffSession.SendFull(currentFrameData);
                                    sendWatch.Stop();
                                    sent++;
                                    consecutiveSendFailures = 0;
                                    diffSequence = 0;
                                    lastFullFrame = frame;
                                    Console.WriteLine("frame " + frame + " FULL sent in " + sendWatch.ElapsedMilliseconds + "ms: frameBytes=" + currentFrameData.Length);
                                }
                                else
                                {
                                    long sequence = diffSequence++;
                                    int result = diffSession.SendDiff(previousFrameData, currentFrameData, sequence, false, false, options.AltHelper);
                                    sendWatch.Stop();
                                    sent++;
                                    consecutiveSendFailures = 0;
                                    Console.WriteLine("frame " + frame + " DIFF sent in " + sendWatch.ElapsedMilliseconds + "ms: seq=" + sequence + ", result=" + result);
                                }

                                previousFrameData = currentFrameData;
                            }
                            else
                            {
                                sendAttempted = true;
                                string message;
                                Stopwatch sendWatch = Stopwatch.StartNew();
                                bool ok = TurzxHelperSender.SendBitmap(options.Root, options.Port, bitmap, options.DevCode, options.SendTimeoutMs, out message);
                                sendWatch.Stop();
                                if (!ok)
                                {
                                    failed++;
                                    consecutiveSendFailures++;
                                    frameStatus = "send_failed";
                                    frameError = message;
                                    Console.WriteLine("frame " + frame + " send failed after " + sendWatch.ElapsedMilliseconds + "ms: " + message);
                                    if (ShouldAbortAfterConsecutiveSendFailures(consecutiveSendFailures, options.MaxConsecutiveSendFailures))
                                    {
                                        frameWatch.Stop();
                                        WriteHeartbeat(options, frame, sent, failed, consecutiveSendFailures, lastFullFrame, periodMs, fetchMs, renderMs, frameWatch.ElapsedMilliseconds, 0, snapshotStatus, "fatal", message);
                                        Console.Error.WriteLine("stream fatal: consecutive send failures reached " + consecutiveSendFailures + "; exiting so watchdog can reopen " + options.Port);
                                        return 1;
                                    }
                                }
                                else
                                {
                                    sent++;
                                    consecutiveSendFailures = 0;
                                    Console.WriteLine("frame " + frame + " sent in " + sendWatch.ElapsedMilliseconds + "ms: " + message);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        frameStatus = "exception";
                        frameError = DescribeException(ex);
                        Console.WriteLine("frame " + frame + " exception: " + frameError);
                        if (sendAttempted || IsLikelyDeviceSendFailure(ex))
                        {
                            consecutiveSendFailures++;
                            Console.WriteLine("frame " + frame + " consecutiveSendFailures=" + consecutiveSendFailures);
                            if (ShouldAbortAfterConsecutiveSendFailures(consecutiveSendFailures, options.MaxConsecutiveSendFailures))
                            {
                                frameWatch.Stop();
                                WriteHeartbeat(options, frame, sent, failed, consecutiveSendFailures, lastFullFrame, periodMs, fetchMs, renderMs, frameWatch.ElapsedMilliseconds, 0, snapshotStatus, "fatal", frameError);
                                Console.Error.WriteLine("stream fatal: consecutive send failures reached " + consecutiveSendFailures + "; exiting so watchdog can reopen " + options.Port);
                                return 1;
                            }
                        }
                    }

                    frameWatch.Stop();
                    int sleepMs = ComputeSleepMilliseconds(frameStartTicks, options.IntervalMs, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
                    WriteHeartbeat(options, frame, sent, failed, consecutiveSendFailures, lastFullFrame, periodMs, fetchMs, renderMs, frameWatch.ElapsedMilliseconds, sleepMs, snapshotStatus, frameStatus, frameError);
                    Console.WriteLine("frame " + frame + " timing: periodMs=" + periodMs + ", fetchMs=" + fetchMs + ", renderMs=" + renderMs + ", elapsedMs=" + frameWatch.ElapsedMilliseconds + ", sleepMs=" + sleepMs + ", data=" + snapshotStatus);
                    if (sleepMs > 0 && (options.FrameCount == 0 || frame < options.FrameCount))
                    {
                        SleepUntil(frameStartTicks + MillisecondsToStopwatchTicks(options.IntervalMs, Stopwatch.Frequency));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("stream fatal: " + DescribeException(ex));
                return 1;
            }
            finally
            {
                if (diffSession != null)
                {
                    diffSession.Dispose();
                }
            }

            Console.WriteLine("TURZX stream stopped: renderedOrSent=" + sent + ", failed=" + failed + ", elapsedMs=" + total.ElapsedMilliseconds);
            return failed == 0 ? 0 : 1;
        }

        internal static Snapshot SelectSnapshotAfterFetchFailureForTest(Snapshot lastSnapshot, Exception error, out string status)
        {
            return SelectSnapshotAfterFetchFailure(lastSnapshot, error, out status);
        }

        internal static int ComputeSleepMillisecondsForTest(long frameStartTicks, int intervalMs, long nowTicks, long frequency)
        {
            return ComputeSleepMilliseconds(frameStartTicks, intervalMs, nowTicks, frequency);
        }

        internal static bool ShouldWritePreviewForTest(long lastPreviewTicks, long nowTicks, long frequency, int previewIntervalSeconds)
        {
            return ShouldWritePreview(lastPreviewTicks, nowTicks, frequency, previewIntervalSeconds);
        }

        internal static int DefaultPreviewIntervalSecondsForTest()
        {
            return DefaultPreviewIntervalSeconds;
        }

        internal static void WriteUtf8TextAtomicallyForTest(string path, string content)
        {
            WriteUtf8TextAtomically(path, content);
        }

        internal static bool WriteHeartbeatCopiesForTest(string previewDir, int frame, string content)
        {
            return WriteHeartbeatCopies(previewDir, frame, content);
        }

        internal static void ApplyStreamIntervalForTest(Snapshot snapshot, int intervalMs)
        {
            ApplyStreamInterval(snapshot, intervalMs);
        }

        internal static bool IsLikelyDeviceSendFailureForTest(Exception error)
        {
            return IsLikelyDeviceSendFailure(error);
        }

        internal static bool ShouldAbortAfterConsecutiveSendFailuresForTest(int consecutiveFailures, int maxConsecutiveFailures)
        {
            return ShouldAbortAfterConsecutiveSendFailures(consecutiveFailures, maxConsecutiveFailures);
        }

        internal static bool ShouldSendFullFrameForTest(int frame, bool hasPreviousFrame, int fullResyncEveryFrames)
        {
            return ShouldSendFullFrame(frame, hasPreviousFrame, fullResyncEveryFrames);
        }

        internal static string DescribeExceptionForTest(Exception error)
        {
            return DescribeException(error);
        }

        private static void ApplyStreamInterval(Snapshot snapshot, int intervalMs)
        {
            if (snapshot == null)
            {
                return;
            }

            double seconds = Math.Max(0, intervalMs) / 1000.0;
            if (snapshot.Health == null)
            {
                snapshot.Health = new HealthSnapshot();
            }
            snapshot.Health.RefreshIntervalSeconds = seconds;

            TimeSnapshot time = snapshot.Time as TimeSnapshot;
            if (time != null)
            {
                time.UpdateIntervalSeconds = seconds;
            }
        }

        private static int ComputeSleepMilliseconds(long frameStartTicks, int intervalMs, long nowTicks, long frequency)
        {
            if (intervalMs <= 0 || frequency <= 0)
            {
                return 0;
            }

            long targetTicks = frameStartTicks + MillisecondsToStopwatchTicks(intervalMs, frequency);
            long remainingTicks = targetTicks - nowTicks;
            if (remainingTicks <= 0)
            {
                return 0;
            }

            double remainingMs = remainingTicks * 1000.0 / frequency;
            return (int)Math.Ceiling(remainingMs);
        }

        private static bool ShouldWritePreview(long lastPreviewTicks, long nowTicks, long frequency, int previewIntervalSeconds)
        {
            if (lastPreviewTicks < 0)
            {
                return true;
            }
            if (previewIntervalSeconds <= 0 || frequency <= 0)
            {
                return false;
            }

            long intervalTicks = (long)previewIntervalSeconds * frequency;
            return nowTicks - lastPreviewTicks >= intervalTicks;
        }

        private static void SleepUntil(long targetTicks)
        {
            while (true)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                long remainingTicks = targetTicks - nowTicks;
                if (remainingTicks <= 0)
                {
                    return;
                }

                double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
                if (remainingMs > 4)
                {
                    Thread.Sleep(Math.Max(1, (int)Math.Floor(remainingMs) - 2));
                }
                else
                {
                    Thread.SpinWait(100);
                }
            }
        }

        private static long MillisecondsToStopwatchTicks(int milliseconds, long frequency)
        {
            if (milliseconds <= 0 || frequency <= 0)
            {
                return 0;
            }
            return (long)Math.Ceiling(milliseconds * (double)frequency / 1000.0);
        }

        private static long TicksToMilliseconds(long ticks, long frequency)
        {
            if (ticks <= 0 || frequency <= 0)
            {
                return 0;
            }
            return (long)Math.Round(ticks * 1000.0 / frequency);
        }

        private static bool ShouldAbortAfterConsecutiveSendFailures(int consecutiveFailures, int maxConsecutiveFailures)
        {
            return maxConsecutiveFailures > 0 && consecutiveFailures >= maxConsecutiveFailures;
        }

        private static bool ShouldSendFullFrame(int frame, bool hasPreviousFrame, int fullResyncEveryFrames)
        {
            return !hasPreviousFrame || (fullResyncEveryFrames > 0 && frame > 1 && frame % fullResyncEveryFrames == 0);
        }

        private static bool IsLikelyDeviceSendFailure(Exception error)
        {
            Exception current = error;
            while (current != null)
            {
                string text = (current.GetType().FullName ?? string.Empty) + " " + (current.Message ?? string.Empty);
                if (ContainsOrdinalIgnoreCase(text, "Device Error") ||
                    ContainsOrdinalIgnoreCase(text, "SendReg false") ||
                    ContainsOrdinalIgnoreCase(text, "serial") ||
                    ContainsOrdinalIgnoreCase(text, "COM7") ||
                    ContainsOrdinalIgnoreCase(text, "RJCP") ||
                    ContainsOrdinalIgnoreCase(text, "UnauthorizedAccessException") ||
                    ContainsOrdinalIgnoreCase(text, "IOException"))
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        private static bool ContainsOrdinalIgnoreCase(string value, string search)
        {
            return value != null && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeException(Exception error)
        {
            if (error == null)
            {
                return "Unknown exception";
            }

            string result = error.GetType().Name + ": " + error.Message;
            Exception current = error.InnerException;
            while (current != null)
            {
                result += " | " + current.GetType().Name + ": " + current.Message;
                current = current.InnerException;
            }

            return result;
        }

        private static void WriteHeartbeat(
            StreamOptions options,
            int frame,
            int sent,
            int failed,
            int consecutiveSendFailures,
            int lastFullFrame,
            long periodMs,
            long fetchMs,
            long renderMs,
            long elapsedMs,
            int sleepMs,
            string snapshotStatus,
            string frameStatus,
            string frameError)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.PreviewDir))
            {
                return;
            }

            StringBuilder json = new StringBuilder();
            json.AppendLine("{");
            AppendJsonProperty(json, "utc", DateTime.UtcNow.ToString("o"), true);
            AppendJsonProperty(json, "port", options.Port, true);
            AppendJsonProperty(json, "status", frameStatus, true);
            AppendJsonProperty(json, "snapshot_status", snapshotStatus, true);
            AppendJsonProperty(json, "error", frameError, true);
            AppendJsonProperty(json, "frame", frame, true);
            AppendJsonProperty(json, "sent", sent, true);
            AppendJsonProperty(json, "failed", failed, true);
            AppendJsonProperty(json, "consecutive_send_failures", consecutiveSendFailures, true);
            AppendJsonProperty(json, "last_full_frame", lastFullFrame, true);
            AppendJsonProperty(json, "period_ms", periodMs, true);
            AppendJsonProperty(json, "fetch_ms", fetchMs, true);
            AppendJsonProperty(json, "render_ms", renderMs, true);
            AppendJsonProperty(json, "elapsed_ms", elapsedMs, true);
            AppendJsonProperty(json, "sleep_ms", sleepMs, false);
            json.AppendLine("}");
            WriteHeartbeatCopies(options.PreviewDir, frame, json.ToString());
        }

        private static bool WriteHeartbeatCopies(string previewDir, int frame, string content)
        {
            if (string.IsNullOrWhiteSpace(previewDir))
            {
                return false;
            }

            string slotName = frame % 2 == 0 ? "stream-heartbeat-a.json" : "stream-heartbeat-b.json";
            bool wrote = TryWriteUtf8TextAtomically(Path.Combine(previewDir, slotName), content);
            if (TryWriteUtf8TextAtomically(Path.Combine(previewDir, "stream-heartbeat.json"), content))
            {
                wrote = true;
            }
            return wrote;
        }

        private static bool TryWriteUtf8TextAtomically(string path, string content)
        {
            try
            {
                WriteUtf8TextAtomically(path, content);
                return true;
            }
            catch
            {
                // Readers can lock one target without taking down the other heartbeat slot.
                return false;
            }
        }

        private static void WriteUtf8TextAtomically(string path, string content)
        {
            WriteFileAtomically(path, delegate(string tempPath)
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            });
        }

        private static void SavePreviewAtomically(Bitmap bitmap, string path)
        {
            WriteFileAtomically(path, delegate(string tempPath)
            {
                bitmap.Save(tempPath, ImageFormat.Png);
            });
        }

        private static void WriteFileAtomically(string path, Action<string> writeTemporaryFile)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }
            Directory.CreateDirectory(directory);

            string tempPath = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                writeTemporaryFile(tempPath);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static void AppendJsonProperty(StringBuilder json, string name, string value, bool comma)
        {
            json.Append("  \"");
            json.Append(JsonEscape(name));
            json.Append("\": ");
            if (value == null)
            {
                json.Append("null");
            }
            else
            {
                json.Append("\"");
                json.Append(JsonEscape(value));
                json.Append("\"");
            }

            if (comma)
            {
                json.Append(",");
            }

            json.AppendLine();
        }

        private static void AppendJsonProperty(StringBuilder json, string name, long value, bool comma)
        {
            json.Append("  \"");
            json.Append(JsonEscape(name));
            json.Append("\": ");
            json.Append(value);
            if (comma)
            {
                json.Append(",");
            }

            json.AppendLine();
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            StringBuilder escaped = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        escaped.Append("\\\\");
                        break;
                    case '"':
                        escaped.Append("\\\"");
                        break;
                    case '\r':
                        escaped.Append("\\r");
                        break;
                    case '\n':
                        escaped.Append("\\n");
                        break;
                    case '\t':
                        escaped.Append("\\t");
                        break;
                    default:
                        if (ch < 32)
                        {
                            escaped.Append("\\u");
                            escaped.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            escaped.Append(ch);
                        }
                        break;
                }
            }

            return escaped.ToString();
        }

        private static Snapshot FetchFrameSnapshot(StreamOptions options, int frame, ref Snapshot lastSnapshot, out string status)
        {
            if (options.UseSample)
            {
                Snapshot sample = SampleSnapshot(frame);
                lastSnapshot = sample;
                status = "sample";
                return sample;
            }

            try
            {
                Snapshot snapshot = FetchSnapshot(options);
                lastSnapshot = snapshot;
                status = "fresh";
                return snapshot;
            }
            catch (Exception ex)
            {
                return SelectSnapshotAfterFetchFailure(lastSnapshot, ex, out status);
            }
        }

        private static Snapshot SelectSnapshotAfterFetchFailure(Snapshot lastSnapshot, Exception error, out string status)
        {
            string errorName = error == null ? "Unknown" : error.GetType().Name;
            if (lastSnapshot != null)
            {
                status = "stale:" + errorName;
                return lastSnapshot;
            }

            status = "empty:" + errorName;
            return new Snapshot
            {
                SchemaVersion = 1,
                Sequence = 0,
                Alert = new AlertSnapshot { Level = "warn", Message = "数据暂不可用" },
                Health = new HealthSnapshot
                {
                    Status = "degraded",
                    Detail = "采集超时，等待下一次数据"
                }
            };
        }

        private static Snapshot FetchSnapshot(StreamOptions options)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(options.MetricsUrl);
            request.Method = "GET";
            request.Timeout = options.HttpTimeoutMs;
            request.ReadWriteTimeout = options.HttpTimeoutMs;
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Snapshot));
                Snapshot snapshot = serializer.ReadObject(stream) as Snapshot;
                if (snapshot == null)
                {
                    throw new InvalidDataException("Snapshot JSON did not deserialize.");
                }

                if (snapshot.SchemaVersion.HasValue && snapshot.SchemaVersion.Value != 1)
                {
                    throw new InvalidDataException("Unsupported schema_version: " + snapshot.SchemaVersion.Value);
                }

                return snapshot;
            }
        }

        private static Snapshot SampleSnapshot(int frame)
        {
            double cpu = 24 + ((frame * 17) % 50);
            double gpu = 12 + ((frame * 13) % 44);
            return new Snapshot
            {
                SchemaVersion = 1,
                Sequence = frame,
                Time = new TimeSnapshot
                {
                    Date = DateTime.Now.ToString("yyyy-MM-dd"),
                    Weekday = ChineseWeekday(DateTime.Now),
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                    UpdateIntervalSeconds = 1.0
                },
                Weather = new WeatherSnapshot
                {
                    City = "北京",
                    TemperatureCelsius = 31,
                    Condition = "晴",
                    Aqi = 38,
                    HumidityPercent = 41
                },
                Alert = new AlertSnapshot { Message = "系统正常", Level = "ok" },
                ForegroundApp = new ForegroundAppSnapshot { Name = "game.exe" },
                Cpu = new CpuSnapshot
                {
                    Model = "AMD Ryzen 9 9950X3D",
                    UsagePercent = cpu,
                    TemperatureCelsius = 63,
                    PowerWatts = 145,
                    ClockGhz = 5.56,
                    CoreVoltage = 1.27,
                    LoadHistoryPercent = new double[] { 18, 28, 31, 29, cpu, 51, 59, 47, 16, 12, 46, 76, 58 }
                },
                Gpu = new GpuSnapshot
                {
                    Model = "NVIDIA GeForce RTX 5090 D",
                    UsagePercent = gpu,
                    TemperatureCelsius = 54,
                    PowerWatts = 154,
                    CoreClockGhz = 2.84,
                    CoreVoltage = 1.00,
                    MemoryClockGhz = 16.4,
                    LoadHistoryPercent = new double[] { 10, 9, 16, 31, gpu, 4, 1, 20, 41, 36, 6, 0, 22 }
                },
                Fps = new FpsSnapshot { Current = 144, Average = 141, Low1Percent = 118, FrameTimeMs = 6.9, Status = "active", Source = "presentmon", SampleAgeSeconds = 0.2, Detail = "game.exe" },
                Memory = new MemorySnapshot { RamUsagePercent = 34, RamUsedGb = 22, RamTotalGb = 64, VramUsagePercent = 14, MotherboardTemperatureCelsius = 41, ModuleTemperaturesCelsius = new double[] { 44, 46 } },
                Network = new NetworkSnapshot { DownloadBytesPerSecond = 10240, UploadBytesPerSecond = 22528, PingMs = 18, JitterMs = 2, PacketLossPercent = 0, DpcPercent = 0.2 },
                Disks = new DiskSnapshot[]
                {
                    new DiskSnapshot { Drive = "C:", Label = "Win11", UsagePercent = 44, FreeText = "343 GB 可用" },
                    new DiskSnapshot { Drive = "D:", Label = "game", UsagePercent = 70, FreeText = "1.1 TB 可用" },
                    new DiskSnapshot { Drive = "E:", Label = "software", UsagePercent = 35, FreeText = "2 TB 可用" }
                },
                TopProcesses = new ProcessSnapshot[]
                {
                    new ProcessSnapshot { Name = "VeryLongGameName-Shipping.exe", CpuPercent = 18, GpuPercent = 62, MemoryGb = 3.1 },
                    new ProcessSnapshot { Name = "chrome.exe", CpuPercent = 6, GpuPercent = 3, MemoryGb = 3.1 },
                    new ProcessSnapshot { Name = "python.exe", Description = "metrics_agent", CpuPercent = 2, GpuPercent = 0, MemoryGb = 0.4 }
                },
                Health = new HealthSnapshot { Status = "诊断服务在线", Detail = "模块正常运转中", HardPageFaultsPerSecond = 0, RefreshIntervalSeconds = 1.0 },
                Trust = new TrustSnapshot
                {
                    Score = 96,
                    Level = "ok",
                    Summary = "可信度 96/100",
                    WorstComponent = "fps",
                    WorstLabel = "FPS",
                    MissingCount = 0,
                    FallbackCount = 0,
                    Items = new TrustItemSnapshot[]
                    {
                        new TrustItemSnapshot { Component = "cpu", Label = "CPU", Score = 100, Status = "ok", Source = "windows_api+lhm" },
                        new TrustItemSnapshot { Component = "gpu", Label = "GPU", Score = 96, Status = "ok", Source = "nvml+lhm" },
                        new TrustItemSnapshot { Component = "fps", Label = "FPS", Score = 92, Status = "ok", Source = "presentmon" }
                    }
                }
            };
        }

        private static string ChineseWeekday(DateTime value)
        {
            string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return names[(int)value.DayOfWeek];
        }

        private static void PrintUsage()
        {
            Console.WriteLine("TURZX.SideScreen.Stream.exe [--sample] [--dry-run] [--diff] [--alt-helper] [--frames N] [--interval-ms 1000] [--preview-interval-seconds 45] [--full-resync-every-frames 300] [--max-consecutive-send-failures 5] [--metrics-url URL] [--root TURZX_ROOT] [--port COM7]");
            Console.WriteLine("frames=0 means infinite. --diff sends one full baseline frame, then command-204 differential frames.");
        }

        private sealed class StreamOptions
        {
            public bool ShowHelp;
            public bool UseSample;
            public bool DryRun;
            public bool UseDiff;
            public bool AltHelper;
            public int FrameCount = 0;
            public int IntervalMs = 3000;
            public int HttpTimeoutMs = DefaultHttpTimeoutMs;
            public int SendTimeoutMs = 240000;
            public int MaxConsecutiveSendFailures = 5;
            public int FullResyncEveryFrames = 300;
            public int PreviewIntervalSeconds = DefaultPreviewIntervalSeconds;
            public string MetricsUrl = DefaultMetricsUrl;
            public string Root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            public string Port = "COM7";
            public string DevCode = TurzxHelperSender.DefaultDevCode;
            public string PreviewDir;

            public static StreamOptions Parse(string[] args)
            {
                StreamOptions options = new StreamOptions();
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--help" || arg == "-h") options.ShowHelp = true;
                    else if (arg == "--sample") options.UseSample = true;
                    else if (arg == "--dry-run") options.DryRun = true;
                    else if (arg == "--diff") options.UseDiff = true;
                    else if (arg == "--alt-helper") options.AltHelper = true;
                    else if (arg == "--frames") options.FrameCount = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--interval-ms") options.IntervalMs = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--timeout-ms") options.HttpTimeoutMs = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--send-timeout-ms") options.SendTimeoutMs = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--max-consecutive-send-failures") options.MaxConsecutiveSendFailures = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--full-resync-every-frames") options.FullResyncEveryFrames = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--preview-interval-seconds") options.PreviewIntervalSeconds = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--metrics-url") options.MetricsUrl = Next(args, ref i, arg);
                    else if (arg == "--root") options.Root = Next(args, ref i, arg);
                    else if (arg == "--port") options.Port = Next(args, ref i, arg);
                    else if (arg == "--dev-code") options.DevCode = Next(args, ref i, arg);
                    else if (arg == "--preview-dir") options.PreviewDir = Next(args, ref i, arg);
                    else throw new ArgumentException("Unknown argument: " + arg);
                }

                if (options.FrameCount < 0) throw new ArgumentOutOfRangeException("frames");
                if (options.IntervalMs < 0) throw new ArgumentOutOfRangeException("interval-ms");
                if (options.MaxConsecutiveSendFailures < 0) throw new ArgumentOutOfRangeException("max-consecutive-send-failures");
                if (options.FullResyncEveryFrames < 0) throw new ArgumentOutOfRangeException("full-resync-every-frames");
                if (options.PreviewIntervalSeconds < 0) throw new ArgumentOutOfRangeException("preview-interval-seconds");
                return options;
            }

            private static string Next(string[] args, ref int index, string arg)
            {
                if (index + 1 >= args.Length) throw new ArgumentException(arg + " requires a value.");
                index++;
                return args[index];
            }
        }
    }
}
