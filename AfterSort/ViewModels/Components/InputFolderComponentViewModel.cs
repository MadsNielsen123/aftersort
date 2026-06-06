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
    public partial int ProcessedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    public partial int TotalCount { get; set; }

    /// <summary>
    /// True when all files have been processed and there is at least one file.
    /// </summary>
    public bool IsCompleted => ProcessedCount == TotalCount && TotalCount > 0;

    #endregion

    #region Constructors
    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}