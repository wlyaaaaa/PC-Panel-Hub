using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace TURZX.SideScreen
{
    public static class SideScreenRenderer
    {
        public const int Width = 480;
        public const int Height = 1920;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static readonly Color BgTop = Hex("#f4faf7");
        private static readonly Color BgMid = Hex("#edf6f2");
        private static readonly Color BgBottom = Hex("#e3f2ec");
        private static readonly Color CardFill = Hex("#ffffff");
        private static readonly Color CardBorder = Hex("#c9dfd6");
        private static readonly Color MiniFill = Hex("#f2faf6");
        private static readonly Color MiniBorder = Hex("#dbeef5");
        private static readonly Color Dark = Hex("#044e37");
        private static readonly Color Muted = Hex("#4a7c6c");
        private static readonly Color Green = Hex("#15803d");
        private static readonly Color GreenSoft = Hex("#dcfce7");
        private static readonly Color GreenLine = Hex("#e6fcf4");
        private static readonly Color CpuBlue = Hex("#0284c7");
        private static readonly Color CpuBlue2 = Hex("#38bdf8");
        private static readonly Color GpuPink = Hex("#db2777");
        private static readonly Color GpuPink2 = Hex("#f472b6");
        private static readonly Color NetGreen = Hex("#059669");
        private static readonly Color NetGreen2 = Hex("#34d399");
        private static readonly Color DiskGreen = Hex("#15803d");
        private static readonly Color DiskGreen2 = Hex("#52b788");
        private static readonly Color Indigo = Hex("#818cf8");

        public static Bitmap Render(Snapshot snapshot)
        {
            if (snapshot == null)
            {
                snapshot = new Snapshot();
            }

            Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (FontSet fonts = new FontSet())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                TimeSnapshot renderTime = CoerceTime(snapshot.Time);

                DrawBackground(g);
                DrawHeader(g, fonts, snapshot, renderTime);
                DrawHardwareCard(g, fonts, 24, 160, "CPU 处理器", Safe(snapshot.Cpu == null ? null : snapshot.Cpu.Model, "CPU"),
                    snapshot.Cpu == null ? null : snapshot.Cpu.UsagePercent,
                    FormatTemperature(snapshot.Cpu == null ? null : snapshot.Cpu.TemperatureCelsius),
                    FormatWatts(snapshot.Cpu == null ? null : snapshot.Cpu.PowerWatts),
                    "主频",
                    FormatGhz(First(snapshot.Cpu == null ? null : snapshot.Cpu.ClockGhz, GhzFromMhz(snapshot.Cpu == null ? null : snapshot.Cpu.ClockMhz))),
                    "核心电压",
                    FormatVoltage(snapshot.Cpu == null ? null : snapshot.Cpu.CoreVoltage),
                    CpuBlue, CpuBlue, CpuBlue2,
                    snapshot.Cpu == null ? null : snapshot.Cpu.LoadHistoryPercent);

                DrawHardwareCard(g, fonts, 24, 424, "GPU 核心", GpuDisplayName(snapshot.Gpu),
                    snapshot.Gpu == null ? null : snapshot.Gpu.UsagePercent,
                    FormatTemperature(First(snapshot.Gpu == null ? null : snapshot.Gpu.TemperatureCelsius, snapshot.Gpu == null ? null : snapshot.Gpu.TemperatureC)),
                    FormatWatts(snapshot.Gpu == null ? null : snapshot.Gpu.PowerWatts),
                    "核心频率",
                    FormatGhz(First(snapshot.Gpu == null ? null : snapshot.Gpu.CoreClockGhz, GhzFromMhz(snapshot.Gpu == null ? null : snapshot.Gpu.CoreClockMhz))),
                    "电压",
                    FormatVoltage(snapshot.Gpu == null ? null : snapshot.Gpu.CoreVoltage),
                    GpuPink, GpuPink, GpuPink2,
                    snapshot.Gpu == null ? null : snapshot.Gpu.LoadHistoryPercent);

                DrawFps(g, fonts, snapshot.Fps);
                DrawMemory(g, fonts, snapshot.Memory, snapshot.Gpu);
                DrawNetwork(g, fonts, snapshot.Network);
                DrawDisks(g, fonts, snapshot.PhysicalDisks, snapshot.Disks);
                DrawApps(g, fonts, snapshot.TopProcesses);
            }

            return bitmap;
        }

        private static void DrawBackground(Graphics g)
        {
            using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), BgTop, BgBottom, LinearGradientMode.Vertical))
            {
                ColorBlend blend = new ColorBlend();
                blend.Positions = new float[] { 0f, 0.5f, 1f };
                blend.Colors = new Color[] { BgTop, BgMid, BgBottom };
                brush.InterpolationColors = blend;
                g.FillRectangle(brush, 0, 0, Width, Height);
            }
        }

        private static void DrawHeader(Graphics g, FontSet fonts, Snapshot snapshot, TimeSnapshot time)
        {
            const float x = 24;
            const float y = 16;
            DrawCard(g, x, y, 432, 130, Color.Transparent);

            WeatherSnapshot weather = snapshot.Weather;
            AlertSnapshot alert = snapshot.Alert;
            ForegroundAppSnapshot app = snapshot.ForegroundApp;

            DateTime now = DisplayNow();
            TimeSnapshot headerTime = ResolveHeaderTime(time, now);
            string date = headerTime.Date;
            string weekday = headerTime.Weekday;
            string clock = headerTime.Time;
            string weatherLine = FormatWeather(weather);
            string environmentLine = WeatherDetailLine(weather);
            string update = FormatSeconds(First(snapshot.Health == null ? null : snapshot.Health.RefreshIntervalSeconds, headerTime.UpdateIntervalSeconds));
            List<string> stripParts = new List<string>();
            stripParts.Add(Safe(alert == null ? null : alert.Message, "系统正常"));
            string foreground = ForegroundName(app);
            if (foreground != "--")
            {
                stripParts.Add("前台 " + foreground);
            }
            if (update != "--")
            {
                stripParts.Add("更新 " + update);
            }
            string strip = String.Join("  ·  ", stripParts.ToArray());

            DrawText(g, clock, fonts.Mono42Bold, Dark, x + 12, y + 18);
            DrawText(g, date + " " + weekday, fonts.Sans14Bold, Muted, x + 14, y + 72);
            DrawRightText(g, weatherLine, fonts.Sans15Bold, Dark, x + 180, y + 28, 228, 24);
            DrawRightText(g, environmentLine, fonts.Sans12Bold, Muted, x + 188, y + 58, 220, 18);

            DrawLine(g, x + 24, y + 102, x + 408, y + 102, GreenLine, 1.2f);
            FillRoundRect(g, x + 24, y + 103, 384, 20, 4, GreenSoft);
            DrawCenteredText(g, FitText(g, strip, fonts.Sans12Bold, 366), fonts.Sans12Bold, Hex("#116631"), x + 24, y + 106, 384, 16);
        }

        private static void DrawHardwareCard(Graphics g, FontSet fonts, float x, float y, string title, string model,
            double? usagePercent, string temperature, string power, string clockLabel, string clockValue,
            string voltageLabel, string voltageValue, Color accent, Color gradientStart, Color gradientEnd, double[] history)
        {
            double usageValue = Value(usagePercent);
            DrawCard(g, x, y, 432, 250, accent);
            DrawText(g, title, fonts.Title, Green, x + 24, y + 22);
            DrawText(g, FitText(g, model, fonts.Sans14Bold, 360), fonts.Sans14Bold, Dark, x + 24, y + 43);

            string usageText = FormatWholeOptional(usagePercent);
            Font usageFont = MeasureWidth(g, usageText, fonts.Mono78Bold) <= 136 ? fonts.Mono78Bold : fonts.Mono68Bold;
            DrawText(g, usageText, usageFont, accent, x + 24, y + 66);
            float usageWidth = MeasureWidth(g, usageText, usageFont);
            if (usagePercent.HasValue)
            {
                DrawText(g, "%", fonts.Sans24Bold, accent, x + 24 + usageWidth + 6, y + 78);
            }
            DrawText(g, title.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) ? "CPU 使用率" : "GPU 使用率", fonts.Sans12Bold, Muted, x + 24, y + 146);

            DrawMiniPanel(g, fonts, x + 186, y + 74, 104, 46, "温度", temperature, Dark);
            DrawMiniPanel(g, fonts, x + 296, y + 74, 112, 46, "功耗", power, Dark);
            DrawMiniPanel(g, fonts, x + 186, y + 126, 104, 46, clockLabel, clockValue, Dark);
            DrawMiniPanel(g, fonts, x + 296, y + 126, 112, 46, voltageLabel, voltageValue, Dark);

            DrawText(g, "负载历史 / 实时采样", fonts.Sans12Bold, Muted, x + 24, y + 181);
            DrawHistory(g, x + 24, y + 198, 384, 38, usageValue, history, accent);
        }

        private static void DrawFps(Graphics g, FontSet fonts, FpsSnapshot fps)
        {
            const float x = 24;
            const float y = 688;
            DrawCard(g, x, y, 432, 130, NetGreen);
            DrawText(g, "FPS / 帧率", fonts.Title, Green, x + 24, y + 18);

            string status = FpsStatus(fps);
            if (status != "active")
            {
                Color statusColor = status == "error" ? GpuPink : status == "stale" ? CpuBlue : Dark;
                DrawText(g, FpsStatusTitle(status), fonts.Sans20Bold, statusColor, x + 24, y + 48);
                DrawText(g, FitText(g, FpsStatusDetail(fps, status), fonts.Sans12, 384), fonts.Sans12, Muted, x + 24, y + 80);
                return;
            }

            string current = FormatWholeOptional(fps == null ? null : fps.Current);
            DrawText(g, current, fonts.Mono64Bold, Dark, x + 24, y + 32);
            DrawText(g, "FPS", fonts.Sans16Bold, Dark, x + 24 + MeasureWidth(g, current, fonts.Mono64Bold) + 10, y + 68);
            DrawRightText(g, "PresentMon · 活跃", fonts.Sans10Bold, NetGreen, x + 258, y + 18, 150, 14);

            DrawMiniPanel(g, fonts, x + 184, y + 44, 68, 54, "平均", FormatWholeOptional(fps == null ? null : fps.Average), Dark);
            DrawMiniPanel(g, fonts, x + 258, y + 44, 64, 54, "1%低", FormatWholeOptional(fps == null ? null : fps.Low1Percent), GpuPink);
            DrawMiniPanel(g, fonts, x + 328, y + 44, 80, 54, "帧时间", FormatMsCompact(fps == null ? null : fps.FrameTimeMs), Dark);
        }

        private static void DrawMemory(Graphics g, FontSet fonts, MemorySnapshot memory, GpuSnapshot gpu)
        {
            const float x = 24;
            const float y = 832;
            DrawCard(g, x, y, 432, 160, Hex("#0ea5e9"));
            DrawText(g, "RAM 内存 / VRAM 显存", fonts.Title, Green, x + 24, y + 18);

            double ramPct = Value(First(memory == null ? null : memory.RamUsagePercent, memory == null ? null : memory.UsedPercent));
            DrawText(g, "RAM 使用率", fonts.Sans13Bold, Dark, x + 24, y + 41);
            DrawText(g, FormatWhole(ramPct) + "%", fonts.Sans13Bold, CpuBlue, x + 106, y + 41);
            string ramDetails = FormatGb(First(memory == null ? null : memory.RamUsedGb, memory == null ? null : memory.UsedGb), "0.0") + " / " + FormatGb(First(memory == null ? null : memory.RamTotalGb, memory == null ? null : memory.TotalGb), "0.0");
            DrawRightText(g, ramDetails, fonts.Mono12Bold, Muted, x + 216, y + 42, 192, 16);
            DrawProgress(g, x + 24, y + 62, 384, 6, ramPct, CpuBlue, CpuBlue2, false);

            double vramPct = Value(First(memory == null ? null : memory.VramUsagePercent, PercentFromValues(memory == null ? null : memory.VramUsedGb, memory == null ? null : memory.VramTotalGb)));
            DrawText(g, "VRAM 使用率", fonts.Sans13Bold, Dark, x + 24, y + 81);
            DrawText(g, FormatWhole(vramPct) + "%", fonts.Sans13Bold, GpuPink, x + 114, y + 81);
            string vramClock = "显存频率 " + FormatGhz(First(gpu == null ? null : gpu.MemoryClockGhz, GhzFromMhz(gpu == null ? null : gpu.MemoryClockMhz)));
            DrawRightText(g, vramClock, fonts.Sans12, Muted, x + 216, y + 82, 192, 16);
            DrawProgress(g, x + 24, y + 102, 384, 6, vramPct, GpuPink, GpuPink2, false);

            double[] dimm = memory == null ? null : memory.ModuleTemperaturesCelsius;
            DrawMetricPill(g, fonts, x + 24, y + 122, 120, "主板 " + FormatTemperature(memory == null ? null : memory.MotherboardTemperatureCelsius), Hex("#ecfdf5"), Dark);
            DrawMetricPill(g, fonts, x + 150, y + 122, 120, "DIMM A " + FormatTemperature(ArrayValue(dimm, 0)), Hex("#e0f2fe"), CpuBlue);
            DrawMetricPill(g, fonts, x + 276, y + 122, 132, "DIMM B " + FormatTemperature(ArrayValue(dimm, 1)), Hex("#fce7f3"), GpuPink);
        }

        private static void DrawNetwork(Graphics g, FontSet fonts, NetworkSnapshot network)
        {
            const float x = 24;
            const float y = 1006;
            DrawCard(g, x, y, 432, 150, NetGreen);
            DrawText(g, "网络实时速率", fonts.Title, Green, x + 24, y + 17);

            DrawFittedText(g, "↓" + FormatRateCompact(First(network == null ? null : network.DownloadBytesPerSecond, network == null ? null : network.RxBytesPerSecond)), new Font[] { fonts.Sans30Bold, fonts.Sans28Bold, fonts.Sans24Bold, fonts.Sans20Bold }, NetGreen, x + 24, y + 39, 176);
            DrawText(g, "下载速度", fonts.Sans12Bold, Muted, x + 24, y + 72);

            DrawFittedText(g, "↑" + FormatRateCompact(First(network == null ? null : network.UploadBytesPerSecond, network == null ? null : network.TxBytesPerSecond)), new Font[] { fonts.Sans30Bold, fonts.Sans28Bold, fonts.Sans24Bold, fonts.Sans20Bold }, CpuBlue, x + 216, y + 39, 192);
            DrawText(g, "上传速度", fonts.Sans12Bold, Muted, x + 216, y + 72);

            DrawMetricPill(g, fonts, x + 24, y + 105, 90, "延迟 " + FormatMsCompact(network == null ? null : network.PingMs), Hex("#ecfdf5"), Dark);
            DrawMetricPill(g, fonts, x + 122, y + 105, 90, "抖动 " + FormatMsCompact(network == null ? null : network.JitterMs), Hex("#ecfdf5"), Muted);
            DrawMetricPill(g, fonts, x + 220, y + 105, 90, "丢包 " + FormatPercent(network == null ? null : network.PacketLossPercent), Hex("#e0f2fe"), CpuBlue);
            DrawMetricPill(g, fonts, x + 318, y + 105, 90, "DPC " + FormatPercentOneDecimal(network == null ? null : network.DpcPercent), Hex("#fce7f3"), GpuPink);
        }

        private static void DrawDisks(Graphics g, FontSet fonts, PhysicalDiskSnapshot[] physicalDisks, DiskSnapshot[] legacyDisks)
        {
            const float x = 24;
            const float y = 1170;
            DrawCard(g, x, y, 432, 390, DiskGreen);
            DrawText(g, "物理磁盘 / 实时 I/O", fonts.Title, Green, x + 24, y + 17);
            DrawRightText(g, "最多显示 4 块", fonts.Sans10Bold, Muted, x + 280, y + 18, 128, 14);

            PhysicalDiskSnapshot[] disks = ResolvePhysicalDisks(physicalDisks, legacyDisks);
            int count = disks == null ? 0 : Math.Min(4, disks.Length);
            if (count == 0)
            {
                DrawCenteredText(g, "无物理磁盘数据", fonts.Sans13Bold, Muted, x + 24, y + 180, 384, 24);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    float rowY = y + 48 + i * 78;
                    DrawPhysicalDiskRow(g, fonts, disks[i], i, x + 24, rowY);
                    if (i < count - 1)
                    {
                        DrawLine(g, x + 24, rowY + 68, x + 408, rowY + 68, GreenLine, 1.1f);
                    }
                }
            }
        }

        private static void DrawPhysicalDiskRow(Graphics g, FontSet fonts, PhysicalDiskSnapshot disk, int index, float x, float y)
        {
            if (disk == null)
            {
                disk = new PhysicalDiskSnapshot();
            }

            Color accent = index % 3 == 0 ? CpuBlue : index % 3 == 1 ? DiskGreen : GpuPink;
            DrawText(g, FitText(g, PhysicalDiskDisplayName(disk), fonts.Sans13Bold, 216), fonts.Sans13Bold, Dark, x, y + 1);
            DrawRightText(g, FitText(g, PhysicalDiskIdentity(disk), fonts.Sans11Bold, 160), fonts.Sans11Bold, Muted, x + 224, y + 3, 160, 16);

            string capacity = FormatPhysicalDiskCapacity(disk);
            DrawDiskMetricPill(g, fonts, x, y + 26, 80, PhysicalDiskVolumes(disk), Hex("#f2faf6"), Muted);
            DrawDiskMetricPill(g, fonts, x + 84, y + 26, 112, capacity, Hex("#ecfdf5"), accent);
            DrawDiskMetricPill(g, fonts, x + 200, y + 26, 60, "读" + FormatDiskRateCompact(disk.ReadBytesPerSecond), Hex("#e0f2fe"), CpuBlue);
            DrawDiskMetricPill(g, fonts, x + 264, y + 26, 60, "写" + FormatDiskRateCompact(disk.WriteBytesPerSecond), Hex("#fce7f3"), GpuPink);
            DrawDiskMetricPill(g, fonts, x + 328, y + 26, 56, "活动" + FormatPercent(disk.ActivityPercent), Hex("#dcfce7"), Green);
        }

        private static void DrawApps(Graphics g, FontSet fonts, ProcessSnapshot[] processes)
        {
            const float x = 24;
            const float y = 1574;
            DrawCard(g, x, y, 432, 330, Indigo);
            DrawText(g, "应用资源排行", fonts.Title, Green, x + 24, y + 18);

            for (int i = 0; i < 3; i++)
            {
                float rowY = y + 48 + i * 88;
                ProcessSnapshot process = processes != null && i < processes.Length ? processes[i] : null;
                DrawProcessRow(g, fonts, process, i, x + 24, rowY);
                if (i < 2)
                {
                    DrawLine(g, x + 24, rowY + 78, x + 408, rowY + 78, GreenLine, 1.2f);
                }
            }
        }

        private static void DrawProcessRow(Graphics g, FontSet fonts, ProcessSnapshot process, int index, float x, float y)
        {
            if (process == null)
            {
                process = new ProcessSnapshot { Name = "--" };
            }

            string name = Safe(process.Name, "--");
            DrawProcessName(g, fonts, name, "", x, y + 10, 225);
            DrawMetricBox(g, fonts, x + 240, y + 10, 68, "CPU", FormatProcessPercent(process.CpuPercent), Hex("#e0f2fe"), Hex("#0369a1"));
            DrawMetricBox(g, fonts, x + 316, y + 10, 68, "RAM", FormatProcessMemory(process), Hex("#dcfce7"), Green);
        }

        private static int DrawProcessName(Graphics g, FontSet fonts, string name, string description, float x, float y, float maxWidth)
        {
            if (MeasureWidth(g, name, fonts.Sans16Bold) > maxWidth)
            {
                string[] lines = WrapTextWithoutEllipsis(g, name, fonts.Sans16Bold, maxWidth, 2);
                DrawText(g, lines.Length > 0 ? lines[0] : "", fonts.Sans16Bold, Dark, x, y + 3);
                if (lines.Length > 1)
                {
                    DrawText(g, lines[1], fonts.Sans16Bold, Dark, x, y + 25);
                    return 2;
                }
                return 1;
            }

            DrawText(g, name, fonts.Sans16Bold, Dark, x, y + 3);
            return 1;
        }

        private static void DrawMiniPanel(Graphics g, FontSet fonts, float x, float y, float width, float height, string label, string value, Color valueColor)
        {
            FillRoundRect(g, x, y, width, height, 6, MiniFill);
            DrawRoundRect(g, x, y, width, height, 6, MiniBorder, 0.8f);
            DrawText(g, label, fonts.Sans12Bold, Muted, x + 12, y + 8);
            DrawFittedText(g, value, new Font[] { fonts.Mono16Bold, fonts.Mono14Bold }, valueColor, x + 12, y + 27, width - 20);
        }

        private static void DrawMetricPill(Graphics g, FontSet fonts, float x, float y, float width, string text, Color fill, Color color)
        {
            FillRoundRect(g, x, y, width, 22, 5, fill);
            DrawCenteredText(g, FitText(g, text, fonts.Mono11Bold, width - 10), fonts.Mono11Bold, color, x, y + 5, width, 13);
        }

        private static void DrawDiskMetricPill(Graphics g, FontSet fonts, float x, float y, float width, string text, Color fill, Color color)
        {
            FillRoundRect(g, x, y, width, 22, 5, fill);
            DrawCenteredText(g, FitText(g, text, fonts.Mono10Bold, width - 8), fonts.Mono10Bold, color, x, y + 5, width, 13);
        }

        private static void DrawMetricBox(Graphics g, FontSet fonts, float x, float y, float width, string label, string value, Color fill, Color color)
        {
            FillRoundRect(g, x, y, width, 42, 6, fill);
            DrawCenteredText(g, label, fonts.Sans10Bold, color, x, y + 7, width, 12);
            DrawCenteredText(g, FitText(g, value, fonts.Mono14Bold, width - 10), fonts.Mono14Bold, color, x, y + 24, width, 14);
        }

        private static void DrawCard(Graphics g, float x, float y, float width, float height, Color accent)
        {
            using (GraphicsPath shadow = CreateRoundRect(x, y + 4, width, height, 14))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(12, 2, 44, 30)))
            {
                g.FillPath(brush, shadow);
            }

            FillRoundRect(g, x, y, width, height, 14, CardFill);
            DrawRoundRect(g, x, y, width, height, 14, CardBorder, 1f);
            if (accent.A > 0)
            {
                FillRoundRect(g, x, y, 4, height, 2, accent);
            }
        }

        private static void DrawProgress(Graphics g, float x, float y, float width, float height, double percent, Color start, Color end, bool showZeroStub)
        {
            FillRoundRect(g, x, y, width, height, height / 2f, Hex("#f0fdf4"));
            double clamped = Clamp(percent, 0, 100);
            float fillWidth = (float)(width * clamped / 100.0);
            if (fillWidth <= 0 && showZeroStub)
            {
                fillWidth = 2;
                start = Hex("#cbd5e1");
                end = Hex("#cbd5e1");
            }
            if (fillWidth > 0)
            {
                if (fillWidth < 3)
                {
                    fillWidth = 3;
                }
                using (GraphicsPath path = CreateRoundRect(x, y, Math.Min(width, fillWidth), height, height / 2f))
                using (LinearGradientBrush brush = new LinearGradientBrush(new RectangleF(x, y, Math.Max(1, fillWidth), height), start, end, LinearGradientMode.Horizontal))
                {
                    g.FillPath(brush, path);
                }
            }
        }

        private static void DrawHistory(Graphics g, float x, float y, float width, float height, double fallbackPercent, double[] history, Color color)
        {
            double[] values = history;
            if (values == null || values.Length < 2)
            {
                values = new double[] { fallbackPercent, fallbackPercent, fallbackPercent, fallbackPercent };
            }

            PointF[] points = new PointF[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                float px = x + width * i / Math.Max(1, values.Length - 1);
                float py = y + height - (float)(height * Clamp(values[i], 0, 100) / 100.0);
                points[i] = new PointF(px, py);
            }

            using (GraphicsPath fill = new GraphicsPath())
            using (GraphicsPath line = new GraphicsPath())
            using (SolidBrush areaBrush = new SolidBrush(Color.FromArgb(26, color)))
            using (Pen pen = new Pen(color, 1.8f))
            {
                if (points.Length >= 3)
                {
                    fill.AddCurve(points, 0.45f);
                    line.AddCurve(points, 0.45f);
                }
                else
                {
                    fill.AddLines(points);
                    line.AddLines(points);
                }
                fill.AddLine(points[points.Length - 1].X, y + height, x, y + height);
                fill.CloseFigure();
                g.FillPath(areaBrush, fill);
                g.DrawPath(pen, line);
            }
        }

        private static void DrawDownArrow(Graphics g, float x, float y, Color color)
        {
            using (Pen pen = new Pen(color, 2.5f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, x, y, x, y + 16);
                g.DrawLine(pen, x - 5, y + 11, x, y + 16);
                g.DrawLine(pen, x + 5, y + 11, x, y + 16);
            }
        }

        private static void DrawUpArrow(Graphics g, float x, float y, Color color)
        {
            using (Pen pen = new Pen(color, 2.5f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, x, y + 16, x, y);
                g.DrawLine(pen, x - 5, y + 5, x, y);
                g.DrawLine(pen, x + 5, y + 5, x, y);
            }
        }

        private static void FillRoundRect(Graphics g, float x, float y, float width, float height, float radius, Color color)
        {
            using (GraphicsPath path = CreateRoundRect(x, y, width, height, radius))
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillPath(brush, path);
            }
        }

        private static void DrawRoundRect(Graphics g, float x, float y, float width, float height, float radius, Color color, float strokeWidth)
        {
            using (GraphicsPath path = CreateRoundRect(x, y, width, height, radius))
            using (Pen pen = new Pen(color, strokeWidth))
            {
                g.DrawPath(pen, path);
            }
        }

        private static GraphicsPath CreateRoundRect(float x, float y, float width, float height, float radius)
        {
            float diameter = Math.Max(0, Math.Min(radius * 2f, Math.Min(width, height)));
            GraphicsPath path = new GraphicsPath();
            if (diameter <= 0)
            {
                path.AddRectangle(new RectangleF(x, y, width, height));
                path.CloseFigure();
                return path;
            }

            RectangleF arc = new RectangleF(x, y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = x + width - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = y + height - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = x;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawText(Graphics g, string text, Font font, Color color, float x, float y)
        {
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.DrawString(Safe(text, ""), font, brush, x, y, StringFormat.GenericTypographic);
            }
        }

        private static void DrawFittedText(Graphics g, string text, Font[] fonts, Color color, float x, float y, float maxWidth)
        {
            Font selected = fonts[fonts.Length - 1];
            for (int i = 0; i < fonts.Length; i++)
            {
                if (MeasureWidth(g, text, fonts[i]) <= maxWidth)
                {
                    selected = fonts[i];
                    break;
                }
            }
            DrawText(g, FitText(g, text, selected, maxWidth), selected, color, x, y);
        }

        private static void DrawCenteredText(Graphics g, string text, Font font, Color color, float x, float y, float width, float height)
        {
            using (StringFormat format = new StringFormat())
            using (SolidBrush brush = new SolidBrush(color))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(Safe(text, ""), font, brush, new RectangleF(x, y, width, height), format);
            }
        }

        private static void DrawRightText(Graphics g, string text, Font font, Color color, float x, float y, float width, float height)
        {
            using (StringFormat format = new StringFormat())
            using (SolidBrush brush = new SolidBrush(color))
            {
                format.Alignment = StringAlignment.Far;
                format.LineAlignment = StringAlignment.Near;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(Safe(text, ""), font, brush, new RectangleF(x, y, width, height), format);
            }
        }

        private static void DrawLine(Graphics g, float x1, float y1, float x2, float y2, Color color, float width)
        {
            using (Pen pen = new Pen(color, width))
            {
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        private static void DrawDashedLine(Graphics g, float x1, float y1, float x2, float y2, Color color)
        {
            using (Pen pen = new Pen(color, 1f))
            {
                pen.DashPattern = new float[] { 3f, 3f };
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        private static void FillEllipse(Graphics g, float x, float y, float width, float height, Color color)
        {
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, x, y, width, height);
            }
        }

        private static string FitText(Graphics g, string text, Font font, float maxWidth)
        {
            text = Safe(text, "");
            if (maxWidth <= 0)
            {
                return "";
            }
            if (MeasureWidth(g, text, font) <= maxWidth)
            {
                return text;
            }

            const string ellipsis = "...";
            if (MeasureWidth(g, ellipsis, font) > maxWidth)
            {
                return "";
            }

            int low = 0;
            int high = text.Length;
            int best = 0;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                string candidate = text.Substring(0, mid).TrimEnd() + ellipsis;
                if (MeasureWidth(g, candidate, font) <= maxWidth)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return text.Substring(0, best).TrimEnd() + ellipsis;
        }

        private static string[] WrapText(Graphics g, string text, Font font, float maxWidth, int maxLines)
        {
            List<string> lines = new List<string>();
            string remaining = Safe(text, "").Trim();
            while (remaining.Length > 0 && lines.Count < maxLines)
            {
                if (MeasureWidth(g, remaining, font) <= maxWidth)
                {
                    lines.Add(remaining);
                    break;
                }

                if (lines.Count == maxLines - 1)
                {
                    lines.Add(FitText(g, remaining, font, maxWidth));
                    break;
                }

                int best = 1;
                for (int i = 1; i <= remaining.Length; i++)
                {
                    if (MeasureWidth(g, remaining.Substring(0, i), font) > maxWidth)
                    {
                        break;
                    }
                    best = i;
                }

                int breakAt = PreferredBreak(remaining, best);
                if (breakAt <= 0)
                {
                    breakAt = best;
                }

                lines.Add(remaining.Substring(0, breakAt).TrimEnd('-', '_', '.', ' '));
                remaining = remaining.Substring(Math.Min(remaining.Length, breakAt)).TrimStart('-', '_', '.', ' ');
            }

            if (lines.Count == 0)
            {
                lines.Add("");
            }
            return lines.ToArray();
        }

        private static string[] WrapTextWithoutEllipsis(Graphics g, string text, Font font, float maxWidth, int maxLines)
        {
            List<string> lines = new List<string>();
            string remaining = Safe(text, "").Trim();
            while (remaining.Length > 0 && lines.Count < maxLines)
            {
                if (MeasureWidth(g, remaining, font) <= maxWidth)
                {
                    lines.Add(remaining);
                    break;
                }

                int best = 1;
                for (int i = 1; i <= remaining.Length; i++)
                {
                    if (MeasureWidth(g, remaining.Substring(0, i), font) > maxWidth)
                    {
                        break;
                    }
                    best = i;
                }

                int breakAt = PreferredBreak(remaining, best);
                if (breakAt <= 0)
                {
                    breakAt = best;
                }

                lines.Add(remaining.Substring(0, Math.Min(remaining.Length, breakAt)).TrimEnd('-', '_', '.', ' '));
                remaining = remaining.Substring(Math.Min(remaining.Length, breakAt)).TrimStart('-', '_', '.', ' ');
            }

            if (lines.Count == 0)
            {
                lines.Add("");
            }
            return lines.ToArray();
        }

        private static int PreferredBreak(string text, int maxIndex)
        {
            int limit = Math.Min(maxIndex, text.Length - 1);
            for (int i = limit; i >= Math.Max(1, limit - 12); i--)
            {
                char ch = text[i];
                if (ch == ' ' || ch == '-' || ch == '_' || ch == '.' || ch == '/')
                {
                    return i;
                }
            }
            return maxIndex;
        }

        private static float MeasureWidth(Graphics g, string text, Font font)
        {
            return g.MeasureString(Safe(text, ""), font, 2000, StringFormat.GenericTypographic).Width;
        }

        internal static TimeSnapshot ResolveHeaderTimeForTest(TimeSnapshot time, DateTime now)
        {
            return ResolveHeaderTime(time, now);
        }

        private static TimeSnapshot ResolveHeaderTime(TimeSnapshot time, DateTime now)
        {
            return new TimeSnapshot
            {
                Date = now.ToString("yyyy-MM-dd", Invariant),
                Weekday = ChineseWeekday(now),
                Time = now.ToString("HH:mm:ss", Invariant),
                Timestamp = time == null ? null : time.Timestamp,
                UpdateIntervalSeconds = time == null ? null : time.UpdateIntervalSeconds
            };
        }

        private static TimeSnapshot CoerceTime(object value)
        {
            TimeSnapshot typed = value as TimeSnapshot;
            if (typed != null)
            {
                return typed;
            }

            string raw = value as string;
            if (!String.IsNullOrWhiteSpace(raw))
            {
                DateTimeOffset parsed;
                if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
                {
                    DateTime local = parsed.DateTime;
                    return new TimeSnapshot
                    {
                        Date = local.ToString("yyyy-MM-dd", Invariant),
                        Weekday = ChineseWeekday(local),
                        Time = local.ToString("HH:mm:ss", Invariant),
                        Timestamp = raw
                    };
                }

                return new TimeSnapshot
                {
                    Date = DisplayNow().ToString("yyyy-MM-dd", Invariant),
                    Weekday = ChineseWeekday(DisplayNow()),
                    Time = raw,
                    Timestamp = raw
                };
            }

            return null;
        }

        private static string GpuDisplayName(GpuSnapshot gpu)
        {
            if (gpu == null)
            {
                return "GPU";
            }
            return Safe(!String.IsNullOrWhiteSpace(gpu.Model) ? gpu.Model : gpu.Name, "GPU");
        }

        private static string ForegroundName(ForegroundAppSnapshot app)
        {
            if (app == null)
            {
                return "--";
            }
            return Safe(!String.IsNullOrWhiteSpace(app.Name) ? app.Name : app.ProcessName, "--");
        }

        private static string NormalizeDrive(string drive)
        {
            string value = Safe(drive, "--").Trim();
            if (value.EndsWith("\\", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 1);
            }
            return value;
        }

        private static string FormatWeather(WeatherSnapshot weather)
        {
            string city = Safe(weather == null ? null : weather.City, "--");
            bool hasTemp = !String.IsNullOrWhiteSpace(weather == null ? null : weather.TemperatureText)
                || First(weather == null ? null : weather.TemperatureCelsius, weather == null ? null : weather.TemperatureC).HasValue;
            string temp = !String.IsNullOrWhiteSpace(weather == null ? null : weather.TemperatureText)
                ? weather.TemperatureText
                : FormatTemperature(First(weather == null ? null : weather.TemperatureCelsius, weather == null ? null : weather.TemperatureC));
            string condition = Safe(!String.IsNullOrWhiteSpace(weather == null ? null : weather.Condition) ? weather.Condition : (weather == null ? null : weather.Summary), "--");
            if (!hasTemp && condition == "--")
            {
                return city == "--" ? "天气暂无数据" : city + " 天气暂无";
            }
            return city + " " + temp + " " + condition;
        }

        private static string WeatherDetailLine(WeatherSnapshot weather)
        {
            List<string> parts = new List<string>();
            parts.Add("AQI " + (weather != null && weather.Aqi.HasValue ? FormatInt(weather.Aqi) : "--"));
            if (weather != null && weather.HumidityPercent.HasValue)
            {
                parts.Add("湿度 " + FormatPercent(weather.HumidityPercent));
            }
            return parts.Count == 0 ? "" : String.Join(" · ", parts.ToArray());
        }

        private static string NetworkDetailLine(NetworkSnapshot network)
        {
            if (network == null)
            {
                return "延迟采样中";
            }
            if (network.JitterMs.HasValue && network.PacketLossPercent.HasValue)
            {
                return "抖" + FormatNumber(network.JitterMs, "0.#") + " 丢" + FormatPercent(network.PacketLossPercent);
            }
            if (network.JitterMs.HasValue)
            {
                return "抖" + FormatNumber(network.JitterMs, "0.#");
            }
            if (network.PacketLossPercent.HasValue)
            {
                return "丢" + FormatPercent(network.PacketLossPercent);
            }
            return "延迟采样中";
        }

        private static string FpsStatus(FpsSnapshot fps)
        {
            string status = fps == null ? "" : Safe(fps.Status, "").Trim().ToLowerInvariant();
            if (status == "active" || status == "idle" || status == "connecting" || status == "stale" || status == "error" || status == "disabled")
            {
                if (status == "active" && fps != null && !fps.Current.HasValue && !fps.Average.HasValue && !fps.Low1Percent.HasValue && !fps.FrameTimeMs.HasValue)
                {
                    return "connecting";
                }
                return status;
            }
            if (status == "warmup" || status == "starting")
            {
                return "connecting";
            }
            if (status == "failed" || status == "unavailable")
            {
                return "error";
            }
            return fps != null && (fps.Current.HasValue || fps.Average.HasValue || fps.Low1Percent.HasValue || fps.FrameTimeMs.HasValue)
                ? "active"
                : "idle";
        }

        private static string FpsStatusTitle(string status)
        {
            if (status == "active")
            {
                return "帧率采集中";
            }
            if (status == "connecting")
            {
                return "正在连接帧源";
            }
            if (status == "stale")
            {
                return "帧数据已过期";
            }
            if (status == "error")
            {
                return "帧率采集异常";
            }
            if (status == "disabled")
            {
                return "帧率采集未启用";
            }
            return "等待游戏帧";
        }

        private static string FpsStatusDetail(FpsSnapshot fps, string status)
        {
            if (status == "connecting")
            {
                return "PresentMon 正在建立采集连接";
            }
            if (status == "stale")
            {
                if (fps != null && fps.SampleAgeSeconds.HasValue)
                {
                    return "PresentMon 最近样本 " + FormatNumber(fps.SampleAgeSeconds, "0.#") + "s 前";
                }
                return "PresentMon 最近样本已失效";
            }
            if (status == "error")
            {
                string detail = fps == null ? null : fps.Detail;
                return String.IsNullOrWhiteSpace(detail) ? "PresentMon 无法读取帧数据" : "PresentMon · " + detail;
            }
            if (status == "disabled")
            {
                return "配置 TIMEAUDIT_DSN 后启用 PresentMon 数据";
            }
            return "PresentMon 正在等待游戏启动";
        }

        private static string FormatTemperature(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatNumber(value, "0") + "°C";
        }

        private static string FormatWatts(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatNumber(value, "0") + "W";
        }

        private static string FormatGhz(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatNumber(value, "0.##") + " GHz";
        }

        private static string FormatVoltage(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatNumber(value, "0.00") + " V";
        }

        private static string FormatMs(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatNumber(value, "0.#") + " ms";
        }

        private static string FormatMsCompact(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatNumber(value, "0.#") + "ms";
        }

        private static string FormatGb(double? value, string pattern)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatNumber(value, pattern) + " GB";
        }

        private static string FormatMemoryGb(double? value)
        {
            return FormatNumber(value, "0.#") + "G";
        }

        private static string FormatProcessMemory(ProcessSnapshot process)
        {
            if (process == null)
            {
                return "--";
            }
            if (process.MemoryGb.HasValue)
            {
                return FormatMemoryGb(process.MemoryGb);
            }
            if (process.MemoryMb.HasValue)
            {
                return FormatMemoryGb(process.MemoryMb.Value / 1024.0);
            }
            return "--";
        }

        private static string FormatDiskFree(DiskSnapshot disk)
        {
            if (disk == null)
            {
                return "--";
            }
            if (!String.IsNullOrWhiteSpace(disk.FreeText))
            {
                return disk.FreeText;
            }
            if (disk.FreeGb.HasValue)
            {
                if (disk.FreeGb.Value >= 1024)
                {
                    return (disk.FreeGb.Value / 1024.0).ToString("0.#", Invariant) + " TB 可用";
                }
                return FormatNumber(disk.FreeGb, "0") + " GB 可用";
            }
            return "--";
        }

        private static PhysicalDiskSnapshot[] ResolvePhysicalDisks(PhysicalDiskSnapshot[] physicalDisks, DiskSnapshot[] legacyDisks)
        {
            if (physicalDisks != null && physicalDisks.Length > 0)
            {
                return physicalDisks;
            }
            // Logical volumes cannot safely preserve the physical-disk filtering contract:
            // they can duplicate a multi-volume SSD and reintroduce RECOVER/small USB drives.
            return new PhysicalDiskSnapshot[0];
        }

        private static string PhysicalDiskIdentity(PhysicalDiskSnapshot disk)
        {
            List<string> parts = new List<string>();
            if (disk != null && !String.IsNullOrWhiteSpace(disk.BusType))
            {
                parts.Add(disk.BusType.Trim());
            }
            if (
                disk != null
                && !String.IsNullOrWhiteSpace(disk.MediaType)
                && !String.Equals(disk.MediaType.Trim(), "Unspecified", StringComparison.OrdinalIgnoreCase)
                && !String.Equals(disk.MediaType.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase)
            )
            {
                parts.Add(disk.MediaType.Trim());
            }
            if (disk != null && disk.TemperatureCelsius.HasValue)
            {
                parts.Add(FormatTemperature(disk.TemperatureCelsius));
            }
            return parts.Count == 0 ? "总线 -- · 温度 --" : String.Join(" · ", parts.ToArray());
        }

        private static string PhysicalDiskDisplayName(PhysicalDiskSnapshot disk)
        {
            string model = Safe(disk == null ? null : disk.Model, "未知磁盘").Trim();
            string[] genericSuffixes = new string[]
            {
                " SCSI Disk Device",
                " ATA Device",
                " USB Device"
            };
            foreach (string suffix in genericSuffixes)
            {
                if (model.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return model.Substring(0, model.Length - suffix.Length).Trim();
                }
            }
            return model;
        }

        private static string PhysicalDiskVolumes(PhysicalDiskSnapshot disk)
        {
            string[] volumes = disk == null || disk.VolumeDrives == null || disk.VolumeDrives.Length == 0
                ? (disk == null ? null : disk.Volumes)
                : disk.VolumeDrives;
            if (volumes == null || volumes.Length == 0)
            {
                return "卷 --";
            }
            string[] normalized = new string[volumes.Length];
            for (int i = 0; i < volumes.Length; i++)
            {
                normalized[i] = NormalizeDrive(volumes[i]);
            }
            return "卷 " + String.Join("/", normalized);
        }

        private static string FormatPhysicalDiskCapacity(PhysicalDiskSnapshot disk)
        {
            if (disk == null)
            {
                return "--";
            }
            string capacity = FormatCapacityCompact(First(disk.CapacityGb, disk.TotalGb));
            string used = FormatPercent(disk.UsedPercent);
            string free = FormatCapacityCompact(disk.FreeGb);
            return capacity + "·" + used + "·余" + free;
        }

        private static string FormatCapacityCompact(double? valueGb)
        {
            if (!valueGb.HasValue)
            {
                return "--";
            }
            double value = Math.Max(0, valueGb.Value);
            if (value >= 1024)
            {
                return (value / 1024.0).ToString(value >= 10240 ? "0.#" : "0.0", Invariant) + "T";
            }
            return value.ToString(value >= 100 ? "0" : "0.#", Invariant) + "G";
        }

        private static string FormatRate(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue)
            {
                return "--";
            }

            double value = Math.Max(0, bytesPerSecond.Value);
            if (value < 1024)
            {
                return value.ToString("0", Invariant) + " B/s";
            }
            value = value / 1024.0;
            if (value < 1024)
            {
                return value.ToString("0", Invariant) + " KB/s";
            }
            value = value / 1024.0;
            if (value < 1024)
            {
                return value.ToString("0.#", Invariant) + " MB/s";
            }
            return (value / 1024.0).ToString("0.#", Invariant) + " GB/s";
        }

        private static string FormatRateCompact(double? bytesPerSecond)
        {
            return FormatRate(bytesPerSecond).Replace(" ", "");
        }

        private static string FormatDiskRateCompact(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue)
            {
                return "--";
            }
            double value = Math.Max(0, bytesPerSecond.Value);
            if (value < 1024)
            {
                return value.ToString("0", Invariant) + "B/s";
            }
            value /= 1024.0;
            if (value < 1024)
            {
                return value.ToString("0", Invariant) + "K/s";
            }
            value /= 1024.0;
            if (value < 1024)
            {
                return value.ToString(value < 10 ? "0.#" : "0", Invariant) + "M/s";
            }
            value /= 1024.0;
            return value.ToString(value < 10 ? "0.#" : "0", Invariant) + "G/s";
        }

        private static string FormatPercent(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return FormatWhole(Value(value)) + "%";
        }

        private static string FormatPercentOneDecimal(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return Clamp(value.Value, 0, 100).ToString("0.0", Invariant) + "%";
        }

        private static string FormatPaddedPercent(double? value)
        {
            int rounded = (int)Math.Round(Clamp(Value(value), 0, 100));
            return rounded.ToString("00", Invariant) + "%";
        }

        private static string FormatProcessPercent(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            int rounded = (int)Math.Round(Clamp(Value(value), 0, 100));
            return rounded.ToString("00", Invariant) + "%";
        }

        private static string FormatWhole(double value)
        {
            return ((int)Math.Round(value)).ToString("0", Invariant);
        }

        private static string FormatWholeOptional(double? value)
        {
            if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value))
            {
                return "--";
            }
            return FormatWhole(value.Value);
        }

        private static string FormatInt(int? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return value.Value.ToString("0", Invariant);
        }

        private static string FitPlain(string value, int maxLength)
        {
            string text = Safe(value, "");
            if (maxLength <= 0 || text.Length <= maxLength)
            {
                return text;
            }
            return text.Substring(0, maxLength);
        }

        private static string FormatNumber(double? value, string pattern)
        {
            if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value))
            {
                return "--";
            }
            return value.Value.ToString(pattern, Invariant);
        }

        private static string FormatSeconds(double? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return value.Value.ToString("0.#", Invariant) + "s";
        }

        private static double? PercentFromValues(double? used, double? total)
        {
            if (!used.HasValue || !total.HasValue || total.Value <= 0)
            {
                return null;
            }
            return used.Value * 100.0 / total.Value;
        }

        private static double? GhzFromMhz(double? value)
        {
            if (!value.HasValue)
            {
                return null;
            }
            return value.Value / 1000.0;
        }

        private static double? First(double? first, double? second)
        {
            return first.HasValue ? first : second;
        }

        private static double? ArrayValue(double[] values, int index)
        {
            return values != null && index >= 0 && index < values.Length ? (double?)values[index] : null;
        }

        private static double Value(double? value)
        {
            if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value))
            {
                return 0;
            }
            return value.Value;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }

        private static string Safe(string value, string fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string ChineseWeekday(DateTime date)
        {
            string[] names = new string[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return names[(int)date.DayOfWeek];
        }

        private static DateTime DisplayNow()
        {
            try
            {
                TimeZoneInfo china = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
                return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, china).DateTime;
            }
            catch (TimeZoneNotFoundException)
            {
                return DateTime.UtcNow.AddHours(8);
            }
            catch (InvalidTimeZoneException)
            {
                return DateTime.UtcNow.AddHours(8);
            }
        }

        private static Color Hex(string value)
        {
            return ColorTranslator.FromHtml(value);
        }

        private sealed class FontSet : IDisposable
        {
            public readonly Font Sans8;
            public readonly Font Sans8Bold;
            public readonly Font Sans9;
            public readonly Font Sans9Bold;
            public readonly Font Sans10Bold;
            public readonly Font Sans11;
            public readonly Font Sans11Bold;
            public readonly Font Sans12;
            public readonly Font Sans12Bold;
            public readonly Font Sans13Bold;
            public readonly Font Sans14Bold;
            public readonly Font Sans15Bold;
            public readonly Font Sans16Bold;
            public readonly Font Sans18Bold;
            public readonly Font Sans20Bold;
            public readonly Font Sans24Bold;
            public readonly Font Sans28Bold;
            public readonly Font Sans30Bold;
            public readonly Font Title;
            public readonly Font Mono10Bold;
            public readonly Font Mono11Bold;
            public readonly Font Mono12Bold;
            public readonly Font Mono13Bold;
            public readonly Font Mono14Bold;
            public readonly Font Mono15Bold;
            public readonly Font Mono16Bold;
            public readonly Font Mono18Bold;
            public readonly Font Mono40Bold;
            public readonly Font Mono42Bold;
            public readonly Font Mono44Bold;
            public readonly Font Mono54Bold;
            public readonly Font Mono60Bold;
            public readonly Font Mono64Bold;
            public readonly Font Mono68Bold;
            public readonly Font Mono78Bold;

            private readonly Font[] all;

            public FontSet()
            {
                Sans8 = Sans(8, FontStyle.Regular);
                Sans8Bold = Sans(8, FontStyle.Bold);
                Sans9 = Sans(9, FontStyle.Regular);
                Sans9Bold = Sans(9, FontStyle.Bold);
                Sans10Bold = Sans(10, FontStyle.Bold);
                Sans11 = Sans(11, FontStyle.Regular);
                Sans11Bold = Sans(11, FontStyle.Bold);
                Sans12 = Sans(12, FontStyle.Regular);
                Sans12Bold = Sans(12, FontStyle.Bold);
                Sans13Bold = Sans(13, FontStyle.Bold);
                Sans14Bold = Sans(14, FontStyle.Bold);
                Sans15Bold = Sans(15, FontStyle.Bold);
                Sans16Bold = Sans(16, FontStyle.Bold);
                Sans18Bold = Sans(18, FontStyle.Bold);
                Sans20Bold = Sans(20, FontStyle.Bold);
                Sans24Bold = Sans(24, FontStyle.Bold);
                Sans28Bold = Sans(28, FontStyle.Bold);
                Sans30Bold = Sans(30, FontStyle.Bold);
                Title = Sans(10, FontStyle.Bold);
                Mono10Bold = Mono(10, FontStyle.Bold);
                Mono11Bold = Mono(11, FontStyle.Bold);
                Mono12Bold = Mono(12, FontStyle.Bold);
                Mono13Bold = Mono(13, FontStyle.Bold);
                Mono14Bold = Mono(14, FontStyle.Bold);
                Mono15Bold = Mono(15, FontStyle.Bold);
                Mono16Bold = Mono(16, FontStyle.Bold);
                Mono18Bold = Mono(18, FontStyle.Bold);
                Mono40Bold = Mono(40, FontStyle.Bold);
                Mono42Bold = Mono(42, FontStyle.Bold);
                Mono44Bold = Mono(44, FontStyle.Bold);
                Mono54Bold = Mono(54, FontStyle.Bold);
                Mono60Bold = Mono(60, FontStyle.Bold);
                Mono64Bold = Mono(64, FontStyle.Bold);
                Mono68Bold = Mono(68, FontStyle.Bold);
                Mono78Bold = Mono(78, FontStyle.Bold);

                all = new Font[]
                {
                    Sans8, Sans8Bold, Sans9, Sans9Bold, Sans10Bold, Sans11, Sans11Bold, Sans12, Sans12Bold, Sans13Bold,
                    Sans14Bold, Sans15Bold, Sans16Bold, Sans18Bold, Sans20Bold, Sans24Bold,
                    Sans28Bold, Sans30Bold, Title, Mono10Bold, Mono11Bold, Mono12Bold, Mono13Bold,
                    Mono14Bold, Mono15Bold, Mono16Bold, Mono18Bold, Mono40Bold, Mono42Bold,
                    Mono44Bold, Mono54Bold, Mono60Bold, Mono64Bold, Mono68Bold, Mono78Bold
                };
            }

            public void Dispose()
            {
                for (int i = 0; i < all.Length; i++)
                {
                    all[i].Dispose();
                }
            }

            private static Font Sans(float size, FontStyle style)
            {
                return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);
            }

            private static Font Mono(float size, FontStyle style)
            {
                return new Font("Consolas", size, style, GraphicsUnit.Pixel);
            }
        }
    }
}
