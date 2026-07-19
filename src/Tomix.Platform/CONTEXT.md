# Tomix.Platform

Dependency-free platform primitives shared by outer projects.

## Responsibilities

- Resolve well-known local tomix paths from process and operating-system state.
- Provide crash-safe atomic file replacement for filesystem-backed stores.

## Cross-folder dependencies

- Has no dependencies on other `Tomix.*` projects.
- May be referenced by outer infrastructure-owning projects such as `/src/Tomix.App` and
  `/src/Tomix.Auth`.
- Must not be referenced by `/src/Tomix.Core` or provider projects.

## Rules

- Use only BCL APIs; do not add third-party package dependencies.
- Keep primitives deterministic where inputs can be passed explicitly.
- Do not add domain types, feature-specific stores, external-service adapters, or business logic.

## Test

Platform behavior is exercised with the filesystem-backed state tests:

```bash
dotnet test tests/Tomix.App.Tests --filter AtomicStateTests
```
