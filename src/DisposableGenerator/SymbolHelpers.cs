using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DisposableGenerator;

internal static class SymbolHelpers
{
    internal const string GenerateDisposableAttributeName = "DisposableGenerator.GenerateDisposableAttribute";
    internal const string GeneratedDisposableAttributeName = "DisposableGenerator.GeneratedDisposableAttribute";
    internal const string DisposeMemberAttributeName = "DisposableGenerator.DisposeMemberAttribute";
    internal const string BorrowedMemberAttributeName = "DisposableGenerator.BorrowedMemberAttribute";

    internal static bool HasAttribute(this ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    internal static AttributeData? GetAttribute(this ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    internal static bool IsPartial(this INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Length > 0 &&
        type.DeclaringSyntaxReferences.All(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

    internal static bool IsFileLocal(this INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.FileKeyword));

    internal static bool RequiresConstrainedDisposalDispatch(this ITypeSymbol type)
    {
        if (type is ITypeParameterSymbol)
        {
            return true;
        }

        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return false;
        }

        return type.IsValueType;
    }

    internal static bool IsNullableValueType(this ITypeSymbol type) =>
        type is INamedTypeSymbol namedType &&
        namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    internal static bool AllowsRefLikeDisposalDispatch(this ITypeSymbol type)
    {
        if (type.IsRefLikeType)
        {
            return true;
        }

        if (type is not ITypeParameterSymbol typeParameter ||
            typeParameter.ContainingSymbol is not INamedTypeSymbol containingType)
        {
            return false;
        }

        return containingType.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration &&
            declaration.ConstraintClauses.Any(clause =>
                clause.Name.Identifier.ValueText == typeParameter.Name &&
                clause.Constraints.Any(constraint =>
                    constraint.DescendantTokens().Select(token => token.ValueText).SequenceEqual(
                        new[] { "allows", "ref", "struct" }))));
    }

    internal static bool IsDisposable(
        this ITypeSymbol type,
        Func<INamedTypeSymbol, bool>? generationAvailable = null)
    {
        if (type.SpecialType == SpecialType.System_IDisposable)
        {
            return true;
        }

        if (type is ITypeParameterSymbol typeParameter &&
            typeParameter.ConstraintTypes.Any(constraint => constraint.IsDisposable(generationAvailable)))
        {
            return true;
        }

        if (type is INamedTypeSymbol namedType)
        {
            if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                namedType.TypeArguments.Length == 1 &&
                namedType.TypeArguments[0].IsDisposable(generationAvailable))
            {
                return true;
            }

            for (var current = namedType; current is not null; current = current.BaseType)
            {
                var generationAttribute = current.GetAttribute(GenerateDisposableAttributeName);
                if (generationAvailable?.Invoke(current.OriginalDefinition) == true &&
                    generationAttribute is not null &&
                    generationAttribute.GetNamedBoolean("GenerateSynchronousDispose", defaultValue: true))
                {
                    return true;
                }
            }
        }

        return type.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_IDisposable);
    }

    internal static bool IsAsyncDisposable(
        this ITypeSymbol type,
        Func<INamedTypeSymbol, bool>? generationAvailable = null)
    {
        if (IsAsyncDisposableInterface(type))
        {
            return true;
        }

        if (type is ITypeParameterSymbol typeParameter &&
            typeParameter.ConstraintTypes.Any(constraint => constraint.IsAsyncDisposable(generationAvailable)))
        {
            return true;
        }

        if (type is INamedTypeSymbol namedType)
        {
            if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                namedType.TypeArguments.Length == 1 &&
                namedType.TypeArguments[0].IsAsyncDisposable(generationAvailable))
            {
                return true;
            }

            for (var current = namedType; current is not null; current = current.BaseType)
            {
                var attribute = current.GetAttribute(GenerateDisposableAttributeName);
                if (generationAvailable?.Invoke(current.OriginalDefinition) == true &&
                    attribute is not null &&
                    attribute.GetNamedBoolean("GenerateAsyncDispose"))
                {
                    return true;
                }
            }
        }

        return type.AllInterfaces.Any(IsAsyncDisposableInterface);
    }

    internal static INamedTypeSymbol? FindTypeByMetadataName(INamedTypeSymbol context, string metadataName)
    {
        var declared = context.ContainingAssembly.GetTypeByMetadataName(metadataName);
        if (declared is not null)
        {
            return declared;
        }

        foreach (var assembly in context.ContainingModule.ReferencedAssemblySymbols)
        {
            var referenced = assembly.GetTypeByMetadataName(metadataName);
            if (referenced is not null)
            {
                return referenced;
            }
        }

        return null;
    }

    private static bool IsAsyncDisposableInterface(ITypeSymbol type) =>
        type is INamedTypeSymbol namedType &&
        namedType.MetadataName == "IAsyncDisposable" &&
        namedType.ContainingNamespace.ToDisplayString() == "System";

    internal static bool GetNamedBoolean(this AttributeData attribute, string name, bool defaultValue = false)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is bool value)
            {
                return value;
            }
        }

        return defaultValue;
    }

    internal static int GetNamedInt(this AttributeData attribute, string name, int defaultValue)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is int value)
            {
                return value;
            }
        }

        return defaultValue;
    }

    internal static Location BestLocation(this ISymbol symbol) =>
        symbol.Locations.FirstOrDefault(location => location.IsInSource) ?? Location.None;

    internal static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;

    internal static string TypeDeclarationName(INamedTypeSymbol type)
    {
        var typeName = EscapeIdentifier(type.Name);
        if (type.TypeParameters.Length == 0)
        {
            return typeName;
        }

        return typeName + "<" + string.Join(", ", type.TypeParameters.Select(TypeParameterDeclarationName)) + ">";
    }

    private static string TypeParameterDeclarationName(ITypeParameterSymbol parameter)
    {
        var variance = parameter.Variance switch
        {
            VarianceKind.In => "in ",
            VarianceKind.Out => "out ",
            _ => string.Empty,
        };
        return variance + EscapeIdentifier(parameter.Name);
    }

    internal static string NamespaceName(INamespaceSymbol namespaceSymbol)
    {
        var segments = new Stack<string>();
        for (var current = namespaceSymbol; current is not null && !current.IsGlobalNamespace; current = current.ContainingNamespace)
        {
            segments.Push(EscapeIdentifier(current.Name));
        }

        return string.Join(".", segments);
    }

    internal static string RegistrationTypeParameterName(INamedTypeSymbol type)
    {
        var names = ContainingTypeParameterNames(type);
        var candidate = "T";
        var suffix = 0;
        while (names.Contains(candidate))
        {
            suffix++;
            candidate = suffix == 1 ? "TDisposable" : "TDisposable" + suffix;
        }

        return candidate;
    }

    internal static string ConstrainedHelperTypeParameterName(INamedTypeSymbol type)
    {
        var names = ContainingTypeParameterNames(type);
        var candidate = "TDisposable";
        var suffix = 1;
        while (names.Contains(candidate))
        {
            suffix++;
            candidate = "TDisposable" + suffix;
        }

        return candidate;
    }

    private static HashSet<string> ContainingTypeParameterNames(INamedTypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.ContainingType)
        {
            foreach (var parameter in current.TypeParameters)
            {
                names.Add(parameter.Name);
            }
        }

        return names;
    }

    internal static string DeclarationKeyword(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface)
        {
            return "interface";
        }

        if (type.TypeKind == TypeKind.Struct)
        {
            return type.IsRecord ? "record struct" : "struct";
        }

        if (type.IsRecord)
        {
            return "record class";
        }

        return "class";
    }

    internal static string PartialDeclarationPrefix(INamedTypeSymbol type)
    {
        var modifiers = new List<string>();
        if (type.IsStatic)
        {
            modifiers.Add("static");
        }

        if (type.TypeKind == TypeKind.Struct && type.IsReadOnly)
        {
            modifiers.Add("readonly");
        }

        if (type.TypeKind == TypeKind.Struct && type.IsRefLikeType)
        {
            modifiers.Add("ref");
        }

        modifiers.Add("partial");
        modifiers.Add(DeclarationKeyword(type));
        return string.Join(" ", modifiers);
    }

    internal static IEnumerable<INamedTypeSymbol> ContainingTypesOuterFirst(INamedTypeSymbol type)
    {
        var containingTypes = new Stack<INamedTypeSymbol>();
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            containingTypes.Push(current);
        }

        return containingTypes;
    }

    internal static int DeclarationOrder(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(item => item.IsInSource);
        return location?.SourceSpan.Start ?? int.MaxValue;
    }

    internal static string DeclarationPath(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault(item => item.IsInSource)?.SourceTree?.FilePath ?? string.Empty;
}
