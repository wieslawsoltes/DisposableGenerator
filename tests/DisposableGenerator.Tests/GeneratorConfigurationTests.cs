using Microsoft.CodeAnalysis;

namespace DisposableGenerator.Tests;

public sealed class GeneratorConfigurationTests
{
    private const string Source = """
        using DisposableGenerator;
        [GenerateDisposable]
        public partial class Owner
        {
            [DisposeMember] private System.IDisposable? _first;
            [DisposeMember] private System.IDisposable? _second;
        }
        """;

    [Fact]
    public void Configuration_changes_registration_and_member_order()
    {
        var properties = new Dictionary<string, string>
        {
            ["DisposableGenerator_RegistrationMethodName"] = "Own",
            ["DisposableGenerator_PostDisposeRegistrationBehavior"] = "DisposeImmediately",
            ["DisposableGenerator_MemberDisposalOrder"] = "Declaration",
            ["DisposableGenerator_GenerateDisposalHooks"] = "false",
        };

        var result = GeneratorTestHarness.Run(Source, properties);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("protected T Own<T>", result.GeneratedSource);
        Assert.Contains("disposable.Dispose();", result.GeneratedSource);
        Assert.DoesNotContain("OnDisposing", result.GeneratedSource);
        Assert.True(result.GeneratedSource.IndexOf("this._first", StringComparison.Ordinal) < result.GeneratedSource.IndexOf("this._second", StringComparison.Ordinal));
    }

    [Fact]
    public void Registration_generation_can_be_disabled()
    {
        var result = GeneratorTestHarness.Run(Source, new Dictionary<string, string>
        {
            ["DisposableGenerator_GenerateRegistrationMethod"] = "false",
        });

        Assert.DoesNotContain("RegisterDisposable", result.GeneratedSource);
        Assert.Contains("Interlocked.Exchange", result.GeneratedSource);
    }

    [Fact]
    public void Invalid_configuration_reports_DISP009_and_uses_fallback()
    {
        var result = GeneratorTestHarness.Run(Source, new Dictionary<string, string>
        {
            ["DisposableGenerator_MemberDisposalOrder"] = "Sideways",
            ["DisposableGenerator_RegistrationMethodName"] = "not valid",
        });

        Assert.Equal(2, result.AllDiagnostics.Count(diagnostic => diagnostic.Id == "DISP009"));
        Assert.Contains("protected T RegisterDisposable<T>", result.GeneratedSource);
        Assert.True(result.GeneratedSource.IndexOf("this._second", StringComparison.Ordinal) < result.GeneratedSource.IndexOf("this._first", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_configuration_is_reported_once_per_property_not_once_per_type()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable] public partial class First { }
            [GenerateDisposable] public partial class Second { }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_DisposalExceptionBehavior"] = "IgnoreEverything",
        });

