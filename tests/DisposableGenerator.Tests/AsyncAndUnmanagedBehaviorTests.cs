using Microsoft.CodeAnalysis;

namespace DisposableGenerator.Tests;

public sealed class AsyncAndUnmanagedBehaviorTests
{
    [Fact]
    public void Ref_struct_disposable_property_uses_non_boxing_sync_call()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using DisposableGenerator;

            public static class Scenario
            {
                public static string Run()
                {
                    var events = new List<string>();
                    new Owner(events).Dispose();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable]
            public sealed partial class Owner(List<string> events)
            {
                [DisposeMember]
                public Resource Resource => new(events);
            }

            public ref struct Resource(List<string> events) : IDisposable
            {
                public void Dispose() => events.Add("disposed");
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("__DisposeRefLike(this.Resource);", result.GeneratedSource, StringComparison.Ordinal);
        var value = (string)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("disposed", value);
    }

    [Fact]
    public async Task Ref_struct_async_disposable_property_uses_non_boxing_async_call()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    await new Owner(events).DisposeAsync();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner(List<string> events)
            {
                [DisposeMember]
                public Resource Resource => new(events);
            }

            public ref struct Resource(List<string> events) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    events.Add("disposed-async");
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("await __DisposeRefLikeAsync(this.Resource).ConfigureAwait(false);", result.GeneratedSource, StringComparison.Ordinal);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("disposed-async", await task);
    }

    [Fact]
    public void Ref_struct_with_explicit_IDisposable_uses_constrained_non_boxing_dispatch()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using DisposableGenerator;

            public static class Scenario
            {
                public static string Run()
                {
                    var events = new List<string>();
                    new Owner(events).Dispose();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable]
            public sealed partial class Owner(List<string> events)
            {
                [DisposeMember]
                public Resource Resource => new(events);
            }

            public ref struct Resource(List<string> events) : IDisposable
            {
                void IDisposable.Dispose() => events.Add("disposed-explicitly");
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("where TDisposable : global::System.IDisposable, allows ref struct", result.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("__DisposeRefLike(this.Resource);", result.GeneratedSource, StringComparison.Ordinal);
        var value = (string)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("disposed-explicitly", value);

        var aggregateResult = GeneratorTestHarness.Run(
            source,
            new Dictionary<string, string>
            {
                ["DisposableGenerator_DisposalExceptionBehavior"] = "ContinueAndAggregate",
            });

        Assert.DoesNotContain(aggregateResult.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("__DisposeRefLike(this.Resource);", aggregateResult.GeneratedSource, StringComparison.Ordinal);
        var aggregateValue = (string)aggregateResult.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("disposed-explicitly", aggregateValue);
    }

    [Fact]
    public async Task Ref_struct_with_explicit_IAsyncDisposable_uses_constrained_non_boxing_dispatch()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    await new Owner(events).DisposeAsync();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner(List<string> events)
            {
                [DisposeMember]
                public Resource Resource => new(events);
            }

            public ref struct Resource(List<string> events) : IAsyncDisposable
            {
                ValueTask IAsyncDisposable.DisposeAsync()
                {
                    events.Add("disposed-explicitly-async");
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("where TDisposable : global::System.IAsyncDisposable, allows ref struct", result.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("await __DisposeRefLikeAsync(this.Resource).ConfigureAwait(false);", result.GeneratedSource, StringComparison.Ordinal);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("disposed-explicitly-async", await task);
    }

    [Fact]
    public async Task AsyncOnlyOwnerDisposesMembersRegistrationsAndHooksInOrder()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    var owner = new Owner(events);
                    await owner.AddAsync(new AsyncResource("dynamic:first", events));
                    await owner.AddAsync(new AsyncResource("dynamic:second", events));
                    await owner.DisposeAsync();
                    await owner.DisposeAsync();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                [DisposeMember] private readonly IAsyncDisposable _first;
                [DisposeMember] private readonly IAsyncDisposable _second;

                public Owner(List<string> events)
                {
                    _events = events;
                    _first = new AsyncResource("first", events);
                    _second = new AsyncResource("second", events);
                }

                public ValueTask<T> AddAsync<T>(T resource) where T : IAsyncDisposable => RegisterAsyncDisposable(resource);
                partial void OnDisposing() => _events.Add("hook:disposing");
                partial void OnDisposed() => _events.Add("hook:disposed");
            }

            public sealed class AsyncResource(string name, List<string> events) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    events.Add(name);
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_ReportUnownedDisposableFields"] = "false",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP022");
        Assert.Contains("global::System.IAsyncDisposable", result.GeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("global::System.IDisposable,", result.GeneratedSource, StringComparison.Ordinal);
        var assembly = result.EmitAndLoad();
        var task = (Task<string>)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("hook:disposing,second,first,dynamic:second,dynamic:first,hook:disposed", await task);
    }

    [Fact]
    public async Task ConjunctiveOwnerUsesTheMatchingMemberDisposalPath()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    new Owner(new DualResource(events, "sync")).Dispose();
                    await new Owner(new DualResource(events, "async")).DisposeAsync();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly DualResource _resource;
                public Owner(DualResource resource) => _resource = resource;
            }

            public sealed class DualResource(List<string> events, string name) : IDisposable, IAsyncDisposable
            {
                public void Dispose() => events.Add(name + ":Dispose");
                public ValueTask DisposeAsync()
                {
                    events.Add(name + ":DisposeAsync");
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("sync:Dispose,async:DisposeAsync", await task);
    }

    [Fact]
    public async Task NullableAsyncOnlyStructIsDisposedThroughItsAsyncPath()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    var owner = new Owner(new AsyncToken(events));
                    await owner.DisposeAsync();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly AsyncToken? _resource;
                public Owner(AsyncToken? resource) => _resource = resource;
            }

            public readonly struct AsyncToken(List<string> events) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    events.Add("async-token");
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP003");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("async-token", await task);
    }

