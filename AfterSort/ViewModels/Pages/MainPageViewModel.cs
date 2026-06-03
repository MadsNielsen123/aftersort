using System.Collections.ObjectModel;
using AfterSort.ViewModels.Components;

namespace AfterSort.ViewModels.Pages;

public partial class MainPageViewModel : ViewModelBase
{
    #region Fields
    #endregion

    #region Properties

    public ObservableCollection<InputFolderComponentViewModel> InputFolders { get; } = [];

    #endregion

    #region Constructors

    public MainPageViewModel()
    {
        // Sample data for development — remove when real folder scanning is wired up
        InputFolders.Add(new InputFolderComponentViewModel
        {
            FolderName = "Downloads",
            ProcessedCount = 12,
            TotalCount = 48
        });
        InputFolders.Add(new InputFolderComponentViewModel
        {
            FolderName = "Screenshots",
            ProcessedCount = 7,
            TotalCount = 7
        });
    }

    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}
