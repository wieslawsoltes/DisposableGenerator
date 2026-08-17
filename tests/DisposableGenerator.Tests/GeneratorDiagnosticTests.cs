using Microsoft.CodeAnalysis;

namespace DisposableGenerator.Tests;

public sealed class GeneratorDiagnosticTests
{
    [Fact]
    public void Valid_type_generates_compilable_disposal_pattern()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                [DisposeMember]
                private System.IDisposable? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("partial class Owner : global::System.IDisposable", result.GeneratedSource);
        Assert.Contains("_resource)?.Dispose()", result.GeneratedSource);
        Assert.Contains("protected T RegisterDisposable<T>", result.GeneratedSource);
        Assert.Contains("global::System.GC.SuppressFinalize(this)", result.GeneratedSource);
    }

    [Fact]
    public void Unrelated_source_edit_keeps_per_type_generation_output_unchanged()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner { }
            """;

        var result = GeneratorTestHarness.RunAfterUnrelatedEdit(source);
        var step = Assert.Single(result.Results.Single().TrackedSteps["DisposableGenerationOutput"]);
        var output = Assert.Single(step.Outputs);

        Assert.Equal(IncrementalStepRunReason.Unchanged, output.Reason);
    }

    [Theory]
    [InlineData("[GenerateDisposable] class Owner { }", "DISP001")]
    [InlineData("[GenerateDisposable] public partial struct Owner { }", "DISP007")]
    [InlineData("[GenerateDisposable] public partial record Owner;", "DISP007")]
    [InlineData("[GenerateDisposable] public partial class Owner { public void Dispose() { } }", "DISP005")]
    public void Invalid_generated_type_reports_expected_diagnostic(string declaration, string diagnosticId)
    {
        var result = GeneratorTestHarness.Run("using DisposableGenerator; " + declaration);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void DisposeMember_without_GenerateDisposable_reports_DISP002()
    {
        const string source = """
            using DisposableGenerator;
            public class Owner
            {
                [DisposeMember]
                private System.IDisposable? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP002");
    }

    [Fact]
    public void Non_disposable_member_reports_DISP003()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                [DisposeMember]
                private string _resource = string.Empty;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP003");
    }

    [Fact]
    public void Disposable_base_not_generated_reports_DISP004()
    {
        const string source = """
            using DisposableGenerator;
            public class LegacyBase : System.IDisposable { public void Dispose() { } }
            [GenerateDisposable]
            public partial class Owner : LegacyBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP004");
    }

    [Fact]
    public void Unannotated_disposable_field_reports_DISP006()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                private System.IDisposable? _borrowed;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP006");
    }

    [Fact]
    public void Unannotated_disposable_property_reports_DISP006()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                public System.IDisposable? Service { get; init; }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP006");
    }

    [Fact]
    public void BorrowedMember_records_non_ownership_without_DISP006()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                [BorrowedMember]
                private System.IDisposable? _borrowed;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP006");
        Assert.DoesNotContain("this._borrowed", result.GeneratedSource);
    }

    [Fact]
    public void Conflicting_ownership_annotations_report_DISP010()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                [DisposeMember, BorrowedMember]
                private System.IDisposable? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP010");
        Assert.DoesNotContain("this._resource", result.GeneratedSource);
    }

    [Fact]
    public void Generated_class_nested_in_generic_record_is_supported()
    {
        const string source = """
            using DisposableGenerator;
            public partial record class Outer<T> where T : class
            {
                [GenerateDisposable]
                public sealed partial class Owner<TValue>(TValue value)
                {
                    public TValue Value { get; } = value;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("partial record class Outer<T>", result.GeneratedSource);
        Assert.Contains("partial class Owner<TValue>", result.GeneratedSource);
    }

    [Theory]
    [InlineData("public readonly partial record struct Outer<T>", "readonly partial record struct Outer<T>")]
    [InlineData("public ref partial struct Outer<T>", "ref partial struct Outer<T>")]
    [InlineData("public partial interface Outer<T>", "partial interface Outer<T>")]
    public void Modern_partial_containing_type_shapes_are_preserved(string containerDeclaration, string generatedDeclaration)
    {
        var source = $$"""
            using DisposableGenerator;
            {{containerDeclaration}}
            {
                [GenerateDisposable]
                public sealed partial class Owner(int value)
                {
                    public int Value { get; } = value;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(generatedDeclaration, result.GeneratedSource);
    }

    [Theory]
    [InlineData("out")]
    [InlineData("in")]
    public void Variant_containing_interface_is_reopened_with_matching_variance(string variance)
    {
        var source = $$"""
            using DisposableGenerator;
            public partial interface Outer<{{variance}} T>
            {
                [GenerateDisposable]
                public sealed partial class Owner { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains($"partial interface Outer<{variance} T>", result.GeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0264");
    }

    [Fact]
    public void CSharp14_field_backed_property_can_be_owned()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class Owner
            {
                [DisposeMember]
                public System.IDisposable Resource
                {
                    get;
                    set => field = value ?? throw new System.ArgumentNullException(nameof(value));
                } = new Resource();
            }

            public sealed class Resource : System.IDisposable
            {
                public void Dispose() { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("this.Resource", result.GeneratedSource);
    }

    [Fact]
    public void CSharp_partial_property_can_be_owned()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class Owner
            {
                private System.IDisposable? _resource;

                [DisposeMember]
                public partial System.IDisposable? Resource { get; set; }

                public partial System.IDisposable? Resource
                {
                    get => _resource;
                    set => _resource = value;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("this.Resource", result.GeneratedSource);
    }

    [Fact]
    public void Init_only_owned_property_does_not_report_mutability_warning()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class Owner
            {
                [DisposeMember]
                public System.IDisposable? Resource { get; init; }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP018");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void File_local_type_reports_DISP011_instead_of_emitting_invalid_partial_source()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            file partial class Owner { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP011");
        Assert.DoesNotContain("partial class Owner :", result.GeneratedSource);
    }

    [Fact]
    public void Existing_non_disposable_framework_base_is_preserved()
    {
        const string source = """
            using DisposableGenerator;
            public class FrameworkBase { }
            [GenerateDisposable]
            public partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("partial class Owner : global::System.IDisposable", result.GeneratedSource);
    }

    [Fact]
    public void Generated_disposable_type_can_be_an_owned_member_in_the_same_compilation()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class Child { }

            [GenerateDisposable]
            public sealed partial class Owner
            {
                [DisposeMember]
                private readonly Child _child = new();
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("this._child", result.GeneratedSource);
    }

    [Fact]
    public void Generated_inheritance_chains_across_compilation_boundaries()
    {
        const string baseSource = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class ExternalBase { }
            """;
        var baseResult = GeneratorTestHarness.Run(baseSource);
        var baseReference = baseResult.EmitToReference();

        const string derivedSource = """
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class Owner : ExternalBase { }
            """;
        var result = GeneratorTestHarness.Run(derivedSource, additionalReferences: [baseReference]);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("protected override void Dispose(bool disposing)", result.GeneratedSource);
    }

    [Fact]
    public void Valid_generated_base_is_recognized_regardless_of_declaration_order()
    {
        const string source = """
            using DisposableGenerator;

            [GenerateDisposable]
            public sealed partial class Owner : BaseOwner { }

            [GenerateDisposable]
            public partial class BaseOwner { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("protected override void Dispose(bool disposing)", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public class InvalidBase { }", "DISP001")]
    [InlineData("public partial class InvalidBase { public void Dispose() { } }", "DISP005")]
    [InlineData("public partial class InvalidBase { private int __DisposableGenerator_disposeState; }", "DISP012")]
    public void Invalid_attributed_base_prevents_derived_override_emission(string baseDeclaration, string diagnosticId)
    {
        var source = $$"""
            using DisposableGenerator;

            [GenerateDisposable]
            public sealed partial class Owner : InvalidBase { }

            [GenerateDisposable]
            {{baseDeclaration}}
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == diagnosticId);
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0115");
        Assert.DoesNotContain("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_generated_member_type_is_not_classified_as_disposable()
    {
        const string source = """
            using DisposableGenerator;

            [GenerateDisposable]
            public sealed class Bad { }

            [GenerateDisposable]
            public sealed partial class Owner
            {
                [DisposeMember] private readonly Bad _bad = new();
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP001");
        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP003");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0030");
        Assert.Contains("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("this._bad", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Nullable_disposable_value_type_can_be_owned()
    {
        const string source = """
            using DisposableGenerator;
            public struct Resource : System.IDisposable
            {
                public void Dispose() { }
            }

            [GenerateDisposable]
            public sealed partial class Owner
            {
                [DisposeMember]
                private readonly Resource? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP003");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Nullable_async_disposable_value_type_can_be_owned()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using DisposableGenerator;

            public struct Resource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => default;
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner
            {
                [DisposeMember]
                private readonly Resource? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP003");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("this._resource", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_generic_parameter_does_not_shadow_containing_type_parameter()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner<T> { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0693");
        Assert.Contains("RegisterDisposable<TDisposable>", result.GeneratedSource);
    }

    [Fact]
    public void Generated_public_and_protected_API_is_documented_for_CS1591()
    {
        const string source = """
            using DisposableGenerator;
            /// <summary>An owner used to verify generated XML documentation.</summary>
            [GenerateDisposable(GenerateAsyncDispose = true, GenerateFinalizer = true)]
            public partial class Owner { }
            """;

        var result = GeneratorTestHarness.Run(source, requirePublicDocumentation: true);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS1591");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Escaped_namespace_identifiers_are_preserved()
    {
        const string source = """
            using DisposableGenerator;
            namespace @class
            {
                [GenerateDisposable]
                public sealed partial class Owner { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("namespace @class", result.GeneratedSource);
    }

    [Fact]
    public void Sanitized_hint_name_collisions_are_prevented_by_stable_hashes()
    {
        const string source = """
            using DisposableGenerator;
            namespace A_B
            {
                [GenerateDisposable] public sealed partial class C { }
            }

            namespace A
            {
                [GenerateDisposable] public sealed partial class B_C { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Null(result.GeneratorResult.Exception);
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(3, result.GeneratorResult.GeneratedSources.Length);
        Assert.Equal(
            result.GeneratorResult.GeneratedSources.Length,
            result.GeneratorResult.GeneratedSources.Select(sourceResult => sourceResult.HintName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Generated_hint_names_are_bounded_for_very_long_type_names()
    {
        var longName = "Owner" + new string('A', 300);
        var source = "using DisposableGenerator; [GenerateDisposable] public sealed partial class " + longName + " { }";

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.All(result.GeneratorResult.GeneratedSources, generated => Assert.True(generated.HintName.Length <= 150));
    }

    [Fact]
    public void Static_owned_member_reports_DISP008()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                [DisposeMember]
                private static System.IDisposable? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP008");
    }

    [Fact]
    public void Field_targeted_owned_attribute_on_auto_property_reports_DISP008_without_emitting_backing_field_access()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class Owner
            {
                [field: DisposeMember]
                public System.IDisposable Resource { get; } = null!;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP008");
        Assert.DoesNotContain("k__BackingField", result.GeneratedSource, StringComparison.Ordinal);
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS1001");
    }

    [Fact]
    public void Public_dispose_entry_points_suppress_finalization_in_finally_blocks()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public sealed partial class SyncOwner { }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class AsyncOwner { }
            """;

        var result = GeneratorTestHarness.Run(source, new Dictionary<string, string>
        {
            ["DisposableGenerator_GenerateRegistrationMethod"] = "false",
        });

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var syncStart = result.GeneratedSource.IndexOf("public void Dispose()", StringComparison.Ordinal);
        var syncEnd = result.GeneratedSource.IndexOf("public async", syncStart, StringComparison.Ordinal);
        var syncDispose = result.GeneratedSource.Substring(syncStart, syncEnd - syncStart);
        Assert.True(syncDispose.IndexOf("try", StringComparison.Ordinal) < syncDispose.IndexOf("Dispose(true);", StringComparison.Ordinal));
        Assert.True(syncDispose.IndexOf("Dispose(true);", StringComparison.Ordinal) < syncDispose.IndexOf("finally", StringComparison.Ordinal));
        Assert.True(syncDispose.IndexOf("finally", StringComparison.Ordinal) < syncDispose.IndexOf("GC.SuppressFinalize(this);", StringComparison.Ordinal));

        var asyncStart = syncEnd;
        var asyncEnd = result.GeneratedSource.IndexOf("ValueTask DisposeAsyncCore()", asyncStart, StringComparison.Ordinal);
        var asyncDispose = result.GeneratedSource.Substring(asyncStart, asyncEnd - asyncStart);
        Assert.True(asyncDispose.IndexOf("try", StringComparison.Ordinal) < asyncDispose.IndexOf("DisposeAsyncCore()", StringComparison.Ordinal));
        var asyncFinally = asyncDispose.LastIndexOf("finally", StringComparison.Ordinal);
        Assert.True(asyncFinally >= 0);
        Assert.True(asyncFinally < asyncDispose.IndexOf("GC.SuppressFinalize(this);", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("private int __DisposableGenerator_disposeState;", "DISP012")]
    [InlineData("private int __DisposableGenerator_disposalStarted;", "DISP012")]
    [InlineData("protected T RegisterDisposable<T>(T value) where T : System.IDisposable => value;", "DISP012")]
    [InlineData("private void OnDisposing() { }", "DISP015")]
    public void Generated_infrastructure_collisions_report_actionable_diagnostics(string member, string diagnosticId)
    {
        var source = $$"""
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                {{member}}
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void Non_method_Dispose_member_reports_DISP012()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                private int Dispose;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP012");
    }

    [Fact]
    public void Registration_method_overload_with_a_different_signature_is_allowed()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                protected void RegisterDisposable<T>(int value) { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP012");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("public void Dispose<T>() { }")]
    [InlineData("public void Dispose(ref bool disposing) { }")]
    public void Non_conflicting_Dispose_overload_is_allowed(string method)
    {
        var source = $$"""
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                {{method}}
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP005");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void BorrowedMember_outside_generated_type_reports_DISP013()
    {
        const string source = """
            using DisposableGenerator;
            public class Owner
            {
                [BorrowedMember]
                private System.IDisposable? _resource;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP013");
    }

    [Fact]
    public void BorrowedMember_on_non_disposable_member_reports_DISP014()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                [BorrowedMember]
                private string _resource = string.Empty;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP014");
    }

    [Fact]
    public void Accessible_non_generated_base_Dispose_member_reports_DISP016()
    {
        const string source = """
            using DisposableGenerator;
            public class FrameworkBase
            {
                protected virtual void Dispose(bool disposing) { }
            }

            [GenerateDisposable]
            public partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
    }

    [Fact]
    public void Accessible_non_generated_base_Dispose_method_reports_DISP016()
    {
        const string source = """
            using DisposableGenerator;
            public class FrameworkBase
            {
                public void Dispose() { }
            }

            [GenerateDisposable]
            public partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
    }

    [Fact]
    public void Intervening_base_Dispose_method_reports_DISP016_in_generated_hierarchy()
    {
        const string source = """
            using System;
            using DisposableGenerator;

            [GenerateDisposable]
            public partial class GeneratedBase { }

            public class InterveningBase : GeneratedBase, IDisposable
            {
                public void Dispose() { }
            }

            [GenerateDisposable]
            public sealed partial class Owner : InterveningBase
            {
                [DisposeMember] private readonly IDisposable _resource = null!;
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_intervening_base_Dispose_method_reports_DISP016_in_generated_hierarchy()
    {
        const string source = """
            using System;
            using DisposableGenerator;

            [GenerateDisposable]
            public partial class GeneratedBase { }

            public class InterveningBase : GeneratedBase, IDisposable
            {
                void IDisposable.Dispose() { }
            }

            [GenerateDisposable]
            public sealed partial class Owner : InterveningBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Accessible_non_generated_base_DisposeAsync_method_reports_DISP016()
    {
        const string source = """
            using System.Threading.Tasks;
            using DisposableGenerator;

            public class FrameworkBase
            {
                public ValueTask DisposeAsync() => default;
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0108");
        Assert.DoesNotContain("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Accessible_static_non_generated_base_Dispose_method_reports_DISP016()
    {
        const string source = """
            using DisposableGenerator;

            public class FrameworkBase
            {
                public static void Dispose() { }
            }

            [GenerateDisposable]
            public sealed partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0108");
        Assert.DoesNotContain("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Accessible_static_non_generated_base_DisposeAsync_method_reports_DISP016()
    {
        const string source = """
            using System.Threading.Tasks;
            using DisposableGenerator;

            public class FrameworkBase
            {
                public static ValueTask DisposeAsync() => default;
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "CS0108");
        Assert.DoesNotContain("partial class Owner", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_unrelated_interface_DisposeAsync_on_owner_does_not_report_DISP005()
    {
        const string source = """
            using DisposableGenerator;

            public interface IFoo
            {
                void DisposeAsync();
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner : IFoo
            {
                void IFoo.DisposeAsync() { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP005");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("public async global::System.Threading.Tasks.ValueTask DisposeAsync()", result.GeneratedSource);
    }

    [Fact]
    public void Explicit_unrelated_interface_DisposeAsync_on_base_does_not_report_DISP016()
    {
        const string source = """
            using DisposableGenerator;

            public interface IFoo
            {
                void DisposeAsync();
            }

            public class FrameworkBase : IFoo
            {
                void IFoo.DisposeAsync() { }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public sealed partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains("public async global::System.Threading.Tasks.ValueTask DisposeAsync()", result.GeneratedSource);
    }

    [Fact]
    public void Generic_base_Dispose_overload_does_not_report_DISP016()
    {
        const string source = """
            using DisposableGenerator;
            public class FrameworkBase
            {
                protected void Dispose<T>() { }
            }

            [GenerateDisposable]
            public partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Generic_base_DisposeAsync_overload_does_not_report_DISP016()
    {
        const string source = """
            using DisposableGenerator;
            public class FrameworkBase
            {
                protected void DisposeAsync<T>() { }
            }

            [GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
            public partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP016");
        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("protected int __DisposableGenerator_disposeState;")]
    [InlineData("protected T RegisterDisposable<T>(T value) where T : System.IDisposable => value;")]
    public void Inherited_generated_infrastructure_collision_reports_DISP012(string member)
    {
        var source = $$"""
            using DisposableGenerator;
            public class FrameworkBase
            {
                {{member}}
            }

            [GenerateDisposable]
            public partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP012");
    }

    [Fact]
    public void Inherited_disposal_hook_collision_reports_DISP015()
    {
        const string source = """
            using DisposableGenerator;
            public class FrameworkBase
            {
                protected void OnDisposing() { }
            }

            [GenerateDisposable]
            public partial class Owner : FrameworkBase { }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP015");
    }

    [Fact]
    public void Finalizer_reports_DISP017()
    {
        const string source = """
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                ~Owner() { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP017");
    }

    [Theory]
    [InlineData("[DisposeMember] private System.IDisposable? _resource;")]
    [InlineData("[DisposeMember] public System.IDisposable? Resource { get; set; }")]
    public void Mutable_owned_member_reports_DISP018(string member)
    {
        var source = $$"""
            using DisposableGenerator;
            [GenerateDisposable]
            public partial class Owner
            {
                {{member}}
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP018");
    }

    [Fact]
    public void Explicit_interface_property_reports_DISP008()
    {
        const string source = """
            using DisposableGenerator;
            public interface IResourceOwner
            {
                System.IDisposable Resource { get; }
            }

            [GenerateDisposable]
            public partial class Owner : IResourceOwner
            {
                [DisposeMember]
                System.IDisposable IResourceOwner.Resource => new Resource();
            }

            public sealed class Resource : System.IDisposable
            {
                public void Dispose() { }
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Id == "DISP008");
    }
}
