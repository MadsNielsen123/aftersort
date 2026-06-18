using AfterSort.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace AfterSort.ViewModels.Components;

/// <summary>
/// Owns the LibVLC <see cref="MediaPlayer"/> lifecycle for the current video: load, play/pause,
/// seek, position/time tracking and disposal. The host view model only points it at a file path.
/// </summary>
public partial class VideoPlayerViewModel : ViewModelBase, IDisposable
{
    private readonly IVideoService _videoService;

    private MediaPlayer? _player;
    private Media? _media;
    private string? _path;

    // Guards the feedback loop when Position is updated from the player rather than the user.
    private bool _suppressSeek;

    public VideoPlayerViewModel(IVideoService videoService)
    {
        _videoService = videoService;
    }

    /// <summary>Bound to the VideoView control; null when nothing is loaded.</summary>
    [ObservableProperty]
    public partial MediaPlayer? Player { get; set; }

    /// <summary>True while the video is actively playing.</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>True while a player is on screen (playing or paused). Hides the thumbnail when set.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Playback position from 0.0 to 1.0. Setting it seeks the player.</summary>
    [ObservableProperty]
    public partial float Position { get; set; }

    [ObservableProperty]
    public partial string TimeText { get; set; } = "0:00 / 0:00";

    /// <summary>
    /// Points the player at a new file (or null to clear), stopping and disposing any current playback.
    /// Does not start playing — the thumbnail stays visible until the user hits play.
    /// </summary>
    public void SetSource(string? path)
    {
        Stop();
        _path = path;
    }

    [RelayCommand]
    private void Toggle()
    {
        if (_path is null)
            return;

        if (_player is { IsPlaying: true })
        {
            _player.Pause();
            IsPlaying = false;
            return;
        }

        // Paused player with media already loaded — resume in place.
        if (_player is { Media: not null })
        {
            _player.Play();
            IsPlaying = true;
            IsActive = true;
            return;
        }

        StartPlayback(position: 0f, autoPlay: true);
    }

    partial void OnPositionChanged(float value)
    {
        if (_suppressSeek)
            return;

        if (_player is not null)
            _player.Position = value;
        else if (_path is not null)
            StartPlayback(value, autoPlay: false); // Scrubbing before first play — start paused at the cursor.
    }

    private void StartPlayback(float position, bool autoPlay)
    {
        var player = _videoService.CreatePlayer();
        var media = _path is null ? null : _videoService.CreateMedia(_path);
        if (player is null || media is null)
        {
            player?.Dispose();
            media?.Dispose();
            return;
        }

        _player = player;
        _media = media;
        _player.PositionChanged += OnPlayerPositionChanged;
        _player.EndReached += OnEndReached;
        Player = _player; // Bind to XAML so VLC can start decoding.

        _player.Play(media);
        IsPlaying = autoPlay;

        // Wait for VLC to actually decode and start rendering before showing the VideoView.
        // This prevents a black flash from the native window appearing before content is ready.
        var target = _player;
        Task.Run(async () =>
        {
            for (var waits = 0; waits < 40 && target is { IsPlaying: false }; waits++)
                await Task.Delay(50);

            if (target is not { IsPlaying: true })
                return;

            // VLC is now rendering frames — safe to show the VideoView.
            Dispatcher.UIThread.Post(() =>
            {
                if (_player == target) // Guard against navigation during wait.
                    IsActive = true;
            });

            if (!autoPlay)
                target.Pause();
            if (position > 0f)
                target.Position = position;
        });
    }

    private void Stop()
    {
        if (_player is not null)
        {
            _player.PositionChanged -= OnPlayerPositionChanged;
            _player.EndReached -= OnEndReached;

            if (_player.IsPlaying)
                _player.Stop();

            var oldPlayer = _player;
            var oldMedia = _media;
            _player = null;
            _media = null;
            Player = null; // Detach from XAML before destroying native objects.

            Dispatcher.UIThread.Post(() =>
            {
                oldPlayer.Dispose();
                oldMedia?.Dispose();
            }, DispatcherPriority.Background);
        }

        IsPlaying = false;
        IsActive = false;
        SetPositionSilently(0f);
        TimeText = "0:00 / 0:00";
    }

    private void OnPlayerPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SetPositionSilently(e.Position);
            if (_player is not null)
            {
                var current = TimeSpan.FromMilliseconds(_player.Time);
                var total = TimeSpan.FromMilliseconds(_player.Length);
                TimeText = $"{FormatTime(current)} / {FormatTime(total)}";
            }
        });
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // LibVLC requires player operations triggered from EndReached to be dispatched off its event thread.
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            IsActive = false; // Reveals the thumbnail again; the player stays loaded for replay.
            SetPositionSilently(0f);
            TimeText = "0:00 / 0:00";
        });
    }

    private void SetPositionSilently(float value)
    {
        _suppressSeek = true;
        Position = value;
        _suppressSeek = false;
    }

    private static string FormatTime(TimeSpan t) =>
        t.Hours > 0
            ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
