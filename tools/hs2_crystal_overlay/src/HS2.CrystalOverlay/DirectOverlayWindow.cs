using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class DirectOverlayWindow : IDisposable
{
    private readonly nint hwnd;
    private bool disposed;

    internal DirectOverlayWindow()
    {
        var exStyle = (uint)(
            NativeMethods.WsExLayered |
            NativeMethods.WsExTransparent |
            NativeMethods.WsExToolWindow |
            NativeMethods.WsExNoActivate);
        hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            "STATIC",
            "HS2 ambient status",
            NativeMethods.WsPopup,
            0,
            0,
            1,
            1,
            0,
            0,
            0,
            0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException(
                "Could not create the ambient layered window.");
        }
    }

    internal void Render(
        IReadOnlyList<OverlayItem> items,
        PixelRect placement,
        DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (items.Count == 0)
        {
            _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
            return;
        }

        var drift = (now.Minute % 5) switch
        {
            1 => new Point(2, 0),
            2 => new Point(2, 2),
            3 => new Point(0, 2),
            4 => new Point(-2, 0),
            _ => Point.Empty,
        };
        var x = placement.X + drift.X;
        var y = placement.Y + drift.Y;

        using var bitmap = new Bitmap(
            placement.Width,
            placement.Height,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var top = 8f;
            foreach (var item in items.Take(2))
            {
                if (item.Request.Kind == OverlayKind.PhoneBattery)
                {
                    DrawPhoneBattery(graphics, item, top);
                    top += 112;
                }
                else
                {
                    DrawMinimalText(
                        graphics,
                        item.Request.Title,
                        new PointF(12, top),
                        (float)item.Policy.Typography.TitlePx,
                        FontStyle.Bold,
                        Color.White);
                    top += (float)item.Policy.Typography.TitlePx + 10;
                }
            }
        }

        var screenDc = NativeMethods.GetDC(0);
        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = NativeMethods.SelectObject(memoryDc, hBitmap);
        try
        {
            var destination = new NativeMethods.Point(x, y);
            var size = new NativeMethods.Size(
                placement.Width,
                placement.Height);
            var source = new NativeMethods.Point(0, 0);
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AcSrcAlpha,
            };
            if (!NativeMethods.UpdateLayeredWindow(
                    hwnd,
                    screenDc,
                    ref destination,
                    ref size,
                    memoryDc,
                    ref source,
                    0,
                    ref blend,
                    NativeMethods.UlwAlpha))
            {
                throw new InvalidOperationException(
                    "Could not update the ambient layered window.");
            }
        }
        finally
        {
            _ = NativeMethods.SelectObject(memoryDc, previous);
            _ = NativeMethods.DeleteObject(hBitmap);
            _ = NativeMethods.DeleteDC(memoryDc);
            _ = NativeMethods.ReleaseDC(0, screenDc);
        }

        _ = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            x,
            y,
            placement.Width,
            placement.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowNoActivate);
    }

    private static void DrawPhoneBattery(
        Graphics graphics,
        OverlayItem item,
        float top)
    {
        var match = Regex.Match(item.Request.Title, @"(?<value>\d{1,3})\s*%");
        var percent = match.Success &&
                      int.TryParse(match.Groups["value"].Value, out var value)
            ? Math.Clamp(value, 0, 100)
            : (int?)null;
        var accent = Color.FromArgb(244, 210, 253, 255);
        var outline = new Rectangle(12, (int)top + 22, 116, 62);
        using var outlinePen = new Pen(Color.FromArgb(238, 238, 253, 255), 3.2f);
        graphics.DrawRoundedRectangle(
            outlinePen,
            outline,
            new Size(15, 15));
        using var terminalBrush = new SolidBrush(
            Color.FromArgb(224, 238, 253, 255));
        graphics.FillRoundedRectangle(
            terminalBrush,
            new Rectangle(outline.Right + 6, outline.Top + 18, 10, 26),
            new Size(5, 5));

        if (percent is not null)
        {
            var fillWidth = Math.Max(
                5,
                (int)Math.Round(
                    (outline.Width - 8) * percent.Value / 100f));
            var fillRect = new Rectangle(
                outline.X + 4,
                outline.Y + 4,
                fillWidth,
                outline.Height - 8);
            using var fillBrush = new LinearGradientBrush(
                fillRect,
                Color.FromArgb(230, 255, 255, 255),
                Color.FromArgb(230, 119, 246, 255),
                LinearGradientMode.Horizontal);
            graphics.FillRoundedRectangle(
                fillBrush,
                fillRect,
                new Size(7, 7));
        }

        if (item.Request.Visual?.IsCharging == true)
        {
            var centerX = outline.Left + outline.Width / 2f;
            var centerY = outline.Top + outline.Height / 2f;
            var lightning = new[]
            {
                new PointF(centerX + 4, centerY - 24),
                new PointF(centerX - 14, centerY + 2),
                new PointF(centerX - 3, centerY + 2),
                new PointF(centerX - 8, centerY + 24),
                new PointF(centerX + 16, centerY - 7),
                new PointF(centerX + 4, centerY - 7),
            };
            using var lightningShadow = new SolidBrush(
                Color.FromArgb(190, 4, 45, 48));
            graphics.FillPolygon(lightningShadow, lightning);
        }

        DrawMinimalText(
            graphics,
            percent is null ? item.Request.Title : $"{percent}%",
            new PointF(164, top),
            78,
            FontStyle.Bold,
            accent);
    }

    private static void DrawMinimalText(
        Graphics graphics,
        string text,
        PointF location,
        float size,
        FontStyle style,
        Color fill)
    {
        using var font = new Font(
            "Microsoft YaHei UI",
            size,
            style,
            GraphicsUnit.Pixel);
        using var shadow = new SolidBrush(Color.FromArgb(105, 0, 9, 12));
        using var fillBrush = new SolidBrush(fill);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString(
            text,
            font,
            shadow,
            new PointF(location.X + 2, location.Y + 2),
            format);
        graphics.DrawString(text, font, fillBrush, location, format);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = NativeMethods.DestroyWindow(hwnd);
    }
}
