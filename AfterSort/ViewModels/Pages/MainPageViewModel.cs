using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.ComponentModel;
using AfterSort.Models;
using AfterSort.Services;
using AfterSort.ViewModels.Components;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AfterSort.ViewModels.Pages;

public partial class MainPageViewModel : ViewModelBase
{
    #region Fields

    private readonly IStorageService _storageService;
    private readonly ISortService _sortService;
    private readonly IVideoService _videoService;
    private readonly IImageService _imageService;

    /// <summary>
    /// Flattened list of all files across all input folders (or a single folder if selected).
    /// Rebuilt whenever input folders or their contents change.
    /// </summary>
    private readonly List<FileItem> _flatFileList = [];

    /// <summary>
    /// Files keyed by name, so a change in an output folder maps straight to the files it affects
    /// without walking the whole queue. Rebuilt with <see cref="_flatFileList"/>.
    /// </summary>
    private readonly Dictionary<string, List<FileItem>> _filesByName = new(StringComparer.OrdinalIgnoreCase);

    // === Preview pipeline state ===
    private CancellationTokenSource? _navigationCts;
    private CancellationTokenSource? _debounceCts;
    private readonly ConcurrentDictionary<string, Bitmap> _previewCache = new();

    /// <summary>
    /// Tracks the insertion order of cached bitmaps for LRU-style eviction.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _cacheInsertionOrder = new();
    private long _cacheInsertionCounter;

    /// <summary>Maximum number of full-quality bitmaps to keep in cache.</summary>
    private const int MaxFullQualityCache = 30;

    /// <summary>Maximum number of preload thumbnails to keep in memory.</summary>
    private const int MaxPreloadCache = 100;

    /// <summary>Throttles concurrent disk I/O to prevent thrashing.</summary>
    private readonly SemaphoreSlim _ioSemaphore = new(4, 4);

