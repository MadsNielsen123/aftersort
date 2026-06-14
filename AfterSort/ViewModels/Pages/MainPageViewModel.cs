using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AfterSort.Models;
using AfterSort.Services;
using AfterSort.ViewModels.Components;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace AfterSort.ViewModels.Pages;

public partial class MainPageViewModel : ViewModelBase
{
    #region Fields

    private readonly IStorageService _storageService;
    private readonly ISortService _sortService;
    private readonly IVideoService _videoService;

    // === LibVLCSharp video playback ===
    private LibVLC? _libVLC;
    private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;

    /// <summary>
    /// Flattened list of all files across all input folders (or a single folder if selected).
    /// Rebuilt whenever input folders or their contents change.
    /// </summary>
    private readonly List<FileItem> _flatFileList = [];

    // === Preview pipeline state ===
    private CancellationTokenSource? _navigationCts;
    private readonly ConcurrentDictionary<string, Bitmap> _previewCache = new();
    
    /// <summary>
    /// Tracks which file paths are currently cached for efficient eviction.
    /// Stores (path, insertionOrder) for LRU-like eviction.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _cacheInsertionOrder = new();
    private long _cacheInsertionCounter;
    
    /// <summary>
    /// Maximum number of full-quality bitmaps to keep in cache.
    /// Higher = more memory, faster browsing of recently viewed files.
    /// </summary>
    private const int MaxFullQualityCache = 30;
    
    /// <summary>
    /// Maximum number of preload thumbnails to keep in memory.
    /// </summary>
    private const int MaxPreloadCache = 100;
    
    /// <summary>
    /// Throttles concurrent disk I/O to prevent thrashing.
    /// </summary>
    private readonly SemaphoreSlim _ioSemaphore = new(4, 4);
    
    /// <summary>
    /// Debounce timer for rapid navigation.
    /// </summary>
    private CancellationTokenSource? _debounceCts;

