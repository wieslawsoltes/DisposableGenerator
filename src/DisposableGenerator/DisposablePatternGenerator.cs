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

        var generatedTypeModels = generatedTypes
            .Combine(options)
            .Select(static (item, _) => CreateTypeOutput(
                item.Left,
                item.Right))
            .WithTrackingName("DisposableGenerationOutput");

        context.RegisterSourceOutput(
            generatedTypeModels,
            static (output, model) => EmitType(output, model));

        context.RegisterSourceOutput(
            ownedMembers.Combine(options),
            static (output, item) => ValidateOwnedMember(
                output,
                item.Left,
                item.Right));

        context.RegisterSourceOutput(
            borrowedMembers.Combine(options),
            static (output, item) => ValidateBorrowedMember(
                output,
                item.Left,
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

    private static DisposableGenerationOutput CreateTypeOutput(
        INamedTypeSymbol type,
        GeneratorOptions options)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var availability = CreateGenerationAvailability(options);
        if (!TryCreateTypeModel(
                diagnostics.Add,
                type,
                options,
                availability,
                out var model))
        {
            return new DisposableGenerationOutput(null, null, diagnostics.ToImmutable());
        }

        var members = GetValidOwnedMembers(
            model!.Type,
            model.GenerateAsyncDispose,
            availability);
        members.Sort((left, right) => CompareOwnedMembers(left, right, options.MemberDisposalOrder));

        ReportImplicitOwnedBackingFields(diagnostics.Add, model.Type);
        if (options.ReportUnownedDisposableFields)
        {
            ReportUnownedMembers(
                diagnostics.Add,
                model.Type,
                availability);
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
        return new DisposableGenerationOutput(
            HintName(model.Type),
            SourceEmitter.Emit(completedModel),
            diagnostics.ToImmutable());
    }

    private static void EmitType(SourceProductionContext context, DisposableGenerationOutput output)
    {
        foreach (var diagnostic in output.Diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
        }

        if (output.HintName is not null && output.Source is not null)
        {
            context.AddSource(output.HintName, SourceText.From(output.Source, Encoding.UTF8));
        }
    }

    private static bool TryCreateTypeModel(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type,
        GeneratorOptions options,
        Func<INamedTypeSymbol, bool> generationAvailable,
        out DisposableTypeModel? model)
    {
        model = null;
        if (type.TypeKind != TypeKind.Class || type.IsStatic || type.IsRecord)
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
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
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.InvalidGenerationMode,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        var asyncDisposableInterface = SymbolHelpers.FindTypeByMetadataName(type, "System.IAsyncDisposable");
        if (generateAsyncDispose && asyncDisposableInterface is null)
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.AsyncDisposeUnavailable,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (!type.IsPartial() || SymbolHelpers.ContainingTypesOuterFirst(type).Any(containing => !containing.IsPartial()))
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.TypeMustBePartial,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (type.IsFileLocal() || SymbolHelpers.ContainingTypesOuterFirst(type).Any(SymbolHelpers.IsFileLocal))
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.FileLocalTypeUnsupported,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (DeclaresDisposalMethod(type))
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.ManualDisposeImplementation,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (type.GetMembers().OfType<IMethodSymbol>().Any(method => method.MethodKind == MethodKind.Destructor))
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.FinalizerUnsupported,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        var generatedBase = FindGeneratedBase(type);
        var hasGeneratedBase = generatedBase is not null;
        if (generatedBase is not null)
        {
            if (!generationAvailable(generatedBase))
            {
                return false;
            }

            var baseAttribute = generatedBase.GetAttribute(SymbolHelpers.GenerateDisposableAttributeName)!;
            if (generateSynchronousDispose != baseAttribute.GetNamedBoolean("GenerateSynchronousDispose", defaultValue: true) ||
                generateAsyncDispose != baseAttribute.GetNamedBoolean("GenerateAsyncDispose"))
            {
                reportDiagnostic?.Invoke(Diagnostic.Create(
                    DiagnosticDescriptors.AsyncGenerationMismatch,
                    type.BestLocation(),
                    type.ToDisplayString()));
                return false;
            }
        }

        if (!hasGeneratedBase && HasUnsupportedDisposableBase(type))
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedDisposableBase,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (!hasGeneratedBase &&
            asyncDisposableInterface is not null &&
            HasUnsupportedAsyncDisposableBase(type))
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedAsyncDisposableBase,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (HasUnsupportedBaseDisposalMember(
                type,
                generatedBase,
                generateSynchronousDispose,
                generateAsyncDispose))
        {
            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedBaseDisposeHook,
                type.BestLocation(),
                type.ToDisplayString()));
            return false;
        }

        if (ReportGeneratedMemberCollisions(
                reportDiagnostic,
                type,
                hasGeneratedBase,
                generateSynchronousDispose,
                generateAsyncDispose,
                generateUnmanagedCleanup,
                options))
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

    private static Func<INamedTypeSymbol, bool> CreateGenerationAvailability(
        GeneratorOptions options)
    {
        var results = new Dictionary<INamedTypeSymbol, bool>(SymbolEqualityComparer.Default);
        var evaluating = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        return type => IsGenerationAvailable(type, options, results, evaluating);
    }

    private static bool IsGenerationAvailable(
        INamedTypeSymbol type,
        GeneratorOptions options,
        Dictionary<INamedTypeSymbol, bool> results,
        HashSet<INamedTypeSymbol> evaluating)
    {
        var definition = type.OriginalDefinition;
        if (results.TryGetValue(definition, out var cached))
        {
            return cached;
        }

        if (!definition.HasAttribute(SymbolHelpers.GenerateDisposableAttributeName))
        {
            results[definition] = false;
            return false;
        }

        if (definition.DeclaringSyntaxReferences.Length == 0)
        {
            var available = definition.HasAttribute(SymbolHelpers.GeneratedDisposableAttributeName) ||
                            HasExpectedGeneratedCoreMethods(type);
            results[definition] = available;
            return available;
        }

        if (!evaluating.Add(definition))
        {
            return false;
        }

        var result = TryCreateTypeModel(
            reportDiagnostic: null,
            definition,
            options,
            generatedBase => IsGenerationAvailable(generatedBase, options, results, evaluating),
            out _);
        evaluating.Remove(definition);
        results[definition] = result;
        return result;
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

    private static void ReportImplicitOwnedBackingFields(Action<Diagnostic> reportDiagnostic, INamedTypeSymbol type)
    {
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(field =>
                     field.IsImplicitlyDeclared &&
                     field.HasAttribute(SymbolHelpers.DisposeMemberAttributeName)))
        {
            var associatedMember = field.AssociatedSymbol ?? field;
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidOwnedMember,
                associatedMember.BestLocation(),
                associatedMember.Name));
        }
    }

    private static void ValidateOwnedMember(
        SourceProductionContext context,
        ISymbol member,
        GeneratorOptions options)
    {
        var generationAvailable = CreateGenerationAvailability(options);
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

        var memberType = GetMemberType(member);
        var supportsSynchronousDispose = memberType is not null && memberType.IsDisposable(generationAvailable);
        var supportsAsynchronousDispose = memberType is not null && memberType.IsAsyncDisposable(generationAvailable);
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
        GeneratorOptions options)
    {
        var generationAvailable = CreateGenerationAvailability(options);
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

        var memberType = GetMemberType(member);
        if (!IsSupportedOwnedMember(member) ||
            memberType is null ||
            (!memberType.IsDisposable(generationAvailable) &&
             !memberType.IsAsyncDisposable(generationAvailable)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidBorrowedMember,
                member.BestLocation(),
                member.Name));
        }
    }

    private static List<OwnedMemberModel> GetValidOwnedMembers(
        INamedTypeSymbol type,
        bool generateAsyncDispose,
        Func<INamedTypeSymbol, bool> generationAvailable)
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
                var supportsSynchronousDispose = memberType.IsDisposable(generationAvailable);
                var supportsAsynchronousDispose = memberType.IsAsyncDisposable(generationAvailable);
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
        IPropertySymbol property =>
            property.RefKind == RefKind.Ref ||
            property.SetMethod is { IsInitOnly: false },
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
         method.ExplicitInterfaceImplementations.Any(implementation =>
             implementation.Name == "DisposeAsync" &&
             implementation.ContainingType.ToDisplayString() == "System.IAsyncDisposable"));

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

    private static bool HasUnsupportedDisposableBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                break;
            }

            if (current.IsDisposable())
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsupportedAsyncDisposableBase(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                break;
            }

            if (current.IsAsyncDisposable())
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
        bool generateAsyncDispose)
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
                    IsSynchronousDisposeMethod(method) &&
                    IsRelevantBaseDisposalMethod(method, type)))
            {
                return true;
            }

            if (generateAsyncDispose && methods.Any(method =>
                    IsAsynchronousDisposeMethod(method) &&
                    IsRelevantBaseDisposalMethod(method, type)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRelevantBaseDisposalMethod(
        IMethodSymbol method,
        INamedTypeSymbol type) =>
        method.ExplicitInterfaceImplementations.Length > 0 ||
        IsAccessibleFromDerivedType(method, type);

    private static bool ReportGeneratedMemberCollisions(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type,
        bool hasGeneratedBase,
        bool generateSynchronousDispose,
        bool generateAsyncDispose,
        bool generateUnmanagedCleanup,
        GeneratorOptions options)
    {
        var hasCollision = ReportSimpleCollision(reportDiagnostic, type, "__DisposableGenerator_disposeState");
        if (generateSynchronousDispose)
        {
            hasCollision |= ReportDisposeNameCollision(reportDiagnostic, type);
        }

        if (generateAsyncDispose)
        {
            hasCollision |= ReportSimpleCollision(reportDiagnostic, type, "DisposeAsync", static item => item is not IMethodSymbol);
            hasCollision |= ReportAsyncCoreCollision(reportDiagnostic, type, hasGeneratedBase);
            if (generateSynchronousDispose && !hasGeneratedBase)
            {
                hasCollision |= ReportSimpleCollision(reportDiagnostic, type, "__DisposableGenerator_asyncCleanupCompleted");
            }
        }

        if (!hasGeneratedBase && options.GenerateRegistrationMethod)
        {
            hasCollision |= ReportSimpleCollision(reportDiagnostic, type, "__DisposableGenerator_disposalStarted");
            hasCollision |= ReportSimpleCollision(reportDiagnostic, type, "__DisposableGenerator_disposeGate");
            hasCollision |= ReportSimpleCollision(reportDiagnostic, type, "__DisposableGenerator_registeredDisposables");
            hasCollision |= ReportRegistrationMethodTypeNameCollision(reportDiagnostic, type, options.RegistrationMethodName);
            hasCollision |= ReportRegistrationMethodCollision(reportDiagnostic, type, options.RegistrationMethodName);
            if (generateAsyncDispose)
            {
                hasCollision |= ReportRegistrationMethodTypeNameCollision(reportDiagnostic, type, options.AsyncRegistrationMethodName);
                hasCollision |= ReportRegistrationMethodCollision(reportDiagnostic, type, options.AsyncRegistrationMethodName);
            }
        }

        if (options.GenerateDisposalHooks)
        {
            hasCollision |= ReportHookCollision(
                reportDiagnostic,
                type,
                "OnDisposing",
                DiagnosticDescriptors.InvalidDisposalHook);
            hasCollision |= ReportHookCollision(
                reportDiagnostic,
                type,
                "OnDisposed",
                DiagnosticDescriptors.InvalidDisposalHook);
        }

        if (generateUnmanagedCleanup)
        {
            if (generateSynchronousDispose)
            {
                hasCollision |= ReportSimpleCollision(reportDiagnostic, type, "__DisposableGenerator_unmanagedDisposeState");
            }

            hasCollision |= ReportUnmanagedHookCollision(reportDiagnostic, type);
        }

        return hasCollision;
    }

    private static bool ReportRegistrationMethodTypeNameCollision(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type,
        string methodName)
    {
        if (!string.Equals(type.Name, methodName, StringComparison.Ordinal))
        {
            return false;
        }

        reportDiagnostic?.Invoke(Diagnostic.Create(
            DiagnosticDescriptors.GeneratedMemberCollision,
            type.BestLocation(),
            methodName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportSimpleCollision(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type,
        string memberName,
        Func<ISymbol, bool>? predicate = null)
    {
        var member = FindAccessibleMember(type, memberName, predicate ?? (static _ => true));
        if (member is null)
        {
            return false;
        }

        reportDiagnostic?.Invoke(Diagnostic.Create(
            DiagnosticDescriptors.GeneratedMemberCollision,
            DiagnosticLocation(member, type),
            memberName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportRegistrationMethodCollision(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type,
        string methodName)
    {
        var member = FindAccessibleMember(
            type,
            methodName,
            static item => item is not IMethodSymbol method || IsConflictingRegistrationMethod(method));
        if (member is null)
        {
            return false;
        }

        reportDiagnostic?.Invoke(Diagnostic.Create(
            DiagnosticDescriptors.GeneratedMemberCollision,
            DiagnosticLocation(member, type),
            methodName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportDisposeNameCollision(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type)
    {
        var member = FindAccessibleMember(type, "Dispose", static item => item is not IMethodSymbol);
        if (member is null)
        {
            return false;
        }

        reportDiagnostic?.Invoke(Diagnostic.Create(
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
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type,
        string hookName,
        DiagnosticDescriptor descriptor)
    {
        var declaredMembers = type.GetMembers(hookName)
            .Where(member => member.Locations.Any(location => location.IsInSource))
            .ToArray();
        var inheritedMember = FindAccessibleBaseMember(type, hookName, static _ => true);

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
        reportDiagnostic?.Invoke(Diagnostic.Create(
            descriptor,
            DiagnosticLocation(conflictingMember, type),
            hookName,
            type.ToDisplayString()));
        return true;
    }

    private static bool ReportUnmanagedHookCollision(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type) =>
        ReportHookCollision(
            reportDiagnostic,
            type,
            "DisposeUnmanaged",
            DiagnosticDescriptors.InvalidUnmanagedCleanupHook);

    private static bool ReportAsyncCoreCollision(
        Action<Diagnostic>? reportDiagnostic,
        INamedTypeSymbol type,
        bool hasGeneratedBase)
    {
        if (hasGeneratedBase)
        {
            var declared = type.GetMembers("DisposeAsyncCore")
                .FirstOrDefault(item => item is not IMethodSymbol && item.Locations.Any(location => location.IsInSource));
            if (declared is null)
            {
                return false;
            }

            reportDiagnostic?.Invoke(Diagnostic.Create(
                DiagnosticDescriptors.GeneratedMemberCollision,
                declared.BestLocation(),
                "DisposeAsyncCore",
                type.ToDisplayString()));
            return true;
        }

        return ReportSimpleCollision(
            reportDiagnostic,
            type,
            "DisposeAsyncCore",
            static item => item is not IMethodSymbol method ||
                           (method.Arity == 0 && method.Parameters.Length == 0));
    }

    private static ISymbol? FindAccessibleMember(
        INamedTypeSymbol type,
        string memberName,
        Func<ISymbol, bool> predicate)
    {
        var declared = type.GetMembers(memberName).FirstOrDefault(item =>
            item.Locations.Any(location => location.IsInSource) && predicate(item));
        return declared ?? FindAccessibleBaseMember(type, memberName, predicate);
    }

    private static ISymbol? FindAccessibleBaseMember(
        INamedTypeSymbol type,
        string memberName,
        Func<ISymbol, bool> predicate)
    {
        for (var current = type.BaseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            var member = current.GetMembers(memberName).FirstOrDefault(item =>
                predicate(item) && IsAccessibleFromDerivedType(item, type));
            if (member is not null)
            {
                return member;
            }
        }

        return null;
    }

    private static bool IsAccessibleFromDerivedType(ISymbol member, INamedTypeSymbol derivedType)
    {
        var hasInternalAccess = member.ContainingAssembly is not null &&
                                (SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, derivedType.ContainingAssembly) ||
                                 member.ContainingAssembly.GivesAccessTo(derivedType.ContainingAssembly));
        return member.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal => hasInternalAccess,
            Accessibility.ProtectedAndInternal => hasInternalAccess,
            _ => false,
        };
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
        Action<Diagnostic> reportDiagnostic,
        INamedTypeSymbol type,
        Func<INamedTypeSymbol, bool> generationAvailable)
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
                (!memberType.IsDisposable(generationAvailable) &&
                 !memberType.IsAsyncDisposable(generationAvailable)))
            {
                continue;
            }

            reportDiagnostic(Diagnostic.Create(
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
