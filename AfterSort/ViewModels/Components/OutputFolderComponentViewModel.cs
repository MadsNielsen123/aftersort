using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels.Components;

public partial class OutputFolderComponentViewModel : ViewModelBase
{
    #region Fields
    #endregion

    #region Properties

    [ObservableProperty]
    public partial string FolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FolderName { get; set; } = string.Empty;

    #endregion

    #region Constructors

    public OutputFolderComponentViewModel()
    {
    }

    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}
