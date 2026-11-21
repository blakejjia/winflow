# GitHub Release Packaging Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement an automated GitHub Actions release workflow that builds both a standalone single-file executable and an Inno Setup installer wizard when a Git tag is pushed.

**Architecture:** A tag-triggered GitHub Actions workflow (`.github/workflows/release.yml`) that restores, tests, and publishes WinFlow as a single-file self-contained .NET 10 application. It then packages the standalone version (raw exe and zip) and compiles a professional Windows setup wizard using Inno Setup via `installer/winflow.iss`, calculates SHA256 checksums, and uploads all assets to a GitHub Release.

**Tech Stack:** GitHub Actions, .NET 10 SDK, Inno Setup 6, PowerShell, `softprops/action-gh-release@v2`.

**Spec:** User request: "针对这个项目，帮我写 GitHub workflow 对于打包的部分。 就是 tag 了以后，它能自己打包成single exe + 安装wizard两个版本的。记住要符合官方推荐的方式"

## Global Constraints

- Must target `net10.0-windows10.0.19041.0` self-contained `win-x64`.
- Single executable must be self-contained (`PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`).
- Installer must support per-user installation without requiring Administrator privileges by default (`PrivilegesRequired=lowest`), but allow elevation if needed.
- Release workflow must strictly follow official GitHub Actions security recommendations (`permissions: contents: write`, specific pinned action versions).
- Release must publish both distribution formats: Standalone Single-file Exe/Zip and Setup Wizard Exe.

## Review Focus

1. **Tag formatting**: Tag name may start with `v` (e.g. `v1.0.0`) or not (e.g. `1.0.0`); the workflow must correctly strip `v` for assembly and installer versioning.
2. **Process lock handling**: Installer must detect and close running `WinFlow.exe` instances during installation or uninstallation.
3. **Clean uninstaller**: The installer wizard must register with Windows Installed Apps with proper DisplayName, DisplayIcon, and UninstallString.
4. **Action permissions**: Ensure GITHUB_TOKEN has explicit `contents: write` permissions so `action-gh-release` succeeds without permission errors.
5. **Workflow dispatch**: Workflow must support `workflow_dispatch` with a custom version input for manual testing without creating git tags.

---

### Task 1: Create Inno Setup Configuration Script

**Files:**
- Create: `installer/winflow.iss`

**Steps:**
- [ ] Create `installer/winflow.iss` with:
  - App metadata (`AppName=WinFlow`, `AppPublisher=WinFlow`, `AppExeName=WinFlow.exe`).
  - Preprocessor variable `{#AppVersion}` with default fallback `1.0.0`.
  - `{autopf}\WinFlow` install directory with `PrivilegesRequired=lowest` and `PrivilegesRequiredOverridesAllowed=dialog`.
  - Source mapping pointing to the publish output directory (`dist\WinFlow.exe`).
  - Tasks for Desktop shortcut and Start Menu shortcut.
  - Run entry to launch WinFlow on installation completion.
  - `CloseApplications=yes` to gracefully close active WinFlow instances.
- [ ] Verify script syntax and parameters against Inno Setup standards.

---

### Task 2: Create GitHub Actions Release Workflow

**Files:**
- Create: `.github/workflows/release.yml`

**Steps:**
- [ ] Create `.github/workflows/release.yml` with:
  - Triggers on `push.tags: ['v*']` and `workflow_dispatch` (with `tag_name` input).
  - Explicit `permissions: contents: write`.
  - Concurrency control to prevent race conditions on the same tag.
  - Job `release` running on `windows-latest`.
  - Step 1: Checkout repository (`actions/checkout@v4`).
  - Step 2: Setup .NET 10 SDK (`actions/setup-dotnet@v4`).
  - Step 3: Parse semantic version from `${{ github.ref_name }}` (stripping `v` prefix).
  - Step 4: Run unit tests (`dotnet test`).
  - Step 5: Publish WinFlow as single-file self-contained win-x64 executable (`dotnet publish`).
  - Step 6: Package portable standalone assets (`WinFlow-$VERSION-win-x64.exe` and `WinFlow-$VERSION-Portable.zip`).
  - Step 7: Install Inno Setup (`choco install innosetup --no-progress`) and compile installer (`iscc installer/winflow.iss /DAppVersion=$VERSION`).
  - Step 8: Generate SHA-256 checksums file (`SHA256SUMS.txt`).
  - Step 9: Create GitHub Release and upload all assets using `softprops/action-gh-release@v2`.

---

### Task 3: Local Verification and Documentation

**Files:**
- Modify: `README.md` (add release & packaging documentation)

**Steps:**
- [ ] Validate `.github/workflows/release.yml` YAML syntax.
- [ ] Validate PowerShell commands used in the workflow locally (tag stripping, publishing flags, zip compression, hash generation).
- [ ] Update `README.md` to document how to trigger releases via git tag or GitHub Actions UI.
- [ ] Review git status and ensure clean working tree.
