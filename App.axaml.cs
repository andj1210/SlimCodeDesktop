using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SlimCodeDesktop.Core;
using SlimCodeDesktop.Themes;

namespace SlimCodeDesktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Before the first window: code-behind bakes ThemeBrushes values
            // into the controls it builds, so an accent applied after this
            // point would only reach the XAML-styled chrome.
            var settings = AppSettingsStore.Load(AppPaths.AppSettingsFile).Values;
            AccentPalette.Apply(this,
                AccentPalette.Resolve(settings.Accent, settings.AccentColorHex, AppPaths.Profile));
            AppIcons.ApplyToAllWindows(this);

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