    /// <summary>
    /// Binary formats we can't decode for preview, shown with an "unsupported" message instead of
    /// being misread as text. HEIC/HEIF and SVG are intentionally absent — those are supported.
    /// </summary>
    private static readonly HashSet<string> UnsupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2",
        ".pef", ".srw", ".raf", ".3fr", ".kdc", ".dcr", ".raw", ".rwl", ".mrw",
        ".nrw", ".srf", ".sr2", ".erf", ".mef", ".mos", ".psd", ".ai", ".eps",
        ".tga",
    };

    #endregion

    #region Properties

    public FolderSelectComponentViewModel InputFolders { get; }
    public FolderSelectComponentViewModel OutputFolders { get; }

    /// <summary>Owns video playback for the current file.</summary>
    public VideoPlayerViewModel VideoPlayer { get; }

    /// <summary>The currently active sort mode.</summary>
    [ObservableProperty]
    public partial SortMode CurrentSortMode { get; set; } = SortMode.Multiple;

    partial void OnCurrentSortModeChanged(SortMode value)
    {
        foreach (var outputFolder in OutputFolderItems)
            outputFolder.IsSingleMode = value == SortMode.Single;
    }

    private IEnumerable<OutputFolderComponentViewModel> OutputFolderItems =>
        OutputFolders.Items.OfType<OutputFolderComponentViewModel>();

    /// <summary>Zero-based index into the flat file list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFileNumber))]
    [NotifyPropertyChangedFor(nameof(CurrentFile))]
    [NotifyPropertyChangedFor(nameof(HasCurrentFile))]
    public partial int CurrentFileIndex { get; set; }

    /// <summary>Total number of files in the current view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFileNumber))]
    [NotifyPropertyChangedFor(nameof(HasCurrentFile))]
    public partial int TotalFileCount { get; set; }

    /// <summary>1-based display number of the current file, or 0 when there are no files.</summary>
    public int CurrentFileNumber => TotalFileCount == 0 ? 0 : CurrentFileIndex + 1;

    /// <summary>Whether there is at least one file to display.</summary>
    public bool HasCurrentFile => TotalFileCount > 0;

    /// <summary>The currently displayed file item.</summary>
    public FileItem? CurrentFile =>
        CurrentFileIndex >= 0 && CurrentFileIndex < _flatFileList.Count
            ? _flatFileList[CurrentFileIndex]
            : null;

    /// <summary>Bitmap for the main preview (image or video thumbnail). Null otherwise.</summary>
    [ObservableProperty]
    public partial Bitmap? PreviewImage { get; set; }

    /// <summary>Preview thumbnail for the previous file in the queue.</summary>
    [ObservableProperty]
    public partial Bitmap? PreviousPreviewImage { get; set; }

    /// <summary>Preview thumbnail for the next file in the queue.</summary>
    [ObservableProperty]
    public partial Bitmap? NextPreviewImage { get; set; }

    /// <summary>The previous file in the queue — drives its sorted badge. Null at the start.</summary>
    [ObservableProperty]
    public partial FileItem? PreviousFile { get; set; }

    /// <summary>The next file in the queue — drives its sorted badge. Null at the end.</summary>
    [ObservableProperty]
    public partial FileItem? NextFile { get; set; }

    /// <summary>Text content for non-image file preview. Null when the current file is an image.</summary>
    [ObservableProperty]
    public partial string? PreviewText { get; set; }

    /// <summary>Whether the current file is an image (controls which preview panel is shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsShowingVideoThumbnail))]
    [NotifyPropertyChangedFor(nameof(IsShowingTextPreview))]
    public partial bool IsCurrentFileImage { get; set; }

    /// <summary>Whether the current file is a video (controls video player UI visibility).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingImagePreview))]
    [NotifyPropertyChangedFor(nameof(IsShowingVideoThumbnail))]
    [NotifyPropertyChangedFor(nameof(IsShowingTextPreview))]
    public partial bool IsCurrentFileVideo { get; set; }

    /// <summary>Whether the full high-res data is currently loading.</summary>
    [ObservableProperty]
    public partial bool IsLoadingFullData { get; set; }

    // === Computed visibility for the XAML ===

    /// <summary>True when showing a non-video image preview.</summary>
    public bool IsShowingImagePreview => IsCurrentFileImage && !IsCurrentFileVideo && !VideoPlayer.IsActive;

    /// <summary>True when showing a video thumbnail with a play overlay (not yet playing).</summary>
    public bool IsShowingVideoThumbnail => IsCurrentFileVideo && !VideoPlayer.IsActive;

    /// <summary>True when showing text preview (non-image, non-video files).</summary>
    public bool IsShowingTextPreview => !IsCurrentFileImage && !IsCurrentFileVideo && !VideoPlayer.IsActive;

    #endregion

    #region Constructors

    public MainPageViewModel(
        IStorageService storageService,
        ISortService sortService,
        IVideoService videoService,
        IImageService imageService,
        VideoPlayerViewModel videoPlayer)
    {
        _storageService = storageService;
        _sortService = sortService;
        _videoService = videoService;
        _imageService = imageService;
        VideoPlayer = videoPlayer;

        // The video player's "active" state drives which preview panel is visible.
        VideoPlayer.PropertyChanged += OnVideoPlayerPropertyChanged;

        InputFolders = new FolderSelectComponentViewModel { Title = "Input Folders" };
        OutputFolders = new FolderSelectComponentViewModel { Title = "Output Folders" };

        InputFolders.ItemFactory = async () =>
        {
            var result = await _storageService.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Input Folder" });
            if (result is not { Count: > 0 })
                return null;

            var vm = new InputFolderComponentViewModel
            {
                FolderName = result[0].Name,
                FolderPath = result[0].Path.LocalPath,
            };
            vm.LoadFiles();
            return vm;
        };

        OutputFolders.ItemFactory = async () =>
        {
            var result = await _storageService.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Output Folder" });
            if (result is not { Count: > 0 })
                return null;

            return new OutputFolderComponentViewModel(_sortService)
            {
                FolderName = result[0].Name,
                FolderPath = result[0].Path.LocalPath,
                IsSingleMode = CurrentSortMode == SortMode.Single,
            };
        };

        InputFolders.Items.CollectionChanged += OnInputFoldersChanged;
        OutputFolders.Items.CollectionChanged += OnOutputFoldersChanged;
    }

    #endregion

    #region Methods

    private void OnVideoPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VideoPlayerViewModel.IsActive))
            return;

        OnPropertyChanged(nameof(IsShowingImagePreview));
        OnPropertyChanged(nameof(IsShowingVideoThumbnail));
        OnPropertyChanged(nameof(IsShowingTextPreview));
    }

    /// <summary>
    /// Handles input folder collection changes — rebuilds the flat file list.
    /// </summary>
    private void OnInputFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var inputFolder in (e.NewItems ?? Array.Empty<object>()).OfType<InputFolderComponentViewModel>())
            inputFolder.FilesAdded += OnInputFolderFilesAdded;

        foreach (var inputFolder in (e.OldItems ?? Array.Empty<object>()).OfType<InputFolderComponentViewModel>())
            inputFolder.FilesAdded -= OnInputFolderFilesAdded;

        RebuildFlatFileList();
    }

    private void OnInputFolderFilesAdded(object? sender, IEnumerable<FileItem> newFiles)
    {
        var added = newFiles as IList<FileItem> ?? newFiles.ToList();

        var oldCount = _flatFileList.Count;
        _flatFileList.AddRange(added);
        IndexFiles(added);
        TotalFileCount = _flatFileList.Count;

        foreach (var file in added)
            RefreshSortedState(file);

        if (oldCount == 0 && TotalFileCount > 0)
        {
            CurrentFileIndex = 0;
            OnPropertyChanged(nameof(CurrentFile));
            SyncOutputFolderStates();
            LoadCurrentFilePreview();
        }

        OnPropertyChanged(nameof(HasCurrentFile));
        OnPropertyChanged(nameof(CurrentFileNumber));
    }

    /// <summary>
    /// Handles output folder changes — scans new folders for what they already contain and keeps
    /// the app's sorted state in sync with them.
    /// </summary>
    private void OnOutputFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var outputFolder in (e.NewItems ?? Array.Empty<object>()).OfType<OutputFolderComponentViewModel>())
        {
            outputFolder.FileStateChanged += OnOutputFolderFileStateChanged;
            outputFolder.ContentChanged += OnOutputFolderContentChanged;
            outputFolder.ContentsScanned += OnOutputFolderContentsScanned;
            outputFolder.UserToggled += OnOutputFolderUserToggled;
            outputFolder.SetCurrentFile(CurrentFile);
            _ = outputFolder.ScanAsync();
        }

        foreach (var outputFolder in (e.OldItems ?? Array.Empty<object>()).OfType<OutputFolderComponentViewModel>())
        {
            outputFolder.FileStateChanged -= OnOutputFolderFileStateChanged;
            outputFolder.ContentChanged -= OnOutputFolderContentChanged;
            outputFolder.ContentsScanned -= OnOutputFolderContentsScanned;
            outputFolder.UserToggled -= OnOutputFolderUserToggled;
            outputFolder.Dispose();
        }

        RefreshAllSortedState();
    }

    private void OnOutputFolderContentsScanned(object? sender, EventArgs e) => RefreshAllSortedState();

    private void OnOutputFolderFileStateChanged(object? sender, FileItem file) => RefreshSortedState(file);

    /// <summary>A file appeared in or vanished from an output folder — update just the files it names.</summary>
    private void OnOutputFolderContentChanged(object? sender, string fileName)
    {
        if (!_filesByName.TryGetValue(fileName, out var affected))
            return;

        foreach (var file in affected)
            RefreshSortedState(file);
    }

    /// <summary>In Single mode, clicking an output folder box moves straight on to the next file.</summary>
    private void OnOutputFolderUserToggled(object? sender, EventArgs e)
    {
        if (CurrentSortMode == SortMode.Single)
            Navigate(1);
    }

    /// <summary>
    /// Recomputes "does this file live in at least one output folder", adjusting its input folder's
    /// counter by the difference rather than recounting the folder.
    /// </summary>
    private void RefreshSortedState(FileItem file)
    {
        var isSorted = OutputFolderItems.Any(o => o.Contains(file));
        if (file.IsSorted == isSorted)
            return;

        file.IsSorted = isSorted;

        if (file.ParentFolder is InputFolderComponentViewModel inputFolder)
            inputFolder.SortedFiles += isSorted ? 1 : -1;
    }

    /// <summary>Full recount, for when the set of output folders or files changed underneath us.</summary>
    private void RefreshAllSortedState()
    {
        var outputFolders = OutputFolderItems.ToList();

        foreach (var file in _flatFileList)
            file.IsSorted = outputFolders.Any(o => o.Contains(file));

        foreach (var inputFolder in InputFolders.Items.OfType<InputFolderComponentViewModel>())
            inputFolder.SortedFiles = inputFolder.Files.Count(f => f.IsSorted);
    }

    /// <summary>Points every output folder's box at the file now on screen.</summary>
    private void SyncOutputFolderStates()
    {
        var file = CurrentFile;
        foreach (var outputFolder in OutputFolderItems)
            outputFolder.SetCurrentFile(file);
    }

    partial void OnCurrentFileIndexChanged(int value) => SyncOutputFolderStates();

    /// <summary>Adds files to the by-name index used to map output folder changes back to files.</summary>
    private void IndexFiles(IEnumerable<FileItem> files)
    {
        foreach (var file in files)
        {
            if (!_filesByName.TryGetValue(file.Name, out var sameName))
                _filesByName[file.Name] = sameName = [];

            sameName.Add(file);
        }
    }

    /// <summary>
    /// Rebuilds the flat file list from every input folder.
    /// </summary>
    private void RebuildFlatFileList()
    {
        var oldCurrentFile = CurrentFile;

        _flatFileList.Clear();
        _flatFileList.AddRange(InputFolders.Items.OfType<InputFolderComponentViewModel>().SelectMany(f => f.Files));

        _filesByName.Clear();
        IndexFiles(_flatFileList);

        TotalFileCount = _flatFileList.Count;
        RefreshAllSortedState();

        var restoredIndex = oldCurrentFile is null ? -1 : _flatFileList.IndexOf(oldCurrentFile);
        CurrentFileIndex = restoredIndex >= 0 ? restoredIndex : 0;

        // CurrentFile is computed, so notify explicitly.
        OnPropertyChanged(nameof(CurrentFile));
        OnPropertyChanged(nameof(HasCurrentFile));
        OnPropertyChanged(nameof(CurrentFileNumber));

        SyncOutputFolderStates();
        LoadCurrentFilePreview();
    }

    private static bool IsUnsupportedImageFormat(FileItem file) =>
        UnsupportedImageExtensions.Contains(file.Metadata.Extension);

    private static string UnsupportedMessage(FileItem file) =>
        $"Unsupported format: {file.Metadata.Extension.ToUpperInvariant()}\n\n{file.Name}\n\n({file.Metadata.FormattedSize})";

    /// <summary>
    /// Loads the preview for the current file. Never blocks on I/O — it shows cached/preloaded data
    /// instantly, then kicks off background work for full quality and neighbour preloading.
    /// </summary>
    private void LoadCurrentFilePreview()
    {
        _navigationCts?.Cancel();
        _navigationCts = new CancellationTokenSource();
        var token = _navigationCts.Token;

        var file = CurrentFile;
        var currentIndex = CurrentFileIndex;

        SyncOutputFolderStates();

        // Point the video player at this file (or clear it), stopping any prior playback.
        VideoPlayer.SetSource(file?.IsVideo == true ? file.FullPath : null);

        if (file is null)
        {
            ShowEmpty();
            return;
        }

        // === PHASE 1: Instant UI update (no I/O, no awaiting) ===
        if (IsUnsupportedImageFormat(file))
        {
            ShowUnsupported(file);
            UpdateNeighborPreviews();
            _ = Task.Run(() => PreloadNeighborsAsync(currentIndex, token), token);
            return;
        }

        if (file.IsImage && _previewCache.TryGetValue(file.FullPath, out var cachedFull))
        {
            ShowImage(cachedFull, loadingFull: false);
            UpdateNeighborPreviews();
            _ = Task.Run(() => PreloadNeighborsAsync(currentIndex, token), token);
            return;
        }

        ShowPreloadOrPlaceholder(file);

        // === PHASE 2: Background pipeline (ordered by priority) ===
        _ = RunBackgroundPipelineAsync(file, currentIndex, token);

        UpdateNeighborPreviews();
    }

    private void ShowEmpty()
    {
        IsCurrentFileImage = false;
        IsCurrentFileVideo = false;
        IsLoadingFullData = false;
        PreviewImage = null;
        PreviewText = null;
    }

    private void ShowUnsupported(FileItem file)
    {
        PreviewImage = null;
        PreviewText = UnsupportedMessage(file);
        IsCurrentFileImage = false;
        IsCurrentFileVideo = false;
        IsLoadingFullData = false;
    }

    private void ShowImage(Bitmap bitmap, bool loadingFull)
    {
        PreviewImage = bitmap;
        PreviewText = null;
        IsCurrentFileImage = true;
        IsCurrentFileVideo = false;
        IsLoadingFullData = loadingFull;
    }

    /// <summary>
    /// Shows whatever the file already has preloaded, or a loading placeholder when nothing is ready.
    /// </summary>
    private void ShowPreloadOrPlaceholder(FileItem file)
    {
        switch (file.PreloadData)
        {
            case Bitmap preloadBmp:
                PreviewImage = preloadBmp;
                PreviewText = null;
                IsCurrentFileImage = file.IsImage;
                IsCurrentFileVideo = file.IsVideo;
                IsLoadingFullData = file.IsImage; // Images still need their full-quality decode.
                break;

            case string preloadStr:
                PreviewText = preloadStr;
                PreviewImage = null;
                IsCurrentFileImage = false;
                IsCurrentFileVideo = false;
                IsLoadingFullData = false;
                break;

            default:
                PreviewImage = null;
                PreviewText = null;
                IsCurrentFileImage = file.IsImage;
                IsCurrentFileVideo = file.IsVideo;
                IsLoadingFullData = true;
                break;
        }
    }

    private void UpdateNeighborPreviews()
    {
        var currentIndex = CurrentFileIndex;

        PreviousFile = currentIndex - 1 >= 0 ? _flatFileList[currentIndex - 1] : null;
        NextFile = currentIndex + 1 < _flatFileList.Count ? _flatFileList[currentIndex + 1] : null;

        PreviousPreviewImage = PreviousFile?.PreloadData as Bitmap;
        NextPreviewImage = NextFile?.PreloadData as Bitmap;
    }

    /// <summary>
    /// Background pipeline that loads data in priority order: current preload → current full quality
    /// → neighbour preloads → neighbour full qualities, then enforces memory limits.
    /// </summary>
    private async Task RunBackgroundPipelineAsync(FileItem file, int currentIndex, CancellationToken token)
    {
        try
        {
            if (file.PreloadData is null)
            {
                var preloadData = await Task.Run(() => TryGeneratePreloadData(file), token);
                if (token.IsCancellationRequested)
                    return;

                if (preloadData is not null)
                {
                    file.PreloadData = preloadData;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                            ApplyPreloadToUi(file, preloadData);
                    });
                }
            }

            if (token.IsCancellationRequested)
                return;

            // Current file's full quality and neighbour preloads run concurrently.
            var fullQualityTask = LoadFullQualityForCurrentAsync(file, token);
            var neighborTask = PreloadNeighborsAsync(currentIndex, token);

            await fullQualityTask;
            if (token.IsCancellationRequested)
                return;

            await neighborTask;
            if (token.IsCancellationRequested)
                return;

            await LoadNeighborFullQualitiesAsync(currentIndex, 5, token);

            EnforceMemoryLimits(currentIndex);
        }
        catch (OperationCanceledException)
        {
            // Expected when navigation changes — ignore.
        }
        catch (Exception)
        {
            // Never crash the app on a background load error.
        }
    }

    private void ApplyPreloadToUi(FileItem file, object preloadData)
    {
        switch (preloadData)
        {
            case Bitmap bmp:
                PreviewImage = bmp;
                PreviewText = null;
                IsCurrentFileImage = file.IsImage;
                IsCurrentFileVideo = file.IsVideo;
                break;
            case string str:
                PreviewText = str;
                PreviewImage = null;
                IsCurrentFileImage = false;
                IsCurrentFileVideo = false;
                break;
        }
    }

    /// <summary>
    /// Loads full quality for the current file (image decode or full text read) and posts to the UI.
    /// </summary>
    private async Task LoadFullQualityForCurrentAsync(FileItem file, CancellationToken token)
    {
        if (file.IsImage && !_previewCache.ContainsKey(file.FullPath))
        {
            var fullBmp = await Task.Run(() => _imageService.LoadFull(file.FullPath), token);
            if (token.IsCancellationRequested)
                return;

            if (fullBmp is not null)
            {
                CacheFull(file.FullPath, fullBmp);
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    PreviewImage = fullBmp;
                    IsLoadingFullData = false;
                });
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    IsLoadingFullData = false;
                    if (PreviewImage is null)
                    {
                        PreviewText = $"Unable to load image: {file.Name}";
                        IsCurrentFileImage = false;
                    }
                });
            }
        }
        else if (!file.IsImage && !file.IsVideo)
        {
            await LoadFullTextAsync(file, token);
        }
        else
        {
            // Video, or image already cached — nothing more to fetch.
            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested)
                    IsLoadingFullData = false;
            });
        }
    }

    private async Task LoadFullTextAsync(FileItem file, CancellationToken token)
    {
        try
        {
            var fullText = await Task.Run(() =>
            {
                var fileInfo = new FileInfo(file.FullPath);
                return fileInfo.Length > 1_048_576
                    ? File.ReadAllText(file.FullPath)[..1_048_576] + "\n\n--- File truncated (>1 MB) ---"
                    : File.ReadAllText(file.FullPath);
            }, token);

            if (token.IsCancellationRequested)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                PreviewText = fullText;
                IsLoadingFullData = false;
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested)
                    return;
                PreviewText = $"Unable to read file: {file.Name}";
                IsLoadingFullData = false;
            });
        }
    }

    /// <summary>
    /// Preloads neighbour thumbnails with throttled concurrency, favouring forward navigation.
    /// </summary>
    private async Task PreloadNeighborsAsync(int currentIndex, CancellationToken token)
    {
        const int range = 15;
        var total = _flatFileList.Count;

        // Build a priority-ordered list: next files first (more likely to be viewed), then previous.
        var indicesToPreload = new List<int>();
        for (var offset = 1; offset <= range; offset++)
        {
            if (currentIndex + offset < total)
                indicesToPreload.Add(currentIndex + offset);
            if (currentIndex - offset >= 0)
                indicesToPreload.Add(currentIndex - offset);
        }

        var tasks = new List<Task>();
        foreach (var idx in indicesToPreload)
        {
            if (token.IsCancellationRequested)
                return;

            var f = _flatFileList[idx];
            if (f.PreloadData is not null)
                continue;

            if (IsUnsupportedImageFormat(f))
            {
                f.PreloadData = UnsupportedMessage(f); // Mark so we skip it fast next time.
                continue;
            }

            tasks.Add(PreloadSingleFileAsync(f, token));

            if (tasks.Count >= 4)
            {
                await Task.WhenAny(tasks);
                tasks.RemoveAll(t => t.IsCompleted);
            }
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    private async Task PreloadSingleFileAsync(FileItem file, CancellationToken token)
    {
        await _ioSemaphore.WaitAsync(token);
        try
        {
            if (token.IsCancellationRequested || file.PreloadData is not null)
                return;

            file.PreloadData = await Task.Run(() => TryGeneratePreloadData(file), token);

            if (file.PreloadData is not null)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        UpdateNeighborPreviews();
                });
            }
        }
        finally
        {
            _ioSemaphore.Release();
        }
    }

    /// <summary>
    /// Loads full-quality images for neighbouring files, favouring the forward direction.
    /// </summary>
    private async Task LoadNeighborFullQualitiesAsync(int currentIndex, int range, CancellationToken token)
    {
        var total = _flatFileList.Count;

        for (var offset = 1; offset <= range; offset++)
        {
            if (token.IsCancellationRequested)
                return;
            await CacheFullQualityIfNeeded(currentIndex + offset, total, token);

            if (token.IsCancellationRequested)
                return;
            await CacheFullQualityIfNeeded(currentIndex - offset, total, token);
        }
    }

    private async Task CacheFullQualityIfNeeded(int index, int total, CancellationToken token)
    {
        if (index < 0 || index >= total)
            return;

        var file = _flatFileList[index];
        if (!file.IsImage || _previewCache.ContainsKey(file.FullPath) || IsUnsupportedImageFormat(file))
            return;

        await _ioSemaphore.WaitAsync(token);
        try
        {
            if (token.IsCancellationRequested)
                return;

            var bmp = await Task.Run(() => _imageService.LoadFull(file.FullPath), token);
            if (bmp is not null)
                CacheFull(file.FullPath, bmp);
        }
        finally
        {
            _ioSemaphore.Release();
        }
    }

    private void CacheFull(string path, Bitmap bitmap)
    {
        if (_previewCache.TryAdd(path, bitmap))
            _cacheInsertionOrder.TryAdd(path, Interlocked.Increment(ref _cacheInsertionCounter));
    }

    private object? TryGeneratePreloadData(FileItem file)
    {
        try
        {
            if (file.IsImage)
                return _imageService.LoadThumbnail(file.FullPath, 400);

            if (file.IsVideo)
                return _videoService.ExtractThumbnail(file.FullPath, TimeSpan.FromSeconds(0.5), 400);

            using var reader = new StreamReader(file.FullPath);
            var buffer = new char[3000];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read) + (read == buffer.Length ? "\n..." : "");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return file.IsImage || file.IsVideo ? null : "Unable to read file.";
        }
    }

    /// <summary>
    /// Evicts cached bitmaps and preload thumbnails far from the current position.
    /// Only inspects cached entries and a bounded scan window, never the whole list.
    /// </summary>
    private void EnforceMemoryLimits(int currentIndex)
    {
        var total = _flatFileList.Count;

        if (_previewCache.Count > MaxFullQualityCache)
        {
            var pathsToKeep = new HashSet<string>();
            for (var i = Math.Max(0, currentIndex - 10); i <= Math.Min(total - 1, currentIndex + 10); i++)
                pathsToKeep.Add(_flatFileList[i].FullPath);

            var evictCandidates = _cacheInsertionOrder
                .Where(kvp => !pathsToKeep.Contains(kvp.Key))
                .OrderBy(kvp => kvp.Value)
                .Take(_previewCache.Count - MaxFullQualityCache + 5) // Evict a few extra to avoid thrashing.
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

        // Drop preload thumbnails outside the working window (bounded scan, not O(n)).
        var radius = MaxPreloadCache / 2;
        var lowerBound = Math.Max(0, currentIndex - radius);
        var upperBound = Math.Min(total - 1, currentIndex + radius);

        for (var i = Math.Max(0, lowerBound - 100); i < lowerBound; i++)
            EvictPreload(_flatFileList[i]);

        for (var i = upperBound + 1; i < Math.Min(total, upperBound + 100); i++)
            EvictPreload(_flatFileList[i]);
    }

    private static void EvictPreload(FileItem file)
    {
        if (file.PreloadData is Bitmap bmp)
            Dispatcher.UIThread.Post(() => bmp.Dispose());
        file.PreloadData = null;
    }

    /// <summary>
    /// Moves by <paramref name="direction"/> in the queue. Copying is owned by the output
    /// folder boxes, so navigating never touches the file system.
    /// </summary>
    private void Navigate(int direction)
    {
        var newIndex = CurrentFileIndex + direction;
        if (newIndex < 0 || newIndex >= TotalFileCount)
            return;

        CurrentFileIndex = newIndex;

        var file = CurrentFile;
        if (file is null)
        {
            LoadCurrentFilePreview();
            return;
        }

        // Show whatever is cached instantly, then debounce the heavy pipeline for rapid navigation.
        ShowCachedPreviewInstant(file);

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var debounceToken = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, debounceToken);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!debounceToken.IsCancellationRequested)
                        LoadCurrentFilePreview();
                });
            }
            catch (OperationCanceledException) { }
        }, debounceToken);
    }

    /// <summary>
    /// Shows whatever is already cached for a file — zero I/O, instant feedback during rapid navigation.
    /// </summary>
    private void ShowCachedPreviewInstant(FileItem file)
    {
        SyncOutputFolderStates();
        VideoPlayer.SetSource(file.IsVideo ? file.FullPath : null);

        if (IsUnsupportedImageFormat(file))
        {
            ShowUnsupported(file);
            UpdateNeighborPreviews();
            return;
        }

        if (file.IsImage && _previewCache.TryGetValue(file.FullPath, out var cachedFull))
        {
            ShowImage(cachedFull, loadingFull: false);
            UpdateNeighborPreviews();
            return;
        }

        ShowPreloadOrPlaceholder(file);
        UpdateNeighborPreviews();
    }

    [RelayCommand]
    private void GoNext() => Navigate(1);

    [RelayCommand]
    private void GoPrevious() => Navigate(-1);

    /// <summary>Jump to a specific file by 1-based number.</summary>
    [RelayCommand]
    private void GoToFile(string? input)
    {
        if (int.TryParse(input, out var number) && number >= 1 && number <= TotalFileCount)
        {
            CurrentFileIndex = number - 1;
            LoadCurrentFilePreview();
        }
    }

    #endregion
}
