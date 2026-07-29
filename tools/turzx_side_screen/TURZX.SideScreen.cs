using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TURZX.SideScreen
{
    public static class SideScreenApp
    {
        private const string DefaultMetricsUrl = "http://127.0.0.1:18765/snapshot";
        private const string DefaultPort = "COM7";

        public static int Main(string[] args)
        {
            try
            {
                AppOptions options = AppOptions.Parse(args);
                if (options.ShowHelp)
                {
                    PrintUsage();
                    return 0;
                }

                Snapshot snapshot = options.UseSample
                    ? SampleSnapshot()
                    : LoadSnapshot(options);

                using (Bitmap bitmap = SideScreenRenderer.Render(snapshot))
                {
                    if (!string.IsNullOrWhiteSpace(options.OutputPath))
                    {
                        string fullOutputPath = Path.GetFullPath(options.OutputPath);
                        string dir = Path.GetDirectoryName(fullOutputPath);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        bitmap.Save(fullOutputPath, ImageFormat.Png);
                        Console.WriteLine("PNG written: " + fullOutputPath);
                    }

                    if (options.SendToDevice)
                    {
                        byte[] frame = TurzxSideScreenProtocol.EncodeBitmap(bitmap);
                        TurzxSideScreenProtocol.SendFullFrame(options.Port, frame, options.RjcpDllPath, false);
                        Console.WriteLine("Full frame sent to " + options.Port);
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static Snapshot LoadSnapshot(AppOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.SnapshotPath))
            {
                using (FileStream stream = File.OpenRead(options.SnapshotPath))
                {
                    return DeserializeSnapshot(stream);
                }
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(options.MetricsUrl);
            request.Method = "GET";
            request.Timeout = options.HttpTimeoutMs;
            request.ReadWriteTimeout = options.HttpTimeoutMs;
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                return DeserializeSnapshot(stream);
            }
        }

        private static Snapshot DeserializeSnapshot(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

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

        private static Snapshot SampleSnapshot()
        {
            return new Snapshot
            {
                SchemaVersion = 1,
                TimestampUnixMs = 1783267355000,
                Sequence = 1,
                Time = new TimeSnapshot
                {
                    Date = "2026-07-05",
                    Weekday = "周日",
                    Time = "17:02:35",
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
                Alert = new AlertSnapshot
                {
                    Level = "ok",
                    Message = "系统正常"
                },
                ForegroundApp = new ForegroundAppSnapshot
                {
                    Name = "game.exe",
                    Title = "Sample foreground"
                },
                Cpu = new CpuSnapshot
                {
                    Model = "AMD Ryzen 9 9950X3D",
                    UsagePercent = 32,
                    TemperatureCelsius = 63,
                    PowerWatts = 145,
                    ClockGhz = 5.56,
                    CoreVoltage = 1.27,
                    LoadHistoryPercent = new double[] { 18, 28, 31, 29, 34, 51, 59, 47, 16, 12, 46, 76, 58 }
                },
                Gpu = new GpuSnapshot
                {
                    Model = "NVIDIA GeForce RTX 5090 D",
                    UsagePercent = 17,
                    TemperatureCelsius = 54,
                    PowerWatts = 154,
                    CoreClockGhz = 2.84,
                    CoreVoltage = 1.00,
                    MemoryClockGhz = 16.4,
                    LoadHistoryPercent = new double[] { 10, 9, 16, 31, 25, 4, 1, 20, 41, 36, 6, 0, 22 }
                },
                Fps = new FpsSnapshot
                {
                    Current = 144,
                    Average = 141,
                    Low1Percent = 118,
                    FrameTimeMs = 6.9,
                    Status = "active",
                    Source = "presentmon",
                    SampleAgeSeconds = 0.2,
                    Detail = "game.exe"
                },
                Memory = new MemorySnapshot
                {
                    RamUsagePercent = 34,
                    RamUsedGb = 22.0,
                    RamTotalGb = 64.0,
                    VramUsagePercent = 14,
                    MotherboardTemperatureCelsius = 41,
                    ModuleTemperaturesCelsius = new double[] { 44, 46 }
                },
                Network = new NetworkSnapshot
                {
                    DownloadBytesPerSecond = 10240,
                    UploadBytesPerSecond = 22528,
                    PingMs = 18,
                    JitterMs = 2,
                    PacketLossPercent = 0,
                    DpcPercent = 0.2
                },
                Disks = new DiskSnapshot[]
                {
                    new DiskSnapshot { Drive = "C:", Label = "Win11", UsagePercent = 44, FreeText = "120 GB 可用" },
                    new DiskSnapshot { Drive = "D:", Label = "game", UsagePercent = 70, FreeText = "320 GB 可用" },
                    new DiskSnapshot { Drive = "E:", Label = "software", UsagePercent = 35, FreeText = "450 GB 可用" }
                },
                PhysicalDisks = new PhysicalDiskSnapshot[]
                {
                    new PhysicalDiskSnapshot { Model = "Samsung SSD 990 PRO 2TB", VolumeDrives = new string[] { "C:" }, BusType = "NVMe", MediaType = "SSD", CapacityGb = 1863, UsedPercent = 61, FreeGb = 726, ReadBytesPerSecond = 118 * 1024 * 1024, WriteBytesPerSecond = 77 * 1024 * 1024, ActivityPercent = 40, TemperatureCelsius = 49, Status = "ok", Source = "sample" },
                    new PhysicalDiskSnapshot { Model = "WD_BLACK SN850X 4TB", VolumeDrives = new string[] { "D:", "E:" }, BusType = "NVMe", MediaType = "SSD", CapacityGb = 3726, UsedPercent = 74, FreeGb = 969, ReadBytesPerSecond = 4 * 1024 * 1024, WriteBytesPerSecond = 16 * 1024 * 1024, ActivityPercent = 11, TemperatureCelsius = 43, Status = "ok", Source = "sample" }
                },
                TopProcesses = new ProcessSnapshot[]
                {
                    new ProcessSnapshot { Name = "VeryLongGameName-Shipping.exe", CpuPercent = 18, GpuPercent = 62, MemoryGb = 3.1 },
                    new ProcessSnapshot { Name = "chrome.exe", CpuPercent = 6, GpuPercent = 3, MemoryGb = 3.1 },
                    new ProcessSnapshot { Name = "python.exe", Description = "metrics_agent", CpuPercent = 2, GpuPercent = 0, MemoryGb = 0.4 }
                },
                Health = new HealthSnapshot
                {
                    Status = "诊断服务在线",
                    Detail = "模块正常运转中",
                    HardPageFaultsPerSecond = 0,
                    RefreshIntervalSeconds = 1.0
                }
            };
        }

        private static void PrintUsage()
        {
            Console.WriteLine("TURZX.SideScreen.exe [--sample] [--snapshot path.json] [--metrics-url url] [--output out.png] [--send] [--port COM7] [--rjcp path]");
            Console.WriteLine("Default is safe: render/fetch only. COM write happens only with --send.");
        }

        private sealed class AppOptions
        {
            public bool ShowHelp;
            public bool UseSample;
            public bool SendToDevice;
            public string SnapshotPath;
            public string MetricsUrl = DefaultMetricsUrl;
            public string OutputPath;
            public string Port = DefaultPort;
            public string RjcpDllPath;
            public int HttpTimeoutMs = 1500;

            public static AppOptions Parse(string[] args)
            {
                AppOptions options = new AppOptions();
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--help" || arg == "-h")
                    {
                        options.ShowHelp = true;
                    }
                    else if (arg == "--sample")
                    {
                        options.UseSample = true;
                    }
                    else if (arg == "--send")
                    {
                        options.SendToDevice = true;
                    }
                    else if (arg == "--snapshot")
                    {
                        options.SnapshotPath = RequireValue(args, ref i, arg);
                    }
                    else if (arg == "--metrics-url")
                    {
                        options.MetricsUrl = RequireValue(args, ref i, arg);
                    }
                    else if (arg == "--output")
                    {
                        options.OutputPath = RequireValue(args, ref i, arg);
                    }
                    else if (arg == "--port")
                    {
                        options.Port = RequireValue(args, ref i, arg);
                    }
                    else if (arg == "--rjcp")
                    {
                        options.RjcpDllPath = RequireValue(args, ref i, arg);
                    }
                    else if (arg == "--timeout-ms")
                    {
                        options.HttpTimeoutMs = int.Parse(RequireValue(args, ref i, arg));
                    }
                    else
                    {
                        throw new ArgumentException("Unknown argument: " + arg);
                    }
                }

                if (!options.UseSample && string.IsNullOrWhiteSpace(options.SnapshotPath) && string.IsNullOrWhiteSpace(options.MetricsUrl))
                {
                    throw new ArgumentException("No snapshot source configured.");
                }

                return options;
            }

            private static string RequireValue(string[] args, ref int index, string arg)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException(arg + " requires a value.");
                }

                index++;
                return args[index];
            }
        }
    }
}
