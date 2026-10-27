using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using SkiaSharp;

namespace EmbySeerrNative
{
    /// <summary>
    /// Draws a "REQUESTED" (amber) or "PARTIAL" (green) badge at the top and a "▶ TRAILER" badge at
    /// the bottom of the posters of Discover channel items, using Emby's supported image-enhancer hook and the SkiaSharp library that Emby
    /// already ships. Applies only to items carrying our badge provider id; everything else is
    /// untouched. If drawing fails for any reason, the original image is used unchanged.
    /// </summary>
    public class BadgeEnhancer : IImageEnhancer
    {
        private readonly ILogger _logger;

        public BadgeEnhancer(ILogManager logManager)
        {
            _logger = logManager.GetLogger("Discover");
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        /// <summary>"requested", "partial", "trailer", "requested+trailer", "partial+trailer", or null.</summary>
        internal static string BadgeOf(BaseItem item)
        {
            if (item == null) return null;
            var cfg = Plugin.Config;
            var badge = item.GetProviderId(DiscoverIds.BadgeKey);
            string status = null;
            if (badge == "requested" && cfg.ShowRequestedBadge) status = badge;
            else if (badge == "partial" && cfg.ShowPartialBadge) status = badge;
            var trailer = cfg.ShowTrailerBadge && cfg.PlayTrailers && item.GetProviderId(DiscoverIds.TrailerKey) == "1";
            if (status == null) return trailer ? "trailer" : null;
            return trailer ? status + "+trailer" : status;
        }

        public bool Supports(BaseItem item, ImageType imageType)
            => imageType == ImageType.Primary && BadgeOf(item) != null;

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
            => "discover-badge-v2-" + (BadgeOf(item) ?? "none");

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalImageSize)
            => originalImageSize;

        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
            => new EnhancedImageInfo { RequiresTransparency = false };

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            var badge = BadgeOf(item);
            try
            {
                if (badge == null) throw new InvalidOperationException("no badge");
                BadgePainter.Paint(inputFile, outputFile, badge);
            }
            catch (Exception ex)
            {
                if (badge != null) _logger.Warn("Discover: could not draw poster badge: {0}", ex.Message);
                try
                {
                    File.Copy(inputFile, outputFile, true); // fall back to the unmodified poster
                }
                catch (Exception copyEx)
                {
                    return Task.FromException(copyEx);
                }
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>SkiaSharp drawing, isolated so a missing/incompatible SkiaSharp only fails here.</summary>
    internal static class BadgePainter
    {
        private static SKTypeface _typeface;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Paint(string inputFile, string outputFile, string badge)
        {
            using (var bitmap = SKBitmap.Decode(inputFile))
            {
                if (bitmap == null) throw new InvalidOperationException("could not decode " + Path.GetFileName(inputFile));
                using (var canvas = new SKCanvas(bitmap))
                {
                    Draw(canvas, bitmap.Width, bitmap.Height, badge);
                    canvas.Flush();
                }
                using (var image = SKImage.FromBitmap(bitmap))
                using (var data = Encode(image, outputFile))
                using (var stream = File.Open(outputFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    data.SaveTo(stream);
                }
            }
        }

        private static SKData Encode(SKImage image, string outputFile)
        {
            var ext = (Path.GetExtension(outputFile) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".png": return image.Encode(SKEncodedImageFormat.Png, 100);
                case ".webp": return image.Encode(SKEncodedImageFormat.Webp, 90);
                default: return image.Encode(SKEncodedImageFormat.Jpeg, 92);
            }
        }

        private static SKTypeface Typeface()
        {
            if (_typeface != null) return _typeface;
            var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Fonts.badge-font.otf");
            _typeface = stream != null ? SKTypeface.FromData(SKData.Create(stream)) : null;
            return _typeface ?? (_typeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold));
        }

        internal static void Draw(SKCanvas canvas, int width, int height, string badge)
        {
            var parts = (badge ?? "").Split('+');
            var scale = Math.Min(width, height * 2f / 3f);
            foreach (var part in parts)
            {
                if (part == "requested")
                    Pill(canvas, height, scale, "REQUESTED", new SKColor(0xF0, 0xA5, 0x30), new SKColor(0x26, 0x18, 0x02), false, false);
                else if (part == "partial")
                    Pill(canvas, height, scale, "PARTIAL", new SKColor(0x52, 0xB5, 0x4B), SKColors.White, false, false);
                else if (part == "trailer")
                    Pill(canvas, height, scale, "TRAILER", new SKColor(0x10, 0x10, 0x10, 0xD8), SKColors.White, true, true);
            }
        }

        /// <summary>A rounded label at the top-left (or bottom-left) corner, optionally with a ▶ icon.</summary>
        private static void Pill(SKCanvas canvas, int height, float scale, string text, SKColor fill, SKColor ink, bool bottom, bool playIcon)
        {
            var margin = scale * 0.045f;
            var h = scale * 0.105f;
            var fontSize = h * 0.52f;

            using (var textPaint = new SKPaint
            {
                IsAntialias = true,
                Color = ink,
                Typeface = Typeface(),
                TextSize = fontSize
            })
            {
                var textWidth = textPaint.MeasureText(text);
                var padX = h * 0.48f;
                var icon = playIcon ? h * 0.42f : 0f;
                var iconGap = playIcon ? h * 0.24f : 0f;
                var top = bottom ? height - margin - h : margin;
                var rect = new SKRect(margin, top, margin + icon + iconGap + textWidth + padX * 2, top + h);

                using (var shadow = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(0, 0, 0, 110),
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, h * 0.12f)
                })
                {
                    var s = rect;
                    s.Offset(0, h * 0.06f);
                    canvas.DrawRoundRect(s, h / 2, h / 2, shadow);
                }
                using (var bg = new SKPaint { IsAntialias = true, Color = fill })
                {
                    canvas.DrawRoundRect(rect, h / 2, h / 2, bg);
                }

                if (playIcon)
                {
                    // ▶ drawn as a path, so the font needs no extra glyph.
                    using (var tri = new SKPath())
                    using (var iconPaint = new SKPaint { IsAntialias = true, Color = ink })
                    {
                        var x = rect.Left + padX * 0.85f;
                        var half = icon / 2;
                        tri.MoveTo(x, rect.MidY - half);
                        tri.LineTo(x + icon * 0.9f, rect.MidY);
                        tri.LineTo(x, rect.MidY + half);
                        tri.Close();
                        canvas.DrawPath(tri, iconPaint);
                    }
                }

                var metrics = textPaint.FontMetrics;
                var baseline = rect.MidY - (metrics.Ascent + metrics.Descent) / 2;
                canvas.DrawText(text, rect.Left + padX + icon + iconGap, baseline, textPaint);
            }
        }
    }
}
