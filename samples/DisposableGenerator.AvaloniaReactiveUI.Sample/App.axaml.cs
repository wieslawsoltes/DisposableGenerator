using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;
using DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;
using DisposableGenerator.AvaloniaReactiveUI.Sample.Views;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = new DisposalLog();
            var applicationService = new ApplicationService(log);
            var workspace = new WorkspaceViewModel(applicationService, log);

            desktop.MainWindow = new MainWindow
            {
                ViewModel = workspace,
            };

            desktop.Exit += (_, _) =>
            {
                workspace.Dispose();
                applicationService.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

