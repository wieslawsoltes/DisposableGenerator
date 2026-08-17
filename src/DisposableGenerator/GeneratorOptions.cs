using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DisposableGenerator;

internal enum PostDisposeRegistrationBehavior
{
    Throw,
    DisposeImmediately,
}

internal enum MemberDisposalOrder
{
    ReverseDeclaration,
    Declaration,
}

internal enum RegisteredResourceDisposalOrder
{
    ReverseRegistration,
    Registration,
}

internal enum DisposalExceptionBehavior
{
    StopOnFirst,
    ContinueAndAggregate,
}

internal sealed class GeneratorOptions
{
    private const string Prefix = "build_property.DisposableGenerator_";

    private GeneratorOptions()
    {
    }

    internal bool GenerateRegistrationMethod { get; private set; } = true;

    internal string RegistrationMethodName { get; private set; } = "RegisterDisposable";

    internal string AsyncRegistrationMethodName { get; private set; } = "RegisterAsyncDisposable";

    internal PostDisposeRegistrationBehavior PostDisposeRegistrationBehavior { get; private set; } = PostDisposeRegistrationBehavior.Throw;

    internal MemberDisposalOrder MemberDisposalOrder { get; private set; } = MemberDisposalOrder.ReverseDeclaration;

    internal RegisteredResourceDisposalOrder RegisteredResourceDisposalOrder { get; private set; } = RegisteredResourceDisposalOrder.ReverseRegistration;

    internal DisposalExceptionBehavior DisposalExceptionBehavior { get; private set; } = DisposalExceptionBehavior.StopOnFirst;

    internal bool GenerateDisposalHooks { get; private set; } = true;

    internal bool ReportUnownedDisposableFields { get; private set; } = true;

    internal List<ConfigurationError> Errors { get; } = new();

    internal static GeneratorOptions From(AnalyzerConfigOptions options)
    {
        var result = new GeneratorOptions();
        result.GenerateRegistrationMethod = result.ReadBoolean(options, "GenerateRegistrationMethod", true);
        result.GenerateDisposalHooks = result.ReadBoolean(options, "GenerateDisposalHooks", true);
        result.ReportUnownedDisposableFields = result.ReadBoolean(options, "ReportUnownedDisposableFields", true);

        if (options.TryGetValue(Prefix + "RegistrationMethodName", out var methodName))
        {
            if (IsValidGeneratedMethodName(methodName))
            {
                result.RegistrationMethodName = methodName;
            }
            else
            {
                result.Errors.Add(new ConfigurationError("DisposableGenerator_RegistrationMethodName", methodName, result.RegistrationMethodName));
            }
        }

        if (options.TryGetValue(Prefix + "AsyncRegistrationMethodName", out var asyncMethodName))
        {
            if (IsValidGeneratedMethodName(asyncMethodName))
            {
                result.AsyncRegistrationMethodName = asyncMethodName;
            }
            else
            {
                result.Errors.Add(new ConfigurationError("DisposableGenerator_AsyncRegistrationMethodName", asyncMethodName, result.AsyncRegistrationMethodName));
            }
        }

        if (string.Equals(
                SymbolHelpers.IdentifierValueText(result.AsyncRegistrationMethodName),
                SymbolHelpers.IdentifierValueText(result.RegistrationMethodName),
                StringComparison.Ordinal))
        {
            var fallbackName = SymbolHelpers.IdentifierValueText(result.RegistrationMethodName) == "RegisterAsyncDisposable"
                ? "RegisterAsyncDisposableResource"
                : "RegisterAsyncDisposable";
            result.Errors.Add(new ConfigurationError(
                "DisposableGenerator_AsyncRegistrationMethodName",
                result.AsyncRegistrationMethodName,
                fallbackName));
            result.AsyncRegistrationMethodName = fallbackName;
        }

        result.PostDisposeRegistrationBehavior = result.ReadEnum(
            options,
            "PostDisposeRegistrationBehavior",
            PostDisposeRegistrationBehavior.Throw);
        result.MemberDisposalOrder = result.ReadEnum(
            options,
            "MemberDisposalOrder",
            MemberDisposalOrder.ReverseDeclaration);
        result.RegisteredResourceDisposalOrder = result.ReadEnum(
            options,
            "RegisteredResourceDisposalOrder",
            RegisteredResourceDisposalOrder.ReverseRegistration);
        result.DisposalExceptionBehavior = result.ReadEnum(
            options,
            "DisposalExceptionBehavior",
            DisposalExceptionBehavior.StopOnFirst);

        return result;
    }

    private static bool IsValidGeneratedMethodName(string methodName)
    {
        var valueText = SymbolHelpers.IdentifierValueText(methodName);
        return IsValidConfiguredIdentifier(methodName, valueText) &&
            SyntaxFacts.GetKeywordKind(valueText) == SyntaxKind.None &&
            valueText != "Dispose" &&
            valueText != "DisposeAsync" &&
            valueText != "DisposeAsyncCore" &&
            valueText != "OnDisposing" &&
            valueText != "OnDisposed" &&
            valueText != "DisposeUnmanaged" &&
            valueText != "__DisposableGenerator_disposeState" &&
            valueText != "__DisposableGenerator_disposalStarted" &&
            valueText != "__DisposableGenerator_asyncCleanupCompleted" &&
            valueText != "__DisposableGenerator_unmanagedDisposeState" &&
            valueText != "__DisposableGenerator_disposeGate" &&
            valueText != "__DisposableGenerator_registeredDisposables";
    }

    private static bool IsValidConfiguredIdentifier(string identifier, string valueText) =>
        SyntaxFacts.IsValidIdentifier(valueText) &&
        (identifier.Length == valueText.Length ||
         (identifier.Length == valueText.Length + 1 && identifier[0] == '@'));

    private bool ReadBoolean(AnalyzerConfigOptions options, string name, bool fallback)
    {
        if (!options.TryGetValue(Prefix + name, out var text))
        {
            return fallback;
        }

        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        Errors.Add(new ConfigurationError("DisposableGenerator_" + name, text, fallback ? "true" : "false"));
        return fallback;
    }

    private T ReadEnum<T>(AnalyzerConfigOptions options, string name, T fallback)
        where T : struct
    {
        if (!options.TryGetValue(Prefix + name, out var text))
        {
            return fallback;
        }

        if (Enum.GetNames(typeof(T)).Any(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse(text, ignoreCase: true, out T value))
        {
            return value;
        }

        Errors.Add(new ConfigurationError("DisposableGenerator_" + name, text, fallback.ToString()!));
        return fallback;
    }
}

internal sealed class ConfigurationError
{
    internal ConfigurationError(string propertyName, string value, string fallback)
    {
        PropertyName = propertyName;
        Value = value;
        Fallback = fallback;
    }

    internal string PropertyName { get; }

    internal string Value { get; }

    internal string Fallback { get; }
}
