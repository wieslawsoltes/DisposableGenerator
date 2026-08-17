using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DisposableGenerator.Tests;

internal static class GeneratorTestHarness
{
    internal static GeneratorRunResult Run(
        string source,
        IReadOnlyDictionary<string, string>? properties = null,
        bool requirePublicDocumentation = false,
        IEnumerable<MetadataReference>? additionalReferences = null,
        IEnumerable<MetadataReference>? platformReferences = null,
        LanguageVersion languageVersion = LanguageVersion.Preview)
    {
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(languageVersion)
            .WithDocumentationMode(requirePublicDocumentation ? DocumentationMode.Diagnose : DocumentationMode.Parse);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "Test0.cs");
        var specificDiagnostics = requirePublicDocumentation
            ? ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("CS1591", ReportDiagnostic.Error)
            : null;
        var references = platformReferences?.ToImmutableArray() ?? PlatformReferences;
        if (additionalReferences is not null)
        {
            references = references.AddRange(additionalReferences);
        }

        var compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                specificDiagnosticOptions: specificDiagnostics));

        var optionsProvider = new TestAnalyzerConfigOptionsProvider(properties);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DisposablePatternGenerator().AsSourceGenerator()],
            additionalTexts: null,
            parseOptions,
            optionsProvider);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var driverDiagnostics);
        var result = driver.GetRunResult();
        return new GeneratorRunResult(
            result.Results.Single(),
            outputCompilation,
            driverDiagnostics);
    }

    internal static GeneratorDriverRunResult RunAfterUnrelatedEdit(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var sourceTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "Owner.cs");
        var unrelatedTree = CSharpSyntaxTree.ParseText(
            "public sealed class Unrelated { }",
            parseOptions,
            path: "Unrelated.cs");
        var compilation = CSharpCompilation.Create(
            "IncrementalGeneratorTests",
            [sourceTree, unrelatedTree],
            PlatformReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var optionsProvider = new TestAnalyzerConfigOptionsProvider(properties: null);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DisposablePatternGenerator().AsSourceGenerator()],
            additionalTexts: null,
            parseOptions,
            optionsProvider,
            new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var updatedUnrelatedTree = CSharpSyntaxTree.ParseText(
            "public sealed class Unrelated { public int Value { get; } }",
            parseOptions,
            path: "Unrelated.cs");
        var updatedCompilation = compilation.ReplaceSyntaxTree(unrelatedTree, updatedUnrelatedTree);
        driver = driver.RunGenerators(updatedCompilation);
        return driver.GetRunResult();
    }

    private static ImmutableArray<MetadataReference> PlatformReferences { get; } =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToImmutableArray<MetadataReference>();

    private sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _globalOptions;

        internal TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string>? properties)
        {
            var values = properties?.ToDictionary(
                item => "build_property." + item.Key,
                item => item.Value,
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
            _globalOptions = new TestAnalyzerConfigOptions(values);
        }

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestAnalyzerConfigOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestAnalyzerConfigOptions.Empty;
    }

    private sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
    {
        internal static TestAnalyzerConfigOptions Empty { get; } = new(new Dictionary<string, string>());

        private readonly IReadOnlyDictionary<string, string> _values;

        internal TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }
}

internal sealed record GeneratorRunResult(
    Microsoft.CodeAnalysis.GeneratorRunResult GeneratorResult,
    Compilation OutputCompilation,
    ImmutableArray<Diagnostic> DriverDiagnostics)
{
    internal ImmutableArray<Diagnostic> AllDiagnostics =>
        DriverDiagnostics
            .AddRange(OutputCompilation.GetDiagnostics());

    internal string GeneratedSource => string.Join(
        "\n",
        GeneratorResult.GeneratedSources.Select(source => source.SourceText.ToString()));

    internal Assembly EmitAndLoad()
    {
        using var assemblyStream = new MemoryStream();
        var emitResult = OutputCompilation.Emit(assemblyStream);
        Assert.True(
            emitResult.Success,
            "Dynamic compilation failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, emitResult.Diagnostics));
        return Assembly.Load(assemblyStream.ToArray());
    }

    internal MetadataReference EmitToReference()
    {
        using var assemblyStream = new MemoryStream();
        var emitResult = OutputCompilation.Emit(assemblyStream);
        Assert.True(
            emitResult.Success,
            "Reference compilation failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, emitResult.Diagnostics));
        return MetadataReference.CreateFromImage(assemblyStream.ToArray());
    }
}
