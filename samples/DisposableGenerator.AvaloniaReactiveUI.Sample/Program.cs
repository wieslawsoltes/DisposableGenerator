using Avalonia;
using ReactiveUI.Avalonia;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            SampleSmokeTest.Run();
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .UseReactiveUI(_ => { })
        .RegisterReactiveUIViewsFromEntryAssembly()
        .LogToTrace();
}
