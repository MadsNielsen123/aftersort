using AfterSort.Services;
using AfterSort.ViewModels;
using AfterSort.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace AfterSort;

public static class ServiceCollectionExtensions
{
    public static void AddAfterSortServices(this IServiceCollection services)
    {
        // SINGLETONS
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<ISortService, SortService>();
        services.AddSingleton<IVideoService, VideoService>();
        services.AddSingleton<MainWindowViewModel>();

        // TRANSIENTS
        services.AddTransient<MainPageViewModel>();
    }
}