        Assert.Single(result.AllDiagnostics.Where(diagnostic => diagnostic.Id == "DISP009"));
    }

    [Fact]
    public void Numeric_enum_configuration_is_rejected()
    {
        var result = GeneratorTestHarness.Run(Source, new Dictionary<string, string>
        {
            ["DisposableGenerator_MemberDisposalOrder"] = "1",
        });

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP009");
    }

    [Fact]
    public void AsyncRegistrationMethodCanBeRenamed()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public partial class Owner { }
            """;
        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_AsyncRegistrationMethodName"] = "OwnAsync",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("OwnAsync<", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationMethodNamesMustBeDistinct()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public partial class Owner { }
            """;
        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_RegistrationMethodName"] = "Own",
            ["DisposableGenerator_AsyncRegistrationMethodName"] = "Own",
        });

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP009");
        Assert.Contains("RegisterAsyncDisposable<", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_method_names_compare_escaped_identifier_value_text()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public partial class Owner { }
            """;
        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_RegistrationMethodName"] = "Own",
            ["DisposableGenerator_AsyncRegistrationMethodName"] = "@Own",
        });

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP009");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0111");
        Assert.Contains("RegisterAsyncDisposable<", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Escaped_generated_member_name_is_still_reserved()
    {
        var result = GeneratorTestHarness.Run(Source, new Dictionary<string, string>
        {
            ["DisposableGenerator_RegistrationMethodName"] = "@Dispose",
        });

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP009");
        Assert.DoesNotContain("@Dispose<", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DisposableGenerator_RegistrationMethodName", "__DisposableGenerator_asyncCleanupCompleted")]
    [InlineData("DisposableGenerator_AsyncRegistrationMethodName", "__DisposableGenerator_unmanagedDisposeState")]
    public void Registration_method_names_cannot_use_conditionally_emitted_state_fields(string propertyName, string methodName)
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true, GenerateUnmanagedCleanup = true)]
            public sealed partial class Owner { }
            """;
        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            [propertyName] = methodName,
        });

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP009");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0102");
        Assert.DoesNotContain(methodName + "<", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DisposableGenerator_RegistrationMethodName")]
    [InlineData("DisposableGenerator_AsyncRegistrationMethodName")]
    public void Registration_method_name_cannot_match_the_generated_owner_type(string propertyName)
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable(GenerateAsyncDispose = true)]
            public sealed partial class Owner { }
            """;
        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            [propertyName] = "Owner",
        });

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP012");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0542");
        Assert.DoesNotContain("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeMember_Order_takes_priority_over_declaration_order()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                [DisposeMember(Order = 10)] private System.IDisposable? _last;
                [DisposeMember(Order = -10)] private System.IDisposable? _first;
                [DisposeMember] private System.IDisposable? _middle;
            }
            """;

        var result = GeneratorTestHarness.Run(source);
        var generated = result.GeneratedSource;

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.True(generated.IndexOf("this._first", StringComparison.Ordinal) < generated.IndexOf("this._middle", StringComparison.Ordinal));
        Assert.True(generated.IndexOf("this._middle", StringComparison.Ordinal) < generated.IndexOf("this._last", StringComparison.Ordinal));
    }

    [Fact]
    public void Registered_resources_can_be_disposed_in_registration_order()
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
                    var owner = new Owner(events);
                    owner.Add(new Resource("first", events));
                    owner.Add(new Resource("second", events));
                    owner.Dispose();
                    return string.Join(",", events);
                }
            }

            [GenerateDisposable]
            public sealed partial class Owner
            {
                private readonly List<string> _events;
                public Owner(List<string> events) => _events = events;
                public void Add(IDisposable resource) => RegisterDisposable(resource);
            }

            public sealed class Resource(string name, List<string> events) : IDisposable
            {
                public void Dispose() => events.Add(name);
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_RegisteredResourceDisposalOrder"] = "Registration",
            ["DisposableGenerator_GenerateDisposalHooks"] = "false",
            ["DisposableGenerator_ReportUnownedDisposableFields"] = "false",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var assembly = result.EmitAndLoad();
        var actual = (string)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("first,second", actual);
    }

    [Fact]
    public void ContinueAndAggregate_attempts_every_cleanup_and_flattens_errors()
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
                    var owner = new Owner(events);
                    owner.Add(new FailingResource("dynamic", events));
                    try
                    {
                        owner.Dispose();
                        return "no-error";
                    }
                    catch (AggregateException exception)
                    {
                        return string.Join(",", events) + "|" + exception.InnerExceptions.Count;
                    }
                }
            }

            [GenerateDisposable]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly IDisposable _first;
                [DisposeMember] private readonly IDisposable _second;

                public Owner(List<string> events)
                {
                    _first = new FailingResource("first", events);
                    _second = new FailingResource("second", events);
                }

                public void Add(IDisposable resource) => RegisterDisposable(resource);
            }

            public sealed class FailingResource(string name, List<string> events) : IDisposable
            {
                public void Dispose()
                {
                    events.Add(name);
                    throw new InvalidOperationException(name);
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_DisposalExceptionBehavior"] = "ContinueAndAggregate",
            ["DisposableGenerator_GenerateDisposalHooks"] = "false",
            ["DisposableGenerator_ReportUnownedDisposableFields"] = "false",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var assembly = result.EmitAndLoad();
        var actual = (string)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("second,first,dynamic|3", actual);
    }

    [Fact]
    public void ContinueAndAggregate_flattens_failures_across_generated_inheritance()
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
                    var owner = new DerivedOwner(events);
                    try
                    {
                        owner.Dispose();
                        return "no-error";
                    }
                    catch (AggregateException exception)
                    {
                        return string.Join(",", events) + "|" + exception.InnerExceptions.Count;
                    }
                }
            }

            [GenerateDisposable]
            public partial class BaseOwner
            {
                [DisposeMember] private readonly IDisposable _baseResource;
                protected BaseOwner(List<string> events) => _baseResource = new FailingResource("base", events);
            }

            [GenerateDisposable]
            public sealed partial class DerivedOwner : BaseOwner
            {
                [DisposeMember] private readonly IDisposable _derivedResource;
                public DerivedOwner(List<string> events) : base(events) => _derivedResource = new FailingResource("derived", events);
            }

            public sealed class FailingResource(string name, List<string> events) : IDisposable
            {
                public void Dispose()
                {
                    events.Add(name);
                    throw new InvalidOperationException(name);
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_DisposalExceptionBehavior"] = "ContinueAndAggregate",
            ["DisposableGenerator_GenerateDisposalHooks"] = "false",
            ["DisposableGenerator_GenerateRegistrationMethod"] = "false",
            ["DisposableGenerator_ReportUnownedDisposableFields"] = "false",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var assembly = result.EmitAndLoad();
        var actual = (string)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.Equal("derived,base|2", actual);
    }

    [Theory]
    [InlineData("StopOnFirst", "ContinueAndAggregate", 0, false)]
    [InlineData("ContinueAndAggregate", "StopOnFirst", 1, true)]
    public void Compiled_generated_base_exception_policy_is_preserved_across_derived_compilation(
        string baseBehavior,
        string derivedBehavior,
        int encodedBehavior,
        bool expectsAggregateCleanup)
    {
        const string baseSource = """
            using DisposableGenerator;

            [GenerateDisposable]
            public partial class ExternalBase { }
            """;
        var baseResult = GeneratorTestHarness.Run(
            baseSource,
            new Dictionary<string, string>
            {
                ["DisposableGenerator_DisposalExceptionBehavior"] = baseBehavior,
            });
        var baseReference = baseResult.EmitToReference();

        const string derivedSource = """
            using DisposableGenerator;

            [GenerateDisposable]
            public sealed partial class Owner : ExternalBase { }
            """;
        var result = GeneratorTestHarness.Run(
            derivedSource,
            new Dictionary<string, string>
            {
                ["DisposableGenerator_DisposalExceptionBehavior"] = derivedBehavior,
            },
            additionalReferences: [baseReference]);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(
            "GeneratedDisposableAttribute(DisposalExceptionBehavior = " + encodedBehavior + ")",
            result.GeneratedSource,
            StringComparison.Ordinal);
        Assert.Equal(
            expectsAggregateCleanup,
            result.GeneratedSource.Contains(
                "List<global::System.Exception>? __exceptions",
                StringComparison.Ordinal));
    }
}
