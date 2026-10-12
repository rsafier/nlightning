# Versioning Policy

This document provides a detailed overview of the versioning strategy adopted for the various packages within the
NLightning repository. Our versioning aims to be transparent and predictable to assist users and contributors in
understanding expected changes.

## Overview

We use [Semantic Versioning 2.0.0 (SemVer)](https://semver.org/) for all packages in the repository. The versioning
format is:
    
```
MAJOR.MINOR.PATCH
```

- MAJOR versions indicate incompatible API changes,
- MINOR versions add functionality in a backward-compatible manner,
- PATCH versions make backward-compatible bug fixes.

## Packages

Each published project under `src/` is versioned on its own. Its `.csproj` carries `<Version>` (plus `<AssemblyVersion>`/`<FileVersion>` and `PackageReleaseNotes` where present), and its changes are recorded in that project's `CHANGELOG.md` (e.g. `src/NLightning.Domain/CHANGELOG.md`, `src/NLightning.Bolt11/CHANGELOG.md`). Bump these together.

- **Core libraries** (`NLightning.Domain`, `NLightning.Application`, `NLightning.Infrastructure*`): versioned independently; a breaking change in one (for example a changed public type or conversion) bumps that package's MAJOR version and is listed under "Breaking" in its changelog.
- **`NLightning.Bolt11`**: the standalone BOLT 11 invoice library, on its own timeline.
- **`NLightning.Daemon` / `NLightning.Client`**: the node and its CLI. They talk over IPC and must come from the same build; an IPC wire change is a breaking change for both.

## Versioning Triggers

1. Major update: backward-incompatible changes to public APIs, persisted formats (key files, database schema without a migration) or the IPC wire format.
2. Minor update: backward-compatible features.
3. Patch update: backward-compatible bug fixes, performance improvements and minor changes.

## Tagging and Release

Tag a release with its version number (for example `v2.0.0`) and list the per-package versions in the release notes.

## Contributing

Contributors are encouraged to follow this versioning policy for any changes made. Proposed changes should include
details in pull requests on whether they constitute major, minor, or patch changes, providing necessary descriptions
and justifications.