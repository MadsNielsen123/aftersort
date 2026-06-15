using AfterSort.Models;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public partial class InputFolderComponentViewModel : FolderItemViewModelBase
{
    // Only these two properties affect the computed IsCompleted property.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    public partial int SortedFiles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    public partial int TotalFiles { get; set; }

    /// <summary>True when all files have been sorted and there is at least one file.</summary>
    public bool IsCompleted => SortedFiles == TotalFiles && TotalFiles > 0;

    /// <summary>All files discovered in this input folder.</summary>
    public List<FileItem> Files { get; private set; } = [];

    public event EventHandler? FilesLoaded;
    public event EventHandler<IEnumerable<FileItem>>? FilesAdded;

    /// <summary>
    /// Scans <see cref="FolderItemViewModelBase.FolderPath"/> on a background thread, posting files to
    /// the UI in batches so a large folder stays responsive.
    /// </summary>
    public void LoadFiles()
    {
        TotalFiles = 0;
        SortedFiles = 0;
        Files = [];

        if (!Directory.Exists(FolderPath))
            return;

        Task.Run(() =>
        {
            var directoryInfo = new DirectoryInfo(FolderPath);
            var batch = new List<FileItem>();
            var count = 0;

            foreach (var fileInfo in directoryInfo.EnumerateFiles())
            {
                batch.Add(FileItem.FromFileInfo(fileInfo, this));
                count++;

                // Post the first file instantly, then every 50 files, to keep the UI fluid.
                if (count == 1 || count % 50 == 0)
                {
                    PublishBatch(batch);
                    batch = [];
                }
            }

            if (batch.Any() || count == 0)
                PublishBatch(batch);
        });
    }

    private void PublishBatch(List<FileItem> batch)
    {
        var published = batch.ToList();
        Dispatcher.UIThread.Post(() =>
        {
            Files.AddRange(published);
            TotalFiles = Files.Count;
            FilesAdded?.Invoke(this, published);
            FilesLoaded?.Invoke(this, EventArgs.Empty);
        });
    }
}
