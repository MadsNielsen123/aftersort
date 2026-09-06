using Avalonia;
using System;

namespace AfterSort;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // X11 defaults to window-manager decorations, which leave WindowDecorationMargin at
            // zero and collapse the custom titlebar. Opt into Avalonia-drawn ones so Linux gets
            // the same chrome as Windows. Still marked experimental in Avalonia 12.0.
#pragma warning disable AVALONIA_X11_CSD
            .With(new X11PlatformOptions { EnableDrawnDecorations = true })
#pragma warning restore AVALONIA_X11_CSD
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
