using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace AfterSort.Services;

/// <summary>
/// Video service implementation using LibVLC for thumbnail extraction.
/// Uses VLC's video callbacks to capture a frame at ~5 seconds headlessly (no window).
/// </summary>
public class VideoService : IVideoService
{
    private LibVLC? _libVLC;
    private static bool _coreInitialized;
    private static readonly object _initLock = new();

    /// <summary>
    /// Ensures LibVLC native libraries are loaded and a LibVLC instance is ready.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_libVLC != null) return;
        lock (_initLock)
        {
            if (!_coreInitialized)
            {
                Core.Initialize();
                _coreInitialized = true;
            }
            _libVLC ??= new LibVLC("--no-audio", "--no-sub-autodetect-file", "--no-stats", "--no-osd",
                                    "--no-snapshot-preview", "--no-video-title-show");
        }
    }

    /// <inheritdoc/>
    public Bitmap? ExtractThumbnail(string videoPath, TimeSpan offset, int targetWidth = 400)
    {
        try
        {
            EnsureInitialized();
            if (_libVLC == null)
                return GeneratePlaceholderThumbnail(targetWidth);

            using var media = new Media(_libVLC, videoPath, FromType.FromPath);

            // Parse media to discover video track info (dimensions, duration)
            media.Parse(MediaParseOptions.ParseLocal, timeout: 5000).GetAwaiter().GetResult();

            // Find video track to get dimensions
            uint origWidth = 0, origHeight = 0;
            foreach (var track in media.Tracks)
            {
                if (track.TrackType == TrackType.Video)
                {
                    origWidth = track.Data.Video.Width;
                    origHeight = track.Data.Video.Height;
                    break;
                }
            }
            if (origWidth == 0 || origHeight == 0)
                return GeneratePlaceholderThumbnail(targetWidth);

            // Calculate thumbnail dimensions (maintain aspect ratio, ensure even numbers)
            float scale = Math.Min((float)targetWidth / origWidth, 1f);
            uint thumbW = ((uint)(origWidth * scale)) & ~1u;
            uint thumbH = ((uint)(origHeight * scale)) & ~1u;
            if (thumbW < 2) thumbW = 2;
            if (thumbH < 2) thumbH = 2;
            uint pitch = thumbW * 4; // RV32 = 4 bytes/pixel (BGRA)
            int bufferSize = (int)(pitch * thumbH);

            // Pin a managed buffer for VLC to write pixel data into
            var frameBuffer = new byte[bufferSize];
            var handle = GCHandle.Alloc(frameBuffer, GCHandleType.Pinned);
            var bufferPtr = handle.AddrOfPinnedObject();

            byte[]? capturedFrame = null;
            var frameReady = new ManualResetEventSlim(false);
            var captureEnabled = false;

            // Prevent delegates from being GC'd during native callbacks
            MediaPlayer.LibVLCVideoLockCb lockCb = (IntPtr opaque, IntPtr planes) =>
            {
                Marshal.WriteIntPtr(planes, bufferPtr);
                return IntPtr.Zero;
            };
            MediaPlayer.LibVLCVideoUnlockCb unlockCb = (IntPtr opaque, IntPtr picture, IntPtr planes) => { };
            MediaPlayer.LibVLCVideoDisplayCb displayCb = (IntPtr opaque, IntPtr picture) =>
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

            // Wait for playback to actually start
            if (!SpinWait.SpinUntil(() => player.IsPlaying, TimeSpan.FromSeconds(3)))
            {
                player.Stop();
                handle.Free();
                return GeneratePlaceholderThumbnail(targetWidth);
            }

            // Seek to the requested offset
            var duration = media.Duration; // milliseconds
            if (duration > 0 && offset.TotalMilliseconds < duration && offset > TimeSpan.Zero)
            {
                player.Position = (float)(offset.TotalMilliseconds / duration);
                Thread.Sleep(300); // Let VLC decode at the new position
            }
            else if (duration > 2000)
            {
                // For short videos, just go 10% in
                player.Position = 0.1f;
                Thread.Sleep(200);
            }

            // Enable capture and wait for the next fully decoded frame
            captureEnabled = true;
            frameReady.Wait(TimeSpan.FromSeconds(5));

            // Stop player — after this, no more callbacks
            player.Stop();

            // Keep delegate references alive until after Stop()
            GC.KeepAlive(lockCb);
            GC.KeepAlive(unlockCb);
            GC.KeepAlive(displayCb);

            handle.Free();

            if (capturedFrame == null)
                return GeneratePlaceholderThumbnail(targetWidth);

            // Convert RV32 (BGRA) frame data to Avalonia Bitmap via SkiaSharp
            var skInfo = new SKImageInfo((int)thumbW, (int)thumbH, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var skBitmap = new SKBitmap(skInfo);
            Marshal.Copy(capturedFrame, 0, skBitmap.GetPixels(), capturedFrame.Length);

            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Seek(0, SeekOrigin.Begin);
            return new Bitmap(ms);
        }
        catch
        {
            return GeneratePlaceholderThumbnail(targetWidth);
        }
    }



    /// <summary>
    /// Generates a styled placeholder thumbnail when frame extraction fails.
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
                new[] { new SKColor(30, 30, 40), new SKColor(50, 50, 65) },
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
        var path = new SKPath();
        path.MoveTo(cx - triSize * 0.4f, cy - triSize);
        path.LineTo(cx - triSize * 0.4f, cy + triSize);
        path.LineTo(cx + triSize * 0.8f, cy);
        path.Close();
        canvas.DrawPath(path, trianglePaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Seek(0, SeekOrigin.Begin);
        return new Bitmap(ms);
    }

    public void Dispose()
    {
        _libVLC?.Dispose();
        _libVLC = null;
        GC.SuppressFinalize(this);
    }
}
