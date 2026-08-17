; Unshipped analyzer release

### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|-------
DISP001 | DisposableGenerator | Error | Generated type and its containing types must be partial
DISP002 | DisposableGenerator | Warning | DisposeMember requires GenerateDisposable
DISP003 | DisposableGenerator | Error | DisposeMember target must implement IDisposable or IAsyncDisposable
DISP004 | DisposableGenerator | Error | Non-generated IDisposable base types are unsupported
DISP005 | DisposableGenerator | Error | Generated types must not implement disposal manually
DISP006 | DisposableGenerator | Info | Disposable fields and properties require an explicit ownership decision
DISP007 | DisposableGenerator | Error | Only non-static, non-record class types are supported
DISP008 | DisposableGenerator | Error | DisposeMember requires a readable instance field or property
DISP009 | DisposableGenerator | Warning | Invalid MSBuild configuration uses its documented fallback
DISP010 | DisposableGenerator | Error | DisposeMember and BorrowedMember cannot be combined
DISP011 | DisposableGenerator | Error | File-local types cannot be augmented from a generated file
DISP012 | DisposableGenerator | Error | User members cannot collide with generated infrastructure
DISP013 | DisposableGenerator | Warning | BorrowedMember requires GenerateDisposable
DISP014 | DisposableGenerator | Warning | BorrowedMember requires a readable instance IDisposable or IAsyncDisposable member
DISP015 | DisposableGenerator | Error | Disposal hooks must use the supported partial implementation signature
DISP016 | DisposableGenerator | Error | Non-generated Dispose(), Dispose(bool), DisposeAsync(), and DisposeAsyncCore() base members are unsupported
DISP017 | DisposableGenerator | Error | Handwritten finalizers conflict with the generator-owned finalization contract
DISP018 | DisposableGenerator | Info | Mutable owned members can leak replaced resources
DISP019 | DisposableGenerator | Error | Async generation requires System.IAsyncDisposable in the target compilation
DISP020 | DisposableGenerator | Error | Async-only ownership requires GenerateAsyncDispose
DISP021 | DisposableGenerator | Error | Generated inheritance must use consistent sync and async generation modes
DISP022 | DisposableGenerator | Warning | Dispose cannot release an async-only owned member
DISP023 | DisposableGenerator | Error | Non-generated IAsyncDisposable base types are unsupported
DISP024 | DisposableGenerator | Error | Unmanaged cleanup hooks must use the supported partial implementation signature
DISP025 | DisposableGenerator | Error | Generation must enable a disposal interface and finalization requires synchronous generation
DISP026 | DisposableGenerator | Error | Generated inheritance must use consistent finalizer generation modes
