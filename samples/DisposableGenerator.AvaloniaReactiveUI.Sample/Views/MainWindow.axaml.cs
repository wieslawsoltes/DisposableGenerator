using DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;
using ReactiveUI.Avalonia;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample.Views;

public sealed partial class MainWindow : ReactiveWindow<WorkspaceViewModel>
{
    public MainWindow()
    {
        InitializeComponent();
    }
}

