using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace DisposableGenerator;

internal sealed class DisposableGenerationOutput : System.IEquatable<DisposableGenerationOutput>
{
    internal DisposableGenerationOutput(
        string? hintName,
        string? source,
        ImmutableArray<Diagnostic> diagnostics)
    {
        HintName = hintName;
        Source = source;
        Diagnostics = diagnostics;
    }

    internal string? HintName { get; }

    internal string? Source { get; }

    internal ImmutableArray<Diagnostic> Diagnostics { get; }

    public bool Equals(DisposableGenerationOutput? other)
    {
        if (other is null ||
            !string.Equals(HintName, other.HintName, System.StringComparison.Ordinal) ||
            !string.Equals(Source, other.Source, System.StringComparison.Ordinal) ||
            Diagnostics.Length != other.Diagnostics.Length)
        {
            return false;
        }

        for (var index = 0; index < Diagnostics.Length; index++)
        {
            if (!DiagnosticEquals(Diagnostics[index], other.Diagnostics[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DisposableGenerationOutput);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = HintName is null ? 0 : System.StringComparer.Ordinal.GetHashCode(HintName);
            hash = (hash * 397) ^ (Source is null ? 0 : System.StringComparer.Ordinal.GetHashCode(Source));
            foreach (var diagnostic in Diagnostics)
            {
                hash = (hash * 397) ^ System.StringComparer.Ordinal.GetHashCode(diagnostic.Id);
                hash = (hash * 397) ^ (int)diagnostic.Severity;
                hash = (hash * 397) ^ System.StringComparer.Ordinal.GetHashCode(diagnostic.GetMessage());
                hash = (hash * 397) ^ diagnostic.Location.SourceSpan.GetHashCode();
                hash = (hash * 397) ^ System.StringComparer.Ordinal.GetHashCode(diagnostic.Location.GetLineSpan().Path ?? string.Empty);
            }

            return hash;
        }
    }

    private static bool DiagnosticEquals(Diagnostic left, Diagnostic right) =>
        left.Id == right.Id &&
        left.Severity == right.Severity &&
        left.GetMessage() == right.GetMessage() &&
        left.Location.SourceSpan.Equals(right.Location.SourceSpan) &&
        string.Equals(
            left.Location.GetLineSpan().Path,
            right.Location.GetLineSpan().Path,
            System.StringComparison.Ordinal);
}

internal sealed class DisposableTypeModel
{
    internal DisposableTypeModel(
        INamedTypeSymbol type,
        bool hasGeneratedBase,
        IReadOnlyList<OwnedMemberModel> members,
        bool generateSynchronousDispose,
        bool generateAsyncDispose,
        bool generateUnmanagedCleanup,
        bool generateFinalizer,
        GeneratorOptions options)
    {
        Type = type;
        HasGeneratedBase = hasGeneratedBase;
        Members = members;
        GenerateSynchronousDispose = generateSynchronousDispose;
        GenerateAsyncDispose = generateAsyncDispose;
        GenerateUnmanagedCleanup = generateUnmanagedCleanup;
        GenerateFinalizer = generateFinalizer;
        Options = options;
    }

    internal INamedTypeSymbol Type { get; }

    internal bool HasGeneratedBase { get; }

    internal IReadOnlyList<OwnedMemberModel> Members { get; }

    internal bool GenerateSynchronousDispose { get; }

    internal bool GenerateAsyncDispose { get; }

    internal bool GenerateUnmanagedCleanup { get; }

    internal bool GenerateFinalizer { get; }

    internal GeneratorOptions Options { get; }
}

internal sealed class OwnedMemberModel
{
    internal OwnedMemberModel(ISymbol symbol, int order, bool supportsSynchronousDispose, bool supportsAsynchronousDispose)
    {
        Symbol = symbol;
        Order = order;
        SupportsSynchronousDispose = supportsSynchronousDispose;
        SupportsAsynchronousDispose = supportsAsynchronousDispose;
    }

    internal ISymbol Symbol { get; }

    internal int Order { get; }

    internal bool SupportsSynchronousDispose { get; }

    internal bool SupportsAsynchronousDispose { get; }
}
