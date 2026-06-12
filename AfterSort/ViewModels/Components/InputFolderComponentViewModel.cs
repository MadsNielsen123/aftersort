using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AfterSort.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public partial class InputFolderComponentViewModel : FolderItemViewModelBase
{
    #region Fields
    #endregion

    #region Properties

    // Only these two properties affect the computed IsCompleted property.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    public partial int SortedFiles { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    public partial int TotalFiles { get; set; }

    /// <summary>
    /// True when all files have been sorted and there is at least one file.
    /// </summary>
    public bool IsCompleted => SortedFiles == TotalFiles && TotalFiles > 0;

    /// <summary>
    /// All files discovered in this input folder.
    /// </summary>
    public List<FileItem> Files { get; private set; } = [];

    #endregion

    #region Events

    public event EventHandler? FilesLoaded;
    public event EventHandler<IEnumerable<FileItem>>? FilesAdded;

    #endregion

    #region Constructors
    #endregion

    #region Lifecycle
    #endregion

    #region Methods

    /// <summary>
    /// Scans the folder at <see cref="FolderItemViewModelBase.FolderPath"/> and populates
    /// <see cref="Files"/> with a <see cref="FileItem"/> for each file found.
    /// Sets <see cref="TotalFiles"/> to the count and <see cref="SortedFiles"/> to 0 immediately,
    /// then loads files in the background to keep the UI fast.
    /// </summary>
    public void LoadFiles()
    {
        TotalFiles = 0;
        SortedFiles = 0;
        Files = new List<FileItem>();

        if (!Directory.Exists(FolderPath))
            return;

        Task.Run(() =>
        {
            var directoryInfo = new DirectoryInfo(FolderPath);
            var batch = new List<FileItem>();
            int count = 0;

            foreach (var fileInfo in directoryInfo.EnumerateFiles())
            {
                batch.Add(FileItem.FromFileInfo(fileInfo, this));
                count++;

                // Post the first file instantly, then every 50 files to keep the UI perfectly fluid
                if (count == 1 || count % 50 == 0)
                {
                    var currentBatch = batch.ToList();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        Files.AddRange(currentBatch);
                        TotalFiles = Files.Count;
                        FilesAdded?.Invoke(this, currentBatch);
                        FilesLoaded?.Invoke(this, EventArgs.Empty);
                    });
                    batch.Clear();
                }
            }

            if (batch.Count > 0 || count == 0)
            {
                var currentBatch = batch.ToList();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Files.AddRange(currentBatch);
                    TotalFiles = Files.Count;
                    FilesAdded?.Invoke(this, currentBatch);
                    FilesLoaded?.Invoke(this, EventArgs.Empty);
                });
            }
        });
    }

    #endregion
}