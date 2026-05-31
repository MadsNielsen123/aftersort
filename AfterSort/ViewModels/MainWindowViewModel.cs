using AfterSort.ViewModels.Pages;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AfterSort.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _currentPage;

    public MainWindowViewModel(MainPageViewModel defaultPage)
    {
        _currentPage = defaultPage;
    }
}
