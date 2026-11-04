using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ClipClip.Engine.Core;
using ClipClip.Engine.Media;
using FFmpeg.AutoGen;

namespace ClipClip.Engine.Editing;

/// <summary>
/// Draws one frame of a project into a BGRA canvas: the video piece at that time with its framing,
/// motion, colour and transition, then every text and sticker layer that is showing. Preview and
/// export both use this, so the preview shows exactly what export makes.
/// </summary>
public sealed unsafe class Compositor : IDisposable
{
    private readonly Dictionary<string, VideoSource?> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GraphicsPath> _textPaths = new();
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private readonly int _scaleFlags;
    private SwsContext* _scaler;
    private SwsContext* _toYuv;
    private AVFrame* _yuv;
    private readonly byte[] _lut = new byte[768];

    public Compositor(int width, int height, bool highQuality)
    {
        FF.EnsureLoaded();
        Width = width;
        Height = height;
        Stride = width * 4;
        Canvas = (byte*)ffmpeg.av_malloc((ulong)(Stride * height));
        _bitmap = new Bitmap(width, height, Stride, PixelFormat.Format32bppArgb, (nint)Canvas);
        _graphics = Graphics.FromImage(_bitmap);
        _graphics.SmoothingMode = SmoothingMode.AntiAlias;
        _graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        _graphics.InterpolationMode = highQuality ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
        _scaleFlags = (int)(highQuality ? SwsFlags.SWS_BICUBIC : SwsFlags.SWS_FAST_BILINEAR);
        _yuv = ffmpeg.av_frame_alloc();
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte* Canvas { get; }

    public void Render(EditorProject project, double time)
    {
        new Span<uint>(Canvas, Width * Height).Fill(0xFF000000);
        (VideoClip? clip, double start) = project.ClipAt(time);
        if (clip != null)
        {
            try
            {
                DrawClip(clip, time - start);
            }
            catch (Exception ex)
            {
                Log.Warn($"Frame of {clip.Title} skipped: {ex.Message}");
            }
        }
        foreach (OverlayLayer layer in project.Layers)
        {
            if (time >= layer.Start && time < layer.End) DrawLayer(layer, time);
        }
    }

    public void CopyTo(byte[] destination) => new ReadOnlySpan<byte>(Canvas, Stride * Height).CopyTo(destination);

    private VideoSource? Source(string path)
    {
        if (_sources.TryGetValue(path, out VideoSource? source)) return source;
        try { source = new VideoSource(path); }
        catch (Exception ex)
        {
            Log.Warn($"Cannot open {Path.GetFileName(path)}: {ex.Message}");
            source = null;
        }
        _sources[path] = source;
        return source;
    }

    private void DrawClip(VideoClip clip, double local)
    {
        VideoSource? source = Source(clip.SourcePath);
        AVFrame* frame = source?.FrameAt(clip.In + local * clip.Speed);
        if (frame == null) return;

        AVFrame* yuv = frame;
        if (frame->format != (int)AVPixelFormat.AV_PIX_FMT_YUV420P && frame->format != (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P)
        {
            if (_yuv->width != frame->width || _yuv->height != frame->height)
            {
                ffmpeg.av_frame_unref(_yuv);
                _yuv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                _yuv->width = frame->width;
                _yuv->height = frame->height;
                FF.Check(ffmpeg.av_frame_get_buffer(_yuv, 32), "Frame buffer");
            }
            _toYuv = ffmpeg.sws_getCachedContext(_toYuv, frame->width, frame->height, (AVPixelFormat)frame->format,
                frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_YUV420P, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            ffmpeg.sws_scale(_toYuv, frame->data, frame->linesize, 0, frame->height, _yuv->data, _yuv->linesize);
            yuv = _yuv;
        }

        // Cover the canvas, then zoom in for motion and the zoom transition.
        int sourceWidth = yuv->width, sourceHeight = yuv->height;
        double target = (double)Width / Height;
        double cropWidth = sourceWidth, cropHeight = sourceHeight;
        if ((double)sourceWidth / sourceHeight > target) cropWidth = sourceHeight * target;
        else cropHeight = sourceWidth / target;

        double zoom = 1, shiftX = 0, shiftY = 0;
        double transition = Math.Max(0.05, clip.TransitionSeconds);
        if (clip.Motion == MotionEffect.SlowZoom) zoom = 1 + 0.18 * Math.Clamp(local / clip.Duration, 0, 1);
        if (clip.Motion == MotionEffect.Shake)
        {
            zoom = 1.07;
            shiftX = Math.Sin(local * 31) * 0.02 + Math.Sin(local * 13) * 0.01;
            shiftY = Math.Cos(local * 27) * 0.02;
        }
        if (clip.Transition == TransitionKind.Zoom && local < transition)
        {
            double remaining = 1 - local / transition;
            zoom *= 1 + 0.35 * remaining * remaining;
        }

        int width = Math.Max(16, (int)(cropWidth / zoom) & ~1);
        int height = Math.Max(16, (int)(cropHeight / zoom) & ~1);
        int x = (int)((sourceWidth - width) / 2.0 + shiftX * width);
        int y = (int)((sourceHeight - height) / 2.0 + shiftY * height);
        x = Math.Clamp(x, 0, sourceWidth - width) & ~1;
        y = Math.Clamp(y, 0, sourceHeight - height) & ~1;

        byte*[] planes =
        [
            yuv->data[0] + y * yuv->linesize[0] + x,
            yuv->data[1] + y / 2 * yuv->linesize[1] + x / 2,
            yuv->data[2] + y / 2 * yuv->linesize[2] + x / 2,
        ];
        int[] strides = [yuv->linesize[0], yuv->linesize[1], yuv->linesize[2]];
        _scaler = ffmpeg.sws_getCachedContext(_scaler, width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
            Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA, _scaleFlags, null, null, null);
        ffmpeg.sws_scale(_scaler, planes, strides, 0, height, [Canvas], [Stride]);

        ApplyColor(clip);

        if (local < transition && clip.Transition is TransitionKind.Fade or TransitionKind.Flash)
            ApplyTransition(clip.Transition, local / transition);
    }

    private void ApplyColor(VideoClip clip)
    {
        double brightness = clip.Brightness, contrast = clip.Contrast, saturation = clip.Saturation;
        int tintRed = 0, tintBlue = 0;
        switch (clip.Filter)
        {
            case FilterPreset.Vivid: contrast *= 1.12; saturation *= 1.4; break;
            case FilterPreset.Warm: tintRed = 14; tintBlue = -12; saturation *= 1.08; break;
            case FilterPreset.Cool: tintRed = -10; tintBlue = 16; break;
            case FilterPreset.Mono: saturation = 0; contrast *= 1.1; break;
            case FilterPreset.Faded: contrast *= 0.82; brightness += 0.06; saturation *= 0.75; break;
        }
        if (Math.Abs(brightness) < 0.001 && Math.Abs(contrast - 1) < 0.001 && Math.Abs(saturation - 1) < 0.001 && tintRed == 0 && tintBlue == 0)
            return;

        for (int i = 0; i < 256; i++)
        {
            double value = (i / 255.0 - 0.5) * contrast + 0.5 + brightness;
            int baseValue = (int)Math.Round(value * 255);
            _lut[i] = (byte)Math.Clamp(baseValue + tintBlue, 0, 255);
            _lut[256 + i] = (byte)Math.Clamp(baseValue, 0, 255);
            _lut[512 + i] = (byte)Math.Clamp(baseValue + tintRed, 0, 255);
        }

        int satFixed = (int)(saturation * 256);
        bool adjustSaturation = satFixed != 256;
        fixed (byte* lut = _lut)
        {
            byte* pixel = Canvas;
            byte* end = Canvas + Stride * Height;
            for (; pixel < end; pixel += 4)
            {
                int b = lut[pixel[0]], g = lut[256 + pixel[1]], r = lut[512 + pixel[2]];
                if (adjustSaturation)
                {
                    int luma = (r * 77 + g * 150 + b * 29) >> 8;
                    r = luma + (((r - luma) * satFixed) >> 8);
                    g = luma + (((g - luma) * satFixed) >> 8);
                    b = luma + (((b - luma) * satFixed) >> 8);
                }
                pixel[0] = (byte)Math.Clamp(b, 0, 255);
                pixel[1] = (byte)Math.Clamp(g, 0, 255);
                pixel[2] = (byte)Math.Clamp(r, 0, 255);
            }
        }
    }

    private void ApplyTransition(TransitionKind kind, double progress)
    {
        int amount = (int)(Math.Clamp(progress, 0, 1) * 256);
        byte* pixel = Canvas;
        byte* end = Canvas + Stride * Height;
        if (kind == TransitionKind.Fade)
        {
            for (; pixel < end; pixel += 4)
            {
                pixel[0] = (byte)((pixel[0] * amount) >> 8);
                pixel[1] = (byte)((pixel[1] * amount) >> 8);
                pixel[2] = (byte)((pixel[2] * amount) >> 8);
            }
        }
        else
        {
            int white = 256 - amount;
            for (; pixel < end; pixel += 4)
            {
                pixel[0] = (byte)(pixel[0] + (((255 - pixel[0]) * white) >> 8));
                pixel[1] = (byte)(pixel[1] + (((255 - pixel[1]) * white) >> 8));
                pixel[2] = (byte)(pixel[2] + (((255 - pixel[2]) * white) >> 8));
            }
        }
    }

    /// <summary>Opacity, scale and vertical offset from the layer's animation at this moment.</summary>
    public static (double Alpha, double Scale, double Lift) Animate(OverlayLayer layer, double time)
    {
        double inProgress = Math.Clamp((time - layer.Start) / 0.35, 0, 1);
        double outProgress = Math.Clamp((layer.End - time) / 0.25, 0, 1);
        double ease = 1 - Math.Pow(1 - inProgress, 3);
        return layer.Animation switch
        {
            LayerAnimation.Fade => (Math.Min(inProgress, outProgress), 1, 0),
            LayerAnimation.Pop => (Math.Min(1, inProgress * 2.5) * outProgress, 0.55 + 0.45 * EaseOutBack(inProgress), 0),
            LayerAnimation.SlideUp => (Math.Min(inProgress, outProgress), 1, (1 - ease) * 0.06),
            _ => (1, 1, 0),
        };
    }

    private static double EaseOutBack(double x)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }

    private void DrawLayer(OverlayLayer layer, double time)
    {
        (double alpha, double scale, double lift) = Animate(layer, time);
        if (alpha <= 0.01) return;
        float centerX = (float)(layer.X * Width);
        float centerY = (float)((layer.Y + lift) * Height);

        GraphicsState state = _graphics.Save();
        try
        {
            _graphics.TranslateTransform(centerX, centerY);
            _graphics.ScaleTransform((float)scale, (float)scale);
            if (layer.Kind == LayerKind.Text) DrawText(layer, alpha);
            else DrawSticker(layer, alpha);
        }
        finally
        {
            _graphics.Restore(state);
        }
    }

    private void DrawText(OverlayLayer layer, double alpha)
    {
        if (string.IsNullOrWhiteSpace(layer.Text)) return;
        float size = (float)(layer.Size * Height);
        string key = $"{layer.Text}|{layer.Font}|{layer.Bold}|{size:F1}";
        if (!_textPaths.TryGetValue(key, out GraphicsPath? path))
        {
            if (_textPaths.Count > 64)
            {
                foreach (GraphicsPath old in _textPaths.Values) old.Dispose();
                _textPaths.Clear();
            }
            FontFamily family = FontFamily.Families.FirstOrDefault(f => f.Name == layer.Font) ?? FontFamily.GenericSansSerif;
            path = new GraphicsPath();
            path.AddString(layer.Text, family, (int)(layer.Bold ? FontStyle.Bold : FontStyle.Regular), size, new PointF(0, 0),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            _textPaths[key] = path;
        }

        int a = (int)(alpha * 255);
        if (layer.Outline)
        {
            using var pen = new Pen(Color.FromArgb(Math.Min(a, 235), 0, 0, 0), size * 0.16f) { LineJoin = LineJoin.Round };
            _graphics.DrawPath(pen, path);
        }
        using var brush = new SolidBrush(Color.FromArgb(a, ParseColor(layer.Color)));
        _graphics.FillPath(brush, path);
    }

    private void DrawSticker(OverlayLayer layer, double alpha)
    {
        if (string.IsNullOrEmpty(layer.ImagePath)) return;
        if (!_images.TryGetValue(layer.ImagePath, out Bitmap? image))
        {
            try
            {
                using var loaded = Image.FromFile(layer.ImagePath);
                image = new Bitmap(loaded);
            }
            catch (Exception ex)
            {
                Log.Warn("Sticker unreadable: " + ex.Message);
                image = null;
            }
            _images[layer.ImagePath] = image;
        }
        if (image == null) return;

        float width = (float)(layer.Size * Width);
        float height = width * image.Height / image.Width;
        var matrix = new ColorMatrix { Matrix33 = (float)alpha };
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        _graphics.DrawImage(image, new Rectangle((int)(-width / 2), (int)(-height / 2), (int)width, (int)height),
            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
    }

    private static Color ParseColor(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch (Exception) { return Color.White; }
    }

    public void Dispose()
    {
        foreach (VideoSource? source in _sources.Values) source?.Dispose();
        foreach (Bitmap? image in _images.Values) image?.Dispose();
        foreach (GraphicsPath path in _textPaths.Values) path.Dispose();
        _graphics.Dispose();
        _bitmap.Dispose();
        ffmpeg.sws_freeContext(_scaler);
        ffmpeg.sws_freeContext(_toYuv);
        AVFrame* yuv = _yuv;
        ffmpeg.av_frame_free(&yuv);
        ffmpeg.av_free(Canvas);
    }
}
