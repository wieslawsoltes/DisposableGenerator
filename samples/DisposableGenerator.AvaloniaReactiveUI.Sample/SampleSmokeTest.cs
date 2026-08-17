using DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;
using DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample;

internal static class SampleSmokeTest
{
    internal static void Run()
    {
        var log = new DisposalLog();
        var applicationService = new ApplicationService(log);
        var workspace = new WorkspaceViewModel(applicationService, log);

        workspace.Editor.Text = "Reactive";
        workspace.Editor.Title = "Generated title";
        workspace.Preview.ZoomPercent = 125;

        if (workspace.Editor.Status != "8 characters" ||
            workspace.Editor.Title != "Generated title" ||
            workspace.Preview.ZoomPercent != 125)
        {
            throw new InvalidOperationException("ReactiveUI-generated properties did not behave as expected.");
        }

        _ = workspace.Editor.SaveCommand;
        _ = workspace.Preview.ToggleLiveCommand;

        workspace.Dispose();
        workspace.Dispose();

        var entries = log.Entries;
        AssertExactlyOnce(entries, "Workspace:leaf-disposing");
        AssertExactlyOnce(entries, "Preview:leaf-member");
        AssertExactlyOnce(entries, "Editor:leaf-member");
        AssertExactlyOnce(entries, "Preview:document-member");
        AssertExactlyOnce(entries, "Editor:document-member");
        AssertExactlyOnce(entries, "Workspace:base-member");

        AssertBefore(entries, "Workspace:leaf-disposing", "Preview:leaf-disposing");
        AssertBefore(entries, "Preview:leaf-disposing", "Preview:document-disposing");
        AssertBefore(entries, "Preview:document-disposing", "Preview:base-disposing");
        AssertBefore(entries, "Preview:base-disposed", "Editor:leaf-disposing");
        AssertBefore(entries, "Editor:leaf-disposing", "Editor:document-disposing");
        AssertBefore(entries, "Editor:document-disposing", "Editor:base-disposing");

        if (entries.Contains("application-service:disposed", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The borrowed application service was disposed by a view model.");
        }

        applicationService.Dispose();
        AssertExactlyOnce(log.Entries, "application-service:disposed");

        Console.WriteLine("Avalonia / ReactiveUI 24 inheritance smoke test passed.");
    }

    private static void AssertExactlyOnce(IReadOnlyList<string> entries, string expected)
    {
        if (entries.Count(entry => entry == expected) != 1)
        {
            throw new InvalidOperationException($"Expected exactly one '{expected}' entry. Actual: {string.Join(", ", entries)}");
        }
    }

    private static void AssertBefore(IReadOnlyList<string> entries, string first, string second)
    {
        var firstIndex = IndexOf(entries, first);
        var secondIndex = IndexOf(entries, second);
        if (firstIndex < 0 || secondIndex < 0 || firstIndex >= secondIndex)
        {
            throw new InvalidOperationException($"Expected '{first}' before '{second}'. Actual: {string.Join(", ", entries)}");
        }
    }

    private static int IndexOf(IReadOnlyList<string> entries, string value)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index] == value)
            {
                return index;
            }
        }

        return -1;
    }
}

