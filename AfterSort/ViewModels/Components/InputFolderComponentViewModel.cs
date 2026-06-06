using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public partial class InputFolderComponentViewModel : ViewModelBase
{
    #region Fields
    #endregion

    #region Properties

    [ObservableProperty]
    public partial string FolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderName { get; set; } = string.Empty;

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

    public InputFolderComponentViewModel()
    {
    }

    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}