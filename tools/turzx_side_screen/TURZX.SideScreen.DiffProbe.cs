using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace TURZX.SideScreen
{
    public static class DiffProbeApp
    {
        public static int Main(string[] args)
        {
            DiffProbeOptions options;
            try
            {
                options = DiffProbeOptions.Parse(args);
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

            int exitCode = 1;
            Exception workerException = null;
            Thread worker = new Thread(delegate()
            {
                try
                {
                    exitCode = Run(options);
                }
                catch (Exception ex)
                {
                    workerException = ex;
                    exitCode = 1;
                }
            });
            worker.IsBackground = true;
            worker.Start();

            if (!worker.Join(options.TimeoutMs))
            {
                Console.Error.WriteLine("DiffProbe TIMEOUT after " + options.TimeoutMs + "ms");
                return 1;
            }

            if (workerException != null)
            {
                Console.Error.WriteLine("DiffProbe exception: " + Unwrap(workerException).GetType().Name + ": " + Unwrap(workerException).Message);
            }

            return exitCode;
        }

        private static int Run(DiffProbeOptions options)
        {
            if (options.Frames < 2)
            {
                throw new ArgumentOutOfRangeException("frames", "DiffProbe needs at least 2 frames.");
            }

            Directory.CreateDirectory(options.PreviewDir);
            Console.WriteLine("DiffProbe starting: frames=" + options.Frames +
                ", intervalMs=" + options.IntervalMs +
                ", dryRun=" + options.DryRun +
                ", swapOrder=" + options.SwapOrder +
                ", flag=" + options.Flag +
                ", altHelper=" + options.AltHelper +
                ", port=" + options.Port);

            if (options.DryRun)
            {
                return RunDry(options);
            }

            return RunCom(options);
        }

        private static int RunDry(DiffProbeOptions options)
        {
            byte[] previous = null;
            for (int frame = 1; frame <= options.Frames; frame++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                using (Bitmap bitmap = SideScreenRenderer.Render(BuildSnapshot(frame)))
                {
                    SavePreview(options, bitmap, frame);
                    byte[] current = TurzxHelperSender.ConvertBitmapToFrameData(options.Root, bitmap);
                    long changed = previous == null ? current.Length : CountChangedBytes(previous, current);
                    Console.WriteLine("frame " + frame + " dry converted in " + watch.ElapsedMilliseconds +
                        "ms: frameBytes=" + current.Length + ", changedBytes=" + changed);
                    previous = current;
                }
            }

            Console.WriteLine("DiffProbe dry-run OK");
            return 0;
        }

        private static int RunCom(DiffProbeOptions options)
        {
            byte[] previous = null;
            using (TurzxHelperSender.DiffSession session = new TurzxHelperSender.DiffSession(options.Root, options.Port, options.DevCode))
            {
                for (int frame = 1; frame <= options.Frames; frame++)
                {
                    Stopwatch frameWatch = Stopwatch.StartNew();
                    try
                    {
                        using (Bitmap bitmap = SideScreenRenderer.Render(BuildSnapshot(frame)))
                        {
                            SavePreview(options, bitmap, frame);
                            byte[] current = session.Convert(bitmap);

                            if (previous == null)
                            {
                                Stopwatch sendWatch = Stopwatch.StartNew();
                                session.SendFull(current);
                                sendWatch.Stop();
                                Console.WriteLine("frame " + frame + " FULL sent in " + sendWatch.ElapsedMilliseconds + "ms: frameBytes=" + current.Length);
                            }
                            else
                            {
                                long changed = CountChangedBytes(previous, current);
                                Stopwatch sendWatch = Stopwatch.StartNew();
                                long sequence = frame - 2;
                                int result = session.SendDiff(previous, current, sequence, options.SwapOrder, options.Flag, options.AltHelper);
                                sendWatch.Stop();
                                Console.WriteLine("frame " + frame + " DIFF sent in " + sendWatch.ElapsedMilliseconds +
                                    "ms: seq=" + sequence + ", result=" + result + ", changedBytes=" + changed);
                            }

                            previous = current;
                        }
                    }
                    catch (Exception ex)
                    {
                        Exception unwrapped = Unwrap(ex);
                        Console.Error.WriteLine("frame " + frame + " failed: " + unwrapped.GetType().Name + ": " + unwrapped.Message);
                        return 1;
                    }

                    frameWatch.Stop();
                    int sleep = options.IntervalMs - (int)Math.Min(int.MaxValue, frameWatch.ElapsedMilliseconds);
                    if (sleep > 0 && frame < options.Frames)
                    {
                        Thread.Sleep(sleep);
                    }
                }
            }

            Console.WriteLine("DiffProbe COM OK");
            return 0;
        }

        private static Snapshot BuildSnapshot(int frame)
        {
            double cpu = 20 + ((frame * 11) % 70);
            double gpu = 10 + ((frame * 7) % 80);
            DateTime now = DateTime.Now;
            return new Snapshot
            {
                SchemaVersion = 1,
                Sequence = frame,
                Time = new TimeSnapshot
                {
                    Date = now.ToString("yyyy-MM-dd"),
                    Weekday = ChineseWeekday(now),
                    Time = now.ToString("HH:mm:ss"),
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
                Alert = new AlertSnapshot { Message = "DIFF PROBE " + frame, Level = "ok" },
                ForegroundApp = new ForegroundAppSnapshot { Name = "diff-probe.exe", Title = "TURZX differential probe" },
                Cpu = new CpuSnapshot
                {
                    Model = "AMD Ryzen 9 9950X3D",
                    UsagePercent = cpu,
                    TemperatureCelsius = 63,
                    PowerWatts = 145,
                    ClockGhz = 5.56,
                    CoreVoltage = 1.27,
                    LoadHistoryPercent = BuildHistory(cpu, frame)
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
                    VramUsedGb = 4.5 + frame,
                    VramTotalGb = 32.0,
                    LoadHistoryPercent = BuildHistory(gpu, frame + 3)
                },
                Fps = new FpsSnapshot { Current = 100 + frame, Average = 98 + frame, Low1Percent = 80 + frame, FrameTimeMs = 9.5, Status = "active", Source = "presentmon", SampleAgeSeconds = 0.1, Detail = "diff-probe.exe" },
                Memory = new MemorySnapshot { RamUsagePercent = 34 + frame, RamUsedGb = 22.0, RamTotalGb = 64.0, VramUsagePercent = 14 + frame, MotherboardTemperatureCelsius = 41, ModuleTemperaturesCelsius = new double[] { 44 + frame / 10.0, 46 + frame / 10.0 } },
                Network = new NetworkSnapshot { DownloadBytesPerSecond = 10240 + frame * 256, UploadBytesPerSecond = 22528 + frame * 512, PingMs = 18, JitterMs = 2, PacketLossPercent = 0, DpcPercent = 0.2 + frame / 100.0 },
                Disks = new DiskSnapshot[]
                {
                    new DiskSnapshot { Drive = "C:", Label = "Win11", UsagePercent = 44, FreeText = "343 GB 可用" },
                    new DiskSnapshot { Drive = "E:", Label = "Predator 4TB", UsagePercent = 35, FreeText = "2 TB 可用" },
                    new DiskSnapshot { Drive = "U:", Label = "AUTO", UsagePercent = 0, FreeText = "hotplug" }
                },
                PhysicalDisks = new PhysicalDiskSnapshot[]
                {
                    new PhysicalDiskSnapshot { Model = "Samsung SSD 990 PRO 2TB", VolumeDrives = new string[] { "C:" }, BusType = "NVMe", MediaType = "SSD", CapacityGb = 1863, UsedPercent = 61, FreeGb = 726, ReadBytesPerSecond = (118 + frame) * 1024 * 1024, WriteBytesPerSecond = (77 + frame) * 1024 * 1024, ActivityPercent = 40 + frame, TemperatureCelsius = 49, Status = "ok", Source = "diff_probe" },
                    new PhysicalDiskSnapshot { Model = "WD_BLACK SN850X 4TB", VolumeDrives = new string[] { "E:" }, BusType = "NVMe", MediaType = "SSD", CapacityGb = 3726, UsedPercent = 35, FreeGb = 2048, ReadBytesPerSecond = frame * 1024 * 1024, WriteBytesPerSecond = (16 + frame) * 1024 * 1024, ActivityPercent = 11 + frame, TemperatureCelsius = 43, Status = "ok", Source = "diff_probe" }
                },
                TopProcesses = new ProcessSnapshot[]
                {
                    new ProcessSnapshot { Name = "diff-probe-frame-" + frame + ".exe", CpuPercent = cpu / 2, GpuPercent = gpu, MemoryGb = 1.0 + frame / 10.0 },
                    new ProcessSnapshot { Name = "chrome.exe", CpuPercent = 6, GpuPercent = 3, MemoryGb = 3.1 },
                    new ProcessSnapshot { Name = "python.exe", Description = "metrics_agent", CpuPercent = 2, GpuPercent = 0, MemoryGb = 0.4 }
                },
                Health = new HealthSnapshot { Status = "差分探针运行中", Detail = "FRAME " + frame, HardPageFaultsPerSecond = 0, RefreshIntervalSeconds = 1.0 }
            };
        }

        private static double[] BuildHistory(double value, int seed)
        {
            double[] history = new double[18];
            for (int i = 0; i < history.Length; i++)
            {
                history[i] = Math.Max(0, Math.Min(100, value + (((i + seed) % 7) - 3) * 4));
            }

            return history;
        }

        private static void SavePreview(DiffProbeOptions options, Bitmap bitmap, int frame)
        {
            string path = Path.Combine(options.PreviewDir, "diff-frame-" + frame.ToString("000") + ".png");
            bitmap.Save(path, ImageFormat.Png);
            bitmap.Save(Path.Combine(options.PreviewDir, "diff-last.png"), ImageFormat.Png);
        }

        private static long CountChangedBytes(byte[] previous, byte[] current)
        {
            long changed = 0;
            int shared = Math.Min(previous.Length, current.Length);
            for (int i = 0; i < shared; i++)
            {
                if (previous[i] != current[i])
                {
                    changed++;
                }
            }

            return changed + Math.Abs(previous.Length - current.Length);
        }

        private static Exception Unwrap(Exception ex)
        {
            System.Reflection.TargetInvocationException tie = ex as System.Reflection.TargetInvocationException;
            return tie != null && tie.InnerException != null ? tie.InnerException : ex;
        }

        private static string ChineseWeekday(DateTime value)
        {
            string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return names[(int)value.DayOfWeek];
        }

        private static void PrintUsage()
        {
            Console.WriteLine("TURZX.SideScreen.DiffProbe.exe [--dry-run] [--frames 6] [--interval-ms 1000] [--root TURZX_ROOT] [--port COM7] [--swap-order] [--flag] [--alt-helper]");
            Console.WriteLine("Real COM mode sends one full baseline frame, then command-204 differential frames through the original TURZX helper.");
        }

        private sealed class DiffProbeOptions
        {
            public bool ShowHelp;
            public bool DryRun;
            public bool SwapOrder;
            public bool Flag;
            public bool AltHelper;
            public int Frames = 6;
            public int IntervalMs = 1000;
            public int TimeoutMs = 90000;
            public string Root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            public string Port = "COM7";
            public string DevCode = TurzxHelperSender.DefaultDevCode;
            public string PreviewDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diff-probe");

            public static DiffProbeOptions Parse(string[] args)
            {
                DiffProbeOptions options = new DiffProbeOptions();
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--help" || arg == "-h") options.ShowHelp = true;
                    else if (arg == "--dry-run") options.DryRun = true;
                    else if (arg == "--swap-order") options.SwapOrder = true;
                    else if (arg == "--flag") options.Flag = true;
                    else if (arg == "--alt-helper") options.AltHelper = true;
                    else if (arg == "--frames") options.Frames = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--interval-ms") options.IntervalMs = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--timeout-ms") options.TimeoutMs = int.Parse(Next(args, ref i, arg));
                    else if (arg == "--root") options.Root = Next(args, ref i, arg);
                    else if (arg == "--port") options.Port = Next(args, ref i, arg);
                    else if (arg == "--dev-code") options.DevCode = Next(args, ref i, arg);
                    else if (arg == "--preview-dir") options.PreviewDir = Next(args, ref i, arg);
                    else throw new ArgumentException("Unknown argument: " + arg);
                }

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