    /// <summary>
    /// Set of file extensions known to be unsupported for image display.
    /// </summary>
    private static readonly HashSet<string> UnsupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2",
        ".pef", ".srw", ".raf", ".3fr", ".kdc", ".dcr", ".raw", ".rwl", ".mrw",
        ".nrw", ".srf", ".sr2", ".erf", ".mef", ".mos", ".psd", ".ai", ".eps",
        ".svg", ".tga",
    };

    #endregion

    #region Properties

    public FolderSelectComponentViewModel InputFolders { get; }
    public FolderSelectComponentViewModel DestFolders { get; }

    /// <summary>
    /// The currently active sort mode.
    /// </summary>
    [ObservableProperty]
    public partial SortMode CurrentSortMode { get; set; } = SortMode.Multiple;

    /// <summary>
    /// Zero-based index into the flat file list.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFileNumber))]
    [NotifyPropertyChangedFor(nameof(CurrentFile))]
    [NotifyPropertyChangedFor(nameof(HasCurrentFile))]
    public partial int CurrentFileIndex { get; set; }

    /// <summary>
    /// Total number of files in the current view.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentFile))]
    public partial int TotalFileCount { get; set; }

    /// <summary>
    /// 1-based display number of the current file.
    /// </summary>
    public int CurrentFileNumber => CurrentFileIndex + 1;

    /// <summary>
    /// Whether there is at least one file to display.
    /// </summary>
    public bool HasCurrentFile => TotalFileCount > 0;

    /// <summary>
    /// The currently displayed file item.
    /// </summary>
    public FileItem? CurrentFile =>
        _flatFileList.Count > 0 && CurrentFileIndex >= 0 && CurrentFileIndex < _flatFileList.Count
            ? _flatFileList[CurrentFileIndex]
            : null;



    /// <summary>
    /// Bitmap for image file preview. Null when the current file is not an image.
    /// </summary>
    [ObservableProperty]
    public partial Bitmap? PreviewImage { get; set; }

    /// <summary>
    /// Preview image for the previous file in the queue.
    /// </summary>
    [ObservableProperty]
    public partial Bitmap? PreviousPreviewImage { get; set; }

    /// <summary>
    /// Preview image for the next file in the queue.
    /// </summary>
    [ObservableProperty]
    public partial Bitmap? NextPreviewImage { get; set; }

    /// <summary>
    /// Text content for non-image file preview. Null when the current file is an image.
    /// </summary>
    [ObservableProperty]
    public partial string? PreviewText { get; set; }

    /// <summary>
    /// Whether the current file is an image (controls which preview panel is shown).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsShowingVideoThumbnail))]
    [NotifyPropertyChangedFor(nameof(IsShowingTextPreview))]
    public partial bool IsCurrentFileImage { get; set; }

    /// <summary>
    /// Whether the current file is a video (controls video player UI visibility).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsShowingVideoThumbnail))]
    [NotifyPropertyChangedFor(nameof(IsShowingTextPreview))]
    public partial bool IsCurrentFileVideo { get; set; }

    /// <summary>
    /// Whether the video is currently playing.
    /// </summary>
    [ObservableProperty]
    public partial bool IsVideoPlaying { get; set; }

    /// <summary>
    /// Whether the video player is active (playing or paused).
    /// Keeps the video view visible when paused.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsShowingVideoThumbnail))]
    [NotifyPropertyChangedFor(nameof(IsShowingTextPreview))]
    public partial bool IsVideoActive { get; set; }

    /// <summary>
    /// The LibVLCSharp MediaPlayer instance for video playback.
    /// Bound to the VideoView control in XAML.
    /// </summary>
    [ObservableProperty]
    public partial LibVLCSharp.Shared.MediaPlayer? VideoPlayer { get; set; }

    /// <summary>
    /// Current video playback position (0.0 to 1.0).
    /// When the user drags the slider, this triggers a seek on the media player.
    /// </summary>
    [ObservableProperty]
    public partial float VideoPosition { get; set; }

    /// <summary>
    /// Guard flag to prevent feedback loop when updating VideoPosition from the player.
    /// </summary>
    private bool _isUpdatingPositionFromPlayer;

    partial void OnVideoPositionChanged(float value)
    {
        if (_isUpdatingPositionFromPlayer) return;

        if (_mediaPlayer is { IsPlaying: true } or { Media: not null })
        {
            _mediaPlayer.Position = value;
        }
        else if (IsCurrentFileVideo && CurrentFile != null)
        {
            StartPlayerPausedAt(value);
        }
    }

    private void StartPlayerPausedAt(float position)
    {
        var file = CurrentFile;
        if (file == null || !file.IsVideo) return;

        EnsureLibVLCInitialized();
        if (_libVLC == null) return;

        if (_mediaPlayer != null) return; // Already starting

        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC)
        {
            EnableMouseInput = false,
            EnableKeyInput = false
        };
        _mediaPlayer.PositionChanged += OnPlayerPositionChanged;
        _mediaPlayer.EndReached += OnVideoEndReached;
        VideoPlayer = _mediaPlayer;

        var media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(file.FullPath));
        _mediaPlayer.Play(media);
        
        IsVideoPlaying = false;
        IsVideoActive = true;

        // Wait for playback to actually begin, then pause and seek to the slider position
        System.Threading.Tasks.Task.Run(async () =>
        {
            // SpinWait until IsPlaying is true, max 2 seconds
            int waits = 0;
            while (_mediaPlayer != null && !_mediaPlayer.IsPlaying && waits < 40)
            {
                await System.Threading.Tasks.Task.Delay(50);
                waits++;
            }

            if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Position = position;
            }
        });
    }

    /// <summary>
    /// Formatted current time of the video.
    /// </summary>
    [ObservableProperty]
    public partial string VideoTimeText { get; set; } = "0:00 / 0:00";

    // === Computed visibility properties for the XAML ===

    /// <summary>
    /// True when showing a non-video image preview (thumbnail or full quality).
    /// </summary>
    public bool IsShowingImagePreview => IsCurrentFileImage && !IsCurrentFileVideo && !IsVideoActive;

    /// <summary>
    /// True when showing a video thumbnail with a play overlay (not yet playing).
    /// </summary>
    public bool IsShowingVideoThumbnail => IsCurrentFileVideo && !IsVideoActive;

    /// <summary>
    /// True when showing text preview (non-image, non-video files).
    /// </summary>
    public bool IsShowingTextPreview => !IsCurrentFileImage && !IsCurrentFileVideo && !IsVideoActive;

    /// <summary>
    /// Whether the full high-res data is currently loading.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoadingFullData { get; set; }

    #endregion

    #region Constructors

    public MainPageViewModel(IStorageService storageService, ISortService sortService, IVideoService videoService)
    {
        _storageService = storageService;
        _sortService = sortService;
        _videoService = videoService;
        InputFolders = new FolderSelectComponentViewModel { Title = "Input Folders" };
        DestFolders = new FolderSelectComponentViewModel { Title = "Dest Folders" };

        InputFolders.ItemFactory = async () =>
        {
            var result = await _storageService.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Input Folder" });
            if (result is { Count: > 0 })
            {
                var vm = new InputFolderComponentViewModel
                {
                    FolderName = result[0].Name,
                    FolderPath = result[0].Path.LocalPath,
                };

                // Scan the folder and populate the file list / TotalFiles count.
                vm.LoadFiles();

                return vm;
            }
            return null;
        };

        DestFolders.ItemFactory = async () =>
        {
            var result = await _storageService.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Destination Folder" });
            if (result is { Count: > 0 })
            {
                return new OutputFolderComponentViewModel
                {
                    FolderName = result[0].Name,
                    FolderPath = result[0].Path.LocalPath
                };
            }
            return null;
        };

        // Rebuild the flat file list whenever input folders are added/removed.
        InputFolders.Items.CollectionChanged += OnInputFoldersChanged;

        // Rebuild the flat file list when a specific input folder is selected.
        InputFolders.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FolderSelectComponentViewModel.SelectedItem))
                RebuildFlatFileList();
        };
    }

    #endregion

    #region Lifecycle
    #endregion

    #region Methods

    /// <summary>
    /// Handles input folder collection changes — rebuilds the flat file list.
    /// </summary>
    private void OnInputFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is InputFolderComponentViewModel inputFolder)
                {
                    inputFolder.FilesAdded += OnInputFolderFilesAdded;
                }
            }
        }
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is InputFolderComponentViewModel inputFolder)
                {
                    inputFolder.FilesAdded -= OnInputFolderFilesAdded;
                }
            }
        }

        RebuildFlatFileList();
    }

    private void OnInputFolderFilesAdded(object? sender, IEnumerable<FileItem> newFiles)
    {
        var selectedInput = InputFolders.SelectedItem as InputFolderComponentViewModel;
        
        // If we are showing all folders, or if the sender is the selected folder, append the items.
        if (selectedInput == null || selectedInput == sender)
        {
            int oldCount = _flatFileList.Count;
            foreach (var file in newFiles)
            {
                _flatFileList.Add(file);
            }
            
            TotalFileCount = _flatFileList.Count;
            
            if (oldCount == 0 && TotalFileCount > 0)
            {
                CurrentFileIndex = 0;
                OnPropertyChanged(nameof(CurrentFile));
                OnPropertyChanged(nameof(HasCurrentFile));
                LoadCurrentFilePreview();
            }
            else
            {
                // Just update the total count without snapping the index or refreshing the image
                OnPropertyChanged(nameof(HasCurrentFile));
            }
            
            OnPropertyChanged(nameof(CurrentFileNumber));
        }
    }

    /// <summary>
    /// Rebuilds the flat file list based on the currently selected input folder,
    /// or all folders if none is selected.
    /// </summary>
    private void RebuildFlatFileList()
    {
        var oldCurrentFile = CurrentFile;

        _flatFileList.Clear();

        var selectedInput = InputFolders.SelectedItem as InputFolderComponentViewModel;

        if (selectedInput is not null)
        {
            // Only show files from the selected folder.
            foreach (var file in selectedInput.Files)
                _flatFileList.Add(file);
        }
        else
        {
            // Show files from all input folders.
            foreach (var item in InputFolders.Items)
            {
                if (item is InputFolderComponentViewModel inputFolder)
                {
                    foreach (var file in inputFolder.Files)
                        _flatFileList.Add(file);
                }
            }
        }

        TotalFileCount = _flatFileList.Count;

        if (oldCurrentFile != null)
        {
            var newIndex = _flatFileList.IndexOf(oldCurrentFile);
            CurrentFileIndex = newIndex >= 0 ? newIndex : (TotalFileCount > 0 ? 0 : 0);
        }
        else
        {
            CurrentFileIndex = TotalFileCount > 0 ? 0 : 0;
        }

        // Force UI updates since CurrentFile is a computed property
        OnPropertyChanged(nameof(CurrentFile));
        OnPropertyChanged(nameof(HasCurrentFile));
        OnPropertyChanged(nameof(CurrentFileNumber));

        LoadCurrentFilePreview();
    }

    /// <summary>
    /// Returns true if the file extension is an unsupported image format (HEIC, RAW, etc.)
    /// that SkiaSharp cannot decode.
    /// </summary>
    private static bool IsUnsupportedImageFormat(FileItem file)
    {
        return UnsupportedImageExtensions.Contains(file.Metadata.Extension);
    }

    /// <summary>
    /// Loads the preview content for the current file.
    /// This method NEVER blocks on I/O — it shows cached/preloaded data instantly,
    /// then kicks off background work for full quality + neighbor preloading.
    /// </summary>
    private void LoadCurrentFilePreview()
    {
        // Cancel any previous navigation pipeline
        _navigationCts?.Cancel();
        _navigationCts = new CancellationTokenSource();
        var token = _navigationCts.Token;

        var file = CurrentFile;
        int currentIndex = CurrentFileIndex;

        // Stop any active video playback when navigating
        StopVideoPlayback();

        if (file is null)
        {
            IsCurrentFileImage = false;
            IsCurrentFileVideo = false;
            IsLoadingFullData = false;
            PreviewImage = null;
            PreviewText = null;
            return;
        }

        // === PHASE 1: Instant UI update (no I/O, no awaiting) ===
        
        // Handle unsupported formats immediately — no loading, no waiting
        if (IsUnsupportedImageFormat(file))
        {
            PreviewImage = null;
            PreviewText = $"Unsupported format: {file.Metadata.Extension.ToUpperInvariant()}\n\n{file.Name}\n\n({file.Metadata.FormattedSize})";
            IsCurrentFileImage = false;
            IsCurrentFileVideo = false;
            IsLoadingFullData = false;
            // Still kick off neighbor preloading
            _ = Task.Run(() => PreloadNeighborsAsync(currentIndex, token), token);
            return;
        }

        // Check if we already have full quality cached
        if (file.IsImage && _previewCache.TryGetValue(file.FullPath, out var cachedFull))
        {
            PreviewImage = cachedFull;
            PreviewText = null;
            IsCurrentFileImage = true;
            IsCurrentFileVideo = false;
            IsLoadingFullData = false;
            // Kick off neighbor preloading only
            _ = Task.Run(() => PreloadNeighborsAsync(currentIndex, token), token);
            return;
        }

        // Check if we have preload data already
        if (file.PreloadData is Bitmap preloadBmp)
        {
            PreviewImage = preloadBmp;
            PreviewText = null;
            IsCurrentFileImage = file.IsImage || file.IsVideo;
            IsCurrentFileVideo = file.IsVideo;
            IsLoadingFullData = file.IsImage; // Full quality still needs loading for images
        }
        else if (file.PreloadData is string preloadStr)
        {
            PreviewText = preloadStr;
            PreviewImage = null;
            IsCurrentFileImage = false;
            IsCurrentFileVideo = false;
            IsLoadingFullData = !file.IsImage && !file.IsVideo; // Full text still loading for text files
        }
        else if (file.IsVideo)
        {
            // No preload yet for video — show placeholder immediately
            PreviewImage = null;
            PreviewText = null;
            IsCurrentFileImage = true;
            IsCurrentFileVideo = true;
            IsLoadingFullData = true;
        }
        else
        {
            // No preload yet — show loading state
            PreviewImage = null;
            PreviewText = file.IsImage ? null : null;
            IsCurrentFileImage = file.IsImage;
            IsCurrentFileVideo = false;
            IsLoadingFullData = true;
        }

        // === PHASE 2: Background pipeline (ordered by priority) ===
        _ = RunBackgroundPipelineAsync(file, currentIndex, token);
        
        UpdateNeighborPreviews();
    }

    private void UpdateNeighborPreviews()
    {
        var currentIndex = CurrentFileIndex;
        
        Bitmap? prevBmp = null;
        if (currentIndex > 0 && currentIndex - 1 < _flatFileList.Count)
        {
            prevBmp = _flatFileList[currentIndex - 1].PreloadData as Bitmap;
        }
        PreviousPreviewImage = prevBmp;

        Bitmap? nextBmp = null;
        if (currentIndex + 1 < _flatFileList.Count)
        {
            nextBmp = _flatFileList[currentIndex + 1].PreloadData as Bitmap;
        }
        NextPreviewImage = nextBmp;
    }

    /// <summary>
    /// Background pipeline that loads data in priority order:
    /// 1. Current file preload (if missing)
    /// 2. Current file full quality
    /// 3. Neighbor preloads
    /// 4. Neighbor full qualities
    /// </summary>
    private async Task RunBackgroundPipelineAsync(FileItem file, int currentIndex, CancellationToken token)
    {
        try
        {
            // Priority 1: Current file preload thumbnail (if not already loaded)
            if (file.PreloadData == null)
            {
                var preloadData = await Task.Run(() =>
                {
                    try { return GeneratePreloadData(file); }
                    catch (OperationCanceledException) { return null; }
                }, token);

                if (token.IsCancellationRequested) return;

                if (preloadData != null)
                {
                    file.PreloadData = preloadData;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (preloadData is Bitmap bmp)
                        {
                            PreviewImage = bmp;
                            PreviewText = null;
                            IsCurrentFileImage = file.IsImage || file.IsVideo;
                            IsCurrentFileVideo = file.IsVideo;
                        }
                        else if (preloadData is string str)
                        {
                            PreviewText = str;
                            PreviewImage = null;
                            IsCurrentFileImage = false;
                            IsCurrentFileVideo = false;
                        }
                    });
                }
            }

            if (token.IsCancellationRequested) return;

            // Priority 2: Current file full quality (start immediately, don't wait for neighbors)
            var fullQualityTask = LoadFullQualityForCurrentAsync(file, token);

            // Priority 3: Start neighbor preloading concurrently with full quality
            var neighborTask = PreloadNeighborsAsync(currentIndex, token);

            // Wait for the current file's full quality first
            await fullQualityTask;
            if (token.IsCancellationRequested) return;

            // Wait for neighbors
            await neighborTask;
            if (token.IsCancellationRequested) return;

            // Priority 4: Neighbor full qualities (lower priority, only after current is done)
            await LoadNeighborFullQualitiesAsync(currentIndex, 5, token);

            // Memory cleanup (efficient — only checks cached items, not all files)
            EnforceMemoryLimits(currentIndex);
        }
        catch (OperationCanceledException)
        {
            // Expected when navigation changes — silently ignore
        }
        catch (Exception)
        {
            // Don't crash the app on unexpected errors
        }
    }

    /// <summary>
    /// Loads full quality for the current file and posts to UI.
    /// </summary>
    private async Task LoadFullQualityForCurrentAsync(FileItem file, CancellationToken token)
    {
        if (file.IsImage && !_previewCache.ContainsKey(file.FullPath))
        {
            var fullBmp = await Task.Run(() =>
            {
                try { return LoadImageWithExif(file.FullPath); }
                catch (OperationCanceledException) { return null; }
            }, token);

            if (fullBmp != null && !token.IsCancellationRequested)
            {
                _previewCache.TryAdd(file.FullPath, fullBmp);
                _cacheInsertionOrder.TryAdd(file.FullPath, Interlocked.Increment(ref _cacheInsertionCounter));

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    PreviewImage = fullBmp;
                    IsLoadingFullData = false;
                });
            }
            else if (!token.IsCancellationRequested)
            {
                // Image failed to load
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    IsLoadingFullData = false;
                    if (PreviewImage == null)
                    {
                        PreviewText = $"Unable to load image: {file.Name}";
                        IsCurrentFileImage = false;
                    }
                });
            }
        }
        else if (!file.IsImage && !file.IsVideo)
        {
            // Text file — load full content
            try
            {
                var fullText = await Task.Run(() =>
                {
                    var fileInfo = new FileInfo(file.FullPath);
                    return fileInfo.Length > 1_048_576
                        ? File.ReadAllText(file.FullPath)[..1_048_576] + "\n\n--- File truncated (>1 MB) ---"
                        : File.ReadAllText(file.FullPath);
                }, token);

                if (!token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        PreviewText = fullText;
                        IsLoadingFullData = false;
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        PreviewText = $"Unable to read file: {file.Name}";
                        IsLoadingFullData = false;
                    });
                }
            }
        }
        else if (file.IsVideo)
        {
            // Video — just mark as loaded once thumbnail is available
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                IsLoadingFullData = false;
            });
        }
        else
        {
            // Image already cached
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                IsLoadingFullData = false;
            });
        }
    }

    /// <summary>
    /// Preloads neighbor thumbnails in parallel with concurrency throttling.
    /// Prioritizes forward direction (next files) slightly over backward.
    /// </summary>
    private async Task PreloadNeighborsAsync(int currentIndex, CancellationToken token)
    {
        const int range = 15;
        int total = _flatFileList.Count;
        
        // Build a priority-ordered list: next files first (more likely to be viewed), then previous
        var indicesToPreload = new List<int>();
        for (int offset = 1; offset <= range; offset++)
        {
            int next = currentIndex + offset;
            if (next < total) indicesToPreload.Add(next);
            
            int prev = currentIndex - offset;
            if (prev >= 0) indicesToPreload.Add(prev);
        }

        // Process in parallel batches with throttled concurrency
        var tasks = new List<Task>();
        foreach (var idx in indicesToPreload)
        {
            if (token.IsCancellationRequested) return;
            
            var f = _flatFileList[idx];
            if (f.PreloadData != null) continue; // Already loaded
            if (IsUnsupportedImageFormat(f))
            {
                // Mark unsupported files with a text placeholder so we skip them fast
                f.PreloadData = $"Unsupported format: {f.Metadata.Extension.ToUpperInvariant()}\n\n{f.Name}\n\n({f.Metadata.FormattedSize})";
                continue;
            }

            tasks.Add(PreloadSingleFileAsync(f, token));
            
            // Limit in-flight tasks to prevent excessive memory pressure
            if (tasks.Count >= 4)
            {
                await Task.WhenAny(tasks);
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Preloads a single file's thumbnail with I/O throttling.
    /// </summary>
    private async Task PreloadSingleFileAsync(FileItem file, CancellationToken token)
    {
        await _ioSemaphore.WaitAsync(token);
        try
        {
            if (token.IsCancellationRequested || file.PreloadData != null) return;
            file.PreloadData = await Task.Run(() =>
            {
                try { return GeneratePreloadData(file); }
                catch (OperationCanceledException) { return null; }
                catch { return null; }
            }, token);

            if (file.PreloadData != null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        UpdateNeighborPreviews();
                    }
                });
            }
        }
        finally
        {
            _ioSemaphore.Release();
        }
    }

    /// <summary>
    /// Loads full quality images for neighboring files.
    /// </summary>
    private async Task LoadNeighborFullQualitiesAsync(int currentIndex, int range, CancellationToken token)
    {
        int total = _flatFileList.Count;
        
        // Prioritize forward direction
        for (int offset = 1; offset <= range; offset++)
        {
            if (token.IsCancellationRequested) return;

            int next = currentIndex + offset;
            if (next < total && _flatFileList[next].IsImage && !_previewCache.ContainsKey(_flatFileList[next].FullPath)
                && !IsUnsupportedImageFormat(_flatFileList[next]))
            {
                var f = _flatFileList[next];
                await _ioSemaphore.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;
                    var bmp = await Task.Run(() =>
                    {
                        try { return LoadImageWithExif(f.FullPath); }
                        catch { return null; }
                    }, token);
                    if (bmp != null)
                    {
                        _previewCache.TryAdd(f.FullPath, bmp);
                        _cacheInsertionOrder.TryAdd(f.FullPath, Interlocked.Increment(ref _cacheInsertionCounter));
                    }
                }
                finally
                {
                    _ioSemaphore.Release();
                }
            }

            if (token.IsCancellationRequested) return;

            int prev = currentIndex - offset;
            if (prev >= 0 && _flatFileList[prev].IsImage && !_previewCache.ContainsKey(_flatFileList[prev].FullPath)
                && !IsUnsupportedImageFormat(_flatFileList[prev]))
            {
                var f = _flatFileList[prev];
                await _ioSemaphore.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;
                    var bmp = await Task.Run(() =>
                    {
                        try { return LoadImageWithExif(f.FullPath); }
                        catch { return null; }
                    }, token);
                    if (bmp != null)
                    {
                        _previewCache.TryAdd(f.FullPath, bmp);
                        _cacheInsertionOrder.TryAdd(f.FullPath, Interlocked.Increment(ref _cacheInsertionCounter));
                    }
                }
                finally
                {
                    _ioSemaphore.Release();
                }
            }
        }
    }

    private object? GeneratePreloadData(FileItem f)
    {
        if (f.IsImage)
        {
            return LoadThumbnailWithExif(f.FullPath, 400);
        }
        else if (f.IsVideo)
        {
            // Extract a real frame from the video at ~5 seconds
            return _videoService.ExtractThumbnail(f.FullPath, TimeSpan.FromSeconds(5), 400);
        }
        else
        {
            try
            {
                using var reader = new StreamReader(f.FullPath);
                var buffer = new char[3000];
                int read = reader.Read(buffer, 0, 3000);
                return new string(buffer, 0, read) + (read == 3000 ? "\n..." : "");
            }
            catch { return "Unable to read file."; }
        }
    }

    /// <summary>
    /// Efficient memory management that only iterates cached items, not all files.
    /// Uses LRU-like eviction based on insertion order.
    /// </summary>
    private void EnforceMemoryLimits(int currentIndex)
    {
        // 1. Evict full quality cache entries beyond the limit
        if (_previewCache.Count > MaxFullQualityCache)
        {
            // Build the set of paths we want to keep (±10 from current)
            var pathsToKeep = new HashSet<string>();
            int total = _flatFileList.Count;
            for (int i = Math.Max(0, currentIndex - 10); i <= Math.Min(total - 1, currentIndex + 10); i++)
            {
                pathsToKeep.Add(_flatFileList[i].FullPath);
            }

            // Find entries to evict: not in keep range, ordered by oldest insertion first
            var evictCandidates = _cacheInsertionOrder
                .Where(kvp => !pathsToKeep.Contains(kvp.Key))
                .OrderBy(kvp => kvp.Value)
                .Take(_previewCache.Count - MaxFullQualityCache + 5) // Evict a few extra to avoid thrashing
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in evictCandidates)
            {
                if (_previewCache.TryRemove(key, out var oldBmp))
                {
                    _cacheInsertionOrder.TryRemove(key, out _);
                    Dispatcher.UIThread.Post(() => oldBmp.Dispose());
                }
            }
        }

        // 2. Evict preload data far from current position
        // Only check items outside ±MaxPreloadCache/2 window
        int preloadRadius = MaxPreloadCache / 2;
        int total2 = _flatFileList.Count;
        
        // Scan outward from the boundary instead of scanning all files
        int lowerBound = Math.Max(0, currentIndex - preloadRadius);
        int upperBound = Math.Min(total2 - 1, currentIndex + preloadRadius);

        // Clear items below the lower bound (scan from start to lower bound)
        // Only scan a limited range to avoid O(n) on every navigation
        int scanStart = Math.Max(0, lowerBound - 100);
        for (int i = scanStart; i < lowerBound; i++)
        {
            var file = _flatFileList[i];
            if (file.PreloadData is Bitmap bmp)
            {
                Dispatcher.UIThread.Post(() => bmp.Dispose());
            }
            file.PreloadData = null;
        }

        // Clear items above the upper bound
        int scanEnd = Math.Min(total2, upperBound + 100);
        for (int i = upperBound + 1; i < scanEnd; i++)
        {
            var file = _flatFileList[i];
            if (file.PreloadData is Bitmap bmp)
            {
                Dispatcher.UIThread.Post(() => bmp.Dispose());
            }
            file.PreloadData = null;
        }
    }

    /// <summary>
    /// Ensures LibVLC is initialized for video playback.
    /// </summary>
    private void EnsureLibVLCInitialized()
    {
        if (_libVLC != null) return;

        try
        {
            Core.Initialize();
            _libVLC = new LibVLC("--no-video-title-show");
        }
        catch
        {
            // LibVLC not available — playback won't work but app won't crash
        }
    }

    /// <summary>
    /// Stops any active video playback and cleans up the media player.
    /// </summary>
    private void StopVideoPlayback()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.PositionChanged -= OnPlayerPositionChanged;
            _mediaPlayer.EndReached -= OnVideoEndReached;

            if (_mediaPlayer.IsPlaying)
                _mediaPlayer.Stop();

            var oldPlayer = _mediaPlayer;
            var media = _mediaPlayer.Media;
            
            _mediaPlayer = null;
            VideoPlayer = null; // Detach XAML first

            // Dispatch dispose to ensure XAML has fully detached before destroying native objects
            Dispatcher.UIThread.Post(() => 
            {
                oldPlayer.Dispose();
                media?.Dispose();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        IsVideoPlaying = false;
        IsVideoActive = false;
        _isUpdatingPositionFromPlayer = true;
        VideoPosition = 0;
        _isUpdatingPositionFromPlayer = false;
        VideoTimeText = "0:00 / 0:00";
    }

    /// <summary>
    /// Toggle play/pause for the current video file.
    /// </summary>
    [RelayCommand]
    private void ToggleVideoPlayback()
    {
        var file = CurrentFile;
        if (file == null || !file.IsVideo) return;

        EnsureLibVLCInitialized();
        if (_libVLC == null) return;

        // If currently playing → pause
        if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            IsVideoPlaying = false;
            // Note: IsVideoActive remains true to keep the view visible
            return;
        }

        // If we have a media player with media loaded (paused state) → resume
        if (_mediaPlayer != null && _mediaPlayer.Media != null)
        {
            _mediaPlayer.Play();
            IsVideoPlaying = true;
            IsVideoActive = true;
            return;
        }

        // Dispose previous player if any
        if (_mediaPlayer != null)
        {
            _mediaPlayer.PositionChanged -= OnPlayerPositionChanged;
            _mediaPlayer.EndReached -= OnVideoEndReached;
            
            var oldPlayer = _mediaPlayer;
            _mediaPlayer = null;
            VideoPlayer = null; // Detach XAML
            
            Dispatcher.UIThread.Post(() => 
            {
                oldPlayer.Dispose();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        // Create new media player for this file
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC)
        {
            EnableMouseInput = false,
            EnableKeyInput = false
        };
        _mediaPlayer.PositionChanged += OnPlayerPositionChanged;
        _mediaPlayer.EndReached += OnVideoEndReached;

        VideoPlayer = _mediaPlayer;

        // Don't use `using` — Media needs to stay alive for seeking/resume to work
        var media = new Media(_libVLC, new Uri(file.FullPath));
        _mediaPlayer.Play(media);
        IsVideoPlaying = true;
        IsVideoActive = true;
    }

    private void OnPlayerPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isUpdatingPositionFromPlayer = true;
            VideoPosition = e.Position;
            _isUpdatingPositionFromPlayer = false;
            if (_mediaPlayer != null)
            {
                var current = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
                var total = TimeSpan.FromMilliseconds(_mediaPlayer.Length);
                VideoTimeText = $"{FormatTime(current)} / {FormatTime(total)}";
            }
        });
    }

    private void OnVideoEndReached(object? sender, EventArgs e)
    {
        // LibVLCSharp requires media player operations from EndReached to be dispatched
        // to avoid deadlocking VLC's event thread.
        Dispatcher.UIThread.Post(() =>
        {
            IsVideoPlaying = false;
            IsVideoActive = false;
            _isUpdatingPositionFromPlayer = true;
            VideoPosition = 0;
            _isUpdatingPositionFromPlayer = false;
            VideoTimeText = "0:00 / 0:00";

            // Show the thumbnail again
            var file = CurrentFile;
            if (file != null)
            {
                IsCurrentFileImage = true;
                IsCurrentFileVideo = true;
                if (file.PreloadData is Bitmap preloadBmp)
                {
                    PreviewImage = preloadBmp;
                }
            }
        });
    }

    private static string FormatTime(TimeSpan t)
    {
        return t.Hours > 0
            ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    private Bitmap? LoadImageWithExif(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null)
            {
                using var fallbackStream = File.OpenRead(path);
                return new Bitmap(fallbackStream);
            }

            if (codec.EncodedOrigin == SkiaSharp.SKEncodedOrigin.TopLeft || codec.EncodedOrigin == SkiaSharp.SKEncodedOrigin.Default)
            {
                using var normalStream = File.OpenRead(path);
                return new Bitmap(normalStream);
            }

            var info = codec.Info;
            using var fullBmp = new SkiaSharp.SKBitmap(info);
            codec.GetPixels(info, fullBmp.GetPixels());
            
            var oriented = ApplyExifOrientation(fullBmp, codec.EncodedOrigin);

            using var image = SkiaSharp.SKImage.FromBitmap(oriented);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 100);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Seek(0, SeekOrigin.Begin);
            
            var bitmap = new Bitmap(ms);

            if (oriented != fullBmp)
                oriented.Dispose();

            return bitmap;
        }
        catch
        {
            try
            {
                using var fs = File.OpenRead(path);
                return new Bitmap(fs);
            }
            catch { return null; }
        }
    }

    private Bitmap? LoadThumbnailWithExif(string path, int targetWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null)
            {
                using var fallbackStream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fallbackStream, targetWidth);
            }
            
            var ratio = (float)targetWidth / codec.Info.Width;
            if (ratio >= 1f)
            {
                // Image is smaller than target, just decode it with EXIF
                return LoadImageWithExif(path);
            }

            var info = codec.Info;
            using var fullBmp = new SkiaSharp.SKBitmap(info);
            codec.GetPixels(info, fullBmp.GetPixels());
            
            var targetInfo = new SkiaSharp.SKImageInfo((int)(info.Width * ratio), (int)(info.Height * ratio));
            using var resized = fullBmp.Resize(targetInfo, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear));
            
            var oriented = ApplyExifOrientation(resized, codec.EncodedOrigin);

            using var image = SkiaSharp.SKImage.FromBitmap(oriented);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
            using var ms = new MemoryStream();
            data.SaveTo(ms);
            ms.Seek(0, SeekOrigin.Begin);
            
            var bitmap = new Bitmap(ms);

            if (oriented != resized)
                oriented.Dispose();

            return bitmap;
        }
        catch
        {
            try
            {
                using var fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, targetWidth);
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Sorts the current file (copies to checked output folders) and navigates to the next file.
    /// </summary>
    private void SortCurrentAndNavigate(int direction)
    {
        var currentFile = CurrentFile;
        if (currentFile is not null && !currentFile.IsSorted)
        {
            // Get all checked output folders.
            var outputFolders = DestFolders.Items
                .OfType<OutputFolderComponentViewModel>()
                .Where(f => f.IsSelected)
                .ToList();

            if (outputFolders.Count > 0)
            {
                // Mark as sorted immediately for snappy UI
                currentFile.IsSorted = true;

                // Fire and forget sorting so navigation is instant
                _ = Task.Run(async () => 
                {
                    await _sortService.SortFileAsync(currentFile, outputFolders);

                    Dispatcher.UIThread.Post(() => 
                    {
                        // Update the parent folder's sorted count.
                        if (currentFile.ParentFolder is InputFolderComponentViewModel inputFolder)
                        {
                            inputFolder.SortedFiles = inputFolder.Files.Count(f => f.IsSorted);
                        }
                    });
                });
            }
        }

        // Navigate immediately — never wait for anything
        var newIndex = CurrentFileIndex + direction;
        if (newIndex >= 0 && newIndex < TotalFileCount)
        {
            CurrentFileIndex = newIndex;
            
            // Cancel previous debounce
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var debounceToken = _debounceCts.Token;
            
            // Debounce: if the user is rapidly pressing next/prev, wait a moment
            // before starting the heavy loading pipeline. But always show cached data instantly.
            var file = CurrentFile;
            if (file != null)
            {
                // Show cached data instantly (no delay)
                ShowCachedPreviewInstant(file);
                
                // Debounce the full pipeline
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(50, debounceToken);
                        if (!debounceToken.IsCancellationRequested)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!debounceToken.IsCancellationRequested)
                                    LoadCurrentFilePreview();
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                }, debounceToken);
            }
            else
            {
                LoadCurrentFilePreview();
            }
        }
    }

    /// <summary>
    /// Shows whatever we already have cached for a file — zero I/O, instant UI update.
    /// Used during rapid navigation to give immediate visual feedback.
    /// </summary>
    private void ShowCachedPreviewInstant(FileItem file)
    {
        // Stop any video that might be playing from the previous file
        StopVideoPlayback();

        // Handle unsupported formats instantly
        if (IsUnsupportedImageFormat(file))
        {
            PreviewImage = null;
            PreviewText = $"Unsupported format: {file.Metadata.Extension.ToUpperInvariant()}\n\n{file.Name}\n\n({file.Metadata.FormattedSize})";
            IsCurrentFileImage = false;
            IsCurrentFileVideo = false;
            IsLoadingFullData = false;
            return;
        }

        // Best case: full quality in cache
        if (file.IsImage && _previewCache.TryGetValue(file.FullPath, out var cachedFull))
        {
            PreviewImage = cachedFull;
            PreviewText = null;
            IsCurrentFileImage = true;
            IsCurrentFileVideo = false;
            IsLoadingFullData = false;
            return;
        }

        // Good case: preloaded thumbnail
        if (file.PreloadData is Bitmap preloadBmp)
        {
            PreviewImage = preloadBmp;
            PreviewText = null;
            IsCurrentFileImage = file.IsImage || file.IsVideo;
            IsCurrentFileVideo = file.IsVideo;
            IsLoadingFullData = file.IsImage;
            return;
        }

        if (file.PreloadData is string preloadStr)
        {
            PreviewText = preloadStr;
            PreviewImage = null;
            IsCurrentFileImage = false;
            IsCurrentFileVideo = false;
            IsLoadingFullData = !file.IsImage && !file.IsVideo;
            return;
        }

        // No cached data — show blank with loading indicator
        PreviewImage = null;
        PreviewText = null;
        IsCurrentFileImage = file.IsImage || file.IsVideo;
        IsCurrentFileVideo = file.IsVideo;
        IsLoadingFullData = true;
    }

    /// <summary>
    /// Navigate to the next file, sorting the current one first.
    /// </summary>
    [RelayCommand]
    private void GoNext()
    {
        SortCurrentAndNavigate(1);
    }

    /// <summary>
    /// Navigate to the previous file, sorting the current one first.
    /// </summary>
    [RelayCommand]
    private void GoPrevious()
    {
        SortCurrentAndNavigate(-1);
    }

    /// <summary>
    /// Jump to a specific file by 1-based number.
    /// </summary>
    [RelayCommand]
    private void GoToFile(string? input)
    {
        if (int.TryParse(input, out var number) && number >= 1 && number <= TotalFileCount)
        {
            CurrentFileIndex = number - 1;
            LoadCurrentFilePreview();
        }
    }

    /// <summary>
    /// Applies EXIF orientation to an SKBitmap using canvas transforms.
    /// Returns the original bitmap if no transform is needed, or a new transformed bitmap.
    /// </summary>
    private static SkiaSharp.SKBitmap ApplyExifOrientation(SkiaSharp.SKBitmap bitmap, SkiaSharp.SKEncodedOrigin origin)
    {
        if (origin == SkiaSharp.SKEncodedOrigin.TopLeft || origin == SkiaSharp.SKEncodedOrigin.Default)
            return bitmap;

        // Determine if width/height need to be swapped (for 90°/270° rotations).
        var needsSwap = origin is SkiaSharp.SKEncodedOrigin.LeftBottom
                                or SkiaSharp.SKEncodedOrigin.RightTop
                                or SkiaSharp.SKEncodedOrigin.LeftTop
                                or SkiaSharp.SKEncodedOrigin.RightBottom;

        var w = needsSwap ? bitmap.Height : bitmap.Width;
        var h = needsSwap ? bitmap.Width : bitmap.Height;

        var result = new SkiaSharp.SKBitmap(w, h);
        using var canvas = new SkiaSharp.SKCanvas(result);

        switch (origin)
        {
            case SkiaSharp.SKEncodedOrigin.TopRight:       // Flip horizontal
                canvas.Scale(-1, 1, w / 2f, 0);
                break;
            case SkiaSharp.SKEncodedOrigin.BottomRight:    // Rotate 180
                canvas.RotateDegrees(180, w / 2f, h / 2f);
                break;
            case SkiaSharp.SKEncodedOrigin.BottomLeft:     // Flip vertical
                canvas.Scale(1, -1, 0, h / 2f);
                break;
            case SkiaSharp.SKEncodedOrigin.LeftTop:        // Transpose
                canvas.Translate(0, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1, 0, 0);
                break;
            case SkiaSharp.SKEncodedOrigin.RightTop:       // Rotate 90 CW
                canvas.Translate(w, 0);
                canvas.RotateDegrees(90);
                break;
            case SkiaSharp.SKEncodedOrigin.RightBottom:    // Transverse
                canvas.Translate(w, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1, 0, 0);
                break;
            case SkiaSharp.SKEncodedOrigin.LeftBottom:     // Rotate 270 CW (90 CCW)
                canvas.Translate(0, h);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(bitmap, 0, 0);
        canvas.Flush();
        return result;
    }

    #endregion
}
