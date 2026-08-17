---
name: use-disposable-generator
description: Install and use the DisposableGenerator NuGet source-generator package in C#/.NET projects. Use when adding generated IDisposable or IAsyncDisposable ownership, classifying owned versus DI/framework-borrowed disposable members, configuring disposal order/registration/hooks/exceptions, resolving DISP diagnostics, or validating a consuming project. Do not use to handwrite an unrelated disposal pattern or to develop the generator implementation itself.
---

# Use DisposableGenerator

Apply explicit lifetime ownership in a consuming C# project and validate the generated contract. Prefer the smallest mode that accurately represents the resources the type owns.

## Workflow

1. Inspect the target project's framework, language version, package version policy, nullable settings, and existing disposal/base-class contract.
2. Stop if a non-generated base already owns `IDisposable` or `IAsyncDisposable`; integrate with that framework's documented hook instead of adding a competing generated contract.
3. Add `DisposableGenerator` as a compile-time-only `PackageReference` with `PrivateAssets="all"`. Follow central package management when present.
4. Make the owner and every containing declaration `partial`. Use a non-static class, not a record or file-local type.
5. Classify every disposable field/property:
   - Add `[DisposeMember]` when this instance owns and must release it.
   - Add `[BorrowedMember]` when DI, a framework, parent, cache, or caller owns it.
   - Do not silence `DISP006` with a pragma when either annotation expresses the decision.
6. Select sync, conjunctive, or async-only generation from the owned resources and the callers' disposal contract.
7. Use dynamic registration only for resources created after construction or not naturally stored as owned members. Await async registration.
8. Configure ordering, late registration, hooks, and exception behavior only when defaults do not meet the lifecycle contract.
9. Build with warnings enabled, inspect every `DISP` diagnostic, and run behavior tests that assert ownership, order, idempotency, and async behavior.

## Load the detailed reference

Read [references/usage.md](references/usage.md) when choosing a disposal mode, using inheritance/dynamic registration/unmanaged cleanup, setting MSBuild properties, or resolving any `DISP001`-`DISP025` diagnostic. It contains the install forms, supported patterns, full configuration table, diagnostic guide, and verification checklist.

## Required outcomes

- Leave ownership visible in source through `[DisposeMember]` and `[BorrowedMember]`.
- Keep the package compile-time-only; do not add a runtime reference or copy the analyzer DLL to output.
- Use `using` for synchronous owners and `await using` for async-only owners.
- Never block on `DisposeAsync()` from synchronous cleanup.
- Prefer `SafeHandle` over raw finalization. Use generated unmanaged cleanup/finalization only for a genuine raw unmanaged resource.
- Add consumer tests for the exact generated behavior being adopted; compilation alone is insufficient for ordering or ownership claims.