    [Fact]
    public async Task RegistrationIsClosedWhileGeneratedDerivedAsyncCleanupIsAwaiting()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var owner = new DerivedOwner(started, release);
                    var disposal = owner.DisposeAsync().AsTask();
                    await started.Task;

                    try
                    {
                        await owner.AddAsync(new TrackingAsyncResource(events));
                        events.Add("registration-accepted");
                    }
                    catch (ObjectDisposedException)
                    {
                        events.Add("registration-rejected");
                    }

                    release.SetResult(true);
                    await disposal;
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public partial class BaseOwner
            {
                public ValueTask<T> AddAsync<T>(T resource) where T : IAsyncDisposable =>
                    RegisterAsyncDisposable(resource);
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class DerivedOwner : BaseOwner
            {
                [DisposeMember] private readonly IAsyncDisposable _resource;

                public DerivedOwner(TaskCompletionSource<bool> started, TaskCompletionSource<bool> release)
                {
                    _resource = new BlockingAsyncResource(started, release);
                }
            }

            public sealed class BlockingAsyncResource(
                TaskCompletionSource<bool> started,
                TaskCompletionSource<bool> release) : IAsyncDisposable
            {
                public async ValueTask DisposeAsync()
                {
                    started.SetResult(true);
                    await release.Task;
                }
            }

            public sealed class TrackingAsyncResource(List<string> events) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    events.Add("late-resource-disposed");
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_ReportUnownedDisposableFields"] = "false",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("registration-rejected", await task);
    }

    [Fact]
    public void AsyncOnlyOwnershipRequiresAnAsyncCapableOwner()
    {
        const string source = """
            using System;
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly IAsyncDisposable? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP020");
    }

    [Fact]
    public void AsyncGenerationWithoutIAsyncDisposableReportsDISP019()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public sealed partial class Owner { }
            """;
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(packageRoot))
        {
            packageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        var netStandardReferencePath = Path.Combine(
            packageRoot,
            "netstandard.library",
            "2.0.3",
            "build",
            "netstandard2.0",
            "ref",
            "netstandard.dll");
        Assert.True(File.Exists(netStandardReferencePath), "The restored NETStandard.Library 2.0 reference assembly was not found.");

        var result = GeneratorTestHarness.Run(
            source,
            platformReferences: [MetadataReference.CreateFromFile(netStandardReferencePath)]);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP019");
    }

