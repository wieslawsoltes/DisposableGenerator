using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace DisposableGenerator;

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
