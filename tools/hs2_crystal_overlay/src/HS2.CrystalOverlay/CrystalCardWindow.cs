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
        RenderExact(item, target, now);
    }

    internal void RenderExact(
        OverlayItem? item,
        PixelRect target,
        DateTimeOffset now,
        DateTimeOffset? notificationScrollStartedAt = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (item is null)
        {
            Hide();
            return;
        }

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
            if (item.Request.Kind == OverlayKind.SystemOperation &&
                item.Request.Visual?.AudioIcon is not null)
            {
                DrawAudioHud(
                    graphics,
                    item,
                    target.Width,
                    target.Height);
            }
            else if (item.Request.Kind ==
                     OverlayKind.PhoneVerificationCode)
            {
                DrawVerificationCode(
                    graphics,
                    item,
                    target.Width,
                    target.Height);
            }
            else if (item.Request.Kind is
                OverlayKind.MediaActive or OverlayKind.MediaTrackChange)
            {
                DrawMedia(
                    graphics,
                    item,
                    target.Width,
                    target.Height,
                    now);
            }
            else if (item.Policy.VisualTier ==
                     OverlayVisualTier.StackedNotification)
            {
                DrawPhoneNotification(
                    graphics,
                    item,
                    target.Width,
                    target.Height,
                    now,
                    notificationScrollStartedAt ?? item.PublishedAt);
            }
            else
            {
                DrawEvent(graphics, item, target.Width, target.Height);
            }
        }

        Present(bitmap, target);
    }

    internal void MoveTo(PixelRect target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        _ = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopmost,
            target.X,
            target.Y,
            target.Width,
            target.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    internal void Hide()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
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
            ? Color.FromArgb(24, 154, 255, 211)
            : Color.FromArgb(22, 255, 255, 255);
        var washEnd = notificationSurface
            ? Color.FromArgb(7, 185, 255, 222)
            : Color.FromArgb(7, 226, 255, 246);
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
        int height,
        DateTimeOffset now)
    {
        var visual = item.Request.Visual;
        var marqueeProgress = visual?.MarqueeProgress ??
            AmbientMarqueeProgress(now);
        var expanded = item.Request.Kind == OverlayKind.MediaTrackChange;
        var identityOnly =
            string.IsNullOrWhiteSpace(item.Request.Body) &&
            visual?.Progress is null &&
            string.IsNullOrWhiteSpace(visual?.Meta);
        var padding = identityOnly
            ? expanded ? 30f : 24f
            : expanded ? 42f : 32f;
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
        var titleTop = identityOnly
            ? expanded ? 32f : 28f
            : expanded ? 18f : 12f;
        using var dot = new SolidBrush(accent);
        graphics.FillEllipse(
            dot,
            contentX,
            identityOnly
                ? titleTop + (expanded ? 16 : 12)
                : expanded ? 36 : 27,
            expanded ? 11 : 9,
            expanded ? 11 : 9);
        contentX += expanded ? 23 : 20;

        var titleRight = expanded ? right - 205 : right;
        DrawScrollingText(
            graphics,
            item.Request.Title,
            new RectangleF(
                contentX,
                titleTop,
                Math.Max(100, titleRight - contentX),
                expanded ? 62 : 64),
            expanded ? 48 : 52,
            FontStyle.Bold,
            Color.FromArgb(252, 255, 255, 255),
            marqueeProgress);

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
                    identityOnly
                        ? titleTop + (expanded ? 64 : 62)
                        : expanded ? 72 : 58,
                    right - contentX,
                    expanded ? 43 : 38),
                expanded ? 34 : 32,
                expanded ? 28 : 26,
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
                marqueeProgress);
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
                    marqueeProgress);
            }
        }

        var hasTimeline = visual?.Progress is not null ||
                          !string.IsNullOrWhiteSpace(visual?.Meta);
        if (hasTimeline)
        {
            var progressY = height - (expanded ? 39 : 36);
            var metaWidth = string.IsNullOrWhiteSpace(visual?.Meta)
                ? 0f
                : Math.Min(300, width * 0.31f);
            var progressRight =
                right - metaWidth - (metaWidth > 0 ? 24 : 0);
            using var timeline = new Pen(
                Color.FromArgb(54, 224, 252, 255),
                2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            graphics.DrawLine(
                timeline,
                contentX,
                progressY,
                progressRight,
                progressY);
            if (visual?.Progress is not null)
            {
                var ratio = (float)Math.Clamp(
                    visual.Progress.Value,
                    0,
                    1);
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
    }

    private static void DrawPhoneNotification(
        Graphics graphics,
        OverlayItem item,
        int width,
        int height,
        DateTimeOffset now,
        DateTimeOffset scrollStartedAt)
    {
        var visual = item.Request.Visual;
        var sizing = NotificationSizing(width, height);
        var padding = sizing.Padding;
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
            sizing.Narrow ? 21 : 24,
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
            sizing.Narrow ? 20 : 23,
            17,
            FontStyle.Regular,
            Color.FromArgb(220, 169, 238, 204));

        var viewport = new RectangleF(
            padding,
            sizing.HeaderHeight,
            width - padding * 2,
            height - sizing.HeaderHeight - sizing.BottomReserve);
        // Titles stay on one fixed line so the body viewport is stable even
        // when a notification carries a very long mixed-language title.
        var titleHeight = MeasureLineHeight(
            graphics,
            item.Request.Title,
            viewport.Width,
            sizing.TitleSize,
            FontStyle.Bold);
        var preferredBodyLineHeight = MeasureWrappedText(
            graphics,
            "字",
            viewport.Width,
            sizing.BodySize,
            FontStyle.Regular);
        var bodyLayout = NotificationBodyLayout.Create(
            height,
            sizing.HeaderHeight,
            titleHeight,
            sizing.TitleBodyGap,
            sizing.BottomReserve,
            preferredBodyLineHeight);
        var bodySize = preferredBodyLineHeight <= 0
            ? sizing.BodySize
            : sizing.BodySize *
                (float)(bodyLayout.LineHeight / preferredBodyLineHeight);
        var bodyHeight = string.IsNullOrWhiteSpace(item.Request.Body)
            ? 0
            : MeasureWrappedText(
                graphics,
                item.Request.Body!,
                viewport.Width,
                bodySize,
                FontStyle.Regular);

        DrawFittedText(
            graphics,
            item.Request.Title,
            new RectangleF(
                viewport.Left,
                viewport.Top,
                viewport.Width,
                titleHeight + 4),
            sizing.TitleSize,
            sizing.TitleSize * 0.62f,
            FontStyle.Bold,
            Color.FromArgb(252, 225, 255, 239));

        if (bodyHeight > 0)
        {
            var scrollProgress = NotificationScrollProgress(
                scrollStartedAt,
                now);
            var bodyProgress = SmoothHeldProgress(
                MarqueeMotion.SpeedUpHeldProgress(scrollProgress));
            var offset = bodyLayout.ShouldScroll(bodyHeight)
                ? (float)bodyLayout.OffsetForProgress(
                    bodyProgress,
                    bodyHeight)
                : 0;
            var bodyViewport = new RectangleF(
                viewport.Left,
                (float)bodyLayout.BodyTop,
                viewport.Width,
                (float)bodyLayout.ViewportHeight);
            var state = graphics.Save();
            graphics.SetClip(bodyViewport);
            DrawWrappedText(
                graphics,
                item.Request.Body!,
                new RectangleF(
                    viewport.Left,
                    bodyViewport.Top - offset,
                    viewport.Width,
                    bodyHeight + 4),
                bodySize,
                FontStyle.Regular,
                Color.FromArgb(242, 174, 247, 211));
            graphics.Restore(state);
        }
    }

    private static void DrawVerificationCode(
        Graphics graphics,
        OverlayItem item,
        int width,
        int height)
    {
        var visual = item.Request.Visual;
        var narrow = width < 900;
        var padding = narrow ? 30f : 40f;
        var accent = ParseColor(
            visual?.AccentHex,
            Color.FromArgb(244, 112, 240, 178));

        using var dot = new SolidBrush(accent);
        graphics.FillEllipse(dot, padding, 27, 10, 10);
        DrawText(
            graphics,
            visual?.Eyebrow ?? "验证码 / CODE",
            new RectangleF(
                padding + 22,
                10,
                width * 0.38f,
                44),
            narrow ? 20 : 23,
            FontStyle.Bold,
            Color.FromArgb(238, 122, 245, 184));

        var source = string.Join(
            " · ",
            new[] { visual?.Subtitle, visual?.Meta }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (source.Length > 0)
        {
            DrawFittedText(
                graphics,
                source,
                new RectangleF(
                    width * 0.40f,
                    10,
                    width * 0.60f - padding,
                    44),
                narrow ? 20 : 22,
                16,
                FontStyle.Regular,
                Color.FromArgb(220, 169, 238, 204),
                StringAlignment.Far);
        }

        var code = visual?.VerificationCode ?? item.Request.Title;
        var preferredSize = code.Length switch
        {
            <= 4 => 120f,
            5 => 114f,
            6 => 108f,
            7 => 100f,
            _ => 92f,
        };
        var codeTop = narrow ? 48f : 50f;
        DrawFittedText(
            graphics,
            code,
            new RectangleF(
                padding,
                codeTop,
                width - padding * 2,
                Math.Max(1, height - codeTop - 14)),
            preferredSize,
            72f,
            FontStyle.Bold,
            Color.FromArgb(252, 225, 255, 239),
            StringAlignment.Center);
    }

    private static void DrawAudioHud(
        Graphics graphics,
        OverlayItem item,
        int width,
        int height)
    {
        var icon = item.Request.Visual?.AudioIcon ??
                   AudioHudIcon.Silent;
        var accent = ParseColor(
            item.Request.Visual?.AccentHex,
            Color.FromArgb(244, 156, 231, 255));
        var narrow = width < 900;
        var iconSize = Math.Clamp(
            height * (narrow ? 0.48f : 0.52f),
            78f,
            122f);
        var left = narrow ? 42f : 54f;
        var iconBounds = new RectangleF(
            left,
            (height - iconSize) / 2f,
            iconSize,
            iconSize);
        DrawAudioIcon(graphics, iconBounds, icon, accent);

        var textLeft = iconBounds.Right + (narrow ? 30f : 42f);
        DrawFittedText(
            graphics,
            item.Request.Title,
            new RectangleF(
                textLeft,
                20,
                Math.Max(1, width - textLeft - left),
                height - 40),
            narrow ? 76f : 88f,
            narrow ? 58f : 68f,
            FontStyle.Bold,
            Color.FromArgb(252, 244, 255, 251));
    }

    private static void DrawAudioIcon(
        Graphics graphics,
        RectangleF bounds,
        AudioHudIcon icon,
        Color accent)
    {
        using var fill = new SolidBrush(accent);
        using var path = new GraphicsPath();
        path.AddPolygon(
        [
            new PointF(bounds.Left + bounds.Width * 0.08f,
                bounds.Top + bounds.Height * 0.39f),
            new PointF(bounds.Left + bounds.Width * 0.30f,
                bounds.Top + bounds.Height * 0.39f),
            new PointF(bounds.Left + bounds.Width * 0.54f,
                bounds.Top + bounds.Height * 0.17f),
            new PointF(bounds.Left + bounds.Width * 0.54f,
                bounds.Top + bounds.Height * 0.83f),
            new PointF(bounds.Left + bounds.Width * 0.30f,
                bounds.Top + bounds.Height * 0.61f),
            new PointF(bounds.Left + bounds.Width * 0.08f,
                bounds.Top + bounds.Height * 0.61f),
        ]);
        graphics.FillPath(fill, path);

        using var pen = new Pen(
            accent,
            Math.Max(4f, bounds.Width * 0.052f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        if (icon == AudioHudIcon.Muted)
        {
            graphics.DrawLine(
                pen,
                bounds.Left + bounds.Width * 0.66f,
                bounds.Top + bounds.Height * 0.35f,
                bounds.Left + bounds.Width * 0.92f,
                bounds.Top + bounds.Height * 0.65f);
            graphics.DrawLine(
                pen,
                bounds.Left + bounds.Width * 0.92f,
                bounds.Top + bounds.Height * 0.35f,
                bounds.Left + bounds.Width * 0.66f,
                bounds.Top + bounds.Height * 0.65f);
            return;
        }

        var waveCount = icon switch
        {
            AudioHudIcon.Low => 1,
            AudioHudIcon.Medium => 2,
            AudioHudIcon.High => 3,
            _ => 0,
        };
        for (var wave = 0; wave < waveCount; wave++)
        {
            var inset = bounds.Width * (0.27f - wave * 0.09f);
            var arcBounds = new RectangleF(
                bounds.Left + bounds.Width * 0.44f - inset,
                bounds.Top + inset,
                bounds.Width * 0.55f + inset * 2,
                bounds.Height - inset * 2);
            graphics.DrawArc(pen, arcBounds, -48, 96);
        }
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
            OverlayKind.SystemOperation or
            OverlayKind.DeviceOrNetwork;
        var operation = item.Request.Kind is
            OverlayKind.SystemOperation or
            OverlayKind.DeviceOrNetwork;
        var narrow = width < 900;
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

        var titleTop = operation ? 48f : compact ? 70f : 84f;
        var hasProgress = visual?.Progress is not null;
        var progressY = height - 64f;
        var operationTitleHeight = Math.Clamp(
            height * 0.38f,
            62f,
            narrow ? 72f : 78f);
        var titleSize = operation
            ? narrow ? 52f : 60f
            : item.Request.Kind == OverlayKind.GameActive
                ? narrow ? 52f : 58f
                : compact ? 50f : (float)item.Policy.Typography.TitlePx;
        var titleHeight = operation
            ? operationTitleHeight
            : compact ? 74f : 90f;
        DrawFittedText(
            graphics,
            item.Request.Title,
            new RectangleF(
                padding,
                titleTop,
                width - padding * 2,
                titleHeight),
            titleSize,
            operation ? narrow ? 44f : 50f : 44f,
            FontStyle.Bold,
            Color.FromArgb(252, 255, 255, 255));
        if (!string.IsNullOrWhiteSpace(item.Request.Body))
        {
            var bodyTop = operation
                ? titleTop + titleHeight - 4f
                : titleTop + (compact ? 66f : 82f);
            var bottomReserve = string.IsNullOrWhiteSpace(visual?.Meta)
                ? operation ? 10f : 16f
                : compact ? 58f : 68f;
            var bodyHeight = hasProgress
                ? Math.Max(0, progressY - bodyTop - 15)
                : Math.Max(0, height - bodyTop - bottomReserve);
            var bodyBounds = new RectangleF(
                padding,
                bodyTop,
                width - padding * 2,
                bodyHeight);
            var bodyColor = Color.FromArgb(239, 246, 252, 254);
            if (operation)
            {
                DrawFittedText(
                    graphics,
                    item.Request.Body!,
                    bodyBounds,
                    narrow ? 30f : 34f,
                    narrow ? 25f : 28f,
                    FontStyle.Bold,
                    bodyColor);
            }
            else
            {
                DrawWrappedText(
                    graphics,
                    item.Request.Body!,
                    bodyBounds,
                    compact
                        ? 38f
                        : (float)item.Policy.Typography.BodyPx,
                    FontStyle.Bold,
                    bodyColor);
            }
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
                using var source = Image.FromFile(path);
                var replacement = new Bitmap(source);
                cachedArtwork?.Dispose();
                cachedArtwork = replacement;
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
        using var outline = new SolidBrush(
            Color.FromArgb(176, 0, 34, 31));
        using var shadow = new SolidBrush(Color.FromArgb(150, 0, 8, 12));
        using var brush = new SolidBrush(fill);
        using var format = Typographic();
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = StringTrimming.EllipsisCharacter;
        if (!wrap)
        {
            format.FormatFlags |= StringFormatFlags.NoWrap;
        }

        var outlineOffset = Math.Clamp(size * 0.045f, 1.4f, 3f);
        DrawTextOutline(
            graphics,
            text,
            font,
            outline,
            bounds,
            format,
            outlineOffset);
        var shadowBounds = bounds;
        shadowBounds.Offset(outlineOffset, outlineOffset + 0.7f);
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

    private static float MeasureLineHeight(
        Graphics graphics,
        string text,
        float width,
        float size,
        FontStyle style)
    {
        using var font = UiFont(size, style);
        using var format = Typographic();
        format.FormatFlags |= StringFormatFlags.NoWrap;
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
        using var outline = new SolidBrush(
            Color.FromArgb(170, 0, 38, 30));
        using var shadow = new SolidBrush(Color.FromArgb(142, 0, 13, 8));
        using var brush = new SolidBrush(fill);
        using var format = Typographic();
        format.Trimming = StringTrimming.None;
        format.LineAlignment = StringAlignment.Near;
        var outlineOffset = Math.Clamp(size * 0.045f, 1.4f, 3f);
        DrawTextOutline(
            graphics,
            text,
            font,
            outline,
            bounds,
            format,
            outlineOffset);
        var shadowBounds = bounds;
        shadowBounds.Offset(outlineOffset, outlineOffset + 0.7f);
        graphics.DrawString(text, font, shadow, shadowBounds, format);
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static void DrawTextOutline(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds,
        StringFormat format,
        float offset)
    {
        var shifted = bounds;
        shifted.Offset(-offset, 0);
        graphics.DrawString(text, font, brush, shifted, format);
        shifted = bounds;
        shifted.Offset(offset, 0);
        graphics.DrawString(text, font, brush, shifted, format);
        shifted = bounds;
        shifted.Offset(0, -offset);
        graphics.DrawString(text, font, brush, shifted, format);
        shifted = bounds;
        shifted.Offset(0, offset);
        graphics.DrawString(text, font, brush, shifted, format);
    }

    private static double NotificationScrollProgress(
        DateTimeOffset scrollStartedAt,
        DateTimeOffset now)
    {
        var elapsed = Math.Max(
            0,
            (now - scrollStartedAt).TotalSeconds);
        return MarqueeMotion.NotificationProgress(elapsed);
    }

    private static double AmbientMarqueeProgress(DateTimeOffset now)
    {
        const double periodSeconds = 16;
        var phase = (now.ToUnixTimeMilliseconds() / 1000d) %
            periodSeconds;
        return phase switch
        {
            < 2 => 0,
            < 7 => (phase - 2) / 5,
            < 9 => 1,
            < 14 => 1 - (phase - 9) / 5,
            _ => 0,
        };
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
        Color fill,
        StringAlignment alignment = StringAlignment.Near)
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
            fill,
            alignment);
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
            OverlayKind.MediaActive => (Width: 820, Height: 176),
            OverlayKind.GameActive => (Width: 980, Height: 240),
            OverlayKind.ImportantTask => (Width: 980, Height: 290),
            OverlayKind.ImportantTaskComplete => (Width: 980, Height: 290),
            OverlayKind.HardwareResolved => (Width: 980, Height: 240),
            OverlayKind.SystemOperation => (Width: 820, Height: 220),
            OverlayKind.DeviceOrNetwork => (Width: 940, Height: 260),
            OverlayKind.MediaTrackChange => (Width: 940, Height: 210),
            OverlayKind.GameAchievement => (Width: 1180, Height: 380),
            OverlayKind.GameSummary => (Width: 1180, Height: 400),
            OverlayKind.PhoneVerificationCode =>
                (Width: 860, Height: 220),
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
        var sizing = NotificationSizing(width, maximumHeight);
        var padding = sizing.Padding;
        var contentWidth = Math.Max(1, width - padding * 2);
        var titleHeight = MeasureLineHeight(
            graphics,
            item.Request.Title,
            contentWidth,
            sizing.TitleSize,
            FontStyle.Bold);
        var preferredBodyLineHeight = MeasureWrappedText(
            graphics,
            "字",
            contentWidth,
            sizing.BodySize,
            FontStyle.Regular);
        var bodyHeight = string.IsNullOrWhiteSpace(item.Request.Body)
            ? 0
            : MeasureWrappedText(
                graphics,
                item.Request.Body!,
                contentWidth,
                sizing.BodySize,
                FontStyle.Regular);
        var visibleBodyHeight = Math.Min(
            bodyHeight,
            preferredBodyLineHeight *
            NotificationBodyLayout.DefaultVisibleLines);
        var desired = (int)Math.Ceiling(
            sizing.HeaderHeight +
            titleHeight +
            (bodyHeight > 0
                ? sizing.TitleBodyGap + visibleBodyHeight
                : 0) +
            sizing.BottomReserve);
        var minimum = Math.Min(190, maximumHeight);
        return Math.Clamp(desired, minimum, maximumHeight);
    }

    private static NotificationCardSizing NotificationSizing(
        int width,
        int height)
    {
        var narrow = width < 900;
        // Three stacked notifications are compressed to roughly 230–240px;
        // both heights need the compact geometry to retain two body lines.
        var shortCard = height <= 240;
        return shortCard
            ? new(
                Padding: 28,
                TitleSize: 44,
                BodySize: 36,
                HeaderHeight: 52,
                TitleBodyGap: 8,
                BottomReserve: 12,
                Narrow: narrow)
            : narrow
                ? new(
                    Padding: 32,
                    TitleSize: 50,
                    BodySize: 40,
                    HeaderHeight: 56,
                    TitleBodyGap: 10,
                    BottomReserve: 16,
                    Narrow: true)
                : new(
                    Padding: 42,
                    TitleSize: 52,
                    BodySize: 42,
                    HeaderHeight: 58,
                    TitleBodyGap: 10,
                    BottomReserve: 16,
                    Narrow: false);
    }

    private readonly record struct NotificationCardSizing(
        float Padding,
        float TitleSize,
        float BodySize,
        float HeaderHeight,
        float TitleBodyGap,
        float BottomReserve,
        bool Narrow);

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
        OverlayKind.PhoneVerificationCode => "验证码",
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