    [Fact]
    public void ConjunctiveOwnerWarnsWhenSyncDisposeCannotReleaseAnAsyncOnlyMember()
    {
        const string source = """
            using System;
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly IAsyncDisposable? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP022");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AsyncAggregatePolicyContinuesAndFlattensFailures()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    var owner = new Owner(events);
                    try
                    {
                        await owner.DisposeAsync();
                        return "no-error";
                    }
                    catch (AggregateException error)
                    {
                        return string.Join(",", events) + "|" + error.InnerExceptions.Count;
                    }
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly IAsyncDisposable _first;
                [DisposeMember] private readonly IAsyncDisposable _second;
                public Owner(List<string> events)
                {
                    _first = new FailingResource("first", events);
                    _second = new FailingResource("second", events);
                }
            }

            public sealed class FailingResource(string name, List<string> events) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    events.Add(name);
                    return ValueTask.FromException(new InvalidOperationException(name));
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_DisposalExceptionBehavior"] = "ContinueAndAggregate",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("second,first|2", await task);
    }

    [Fact]
    public async Task AsyncGeneratedInheritanceCleansDerivedThenBaseAndRootRegistrations()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    var owner = new Owner(events);
                    await owner.AddAsync(new Resource("dynamic", events));
                    await owner.DisposeAsync();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public partial class BaseOwner
            {
                [DisposeMember] private readonly IAsyncDisposable _baseResource;
                protected BaseOwner(List<string> events) => _baseResource = new Resource("base", events);
                protected ValueTask<T> AddAsync<T>(T resource) where T : IAsyncDisposable => RegisterAsyncDisposable(resource);
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner : BaseOwner
            {
                [DisposeMember] private readonly IAsyncDisposable _derivedResource;
                public Owner(List<string> events) : base(events) => _derivedResource = new Resource("derived", events);
                public new ValueTask<T> AddAsync<T>(T resource) where T : IAsyncDisposable => base.AddAsync(resource);
            }

            public sealed class Resource(string name, List<string> events) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    events.Add(name);
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("derived,base,dynamic", await task);
    }

    [Fact]
    public async Task ConcurrentSyncAndAsyncDisposalHaveOneWinner()
    {
        const string source = """
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<int> Run()
                {
                    var counter = new Counter();
                    var owner = new Owner(new DualResource(counter));
                    var operations = Enumerable.Range(0, 32)
                        .Select(index => index % 2 == 0
                            ? Task.Run(owner.Dispose)
                            : Task.Run(async () => await owner.DisposeAsync()))
                        .ToArray();
                    await Task.WhenAll(operations);
                    return counter.Value;
                }
            }

