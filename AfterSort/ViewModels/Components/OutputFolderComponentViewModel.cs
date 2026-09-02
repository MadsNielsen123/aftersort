using AfterSort.Models;
using AfterSort.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AfterSort.ViewModels.Components;

/// <summary>
/// An output folder. Its box is the live answer to "does the current file live in this folder?" —
/// clicking it copies the file there, clicking again deletes it. The folder's contents are held as
/// an in-memory name index kept current by a <see cref="FileSystemWatcher"/>, so the answer is
/// metadata-cheap and also survives changes made outside the app.
/// </summary>
public partial class OutputFolderComponentViewModel : FolderItemViewModelBase, IDisposable
{
    private readonly ISortService _sortService;

    /// <summary>Names of the files in the folder. UI-thread only.</summary>
    private readonly HashSet<string> _presentNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source paths with an add/remove task in flight. UI-thread only.</summary>
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private FileItem? _currentFile;

    public OutputFolderComponentViewModel(ISortService sortService)
    {
        _sortService = sortService;
    }

    /// <summary>Raised after one of this folder's own add/remove tasks finished for a file.</summary>
    public event EventHandler<FileItem>? FileStateChanged;

    /// <summary>Raised when a file name appears in or disappears from the folder.</summary>
    public event EventHandler<string>? ContentChanged;

    /// <summary>Raised after the folder's contents have been (re)scanned from disk.</summary>
    public event EventHandler? ContentsScanned;

    /// <summary>Raised when the user clicks the box, whichever way it went.</summary>
    public event EventHandler? UserToggled;

    /// <summary>
    /// Whether the current file lives in this folder. Display only — never written by the view,
    /// so the box can't drift out of sync with the folder.
    /// </summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>True while the initial disk scan is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsScanning { get; set; } = true;

    /// <summary>True while an add/remove task for the current file is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; set; }

    /// <summary>Last failure for this folder, shown as a warning icon. Null when healthy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Drives the round box used in Single mode.</summary>
    [ObservableProperty]
    public partial bool IsSingleMode { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The box is only usable once we know the folder's contents and nothing is in flight.</summary>
    public bool CanToggle => !IsScanning && !IsBusy && _currentFile is not null;

    /// <summary>Reads the folder's contents from disk and starts watching it for outside changes.</summary>
    public async Task ScanAsync()
    {
        StartWatching();

        IsScanning = true;
        try
        {
            var names = await _sortService.ScanFolderAsync(FolderPath);
            _presentNames.Clear();
            foreach (var name in names)
                _presentNames.Add(name);

            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _presentNames.Clear();
            ErrorMessage = $"Could not read folder: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            RefreshCheckedState();
            ContentsScanned?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True when a copy of <paramref name="file"/> lives in this folder.</summary>
    public bool Contains(FileItem file) => _presentNames.Contains(file.Name);

    /// <summary>
    /// Points the box at the file now on screen. It always renders the folder's real state, so a
    /// file whose copy hasn't finished yet shows as not present (and stays locked until it has).
    /// </summary>
    public void SetCurrentFile(FileItem? file)
    {
        _currentFile = file;
        IsBusy = file is not null && _pendingPaths.Contains(file.FullPath);
        RefreshCheckedState();
        OnPropertyChanged(nameof(CanToggle));
    }

    /// <summary>Adds the current file to this folder, or removes it if it's already there.</summary>
    [RelayCommand]
    private void Toggle()
    {
        var file = _currentFile;
        if (file is null || IsScanning || IsBusy)
            return;

        if (!_pendingPaths.Add(file.FullPath))
            return; // Already in flight — the box should have been locked.

        IsBusy = true;
        ErrorMessage = null;
        _ = ApplyAsync(file, add: !Contains(file));

        UserToggled?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyAsync(FileItem file, bool add)
    {
        try
        {
            if (add)
                await _sortService.CopyToFolderAsync(file, FolderPath);
            else
                await _sortService.RemoveFromFolderAsync(file, FolderPath);

            SetPresent(file.Name, add);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"{(add ? "Copy to" : "Remove from")} {FolderName} failed: {ex.Message}";

            // Fall back to what's actually on disk so the box never lies about the result.
            try
            {
                SetPresent(file.Name, _sortService.ExistsInFolder(file, FolderPath));
            }
            catch
            {
                SetPresent(file.Name, false);
            }
        }
        finally
        {
            _pendingPaths.Remove(file.FullPath);

            if (ReferenceEquals(_currentFile, file))
                IsBusy = false;

            RefreshCheckedState();
            FileStateChanged?.Invoke(this, file);
        }
    }

    /// <summary>
    /// Watches the folder so copies, deletions and renames made outside the app show up too.
    /// </summary>
    private void StartWatching()
    {
        StopWatching();

        try
        {
            _watcher = new FileSystemWatcher(FolderPath)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
            };

            _watcher.Created += (_, e) => OnFolderChanged(e.Name, present: true);
            _watcher.Deleted += (_, e) => OnFolderChanged(e.Name, present: false);
            _watcher.Renamed += (_, e) =>
            {
                OnFolderChanged(e.OldName, present: false);
                OnFolderChanged(e.Name, present: true);
            };

            // The change buffer overflowed — the index may have gaps, so read the folder again.
            _watcher.Error += (_, _) => Dispatcher.UIThread.Post(() => _ = ScanAsync());

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _watcher = null;
            ErrorMessage = $"Could not watch folder: {ex.Message}";
        }
    }

    private void StopWatching()
    {
        if (_watcher is null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>Applies a watcher event. Called on a watcher thread, so it hops to the UI thread.</summary>
    private void OnFolderChanged(string? name, bool present)
    {
        if (string.IsNullOrEmpty(name) || _sortService.IsInProgressArtifact(name))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            SetPresent(name, present);
            ContentChanged?.Invoke(this, name);
        });
    }

    /// <summary>Records whether a file name is in the folder and refreshes the box if it's on screen.</summary>
    private void SetPresent(string name, bool present)
    {
        var changed = present ? _presentNames.Add(name) : _presentNames.Remove(name);
        if (changed && string.Equals(_currentFile?.Name, name, StringComparison.OrdinalIgnoreCase))
            RefreshCheckedState();
    }

    private void RefreshCheckedState() =>
        IsChecked = _currentFile is not null && Contains(_currentFile);

    public void Dispose()
    {
        StopWatching();
        GC.SuppressFinalize(this);
    }
}
