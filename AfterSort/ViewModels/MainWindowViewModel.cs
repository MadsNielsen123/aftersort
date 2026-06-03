using AfterSort.ViewModels.Pages;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    #region Fields

    [ObservableProperty]
    private ViewModelBase _currentPage;

    #endregion

    #region Constructors

    public MainWindowViewModel(MainPageViewModel defaultPage)
    {
        _currentPage = defaultPage;
    }

    #endregion

    #region Properties
    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}
