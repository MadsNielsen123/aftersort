using AfterSort.ViewModels.Components;

namespace AfterSort.ViewModels.Pages;

public partial class MainPageViewModel : ViewModelBase
{
    #region Fields
    #endregion

    #region Properties

    public FolderSelectComponentViewModel InputFolders { get; }
    public FolderSelectComponentViewModel DestFolders { get; }

    #endregion

    #region Constructors

    private readonly Services.IStorageService _storageService;

    public MainPageViewModel(Services.IStorageService storageService)
    {
        _storageService = storageService;
        InputFolders = new FolderSelectComponentViewModel { Title = "Input Folders" };
        DestFolders = new FolderSelectComponentViewModel { Title = "Dest Folders" };

        InputFolders.ItemFactory = async () =>
        {
            var result = await _storageService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select Input Folder" });
            if (result != null && result.Count > 0)
            {
                return new InputFolderComponentViewModel
                {
                    FolderName = result[0].Name,
                    FolderPath = result[0].Path.LocalPath,
                    ProcessedCount = 0,
                    TotalCount = 0
                };
            }
            return null;
        };

        DestFolders.ItemFactory = async () =>
        {
            var result = await _storageService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Select Destination Folder" });
            if (result != null && result.Count > 0)
            {
                return new OutputFolderComponentViewModel
                {
                    FolderName = result[0].Name,
                    FolderPath = result[0].Path.LocalPath
                };
            }
            return null;
        };

    }

    #endregion

    #region Lifecycle
    #endregion

    #region Methods
    #endregion
}
