using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using HS2.CrystalOverlay.Core;

namespace HS2_CrystalOverlay;

internal sealed class CrystalCardWindow : IDisposable
{
    private readonly nint hwnd;
    private Image? cachedArtwork;
    private string? cachedArtworkPath;
    private bool disposed;

    internal CrystalCardWindow(string caption = "HS2 crystal card")
    {
        var exStyle = (uint)(
            NativeMethods.WsExLayered |
            NativeMethods.WsExTransparent |
            NativeMethods.WsExToolWindow |
            NativeMethods.WsExNoActivate);
        hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            "STATIC",
            caption,
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
                "Could not create the crystal card window.");
        }
    }

    internal void Render(
        OverlayItem? item,
        OverlayPlacement placement)
    {
        Render(item, placement.Card, DateTimeOffset.Now);
    }

    internal void Render(
        OverlayItem? item,
        PixelRect maximum,
        DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (item is null)
        {
            _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
            return;
        }

        var target = ResolveCardRect(item, maximum);
        using var bitmap = new Bitmap(
            target.Width,
            target.Height,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            ConfigureGraphics(graphics);
            graphics.Clear(Color.Transparent);
            DrawCrystalSurface(
                graphics,
                target.Width,
                target.Height,
                item.Policy.VisualTier ==
                OverlayVisualTier.StackedNotification);
            if (item.Request.Kind is
                OverlayKind.MediaActive or OverlayKind.MediaTrackChange)
            {
                DrawMedia(graphics, item, target.Width, target.Height);
            }
            else if (item.Policy.VisualTier ==
                     OverlayVisualTier.StackedNotification)
            {
                DrawPhoneNotification(
                    graphics,
                    item,
                    target.Width,
                    target.Height,
                    now);
            }
            else
            {
                DrawEvent(graphics, item, target.Width, target.Height);
            }
        }

        Present(bitmap, target);
    }

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint =
            System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    }

    private static void DrawCrystalSurface(
        Graphics graphics,
        int width,
        int height,
        bool notificationSurface)
    {
        var outer = new RectangleF(1.5f, 1.5f, width - 3, height - 3);
        using var path = RoundedRectangle(outer, 34);
        var washStart = notificationSurface
            ? Color.FromArgb(18, 125, 255, 193)
            : Color.FromArgb(16, 255, 255, 255);
        var washEnd = notificationSurface
            ? Color.FromArgb(5, 157, 245, 205)
            : Color.FromArgb(5, 226, 250, 255);
        using var clearWash = new LinearGradientBrush(
            outer,
            washStart,
            washEnd,
            LinearGradientMode.ForwardDiagonal);
        graphics.FillPath(clearWash, path);

        using var edge = new Pen(
            notificationSurface
                ? Color.FromArgb(152, 186, 255, 222)
                : Color.FromArgb(142, 246, 255, 255),
            1.35f);
        graphics.DrawPath(edge, path);

        var inner = new RectangleF(4, 4, width - 8, height - 8);
        using var innerPath = RoundedRectangle(inner, 31);
        using var innerEdge = new Pen(
            notificationSurface
                ? Color.FromArgb(34, 98, 244, 178)
                : Color.FromArgb(26, 173, 247, 255),
            1);
        graphics.DrawPath(innerEdge, innerPath);

        using var highlight = new LinearGradientBrush(
            new RectangleF(48, 2, width - 96, 2),
            Color.Transparent,
            notificationSurface
                ? Color.FromArgb(184, 208, 255, 232)
                : Color.FromArgb(178, 255, 255, 255),
            LinearGradientMode.Horizontal);
        graphics.FillRectangle(highlight, 48, 2, width - 96, 1.3f);
    }

    private void DrawMedia(
        Graphics graphics,
        OverlayItem item,
        int width,
        int height)
    {
        var visual = item.Request.Visual;
        var expanded = item.Request.Kind == OverlayKind.MediaTrackChange;
        var padding = expanded ? 42f : 32f;
        var contentX = padding;
        var artworkSize = height - padding * 2;
        if (TryDrawArtwork(
                graphics,
                visual?.ArtworkPath,
                new RectangleF(padding, padding, artworkSize, artworkSize)))
        {
            contentX += artworkSize + 28;
        }

        var right = width - padding;
        var accent = ParseColor(
            visual?.AccentHex,
            Color.FromArgb(244, 137, 247, 255));
        var titleTop = expanded ? 18f : 12f;
        using var dot = new SolidBrush(accent);
        graphics.FillEllipse(
            dot,
            contentX,
            expanded ? 36 : 27,
            expanded ? 11 : 9,
            expanded ? 11 : 9);
        contentX += expanded ? 23 : 20;

        var titleRight = expanded ? right - 205 : right;
        DrawFittedText(
            graphics,
            item.Request.Title,
            new RectangleF(
                contentX,
                titleTop,
                Math.Max(100, titleRight - contentX),
                expanded ? 53 : 42),
            expanded ? 40 : 31,
            expanded ? 28 : 22,
            FontStyle.Bold,
            Color.FromArgb(252, 255, 255, 255));

        if (expanded)
        {
            DrawText(
                graphics,
                visual?.Eyebrow ??
                SourceName(item.Request.Source),
                new RectangleF(
                    right - 190,
                    25,
                    190,
                    40),
                19,
                FontStyle.Bold,
                Color.FromArgb(207, 206, 250, 255),
                StringAlignment.Far);
        }

        var subtitle = string.Join(
            "  ·  ",
            new[]
            {
                visual?.TranslatedTitle,
                visual?.Subtitle,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            DrawFittedText(
                graphics,
                subtitle,
                new RectangleF(
                    contentX,
                    expanded ? 68 : 48,
                    right - contentX,
                    expanded ? 35 : 28),
                expanded ? 24 : 20,
                expanded ? 18 : 16,
                FontStyle.Regular,
                Color.FromArgb(225, 224, 246, 249));
        }

        if (!string.IsNullOrWhiteSpace(item.Request.Body))
        {
            var hasTranslation =
                !string.IsNullOrWhiteSpace(visual?.SecondaryBody);
            DrawScrollingText(
                graphics,
                item.Request.Body!,
                new RectangleF(
                    contentX,
                    expanded ? 105 : 78,
                    right - contentX,
                    expanded
                        ? (hasTranslation ? 68 : 108)
                        : (hasTranslation ? 50 : 80)),
                expanded ? 47 : 39,
                FontStyle.Bold,
                Color.FromArgb(250, 255, 255, 255),
                visual?.MarqueeProgress ?? 0);
            if (hasTranslation)
            {
                DrawScrollingText(
                    graphics,
                    visual!.SecondaryBody!,
                    new RectangleF(
                        contentX,
                        expanded ? 174 : 126,
                        right - contentX,
                        expanded ? 43 : 32),
                    expanded ? 27 : 23,
                    FontStyle.Regular,
                    Color.FromArgb(230, 213, 247, 250),
                    visual.MarqueeProgress ?? 0);
            }
        }

        var progressY = height - (expanded ? 39 : 36);
        var metaWidth = string.IsNullOrWhiteSpace(visual?.Meta)
            ? 0f
            : Math.Min(300, width * 0.31f);
        var progressRight = right - metaWidth - (metaWidth > 0 ? 24 : 0);
        using var track = new Pen(Color.FromArgb(54, 224, 252, 255), 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(track, contentX, progressY, progressRight, progressY);
        if (visual?.Progress is not null)
        {
            var ratio = (float)Math.Clamp(visual.Progress.Value, 0, 1);
            using var progress = new Pen(accent, 3.1f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawLine(
                progress,
                contentX,
                progressY,
                contentX + (progressRight - contentX) * ratio,
                progressY);
        }

        if (metaWidth > 0)
        {
            DrawText(
                graphics,
                visual!.Meta!,
                new RectangleF(
                    right - metaWidth,
                    progressY - 22,
                    metaWidth,
                    38),
                expanded ? 21 : 19,
                FontStyle.Regular,
                Color.FromArgb(220, 230, 244, 247),
                StringAlignment.Far);
        }
    }

    private static void DrawPhoneNotification(
        Graphics graphics,
        OverlayItem item,
        int width,
        int height,
        DateTimeOffset now)
    {
        var visual = item.Request.Visual;
        var narrow = width < 900;
        var padding = narrow ? 32f : 42f;
        var titleSize = narrow ? 47f : 56f;
        var bodySize = narrow ? 39f : 44f;
        var headerHeight = narrow ? 58f : 64f;
        var accent = ParseColor(
            visual?.AccentHex,
            Color.FromArgb(244, 112, 240, 178));

        using var dot = new SolidBrush(accent);
        graphics.FillEllipse(dot, padding, 28, 10, 10);
        DrawText(
            graphics,
            visual?.Eyebrow ?? KindLabel(item.Request.Kind),
            new RectangleF(
                padding + 22,
                12,
                width * 0.52f,
                48),
            narrow ? 21 : 24,
            FontStyle.Bold,
            Color.FromArgb(238, 122, 245, 184));
        DrawFittedText(
            graphics,
            visual?.Subtitle ?? SourceName(item.Request.Source),
            new RectangleF(
                width * 0.54f,
                12,
                width - width * 0.54f - padding,
                48),
            narrow ? 20 : 23,
            17,
            FontStyle.Regular,
            Color.FromArgb(220, 169, 238, 204));

        var viewport = new RectangleF(
            padding,
            headerHeight,
            width - padding * 2,
            height - headerHeight - 24);
        var titleHeight = MeasureWrappedText(
            graphics,
            item.Request.Title,
            viewport.Width,
            titleSize,
            FontStyle.Bold);
        var bodyHeight = string.IsNullOrWhiteSpace(item.Request.Body)
            ? 0
            : MeasureWrappedText(
                graphics,
                item.Request.Body!,
                viewport.Width,
                bodySize,
                FontStyle.Regular);
        var contentHeight = titleHeight +
            (bodyHeight > 0 ? 14 + bodyHeight : 0);
        var overflow = Math.Max(0, contentHeight - viewport.Height);
        var scrollProgress = NotificationScrollProgress(item, now);
        var offset = overflow <= 0
            ? 0
            : overflow * SmoothHeldProgress(scrollProgress);

        var state = graphics.Save();
        graphics.SetClip(viewport);
        var y = viewport.Top - offset;
        DrawWrappedText(
            graphics,
            item.Request.Title,
            new RectangleF(
                viewport.Left,
                y,
                viewport.Width,
                titleHeight + 4),
            titleSize,
            FontStyle.Bold,
            Color.FromArgb(252, 225, 255, 239));
        y += titleHeight + 14;
        if (bodyHeight > 0)
        {
            DrawWrappedText(
                graphics,
                item.Request.Body!,
                new RectangleF(
                    viewport.Left,
                    y,
                    viewport.Width,
                    bodyHeight + 4),
                bodySize,
                FontStyle.Regular,
                Color.FromArgb(242, 174, 247, 211));
        }

        graphics.Restore(state);
    }

    private static void DrawEvent(
        Graphics graphics,
        OverlayItem item,
        int width,
        int height)
    {
        var visual = item.Request.Visual;
        var compact = item.Request.Kind is
            OverlayKind.GameActive or
            OverlayKind.ImportantTask or
            OverlayKind.ImportantTaskComplete or
            OverlayKind.HardwareResolved or
            OverlayKind.SystemOperation;
        var padding = compact ? 40f : 50f;
        var accent = ParseColor(
            visual?.AccentHex,
            item.Policy.VisualTier == OverlayVisualTier.Emphasis
                ? Color.FromArgb(250, 255, 117, 137)
                : Color.FromArgb(244, 137, 247, 255));
        using var dot = new SolidBrush(accent);
        graphics.FillEllipse(dot, padding, 32, 10, 10);
        DrawText(
            graphics,
            visual?.Eyebrow ?? KindLabel(item.Request.Kind),
            new RectangleF(padding + 22, 16, width * 0.55f, 50),
            compact ? 24 : 28,
            FontStyle.Bold,
            Color.FromArgb(228, 232, 252, 255));
        DrawText(
            graphics,
            SourceName(item.Request.Source),
            new RectangleF(width - padding - 220, 18, 220, 48),
            compact ? 21 : 24,
            FontStyle.Regular,
            Color.FromArgb(204, 217, 243, 248),
            StringAlignment.Far);

        var titleTop = compact ? 74f : 84f;
        var hasProgress = visual?.Progress is not null;
        var progressY = height - 64f;
        DrawText(
            graphics,
            item.Request.Title,
            new RectangleF(
                padding,
                titleTop,
                width - padding * 2,
                compact ? 72 : 90),
            compact ? 48 : (float)item.Policy.Typography.TitlePx,
            FontStyle.Bold,
            Color.FromArgb(252, 255, 255, 255));
        if (!string.IsNullOrWhiteSpace(item.Request.Body))
        {
            DrawText(
                graphics,
                item.Request.Body!,
                new RectangleF(
                    padding,
                    titleTop + (compact ? 66 : 82),
                    width - padding * 2,
                    hasProgress
                        ? Math.Max(
                            0,
                            progressY -
                            (titleTop + (compact ? 66 : 82)) -
                            15)
                        : height - titleTop - 126),
                compact ? 34 : (float)item.Policy.Typography.BodyPx,
                FontStyle.Bold,
                Color.FromArgb(239, 246, 252, 254),
                StringAlignment.Near,
                wrap: true);
        }

        if (visual?.Progress is not null)
        {
            var ratio =
                (float)Math.Clamp(visual.Progress.Value, 0, 1);
            using var track = new Pen(
                Color.FromArgb(74, 224, 252, 255),
                3.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawLine(
                track,
                padding,
                progressY,
                width - padding,
                progressY);
            using var progress = new Pen(accent, 5.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawLine(
                progress,
                padding,
                progressY,
                padding + (width - padding * 2) * ratio,
                progressY);
        }

        if (!string.IsNullOrWhiteSpace(visual?.Meta))
        {
            var metaHeight = compact ? 38f : 52f;
            var metaBottomMargin = compact ? 13f : 10f;
            DrawText(
                graphics,
                visual.Meta!,
                new RectangleF(
                    padding,
                    height - metaHeight - metaBottomMargin,
                    width - padding * 2,
                    metaHeight),
                compact ? 25 : (float)item.Policy.Typography.MetaPx,
                FontStyle.Regular,
                Color.FromArgb(214, 221, 239, 243));
        }
    }

    private bool TryDrawArtwork(
        Graphics graphics,
        string? path,
        RectangleF destination)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            if (!string.Equals(
                    path,
                    cachedArtworkPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                cachedArtwork?.Dispose();
                cachedArtwork = Image.FromFile(path);
                cachedArtworkPath = path;
            }

            var state = graphics.Save();
            using var clip = RoundedRectangle(destination, 24);
            graphics.SetClip(clip);
            graphics.DrawImage(cachedArtwork!, destination);
            graphics.Restore(state);
            using var edge = new Pen(
                Color.FromArgb(114, 229, 255, 255),
                1.2f);
            graphics.DrawPath(edge, clip);
            return true;
        }
        catch
        {
            cachedArtwork?.Dispose();
            cachedArtwork = null;
            cachedArtworkPath = null;
            return false;
        }
    }

    private static void DrawText(
        Graphics graphics,
        string text,
        RectangleF bounds,
        float size,
        FontStyle style,
        Color fill,
        StringAlignment alignment = StringAlignment.Near,
        bool wrap = false)
    {
        using var font = UiFont(size, style);
        using var shadow = new SolidBrush(Color.FromArgb(126, 0, 8, 12));
        using var brush = new SolidBrush(fill);
        using var format = Typographic();
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = StringTrimming.EllipsisCharacter;
        if (!wrap)
        {
            format.FormatFlags |= StringFormatFlags.NoWrap;
        }

        var shadowBounds = bounds;
        shadowBounds.Offset(1.7f, 2.1f);
        graphics.DrawString(text, font, shadow, shadowBounds, format);
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static float MeasureWrappedText(
        Graphics graphics,
        string text,
        float width,
        float size,
        FontStyle style)
    {
        using var font = UiFont(size, style);
        using var format = Typographic();
        format.Trimming = StringTrimming.None;
        var measured = graphics.MeasureString(
            text,
            font,
            new SizeF(Math.Max(1, width), 10_000),
            format);
        return Math.Max(size * 1.22f, measured.Height);
    }

    private static void DrawWrappedText(
        Graphics graphics,
        string text,
        RectangleF bounds,
        float size,
        FontStyle style,
        Color fill)
    {
        using var font = UiFont(size, style);
        using var shadow = new SolidBrush(Color.FromArgb(112, 0, 13, 8));
        using var brush = new SolidBrush(fill);
        using var format = Typographic();
        format.Trimming = StringTrimming.None;
        format.LineAlignment = StringAlignment.Near;
        var shadowBounds = bounds;
        shadowBounds.Offset(1.6f, 2f);
        graphics.DrawString(text, font, shadow, shadowBounds, format);
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static double NotificationScrollProgress(
        OverlayItem item,
        DateTimeOffset now)
    {
        if (item.ExpiresAt is { } expiresAt &&
            item.Policy.Duration is { } duration &&
            duration > TimeSpan.Zero)
        {
            var remaining = Math.Clamp(
                (expiresAt - now).TotalMilliseconds,
                0,
                duration.TotalMilliseconds);
            return 1 - remaining / duration.TotalMilliseconds;
        }

        var elapsed = Math.Max(
            0,
            (now - item.PublishedAt).TotalSeconds);
        return elapsed % 9 / 9;
    }

    private static float SmoothHeldProgress(double value)
    {
        var progress = Math.Clamp(value, 0, 1);
        if (progress <= 0.16)
        {
            return 0;
        }

        if (progress >= 0.84)
        {
            return 1;
        }

        var normalized = (float)((progress - 0.16) / 0.68);
        return normalized * normalized * (3 - 2 * normalized);
    }

    private static void DrawFittedText(
        Graphics graphics,
        string text,
        RectangleF bounds,
        float preferredSize,
        float minimumSize,
        FontStyle style,
        Color fill)
    {
        using var preferred = UiFont(preferredSize, style);
        using var format = Typographic();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        var measured = graphics.MeasureString(
            text,
            preferred,
            new SizeF(float.MaxValue, bounds.Height),
            format).Width;
        var size = measured <= bounds.Width || measured <= 0
            ? preferredSize
            : Math.Max(
                minimumSize,
                preferredSize * bounds.Width / measured);
        DrawText(
            graphics,
            text,
            bounds,
            size,
            style,
            fill);
    }

    private static void DrawScrollingText(
        Graphics graphics,
        string text,
        RectangleF bounds,
        float size,
        FontStyle style,
        Color fill,
        double lineProgress)
    {
        using var font = UiFont(size, style);
        using var format = Typographic();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        var textWidth = graphics.MeasureString(
            text,
            font,
            new SizeF(float.MaxValue, bounds.Height),
            format).Width;
        var overflow = textWidth - bounds.Width;
        if (overflow <= 8)
        {
            DrawText(graphics, text, bounds, size, style, fill);
            return;
        }

        var offset = (float)MarqueeMotion.OffsetForLine(
            overflow,
            lineProgress);
        var state = graphics.Save();
        graphics.SetClip(bounds);
        DrawText(
            graphics,
            text,
            new RectangleF(
                bounds.X - offset,
                bounds.Y,
                textWidth + 12,
                bounds.Height),
            size,
            style,
            fill);
        graphics.Restore(state);
    }

    private static Font UiFont(float size, FontStyle style) =>
        new("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);

    private static StringFormat Typographic() =>
        new(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter,
        };

    private static GraphicsPath RoundedRectangle(
        RectangleF bounds,
        float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Top,
            diameter,
            diameter,
            270,
            90);
        path.AddArc(
            bounds.Right - diameter,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(
            bounds.Left,
            bounds.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);
        path.CloseFigure();
        return path;
    }

    private void Present(Bitmap bitmap, PixelRect target)
    {
        var screenDc = NativeMethods.GetDC(0);
        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = NativeMethods.SelectObject(memoryDc, hBitmap);
        try
        {
            var destination = new NativeMethods.Point(target.X, target.Y);
            var size = new NativeMethods.Size(target.Width, target.Height);
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
                    "Could not update the crystal card window.");
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
            target.X,
            target.Y,
            target.Width,
            target.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowNoActivate);
    }

    internal static PixelRect ResolveCardRect(
        OverlayItem item,
        PixelRect maximum)
    {
        if (item.Policy.VisualTier ==
            OverlayVisualTier.StackedNotification)
        {
            var notificationHeight = MeasureNotificationCardHeight(
                item,
                maximum.Width,
                maximum.Height);
            return new PixelRect(
                maximum.X,
                maximum.Bottom - notificationHeight,
                maximum.Width,
                notificationHeight);
        }

        var desired = item.Request.Kind switch
        {
            OverlayKind.MediaActive => (Width: 900, Height: 230),
            OverlayKind.GameActive => (Width: 980, Height: 240),
            OverlayKind.ImportantTask => (Width: 980, Height: 290),
            OverlayKind.ImportantTaskComplete => (Width: 980, Height: 290),
            OverlayKind.HardwareResolved => (Width: 980, Height: 240),
            OverlayKind.SystemOperation => (Width: 820, Height: 220),
            OverlayKind.DeviceOrNetwork => (Width: 940, Height: 260),
            OverlayKind.MediaTrackChange => (Width: 1050, Height: 300),
            OverlayKind.GameAchievement => (Width: 1180, Height: 380),
            OverlayKind.GameSummary => (Width: 1180, Height: 400),
            _ => (Width: maximum.Width, Height: maximum.Height),
        };
        var width = Math.Min(maximum.Width, desired.Width);
        var height = Math.Min(maximum.Height, desired.Height);
        return new PixelRect(
            maximum.X,
            maximum.Bottom - height,
            width,
            height);
    }

    private static int MeasureNotificationCardHeight(
        OverlayItem item,
        int width,
        int maximumHeight)
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        ConfigureGraphics(graphics);
        var narrow = width < 900;
        var padding = narrow ? 32f : 42f;
        var titleSize = narrow ? 47f : 56f;
        var bodySize = narrow ? 39f : 44f;
        var contentWidth = Math.Max(1, width - padding * 2);
        var titleHeight = MeasureWrappedText(
            graphics,
            item.Request.Title,
            contentWidth,
            titleSize,
            FontStyle.Bold);
        var bodyHeight = string.IsNullOrWhiteSpace(item.Request.Body)
            ? 0
            : MeasureWrappedText(
                graphics,
                item.Request.Body!,
                contentWidth,
                bodySize,
                FontStyle.Regular);
        var desired = (int)Math.Ceiling(
            (narrow ? 58 : 64) +
            titleHeight +
            (bodyHeight > 0 ? 14 + bodyHeight : 0) +
            24);
        var minimum = Math.Min(190, maximumHeight);
        return Math.Clamp(desired, minimum, maximumHeight);
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.TrimStart('#');
        return text.Length == 6 &&
               int.TryParse(
                   text,
                   System.Globalization.NumberStyles.HexNumber,
                   null,
                   out var rgb)
            ? Color.FromArgb(
                244,
                (rgb >> 16) & 0xff,
                (rgb >> 8) & 0xff,
                rgb & 0xff)
            : fallback;
    }

    private static string KindLabel(OverlayKind kind) => kind switch
    {
        OverlayKind.Glance => "抬眼总览",
        OverlayKind.GameActive => "游戏进行中",
        OverlayKind.GameAchievement => "成就已解锁",
        OverlayKind.GameSummary => "本次游戏",
        OverlayKind.SystemOperation => "系统操作",
        OverlayKind.DeviceOrNetwork => "设备状态",
        OverlayKind.ImportantTask => "重要任务",
        OverlayKind.ImportantTaskComplete => "任务已完成",
        OverlayKind.HardwareAlert => "硬件告警",
        OverlayKind.HardwareResolved => "告警已恢复",
        OverlayKind.PhoneConnection => "手机连接",
        OverlayKind.PhoneNotification => "手机通知",
        OverlayKind.PhoneDynamic => "手机动态",
        OverlayKind.PhoneCall => "来电",
        OverlayKind.PhoneTransfer => "跨设备传输",
        _ => "HS2",
    };

    private static string SourceName(OverlaySource source) => source switch
    {
        OverlaySource.NetEase => "网易云音乐",
        OverlaySource.Steam => "STEAM",
        OverlaySource.XiaomiHyperConnect => "小米妙享",
        OverlaySource.PhoneLink => "手机连接",
        OverlaySource.Hardware => "TURZX",
        OverlaySource.Task => "任务 / TASK",
        OverlaySource.Game => "游戏 / GAME",
        _ => "WINDOWS",
    };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cachedArtwork?.Dispose();
        cachedArtwork = null;
        cachedArtworkPath = null;
        _ = NativeMethods.DestroyWindow(hwnd);
    }
}