            [GenerateDisposable(GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly DualResource _resource;
                public Owner(DualResource resource) => _resource = resource;
            }

            public sealed class Counter { public int Value; }
            public sealed class DualResource(Counter counter) : IDisposable, IAsyncDisposable
            {
                public void Dispose() => Interlocked.Increment(ref counter.Value);
                public ValueTask DisposeAsync()
                {
                    Interlocked.Increment(ref counter.Value);
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<int>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal(1, await task);
    }

    [Fact]
    public async Task LosingAsyncCallerCannotRunUnmanagedCleanupBeforeSyncWinnerFinishes()
    {
        const string source = """
            using System;
            using System.Collections.Concurrent;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new ConcurrentQueue<string>();
                    using var started = new ManualResetEventSlim();
                    using var release = new ManualResetEventSlim();
                    var owner = new Owner(events, started, release);
                    var synchronous = Task.Run(owner.Dispose);
                    started.Wait();
                    await owner.DisposeAsync();
                    var premature = events.Contains("unmanaged");
                    release.Set();
                    await synchronous;
                    return premature + "|" + string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateAsyncDispose = true, GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner
            {
                private readonly ConcurrentQueue<string> _events;
                [DisposeMember] private readonly Resource _resource;
                public Owner(ConcurrentQueue<string> events, ManualResetEventSlim started, ManualResetEventSlim release)
                {
                    _events = events;
                    _resource = new Resource(events, started, release);
                }
                partial void DisposeUnmanaged() => _events.Enqueue("unmanaged");
            }

            public sealed class Resource(
                ConcurrentQueue<string> events,
                ManualResetEventSlim started,
                ManualResetEventSlim release) : IDisposable, IAsyncDisposable
            {
                public void Dispose()
                {
                    events.Enqueue("managed:start");
                    started.Set();
                    release.Wait();
                    events.Enqueue("managed:end");
                }

                public ValueTask DisposeAsync()
                {
                    events.Enqueue("managed:async");
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("False|managed:start,managed:end,unmanaged", await task);
    }

    [Fact]
    public async Task LateAsyncRegistrationCanDisposeImmediately()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    var owner = new Owner();
                    await owner.DisposeAsync();
                    var resource = new Resource(events);
                    var returned = await owner.AddAsync(resource);
                    return ReferenceEquals(resource, returned) + "|" + string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                public ValueTask<T> AddAsync<T>(T resource) where T : IAsyncDisposable => RegisterAsyncDisposable(resource);
            }

            public sealed class Resource(List<string> events) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    events.Add("late");
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_PostDisposeRegistrationBehavior"] = "DisposeImmediately",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("True|late", await task);
    }

    [Fact]
    public void UnmanagedCleanupRunsAfterManagedCleanupDuringDispose()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using DisposableGenerator;

            public static class Scenario
            {
                public static string Run()
                {
                    var events = new List<string>();
                    new Owner(events).Dispose();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                [DisposeMember] private readonly IDisposable _managed;
                public Owner(List<string> events)
                {
                    _events = events;
                    _managed = new Callback(() => events.Add("managed"));
                }
                partial void DisposeUnmanaged() => _events.Add("unmanaged");
            }

            public sealed class Callback(Action callback) : IDisposable
            {
                public void Dispose() => callback();
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var actual = (string)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("managed,unmanaged", actual);
    }

    [Fact]
    public async Task ConjunctiveDisposeAsyncRunsUnmanagedCleanupAfterAsyncCleanup()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    await new Owner(events).DisposeAsync();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateAsyncDispose = true, GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                [DisposeMember] private readonly IAsyncDisposable _managed;
                public Owner(List<string> events)
                {
                    _events = events;
                    _managed = new Callback(() => events.Add("async"));
                }
                partial void DisposeUnmanaged() => _events.Add("unmanaged");
            }

            public sealed class Callback(Action callback) : IAsyncDisposable
            {
                public ValueTask DisposeAsync()
                {
                    callback();
                    return default;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP022");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("async,unmanaged", await task);
    }

    [Fact]
    public async Task ConjunctiveAggregatePolicyCombinesAsyncAndUnmanagedFailures()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    try
                    {
                        await new Owner(events).DisposeAsync();
                        return "no-error";
                    }
                    catch (AggregateException error)
                    {
                        return string.Join(",", events) + "|" + error.InnerExceptions.Count;
                    }
                }
            }

            [GenerateDisposable(GenerateAsyncDispose = true, GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                [DisposeMember] private readonly Resource _resource;
                public Owner(List<string> events)
                {
                    _events = events;
                    _resource = new Resource(events);
                }
                partial void DisposeUnmanaged()
                {
                    _events.Add("unmanaged");
                    throw new InvalidOperationException("unmanaged");
                }
            }

            public sealed class Resource(List<string> events) : IDisposable, IAsyncDisposable
            {
                public void Dispose() => throw new InvalidOperationException("sync");
                public ValueTask DisposeAsync()
                {
                    events.Add("async");
                    return ValueTask.FromException(new InvalidOperationException("async"));
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_DisposalExceptionBehavior"] = "ContinueAndAggregate",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("async,unmanaged|2", await task);
    }

    [Fact]
    public void StopOnFirstPreservesManagedFailureWhenUnmanagedCleanupAlsoFails()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using DisposableGenerator;

            public static class Scenario
            {
                public static string Run()
                {
                    var events = new List<string>();
                    try
                    {
                        new Owner(events).Dispose();
                        return "no-error";
                    }
                    catch (InvalidOperationException error)
                    {
                        return error.Message + "|" + string.Join(",", events);
                    }
                }
            }

            [GenerateDisposable(GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                [DisposeMember] private readonly Resource _resource;

                public Owner(List<string> events)
                {
                    _events = events;
                    _resource = new Resource(events);
                }

                partial void DisposeUnmanaged()
                {
                    _events.Add("unmanaged");
                    throw new InvalidOperationException("unmanaged");
                }
            }

            public sealed class Resource(List<string> events) : IDisposable
            {
                public void Dispose()
                {
                    events.Add("managed");
                    throw new InvalidOperationException("managed");
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var value = (string)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("managed|managed,unmanaged", value);
    }

    [Fact]
    public async Task ConjunctiveStopOnFirstPreservesAsyncFailureWhenUnmanagedCleanupAlsoFails()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public static class Scenario
            {
                public static async Task<string> Run()
                {
                    var events = new List<string>();
                    try
                    {
                        await new Owner(events).DisposeAsync();
                        return "no-error";
                    }
                    catch (InvalidOperationException error)
                    {
                        return error.Message + "|" + string.Join(",", events);
                    }
                }
            }

            [GenerateDisposable(GenerateAsyncDispose = true, GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                [DisposeMember] private readonly Resource _resource;

                public Owner(List<string> events)
                {
                    _events = events;
                    _resource = new Resource(events);
                }

                partial void DisposeUnmanaged()
                {
                    _events.Add("unmanaged");
                    throw new InvalidOperationException("unmanaged");
                }
            }

            public sealed class Resource(List<string> events) : IDisposable, IAsyncDisposable
            {
                public void Dispose() { }

                public ValueTask DisposeAsync()
                {
                    events.Add("async");
                    return ValueTask.FromException(new InvalidOperationException("async"));
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var task = (Task<string>)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("async|async,unmanaged", await task);
    }

    [Fact]
    public void GeneratedFinalizerInvokesOnlyUnmanagedCleanupOnFinalizationPath()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Reflection;
            using DisposableGenerator;

            public static class Scenario
            {
                public static string Run()
                {
                    var events = new List<string>();
                    var owner = new Owner(events);
                    typeof(Owner).GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null);
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable(GenerateFinalizer = true)]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                [DisposeMember] private readonly IDisposable _managed;
                public Owner(List<string> events)
                {
                    _events = events;
                    _managed = new Callback(() => events.Add("managed"));
                }
                partial void DisposeUnmanaged()
                {
                    _events.Add("unmanaged");
                    throw new InvalidOperationException("Finalizer-path failures must be contained.");
                }
            }

            public sealed class Callback(Action callback) : IDisposable
            {
                public void Dispose() => callback();
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("~Owner()", result.GeneratedSource, StringComparison.Ordinal);
        var actual = (string)result.EmitAndLoad().GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("unmanaged", actual);
    }

    [Theory]
    [InlineData("[GenerateDisposable(GenerateSynchronousDispose = false)]")]
    [InlineData("[GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true, GenerateFinalizer = true)]")]
    public void InvalidGenerationModesReportDISP025(string attribute)
    {
        var source = $$"""
            using DisposableGenerator;
            {{attribute}}
            public sealed partial class Owner { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP025");
    }

    [Fact]
    public void GeneratedHierarchyMustUseTheSameDisposalMode()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public partial class BaseOwner { }
            [GenerateDisposable]
            public sealed partial class Owner : BaseOwner { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP021");
    }

    [Fact]
    public void ManualDisposeAsyncReportsDISP005()
    {
        const string source = """
            using System.Threading.Tasks;
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                public ValueTask DisposeAsync() => default;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP005");
    }

    [Fact]
    public void NonGeneratedAsyncDisposableBaseReportsDISP023()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using DisposableGenerator;
            public class FrameworkBase : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => default;
            }
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public sealed partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP023");
    }

    [Fact]
    public void InvalidUnmanagedHookReportsDISP024()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner
            {
                private void DisposeUnmanaged() { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP024");
    }

    [Fact]
    public void BorrowedAsyncDisposableIsAValidOwnershipDecision()
    {
        const string source = """
            using System;
            using DisposableGenerator;
            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                [BorrowedMember] private readonly IAsyncDisposable? _service;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id is "DISP006" or "DISP014");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void GeneratedAsyncInheritanceChainsAcrossCompilationBoundaries()
    {
        const string baseSource = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public partial class ExternalBase { }
            """;
        var baseResult = GeneratorTestHarness.Run(baseSource);
        var baseReference = baseResult.EmitToReference();

        const string derivedSource = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner : ExternalBase { }
            """;
        var result = GeneratorTestHarness.Run(derivedSource, additionalReferences: [baseReference]);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("protected override async global::System.Threading.Tasks.ValueTask DisposeAsyncCore()", result.GeneratedSource, StringComparison.Ordinal);
    }
}
