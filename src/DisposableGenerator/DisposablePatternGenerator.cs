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

        context.RegisterSourceOutput(
            generatedTypes.Combine(options).Combine(context.CompilationProvider),
            static (output, item) => GenerateType(output, item.Left.Left, item.Left.Right, item.Right));

        var ownedMembers = context.SyntaxProvider.ForAttributeWithMetadataName(
            SymbolHelpers.DisposeMemberAttributeName,
            static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax,
            static (attributeContext, _) => attributeContext.TargetSymbol);

        context.RegisterSourceOutput(
            ownedMembers.Combine(context.CompilationProvider),
            static (output, item) => ValidateOwnedMember(output, item.Left, item.Right));

        var borrowedMembers = context.SyntaxProvider.ForAttributeWithMetadataName(
            SymbolHelpers.BorrowedMemberAttributeName,
            static (node, _) => node is VariableDeclaratorSyntax or PropertyDeclarationSyntax,
            static (attributeContext, _) => attributeContext.TargetSymbol);

        context.RegisterSourceOutput(
            borrowedMembers.Combine(context.CompilationProvider),
            static (output, item) => ValidateBorrowedMember(output, item.Left, item.Right));
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

    private static void GenerateType(SourceProductionContext context, INamedTypeSymbol type, GeneratorOptions options, Compilation compilation)
    {
        if (type.TypeKind != TypeKind.Class || type.IsStatic || type.IsRecord)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedType,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
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
            return;
        }

        var asyncDisposableInterface = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
        if (generateAsyncDispose && asyncDisposableInterface is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AsyncDisposeUnavailable,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
        }

        if (!type.IsPartial() || SymbolHelpers.ContainingTypesOuterFirst(type).Any(containing => !containing.IsPartial()))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.TypeMustBePartial,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
        }

        if (type.IsFileLocal() || SymbolHelpers.ContainingTypesOuterFirst(type).Any(SymbolHelpers.IsFileLocal))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.FileLocalTypeUnsupported,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
        }

        if (DeclaresDisposalMethod(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.ManualDisposeImplementation,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
        }

        if (type.GetMembers().OfType<IMethodSymbol>().Any(method => method.MethodKind == MethodKind.Destructor))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.FinalizerUnsupported,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
        }

        var generatedBase = FindGeneratedBase(type);
        var hasGeneratedBase = generatedBase is not null;
        if (generatedBase is not null)
        {
            var baseAttribute = generatedBase.GetAttribute(SymbolHelpers.GenerateDisposableAttributeName)!;
            if (generateSynchronousDispose != baseAttribute.GetNamedBoolean("GenerateSynchronousDispose", defaultValue: true) ||
                generateAsyncDispose != baseAttribute.GetNamedBoolean("GenerateAsyncDispose"))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AsyncGenerationMismatch,
                    type.BestLocation(),
                    type.ToDisplayString()));
                return;
            }
        }

        var disposableInterface = compilation.GetSpecialType(SpecialType.System_IDisposable);
        if (!hasGeneratedBase && HasUnsupportedDisposableBase(type, disposableInterface))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedDisposableBase,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
        }

        if (!hasGeneratedBase &&
            asyncDisposableInterface is not null &&
            HasUnsupportedAsyncDisposableBase(type, asyncDisposableInterface))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedAsyncDisposableBase,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
        }

        if (generateSynchronousDispose && !hasGeneratedBase && HasUnsupportedBaseDisposeHook(type, compilation))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedBaseDisposeHook,
                type.BestLocation(),
                type.ToDisplayString()));
            return;
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
            return;
        }

        var members = GetValidOwnedMembers(type, disposableInterface, asyncDisposableInterface, generateAsyncDispose);
        members.Sort((left, right) => CompareOwnedMembers(left, right, options.MemberDisposalOrder));

        ReportImplicitOwnedBackingFields(context, type);

        if (options.ReportUnownedDisposableFields)
        {
            ReportUnownedMembers(context, type, disposableInterface, asyncDisposableInterface);
        }

        var model = new DisposableTypeModel(
            type,
            hasGeneratedBase,
            members,
            generateSynchronousDispose,
            generateAsyncDispose,
            generateUnmanagedCleanup,
            generateFinalizer,
            options);
        context.AddSource(HintName(type), SourceText.From(SourceEmitter.Emit(model), Encoding.UTF8));
    }

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

    private static void ValidateOwnedMember(SourceProductionContext context, ISymbol member, Compilation compilation)
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
        var supportsSynchronousDispose = memberType is not null && memberType.IsDisposable(disposableInterface);
        var supportsAsynchronousDispose = memberType is not null && memberType.IsAsyncDisposable(asyncDisposableInterface);
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

    private static void ValidateBorrowedMember(SourceProductionContext context, ISymbol member, Compilation compilation)
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
            (!memberType.IsDisposable(disposableInterface) && !memberType.IsAsyncDisposable(asyncDisposableInterface)))
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
        bool generateAsyncDispose)
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
                var supportsSynchronousDispose = memberType.IsDisposable(disposableInterface);
                var supportsAsynchronousDispose = memberType.IsAsyncDisposable(asyncDisposableInterface);
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

    private static bool HasUnsupportedBaseDisposeHook(INamedTypeSymbol type, Compilation compilation)
    {
        for (var current = type.BaseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var method in current.GetMembers("Dispose").OfType<IMethodSymbol>())
            {
                if (!method.IsStatic &&
                    method.Arity == 0 &&
                    (method.Parameters.Length == 0 ||
                     (method.Parameters.Length == 1 &&
                      method.Parameters[0].RefKind == RefKind.None &&
                      method.Parameters[0].Type.SpecialType == SpecialType.System_Boolean)) &&
                    compilation.IsSymbolAccessibleWithin(method, type))
                {
                    return true;
                }
            }
        }

        return false;
    }

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
            hasCollision |= ReportRegistrationMethodCollision(context, type, options.RegistrationMethodName, compilation);
            if (generateAsyncDispose)
            {
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
        INamedTypeSymbol? asyncDisposableInterface)
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
                (!memberType.IsDisposable(disposableInterface) && !memberType.IsAsyncDisposable(asyncDisposableInterface)))
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
