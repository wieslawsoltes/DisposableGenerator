# Changelog

All notable changes to DisposableGenerator are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-08-17

### Added

- Incremental source generation for explicit `IDisposable` and `IAsyncDisposable`
  ownership with `[GenerateDisposable]`, `[DisposeMember]`, and `[BorrowedMember]`.
- Thread-safe, idempotent synchronous and asynchronous disposal across generated
  inheritance chains.
- Configurable member and dynamic-registration disposal order, late-registration
  behavior, exception handling, generated method names, and disposal hooks.
- Optional unmanaged cleanup and generator-owned finalization.
- Diagnostics `DISP001` through `DISP026` for unsupported patterns, ownership
  decisions, unsafe lifetime configurations, and inheritance mismatches.
- Analyzer-only NuGet packaging with no runtime dependency, plus isolated package
  consumers covering .NET 8, .NET 9, and .NET 10.
- Console and Avalonia/ReactiveUI samples demonstrating generated lifetime
  management in real applications.

[Unreleased]: https://github.com/wieslawsoltes/DisposableGenerator/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/wieslawsoltes/DisposableGenerator/releases/tag/v1.0.0
