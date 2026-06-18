using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace AfterSort.Services;

/// <summary>
/// LibVLC-backed video service. Lazily initialises a single shared <see cref="LibVLC"/> used for
/// both headless thumbnail capture (via video callbacks) and on-screen playback.
/// </summary>
public class VideoService : IVideoService
{
    private static readonly object InitLock = new();
    private static bool _coreInitialized;

    private LibVLC? _libVLC;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".flv",
    };

    public bool IsSupported(string extension) => VideoExtensions.Contains(extension);

    public MediaPlayer? CreatePlayer()
    {
        var libVLC = EnsureInitialized();
        return libVLC is null
            ? null
            : new MediaPlayer(libVLC) { EnableMouseInput = false, EnableKeyInput = false };
    }

    public Media? CreateMedia(string path)
    {
        var libVLC = EnsureInitialized();
        return libVLC is null ? null : new Media(libVLC, new Uri(path));
    }

    /// <summary>
    /// Loads the native libraries and returns the shared instance, or null if VLC is unavailable.
    /// </summary>
    private LibVLC? EnsureInitialized()
    {
        if (_libVLC != null)
            return _libVLC;

        lock (InitLock)
        {
            if (_libVLC != null)
                return _libVLC;

            try
            {
                if (!_coreInitialized)
                {
                    Core.Initialize();
                    _coreInitialized = true;
                }

                _libVLC = new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview");
            }
            catch
            {
                // VLC native libs missing — playback/thumbnails degrade gracefully to placeholders.
            }

            return _libVLC;
        }
    }

    public Bitmap? ExtractThumbnail(string videoPath, TimeSpan offset, int targetWidth = 400)
    {
        try
        {
            var libVLC = EnsureInitialized();
            if (libVLC is null)
                return GeneratePlaceholderThumbnail(targetWidth);

            using var media = new Media(libVLC, videoPath, FromType.FromPath);
            media.AddOption(":no-audio"); // Headless capture — no audio device needed.
            media.Parse(MediaParseOptions.ParseLocal, timeout: 5000).GetAwaiter().GetResult();

            if (!TryGetVideoSize(media, out var origWidth, out var origHeight))
                return GeneratePlaceholderThumbnail(targetWidth);

            // Maintain aspect ratio; RV32 needs even dimensions.
            var scale = Math.Min((float)targetWidth / origWidth, 1f);
            var thumbW = Math.Max(2u, ((uint)(origWidth * scale)) & ~1u);
            var thumbH = Math.Max(2u, ((uint)(origHeight * scale)) & ~1u);
            var pitch = thumbW * 4; // RV32 = 4 bytes/pixel (BGRA)
            var bufferSize = (int)(pitch * thumbH);

            var capturedFrame = CaptureFrame(media, offset, thumbW, thumbH, pitch, bufferSize);
            if (capturedFrame is null)
                return GeneratePlaceholderThumbnail(targetWidth);

            return FrameToBitmap(capturedFrame, (int)thumbW, (int)thumbH);
        }
        catch
        {
            return GeneratePlaceholderThumbnail(targetWidth);
        }
    }

    private static bool TryGetVideoSize(Media media, out uint width, out uint height)
    {
        var videoTrack = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        width = videoTrack.Data.Video.Width;
        height = videoTrack.Data.Video.Height;

        if (width == 0 || height == 0)
            return false;

        // Apply SAR (Sample Aspect Ratio) correction for anamorphic video.
        var sarNum = videoTrack.Data.Video.SarNum;
        var sarDen = videoTrack.Data.Video.SarDen;
        if (sarNum > 0 && sarDen > 0 && sarNum != sarDen)
            width = (uint)(width * sarNum / sarDen);

        // Swap dimensions for 90°/270° rotations (common in iPhone recordings).
        var orientation = videoTrack.Data.Video.Orientation;
        if (orientation is VideoOrientation.LeftTop
            or VideoOrientation.RightTop
            or VideoOrientation.RightBottom
            or VideoOrientation.LeftBottom)
        {
            (width, height) = (height, width);
        }

        return true;
    }

    /// <summary>
    /// Plays the media headlessly into a pinned buffer and copies out the first decoded frame
    /// at the requested position.
    /// </summary>
    private byte[]? CaptureFrame(Media media, TimeSpan offset, uint thumbW, uint thumbH, uint pitch, int bufferSize)
    {
        var frameBuffer = new byte[bufferSize];
        var handle = GCHandle.Alloc(frameBuffer, GCHandleType.Pinned);
        var bufferPtr = handle.AddrOfPinnedObject();

        byte[]? capturedFrame = null;
        var frameReady = new ManualResetEventSlim(false);
        var captureEnabled = false;

        MediaPlayer.LibVLCVideoLockCb lockCb = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, bufferPtr);
            return IntPtr.Zero;
        };
        MediaPlayer.LibVLCVideoUnlockCb unlockCb = (_, _, _) => { };
        MediaPlayer.LibVLCVideoDisplayCb displayCb = (_, _) =>
        {
            if (captureEnabled && !frameReady.IsSet)
            {
                capturedFrame = new byte[bufferSize];
                Array.Copy(frameBuffer, capturedFrame, bufferSize);
                frameReady.Set();
            }
        };

        using var player = new MediaPlayer(media);
        player.SetVideoFormat("RV32", thumbW, thumbH, pitch);
        player.SetVideoCallbacks(lockCb, unlockCb, displayCb);
        player.Play();

        try
        {
            if (!SpinWait.SpinUntil(() => player.IsPlaying, TimeSpan.FromSeconds(3)))
                return null;

            SeekForThumbnail(player, media.Duration, offset);

            captureEnabled = true;
            frameReady.Wait(TimeSpan.FromSeconds(5));
            player.Stop();

            // Delegates must stay alive until after Stop() returns.
            GC.KeepAlive(lockCb);
            GC.KeepAlive(unlockCb);
            GC.KeepAlive(displayCb);

            return capturedFrame;
        }
        finally
        {
            handle.Free();
        }
    }

    private static void SeekForThumbnail(MediaPlayer player, long durationMs, TimeSpan offset)
    {
        if (durationMs > 0 && offset > TimeSpan.Zero && offset.TotalMilliseconds < durationMs)
        {
            player.Position = (float)(offset.TotalMilliseconds / durationMs);
            Thread.Sleep(300); // Let VLC decode at the new position.
        }
        else if (durationMs > 2000)
        {
            player.Position = 0.1f; // Short video — grab an early frame.
            Thread.Sleep(200);
        }
    }

    private static Bitmap FrameToBitmap(byte[] frame, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var skBitmap = new SKBitmap(info);
        Marshal.Copy(frame, 0, skBitmap.GetPixels(), frame.Length);

        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    /// <summary>
    /// Draws a dark gradient with a play glyph for videos whose frame can't be captured.
    /// </summary>
    private static Bitmap GeneratePlaceholderThumbnail(int targetWidth)
    {
        var height = (int)(targetWidth * 0.75);
        var info = new SKImageInfo(targetWidth, height);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        using var gradientPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(targetWidth, height),
                [new SKColor(30, 30, 40), new SKColor(50, 50, 65)],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, targetWidth, height, gradientPaint);

        var cx = targetWidth / 2f;
        var cy = height / 2f;
        var radius = Math.Min(targetWidth, height) * 0.15f;

        using var circlePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 60),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawCircle(cx, cy, radius, circlePaint);

        using var trianglePaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        var triSize = radius * 0.55f;
        using var path = new SKPath();
        path.MoveTo(cx - triSize * 0.4f, cy - triSize);
        path.LineTo(cx - triSize * 0.4f, cy + triSize);
        path.LineTo(cx + triSize * 0.8f, cy);
        path.Close();
        canvas.DrawPath(path, trianglePaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    public void Dispose()
    {
        _libVLC?.Dispose();
        _libVLC = null;
        GC.SuppressFinalize(this);
    }
}
