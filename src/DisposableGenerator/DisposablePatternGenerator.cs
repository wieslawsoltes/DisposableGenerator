using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DisposableGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class DisposablePatternGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output =>
            output.AddSource("DisposableGenerator.Attributes.g.cs", SourceText.From(AttributeSource.Text, Encoding.UTF8)));

        var options = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => GeneratorOptions.From(provider.GlobalOptions));

        context.RegisterSourceOutput(options, static (output, generatorOptions) =>
            ReportConfigurationErrors(output, generatorOptions));

        var generatedTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            SymbolHelpers.GenerateDisposableAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);

        var ownedMembers = context.SyntaxProvider.ForAttributeWithMetadataName(
            SymbolHelpers.DisposeMemberAttributeName,
            static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax,
            static (attributeContext, _) => attributeContext.TargetSymbol);

        var borrowedMembers = context.SyntaxProvider.ForAttributeWithMetadataName(
            SymbolHelpers.BorrowedMemberAttributeName,
            static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax,
            static (attributeContext, _) => attributeContext.TargetSymbol);

        context.RegisterSourceOutput(
            generatedTypes.Collect()
                .Combine(ownedMembers.Collect())
                .Combine(borrowedMembers.Collect())
                .Combine(options)
                .Combine(context.CompilationProvider),
            static (output, item) => GenerateTypes(
                output,
                item.Left.Left.Left.Left,
                item.Left.Left.Left.Right,
                item.Left.Left.Right,
                item.Left.Right,
                item.Right));
    }

    private static void ReportConfigurationErrors(SourceProductionContext context, GeneratorOptions options)
    {
        foreach (var error in options.Errors)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidConfiguration,
                Location.None,
                error.PropertyName,
                error.Value,
                error.Fallback));
        }
    }

    private static void GenerateTypes(
        SourceProductionContext context,
        ImmutableArray<INamedTypeSymbol> types,
        ImmutableArray<ISymbol> ownedMembers,
        ImmutableArray<ISymbol> borrowedMembers,
        GeneratorOptions options,
        Compilation compilation)
    {
        var candidateSet = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var distinctCandidates = new List<INamedTypeSymbol>();
        foreach (var type in types)
        {
            var definition = type.OriginalDefinition;
            if (candidateSet.Add(definition))
            {
                distinctCandidates.Add(definition);
            }
        }

        var candidates = distinctCandidates
            .OrderBy(InheritanceDepth)
            .ToArray();
        var generatedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var models = new List<DisposableTypeModel>();

        foreach (var type in candidates)
        {
            if (TryCreateTypeModel(
                    context,
                    type,
                    options,
                    compilation,
                    candidateSet,
                    generatedTypes,
                    out var model))
            {
                generatedTypes.Add(type);
                models.Add(model!);
            }
        }

        foreach (var member in ownedMembers)
        {
            ValidateOwnedMember(context, member, compilation, generatedTypes);
        }

        foreach (var member in borrowedMembers)
        {
            ValidateBorrowedMember(context, member, compilation, generatedTypes);
        }

        var disposableInterface = compilation.GetSpecialType(SpecialType.System_IDisposable);
        var asyncDisposableInterface = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        foreach (var model in models)
        {
            var members = GetValidOwnedMembers(
                model.Type,
                disposableInterface,
                asyncDisposableInterface,
                model.GenerateAsyncDispose,
                generatedTypes);
            members.Sort((left, right) => CompareOwnedMembers(left, right, options.MemberDisposalOrder));

            ReportImplicitOwnedBackingFields(context, model.Type);
            if (options.ReportUnownedDisposableFields)
            {
                ReportUnownedMembers(
                    context,
                    model.Type,
                    disposableInterface,
                    asyncDisposableInterface,
                    generatedTypes);
            }

            var completedModel = new DisposableTypeModel(
                model.Type,
                model.HasGeneratedBase,
                members,
                model.GenerateSynchronousDispose,
                model.GenerateAsyncDispose,
                model.GenerateUnmanagedCleanup,
                model.GenerateFinalizer,
                model.Options);
            context.AddSource(HintName(model.Type), SourceText.From(SourceEmitter.Emit(completedModel), Encoding.UTF8));
        }
    }

    private static int InheritanceDepth(INamedTypeSymbol type)
    {
        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static bool TryCreateTypeModel(
        SourceProductionContext context,
        INamedTypeSymbol type,
        GeneratorOptions options,
        Compilation compilation,
        HashSet<INamedTypeSymbol> candidates,
        HashSet<INamedTypeSymbol> generatedTypes,
        out DisposableTypeModel? model)
    {
        model = null;
        if (type.TypeKind != TypeKind.Class || type.IsStatic || type.IsRecord)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedType,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        var generationAttribute = type.GetAttribute(SymbolHelpers.GenerateDisposableAttributeName)!;
        var generateSynchronousDispose = generationAttribute.GetNamedBoolean("GenerateSynchronousDispose", defaultValue: true);
        var generateAsyncDispose = generationAttribute.GetNamedBoolean("GenerateAsyncDispose");
        var generateFinalizer = generationAttribute.GetNamedBoolean("GenerateFinalizer");
        var generateUnmanagedCleanup = generateFinalizer || generationAttribute.GetNamedBoolean("GenerateUnmanagedCleanup");
        if ((!generateSynchronousDispose && !generateAsyncDispose) ||
            (generateFinalizer && !generateSynchronousDispose))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidGenerationMode,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        var asyncDisposableInterface = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        if (generateAsyncDispose && asyncDisposableInterface is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AsyncDisposeUnavailable,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (!type.IsPartial() || SymbolHelpers.ContainingTypesOuterFirst(type).Any(containing => !containing.IsPartial()))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.TypeMustBePartial,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (type.IsFileLocal() || SymbolHelpers.ContainingTypesOuterFirst(type).Any(SymbolHelpers.IsFileLocal))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.FileLocalTypeUnsupported,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (DeclaresDisposalMethod(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.ManualDisposeImplementation,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (type.GetMembers().OfType<IMethodSymbol>().Any(method => method.MethodKind == MethodKind.Destructor))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.FinalizerUnsupported,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        var generatedBase = FindGeneratedBase(type);
        var hasGeneratedBase = generatedBase is not null;
        if (generatedBase is not null)
        {
            if (!IsAvailableGeneratedBase(generatedBase, candidates, generatedTypes))
            {
                return false;
            }

            var baseAttribute = generatedBase.GetAttribute(SymbolHelpers.GenerateDisposableAttributeName)!;
            if (generateSynchronousDispose != baseAttribute.GetNamedBoolean("GenerateSynchronousDispose", defaultValue: true) ||
                generateAsyncDispose != baseAttribute.GetNamedBoolean("GenerateAsyncDispose"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AsyncGenerationMismatch,
                    type.BestLocation(),
                    type.ToDisplayString()));
                return false;
            }
        }

        var disposableInterface = compilation.GetSpecialType(SpecialType.System_IDisposable);
        if (!hasGeneratedBase && HasUnsupportedDisposableBase(type, disposableInterface))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedDisposableBase,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (!hasGeneratedBase &&
            asyncDisposableInterface is not null &&
            HasUnsupportedAsyncDisposableBase(type, asyncDisposableInterface))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedAsyncDisposableBase,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (HasUnsupportedBaseDisposalMember(
                type,
                generatedBase,
                generateSynchronousDispose,
                generateAsyncDispose,
                compilation))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedBaseDisposeHook,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (ReportGeneratedMemberCollisions(
                context,
                type,
                hasGeneratedBase,
                generateSynchronousDispose,
                generateAsyncDispose,
                generateUnmanagedCleanup,
                options,
                compilation))
        {
            return false;
        }

        model = new DisposableTypeModel(
            type,
            hasGeneratedBase,
            Array.Empty<OwnedMemberModel>(),
            generateSynchronousDispose,
            generateAsyncDispose,
            generateUnmanagedCleanup,
            generateFinalizer,
            options);
        return true;
    }

    private static bool IsAvailableGeneratedBase(
        INamedTypeSymbol generatedBase,
        HashSet<INamedTypeSymbol> candidates,
        HashSet<INamedTypeSymbol> generatedTypes)
    {
        var definition = generatedBase.OriginalDefinition;
        if (candidates.Contains(definition))
        {
            return generatedTypes.Contains(definition);
        }

        if (definition.DeclaringSyntaxReferences.Length > 0)
        {
            return false;
        }

        return definition.HasAttribute(SymbolHelpers.GeneratedDisposableAttributeName) ||
               HasExpectedGeneratedCoreMethods(generatedBase);
    }

    private static bool HasExpectedGeneratedCoreMethods(INamedTypeSymbol type)
    {
        var generationAttribute = type.GetAttribute(SymbolHelpers.GenerateDisposableAttributeName);
        if (generationAttribute is null)
        {
            return false;
        }

        var generatesSynchronousDispose = generationAttribute.GetNamedBoolean("GenerateSynchronousDispose", defaultValue: true);
        var generatesAsyncDispose = generationAttribute.GetNamedBoolean("GenerateAsyncDispose");
        return (!generatesSynchronousDispose || type.GetMembers("Dispose").OfType<IMethodSymbol>().Any(method =>
                   !method.IsStatic &&
                   method.Arity == 0 &&
                   method.ReturnsVoid &&
                   method.Parameters.Length == 1 &&
                   method.Parameters[0].Type.SpecialType == SpecialType.System_Boolean &&
                   IsOverridableGeneratedCore(method))) &&
               (!generatesAsyncDispose || type.GetMembers("DisposeAsyncCore").OfType<IMethodSymbol>().Any(method =>
                   !method.IsStatic &&
                   method.Arity == 0 &&
                   method.Parameters.Length == 0 &&
                   method.ReturnType.ToDisplayString() == "System.Threading.Tasks.ValueTask" &&
                   IsOverridableGeneratedCore(method)));
    }

    private static bool IsOverridableGeneratedCore(IMethodSymbol method) =>
        (method.IsVirtual || method.IsOverride || method.IsAbstract) &&
        !method.IsSealed &&
        method.DeclaredAccessibility is Accessibility.Public or
            Accessibility.Protected or
            Accessibility.ProtectedOrInternal;

    private static void ReportImplicitOwnedBackingFields(SourceProductionContext context, INamedTypeSymbol type)
    {
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(field =>
                     field.IsImplicitlyDeclared &&
                     field.HasAttribute(SymbolHelpers.DisposeMemberAttributeName)))
        {
            var associatedMember = field.AssociatedSymbol ?? field;
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidOwnedMember,
                associatedMember.BestLocation(),
                associatedMember.Name));
        }
    }

    private static void ValidateOwnedMember(
        SourceProductionContext context,
        ISymbol member,
        Compilation compilation,
        HashSet<INamedTypeSymbol> generatedTypes)
    {
        var containingType = member.ContainingType;
        if (containingType is null)
        {
            return;
        }

        if (member.HasAttribute(SymbolHelpers.BorrowedMemberAttributeName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.ConflictingOwnership,
                member.BestLocation(),
                member.Name));
        }

        if (!containingType.HasAttribute(SymbolHelpers.GenerateDisposableAttributeName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MemberRequiresGeneratedType,
                member.BestLocation(),
                member.Name));
        }

        if (!IsSupportedOwnedMember(member))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidOwnedMember,
                member.BestLocation(),
                member.Name));
            return;
        }

        var disposableInterface = compilation.GetSpecialType(SpecialType.System_IDisposable);
        var asyncDisposableInterface = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        var memberType = GetMemberType(member);
        var supportsSynchronousDispose = memberType is not null && memberType.IsDisposable(disposableInterface, generatedTypes);
        var supportsAsynchronousDispose = memberType is not null && memberType.IsAsyncDisposable(asyncDisposableInterface, generatedTypes);
        if (memberType is not null && !supportsSynchronousDispose && !supportsAsynchronousDispose)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MemberMustBeDisposable,
                member.BestLocation(),
                member.Name,
                memberType.ToDisplayString()));
            return;
        }

        if (supportsAsynchronousDispose && !supportsSynchronousDispose)
        {
            var generationAttribute = containingType.GetAttribute(SymbolHelpers.GenerateDisposableAttributeName);
            if (generationAttribute is null || !generationAttribute.GetNamedBoolean("GenerateAsyncDispose"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AsyncMemberRequiresAsyncGeneration,
                    member.BestLocation(),
                    member.Name,
                    containingType.ToDisplayString()));
                return;
            }
            if (generationAttribute.GetNamedBoolean("GenerateSynchronousDispose", defaultValue: true))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AsyncOnlyMemberSkippedBySyncDispose,
                    member.BestLocation(),
                member.Name));
            }
        }

        if (IsMutableOwnedMember(member))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MutableOwnedMember,
                member.BestLocation(),
                member.Name));
        }
    }

    private static void ValidateBorrowedMember(
        SourceProductionContext context,
        ISymbol member,
        Compilation compilation,
        HashSet<INamedTypeSymbol> generatedTypes)
    {
        var containingType = member.ContainingType;
        if (containingType is null)
        {
            return;
        }

        if (!containingType.HasAttribute(SymbolHelpers.GenerateDisposableAttributeName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.BorrowedMemberRequiresGeneratedType,
                member.BestLocation(),
                member.Name));
        }

        var disposableInterface = compilation.GetSpecialType(SpecialType.System_IDisposable);
        var asyncDisposableInterface = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        var memberType = GetMemberType(member);
        if (!IsSupportedOwnedMember(member) ||
            memberType is null ||
            (!memberType.IsDisposable(disposableInterface, generatedTypes) &&
             !memberType.IsAsyncDisposable(asyncDisposableInterface, generatedTypes)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidBorrowedMember,
                member.BestLocation(),
                member.Name));
        }
    }

    private static List<OwnedMemberModel> GetValidOwnedMembers(
        INamedTypeSymbol type,
        INamedTypeSymbol disposableInterface,
        INamedTypeSymbol? asyncDisposableInterface,
        bool generateAsyncDispose,
        HashSet<INamedTypeSymbol> generatedTypes)
    {
        var result = new List<OwnedMemberModel>();
        foreach (var member in type.GetMembers().Where(member =>
                     member.HasAttribute(SymbolHelpers.DisposeMemberAttributeName) &&
                     !member.HasAttribute(SymbolHelpers.BorrowedMemberAttributeName)))
        {
            if (!IsSupportedOwnedMember(member))
            {
                continue;
            }

            var memberType = GetMemberType(member);
            if (memberType is not null)
            {
                var supportsSynchronousDispose = memberType.IsDisposable(disposableInterface, generatedTypes);
                var supportsAsynchronousDispose = memberType.IsAsyncDisposable(asyncDisposableInterface, generatedTypes);
                if (supportsSynchronousDispose || (generateAsyncDispose && supportsAsynchronousDispose))
                {
                    result.Add(new OwnedMemberModel(
                        member,
                        GetMemberOrder(member),
                        supportsSynchronousDispose,
                        supportsAsynchronousDispose));
                }
            }
        }

        return result;
    }

    private static int GetMemberOrder(ISymbol member)
    {
        var attribute = member.GetAttribute(SymbolHelpers.DisposeMemberAttributeName);
        if (attribute is null)
        {
            return 0;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == "Order" && argument.Value.Value is int order)
            {
                return order;
            }
        }

        return 0;
    }

    private static bool IsSupportedOwnedMember(ISymbol member) => member switch
    {
        IFieldSymbol field => !field.IsImplicitlyDeclared && !field.IsStatic && !field.IsConst,
        IPropertySymbol property =>
            !property.IsStatic &&
            !property.IsIndexer &&
            property.GetMethod is not null &&
            property.ExplicitInterfaceImplementations.Length == 0,
        _ => false,
    };

    private static bool IsMutableOwnedMember(ISymbol member) => member switch
    {
        IFieldSymbol field => !field.IsReadOnly,
        IPropertySymbol property => property.SetMethod is { IsInitOnly: false },
        _ => false,
    };

    private static ITypeSymbol? GetMemberType(ISymbol member) => member switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null,
    };

    private static bool DeclaresDisposalMethod(INamedTypeSymbol type) =>
        type.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(method =>
                !method.IsImplicitlyDeclared &&
                method.Locations.Any(location => location.IsInSource) &&
                (IsSynchronousDisposeMethod(method) || IsAsynchronousDisposeMethod(method)));

    private static bool IsSynchronousDisposeMethod(IMethodSymbol method) =>
        (method.Name == "Dispose" ||
         method.ExplicitInterfaceImplementations.Any(implementation =>
             implementation.Name == "Dispose" &&
             implementation.ContainingType.SpecialType == SpecialType.System_IDisposable)) &&
        method.Arity == 0 &&
        (method.Parameters.Length == 0 ||
         (method.Parameters.Length == 1 &&
          method.Parameters[0].RefKind == RefKind.None &&
          method.Parameters[0].Type.SpecialType == SpecialType.System_Boolean));

    private static bool IsAsynchronousDisposeMethod(IMethodSymbol method) =>
        method.Arity == 0 &&
        method.Parameters.Length == 0 &&
        (method.Name == "DisposeAsync" ||
         method.Name == "DisposeAsyncCore" ||
         method.ExplicitInterfaceImplementations.Any(implementation => implementation.Name == "DisposeAsync"));

    private static INamedTypeSymbol? FindGeneratedBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.HasAttribute(SymbolHelpers.GenerateDisposableAttributeName))
            {
                return current;
            }
        }

        return null;
    }

    private static bool HasUnsupportedDisposableBase(INamedTypeSymbol type, INamedTypeSymbol disposableInterface)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                break;
            }

            if (current.IsDisposable(disposableInterface))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsupportedAsyncDisposableBase(INamedTypeSymbol type, INamedTypeSymbol asyncDisposableInterface)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                break;
            }

            if (current.IsAsyncDisposable(asyncDisposableInterface))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsupportedBaseDisposalMember(
        INamedTypeSymbol type,
        INamedTypeSymbol? generatedBase,
        bool generateSynchronousDispose,
        bool generateAsyncDispose,
        Compilation compilation)
    {
        for (var current = type.BaseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (generatedBase is not null &&
                SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, generatedBase.OriginalDefinition))
            {
                break;
            }

            var methods = current.GetMembers().OfType<IMethodSymbol>();
            if (generateSynchronousDispose && methods.Any(method =>
                    !method.IsStatic &&
                    IsSynchronousDisposeMethod(method) &&
                    IsRelevantBaseDisposalMethod(method, type, compilation)))
            {
                return true;
            }

            if (generateAsyncDispose && methods.Any(method =>
                    !method.IsStatic &&
                    IsAsynchronousDisposeMethod(method) &&
                    IsRelevantBaseDisposalMethod(method, type, compilation)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRelevantBaseDisposalMethod(
        IMethodSymbol method,
        INamedTypeSymbol type,
        Compilation compilation) =>
        method.ExplicitInterfaceImplementations.Length > 0 ||
        compilation.IsSymbolAccessibleWithin(method, type);

    private static bool ReportGeneratedMemberCollisions(
        SourceProductionContext context,
        INamedTypeSymbol type,
        bool hasGeneratedBase,
        bool generateSynchronousDispose,
        bool generateAsyncDispose,
        bool generateUnmanagedCleanup,
        GeneratorOptions options,
        Compilation compilation)
    {
        var hasCollision = ReportSimpleCollision(context, type, "__DisposableGenerator_disposeState", compilation);
        if (generateSynchronousDispose)
        {
            hasCollision |= ReportDisposeNameCollision(context, type, compilation);
        }

        if (generateAsyncDispose)
        {
            hasCollision |= ReportSimpleCollision(context, type, "DisposeAsync", compilation, static item => item is not IMethodSymbol);
            hasCollision |= ReportAsyncCoreCollision(context, type, hasGeneratedBase, compilation);
            if (generateSynchronousDispose && !hasGeneratedBase)
            {
                hasCollision |= ReportSimpleCollision(context, type, "__DisposableGenerator_asyncCleanupCompleted", compilation);
            }
        }

        if (!hasGeneratedBase && options.GenerateRegistrationMethod)
        {
            hasCollision |= ReportSimpleCollision(context, type, "__DisposableGenerator_disposalStarted", compilation);
            hasCollision |= ReportSimpleCollision(context, type, "__DisposableGenerator_disposeGate", compilation);
            hasCollision |= ReportSimpleCollision(context, type, "__DisposableGenerator_registeredDisposables", compilation);
            hasCollision |= ReportRegistrationMethodTypeNameCollision(context, type, options.RegistrationMethodName);
            hasCollision |= ReportRegistrationMethodCollision(context, type, options.RegistrationMethodName, compilation);
            if (generateAsyncDispose)
            {
                hasCollision |= ReportRegistrationMethodTypeNameCollision(context, type, options.AsyncRegistrationMethodName);
                hasCollision |= ReportRegistrationMethodCollision(context, type, options.AsyncRegistrationMethodName, compilation);
            }
        }

        if (options.GenerateDisposalHooks)
        {
            hasCollision |= ReportHookCollision(
                context,
                type,
                "OnDisposing",
                DiagnosticDescriptors.InvalidDisposalHook,
                compilation);
            hasCollision |= ReportHookCollision(
                context,
                type,
                "OnDisposed",
                DiagnosticDescriptors.InvalidDisposalHook,
                compilation);
        }

        if (generateUnmanagedCleanup)
        {
            if (generateSynchronousDispose)
            {
                hasCollision |= ReportSimpleCollision(context, type, "__DisposableGenerator_unmanagedDisposeState", compilation);
            }

            hasCollision |= ReportUnmanagedHookCollision(context, type, compilation);
        }

        return hasCollision;
    }

    private static bool ReportRegistrationMethodTypeNameCollision(
        SourceProductionContext context,
        INamedTypeSymbol type,
        string methodName)
    {
        if (!string.Equals(type.Name, methodName, StringComparison.Ordinal))
        {
            return false;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.GeneratedMemberCollision,
            type.BestLocation(),
            methodName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportSimpleCollision(
        SourceProductionContext context,
        INamedTypeSymbol type,
        string memberName,
        Compilation compilation,
        Func<ISymbol, bool>? predicate = null)
    {
        var member = FindAccessibleMember(type, memberName, compilation, predicate ?? (static _ => true));
        if (member is null)
        {
            return false;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.GeneratedMemberCollision,
            DiagnosticLocation(member, type),
            memberName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportRegistrationMethodCollision(
        SourceProductionContext context,
        INamedTypeSymbol type,
        string methodName,
        Compilation compilation)
    {
        var member = FindAccessibleMember(
            type,
            methodName,
            compilation,
            static item => item is not IMethodSymbol method || IsConflictingRegistrationMethod(method));
        if (member is null)
        {
            return false;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.GeneratedMemberCollision,
            DiagnosticLocation(member, type),
            methodName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportDisposeNameCollision(
        SourceProductionContext context,
        INamedTypeSymbol type,
        Compilation compilation)
    {
        var member = FindAccessibleMember(type, "Dispose", compilation, static item => item is not IMethodSymbol);
        if (member is null)
        {
            return false;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.GeneratedMemberCollision,
            DiagnosticLocation(member, type),
            "Dispose",
            type.ToDisplayString()));
        return true;
    }

    private static bool IsConflictingRegistrationMethod(IMethodSymbol method) =>
        method.Arity == 1 &&
        method.Parameters.Length == 1 &&
        method.Parameters[0].RefKind == RefKind.None &&
        SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, method.TypeParameters[0]);

    private static bool ReportHookCollision(
        SourceProductionContext context,
        INamedTypeSymbol type,
        string hookName,
        DiagnosticDescriptor descriptor,
        Compilation compilation)
    {
        var declaredMembers = type.GetMembers(hookName)
            .Where(member => member.Locations.Any(location => location.IsInSource))
            .ToArray();
        var inheritedMember = FindAccessibleBaseMember(type, hookName, compilation, static _ => true);

        if (declaredMembers.Length == 0 && inheritedMember is null)
        {
            return false;
        }

        if (declaredMembers.Length == 1 &&
            declaredMembers[0] is IMethodSymbol method &&
            IsValidHookImplementation(method) &&
            inheritedMember is null)
        {
            return false;
        }

        var conflictingMember = declaredMembers.FirstOrDefault() ?? inheritedMember!;
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
            DiagnosticLocation(conflictingMember, type),
            hookName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportUnmanagedHookCollision(
        SourceProductionContext context,
        INamedTypeSymbol type,
        Compilation compilation) =>
        ReportHookCollision(
            context,
            type,
            "DisposeUnmanaged",
            DiagnosticDescriptors.InvalidUnmanagedCleanupHook,
            compilation);

    private static bool ReportAsyncCoreCollision(
        SourceProductionContext context,
        INamedTypeSymbol type,
        bool hasGeneratedBase,
        Compilation compilation)
    {
        if (hasGeneratedBase)
        {
            var declared = type.GetMembers("DisposeAsyncCore")
                .FirstOrDefault(item => item is not IMethodSymbol && item.Locations.Any(location => location.IsInSource));
            if (declared is null)
            {
                return false;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.GeneratedMemberCollision,
                declared.BestLocation(),
                "DisposeAsyncCore",
                type.ToDisplayString()));
            return true;
        }

        return ReportSimpleCollision(
            context,
            type,
            "DisposeAsyncCore",
            compilation,
            static item => item is not IMethodSymbol method ||
                           (method.Arity == 0 && method.Parameters.Length == 0));
    }

    private static ISymbol? FindAccessibleMember(
        INamedTypeSymbol type,
        string memberName,
        Compilation compilation,
        Func<ISymbol, bool> predicate)
    {
        var declared = type.GetMembers(memberName).FirstOrDefault(item =>
            item.Locations.Any(location => location.IsInSource) && predicate(item));
        return declared ?? FindAccessibleBaseMember(type, memberName, compilation, predicate);
    }

    private static ISymbol? FindAccessibleBaseMember(
        INamedTypeSymbol type,
        string memberName,
        Compilation compilation,
        Func<ISymbol, bool> predicate)
    {
        for (var current = type.BaseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            var member = current.GetMembers(memberName).FirstOrDefault(item =>
                predicate(item) && compilation.IsSymbolAccessibleWithin(item, type));
            if (member is not null)
            {
                return member;
            }
        }

        return null;
    }

    private static bool IsValidHookImplementation(IMethodSymbol method)
    {
        if (method.IsStatic ||
            !method.ReturnsVoid ||
            method.Parameters.Length != 0 ||
            method.DeclaredAccessibility != Accessibility.Private)
        {
            return false;
        }

        return method.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is MethodDeclarationSyntax declaration &&
            declaration.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword) &&
            (declaration.Body is not null || declaration.ExpressionBody is not null));
    }

    private static Location DiagnosticLocation(ISymbol member, INamedTypeSymbol type)
    {
        var memberLocation = member.BestLocation();
        return memberLocation == Location.None ? type.BestLocation() : memberLocation;
    }

    private static void ReportUnownedMembers(
        SourceProductionContext context,
        INamedTypeSymbol type,
        INamedTypeSymbol disposableInterface,
        INamedTypeSymbol? asyncDisposableInterface,
        HashSet<INamedTypeSymbol> generatedTypes)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared ||
                member.HasAttribute(SymbolHelpers.DisposeMemberAttributeName) ||
                member.HasAttribute(SymbolHelpers.BorrowedMemberAttributeName) ||
                !IsSupportedOwnedMember(member))
            {
                continue;
            }

            var memberType = GetMemberType(member);
            if (memberType is null ||
                (!memberType.IsDisposable(disposableInterface, generatedTypes) &&
                 !memberType.IsAsyncDisposable(asyncDisposableInterface, generatedTypes)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnownedDisposableMember,
                member.BestLocation(),
                member.Name));
        }
    }

    private static int CompareOwnedMembers(OwnedMemberModel left, OwnedMemberModel right, MemberDisposalOrder disposalOrder)
    {
        var orderComparison = left.Order.CompareTo(right.Order);
        if (orderComparison != 0)
        {
            return orderComparison;
        }

        var declarationComparison = CompareByDeclaration(left.Symbol, right.Symbol);
        return disposalOrder == MemberDisposalOrder.Declaration
            ? declarationComparison
            : CompareByDeclaration(right.Symbol, left.Symbol);
    }

    private static int CompareByDeclaration(ISymbol left, ISymbol right)
    {
        var pathComparison = StringComparer.Ordinal.Compare(SymbolHelpers.DeclarationPath(left), SymbolHelpers.DeclarationPath(right));
        if (pathComparison != 0)
        {
            return pathComparison;
        }

        var positionComparison = SymbolHelpers.DeclarationOrder(left).CompareTo(SymbolHelpers.DeclarationOrder(right));
        return positionComparison != 0
            ? positionComparison
            : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }

    private static string HintName(INamedTypeSymbol type)
    {
        var displayName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        const int maximumStemLength = 120;
        var builder = new StringBuilder(Math.Min(displayName.Length, maximumStemLength) + 32);
        uint hash = 2166136261;
        foreach (var character in displayName)
        {
            if (builder.Length < maximumStemLength)
            {
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            }
            unchecked
            {
                hash ^= character;
                hash *= 16777619;
            }
        }

        builder.Append('_');
        builder.Append(hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(".Disposable.g.cs");
        return builder.ToString();
    }
}
