using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AfterSort.Models;
using AfterSort.Services;
using AfterSort.ViewModels.Components;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AfterSort.ViewModels.Pages;

public partial class MainPageViewModel : ViewModelBase
{
    #region Fields

    private readonly IStorageService _storageService;
    private readonly ISortService _sortService;

    /// <summary>
    /// Flattened list of all files across all input folders (or a single folder if selected).
    /// Rebuilt whenever input folders or their contents change.
    /// </summary>
    private readonly List<FileItem> _flatFileList = [];

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
    /// Text content for non-image file preview. Null when the current file is an image.
    /// </summary>
    [ObservableProperty]
    public partial string? PreviewText { get; set; }

    /// <summary>
    /// Whether the current file is an image (controls which preview panel is shown).
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurrentFileImage { get; set; }

    /// <summary>
    /// Whether the current file is a video (controls video player UI visibility).
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurrentFileVideo { get; set; }

    /// <summary>
    /// Whether the full high-res data is currently loading.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoadingFullData { get; set; }

    #endregion

    #region Constructors

    public MainPageViewModel(IStorageService storageService, ISortService sortService)
    {
        _storageService = storageService;
        _sortService = sortService;
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

    private System.Threading.CancellationTokenSource? _previewCts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> _previewCache = new();

    /// <summary>
    /// Loads the preview content (image bitmap or text) for the current file.
    /// </summary>
    private async void LoadCurrentFilePreview()
    {
        _previewCts?.Cancel();
        _previewCts = new System.Threading.CancellationTokenSource();
        var token = _previewCts.Token;

        var file = CurrentFile;
        int currentIndex = CurrentFileIndex;
        if (file is null)
        {
            IsCurrentFileImage = false;
            IsCurrentFileVideo = false;
            IsLoadingFullData = false;
            PreviewImage = null;
            PreviewText = null;
            return;
        }

        // 1. Show Current Preload if available, or generate it immediately
        if (file.PreloadData == null)
        {
            file.PreloadData = await System.Threading.Tasks.Task.Run(() => GeneratePreloadData(file), token);
        }
        if (token.IsCancellationRequested) return;

        if (file.PreloadData is Bitmap preloadBmp)
        {
            PreviewImage = preloadBmp;
            PreviewText = null;
            IsCurrentFileImage = file.IsImage || file.IsVideo;
            IsCurrentFileVideo = file.IsVideo;
        }
        else if (file.PreloadData is string preloadStr)
        {
            PreviewText = preloadStr;
            PreviewImage = null;
            IsCurrentFileImage = false;
            IsCurrentFileVideo = false;
        }
        else
        {
            PreviewImage = null;
            PreviewText = null;
        }

        // Check if we already have the full quality
        if (file.IsImage && _previewCache.TryGetValue(file.FullPath, out var cachedFull))
        {
            PreviewImage = cachedFull;
            IsLoadingFullData = false;
        }
        else if (file.IsImage || (!file.IsImage && !file.IsVideo))
        {
            IsLoadingFullData = true;
        }
        else
        {
            IsLoadingFullData = false; // Video is "loaded" once thumbnail is there
        }

        // 2. Start the background coordinator
        _ = System.Threading.Tasks.Task.Run(async () => 
        {
            // Priority 2: ±10 Preloads
            await GeneratePreloadsAsync(currentIndex, 10, token);
            if (token.IsCancellationRequested) return;

            // Priority 3: Full quality of current file
            if (file.IsImage && !_previewCache.ContainsKey(file.FullPath))
            {
                var fullBmp = LoadImageWithExif(file.FullPath);
                if (fullBmp != null)
                {
                    _previewCache.TryAdd(file.FullPath, fullBmp);
                    if (!token.IsCancellationRequested)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                        {
                            PreviewImage = fullBmp;
                            IsLoadingFullData = false;
                        });
                    }
                }
            }
            else if (!file.IsImage && !file.IsVideo)
            {
                try
                {
                    var fileInfo = new System.IO.FileInfo(file.FullPath);
                    var fullText = fileInfo.Length > 1_048_576 
                        ? System.IO.File.ReadAllText(file.FullPath)[..1_048_576] + "\n\n--- File truncated (>1 MB) ---"
                        : System.IO.File.ReadAllText(file.FullPath);

                    if (!token.IsCancellationRequested)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                        {
                            PreviewText = fullText;
                            IsLoadingFullData = false;
                        });
                    }
                }
                catch 
                {
                    if (!token.IsCancellationRequested)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                        {
                            PreviewText = $"Unable to read file: {file.Name}";
                            IsLoadingFullData = false;
                        });
                    }
                }
            }

            if (token.IsCancellationRequested) return;

            // Priority 4: ±5 Full Qualities
            await GenerateFullQualitiesAsync(currentIndex, 5, token);

            // Cleanup memory limits
            EnforceMemoryLimits(currentIndex);

        }, token);
    }

    private object? GeneratePreloadData(FileItem f)
    {
        if (f.IsImage)
        {
            return LoadThumbnailWithExif(f.FullPath, 400);
        }
        else if (f.IsVideo)
        {
            return GenerateVideoThumbnail();
        }
        else
        {
            try
            {
                using var reader = new System.IO.StreamReader(f.FullPath);
                var buffer = new char[3000];
                int read = reader.Read(buffer, 0, 3000);
                return new string(buffer, 0, read) + (read == 3000 ? "\n..." : "");
            }
            catch { return "Unable to read file."; }
        }
    }

    private async Task GeneratePreloadsAsync(int currentIndex, int range, System.Threading.CancellationToken token)
    {
        int total = _flatFileList.Count;
        for (int offset = 1; offset <= range; offset++)
        {
            if (token.IsCancellationRequested) return;
            int next = currentIndex + offset;
            int prev = currentIndex - offset;

            if (next < total && _flatFileList[next].PreloadData == null)
            {
                var f = _flatFileList[next];
                f.PreloadData = await System.Threading.Tasks.Task.Run(() => GeneratePreloadData(f), token);
            }
            if (token.IsCancellationRequested) return;
            
            if (prev >= 0 && _flatFileList[prev].PreloadData == null)
            {
                var f = _flatFileList[prev];
                f.PreloadData = await System.Threading.Tasks.Task.Run(() => GeneratePreloadData(f), token);
            }
        }
    }

    private async Task GenerateFullQualitiesAsync(int currentIndex, int range, System.Threading.CancellationToken token)
    {
        int total = _flatFileList.Count;
        for (int offset = 1; offset <= range; offset++)
        {
            if (token.IsCancellationRequested) return;
            int next = currentIndex + offset;
            int prev = currentIndex - offset;

            if (next < total && _flatFileList[next].IsImage && !_previewCache.ContainsKey(_flatFileList[next].FullPath))
            {
                var f = _flatFileList[next];
                var bmp = await System.Threading.Tasks.Task.Run(() => LoadImageWithExif(f.FullPath), token);
                if (bmp != null) _previewCache.TryAdd(f.FullPath, bmp);
            }
            if (token.IsCancellationRequested) return;
            
            if (prev >= 0 && _flatFileList[prev].IsImage && !_previewCache.ContainsKey(_flatFileList[prev].FullPath))
            {
                var f = _flatFileList[prev];
                var bmp = await System.Threading.Tasks.Task.Run(() => LoadImageWithExif(f.FullPath), token);
                if (bmp != null) _previewCache.TryAdd(f.FullPath, bmp);
            }
        }
    }

    private void EnforceMemoryLimits(int currentIndex)
    {
        // 1. Enforce Full Quality Limit (±10)
        var pathsToKeepFull = new System.Collections.Generic.HashSet<string>();
        int total = _flatFileList.Count;
        
        for (int i = System.Math.Max(0, currentIndex - 10); i <= System.Math.Min(total - 1, currentIndex + 10); i++)
        {
            pathsToKeepFull.Add(_flatFileList[i].FullPath);
        }

        var keysToRemove = new System.Collections.Generic.List<string>();
        foreach (var key in _previewCache.Keys)
        {
            if (!pathsToKeepFull.Contains(key)) keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
        {
            if (_previewCache.TryRemove(key, out var oldBmp))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => oldBmp.Dispose());
            }
        }

        // 2. Enforce Preload Data Limit (±50)
        for (int i = 0; i < total; i++)
        {
            if (System.Math.Abs(i - currentIndex) > 50)
            {
                var file = _flatFileList[i];
                if (file.PreloadData is Bitmap bmp)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => bmp.Dispose());
                }
                file.PreloadData = null;
            }
        }
    }

    private Bitmap GenerateVideoThumbnail()
    {
        var info = new SkiaSharp.SKImageInfo(400, 300);
        using var surface = SkiaSharp.SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SkiaSharp.SKColor(20, 20, 20));
        
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true };
        var pathObj = new SkiaSharp.SKPath();
        pathObj.MoveTo(160, 100);
        pathObj.LineTo(160, 200);
        pathObj.LineTo(260, 150);
        pathObj.Close();
        canvas.DrawPath(pathObj, paint);
        
        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        using var ms = new System.IO.MemoryStream();
        data.SaveTo(ms);
        ms.Seek(0, System.IO.SeekOrigin.Begin);
        return new Bitmap(ms);
    }

    private Bitmap? LoadImageWithExif(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null)
            {
                using var fallbackStream = System.IO.File.OpenRead(path);
                return new Bitmap(fallbackStream);
            }

            if (codec.EncodedOrigin == SkiaSharp.SKEncodedOrigin.TopLeft || codec.EncodedOrigin == SkiaSharp.SKEncodedOrigin.Default)
            {
                using var normalStream = System.IO.File.OpenRead(path);
                return new Bitmap(normalStream);
            }

            var info = codec.Info;
            using var fullBmp = new SkiaSharp.SKBitmap(info);
            codec.GetPixels(info, fullBmp.GetPixels());
            
            var oriented = ApplyExifOrientation(fullBmp, codec.EncodedOrigin);

            using var image = SkiaSharp.SKImage.FromBitmap(oriented);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 100);
            using var ms = new System.IO.MemoryStream();
            data.SaveTo(ms);
            ms.Seek(0, System.IO.SeekOrigin.Begin);
            
            var bitmap = new Bitmap(ms);

            if (oriented != fullBmp)
                oriented.Dispose();

            return bitmap;
        }
        catch
        {
            try
            {
                using var fs = System.IO.File.OpenRead(path);
                return new Bitmap(fs);
            }
            catch { return null; }
        }
    }

    private Bitmap? LoadThumbnailWithExif(string path, int targetWidth)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null)
            {
                using var fallbackStream = System.IO.File.OpenRead(path);
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
            using var ms = new System.IO.MemoryStream();
            data.SaveTo(ms);
            ms.Seek(0, System.IO.SeekOrigin.Begin);
            
            var bitmap = new Bitmap(ms);

            if (oriented != resized)
                oriented.Dispose();

            return bitmap;
        }
        catch
        {
            try
            {
                using var fs = System.IO.File.OpenRead(path);
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
                _ = System.Threading.Tasks.Task.Run(async () => 
                {
                    await _sortService.SortFileAsync(currentFile, outputFolders);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
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

        // Navigate.
        var newIndex = CurrentFileIndex + direction;
        if (newIndex >= 0 && newIndex < TotalFileCount)
        {
            CurrentFileIndex = newIndex;
            LoadCurrentFilePreview();
        }
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
