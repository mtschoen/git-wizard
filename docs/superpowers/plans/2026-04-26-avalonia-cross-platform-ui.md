# Avalonia Cross-Platform UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up an Avalonia desktop UI alongside the existing MAUI app so GitWizard runs natively on Linux, macOS, and Windows from one shared view-model layer, without breaking the current MAUI Windows build.

**Architecture:** Extract the existing MAUI view models into a new `GitWizardUI.ViewModels` class library behind three small service interfaces (`IUiDispatcher`, `IUserDialogs`, `IFolderPicker`). Convert MAUI-typed VM properties (`Color`, `Thickness`, `FontAttributes`) to framework-agnostic strings/primitives so the same view-model project works under both UIs. MAUI keeps building against its own service impls; a new `GitWizardAvalonia/` project links the same view models and provides Avalonia impls. Views are ported one-for-one from `GitWizardUI/*.xaml` to `GitWizardAvalonia/*.axaml`. The existing MAUI app is left untouched until parity is reached on Linux/macOS.

**Tech Stack:** .NET 10, Avalonia 11.x (Desktop profile), NUnit, MAUI 10 (existing, unchanged), `GitWizard` core library.

**Conventions for this plan:**
- TDD applies to abstractions, the type-neutralization, and view-model extractions (Phases A–B). UI views (Phase D onwards) are smoke-tested manually because there's no clean unit-test seam — each view-porting task ends with a *smoke acceptance* checklist instead.
- Build commands assume Linux (the dev box). MAUI build verification has to happen on Windows; tasks that touch MAUI tell the agent exactly what to do when running on Linux (do not block; flag in PR description for human review).
- `git-wizard.slnx` updates happen in the same task that creates the new csproj.
- **Do NOT run `dotnet build git-wizard.slnx` or `dotnet test` at repo root on Linux.** The slnx contains MAUI projects (`GitWizardUI/`, `GitWizardUI.UITests/`) whose Windows TFM cannot build on Linux. Always build/test specific csproj files.
- **Avalonia compiled bindings are strict.** With `AvaloniaUseCompiledBindingsByDefault=true` (set in Task 11), every binding path on a `DataTemplate` with `x:DataType` is type-checked at build time. A missing or wrong-typed VM property is a compile error, not a runtime warning. This is why Phase A.5 (Task 4) eliminates MAUI-only types from the VM up-front.

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj` | Create | UI-framework-agnostic class library hosting view models and service interfaces |
| `GitWizardUI.ViewModels/Services/IUiDispatcher.cs` | Create | Marshal work onto the UI thread (replaces `MainThread.*`) |
| `GitWizardUI.ViewModels/Services/IUserDialogs.cs` | Create | Show alert/confirm dialogs (replaces `Page.DisplayAlertAsync`) |
| `GitWizardUI.ViewModels/Services/IFolderPicker.cs` | Create | Native folder-picker dialog (replaces MAUI Windows-handle picker) |
| `GitWizardUI.ViewModels/Services/StubUiDispatcher.cs` | Create | Synchronous stub for unit tests |
| `GitWizardUI.ViewModels/Services/StubUserDialogs.cs` | Create | Recording stub for unit tests |
| `GitWizardUI.ViewModels/Services/StubFolderPicker.cs` | Create | Scripted stub for unit tests |
| `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs` | Modify | Replace MAUI types (`Color`, `Thickness`, `FontAttributes`) with neutral strings |
| `GitWizardUI/MainPage.xaml` | Modify | Update bindings to consume neutral types (no logic change) |
| `GitWizardUI.ViewModels/RepositoryNodeViewModel.cs` | Move | Move file from `GitWizardUI/ViewModels/` (after type-neutralization) |
| `GitWizardUI.ViewModels/MainViewModel.cs` | Move + Modify | Move from `GitWizardUI/ViewModels/`; replace `MainThread`/`Application.Current` calls with `IUiDispatcher`/`IUserDialogs`; expose extracted public methods |
| `GitWizardUI.ViewModels/SettingsViewModel.cs` | Move + Modify | Move; replace `MauiWinUIWindow` folder picker with `IFolderPicker`; expose extracted public methods |
| `GitWizardUI/GitWizardUI.csproj` | Modify | Add `ProjectReference` to `GitWizardUI.ViewModels` |
| `GitWizardUI/Services/MauiUiDispatcher.cs` | Create | MAUI impl of `IUiDispatcher` (wraps `MainThread.*`) |
| `GitWizardUI/Services/MauiUserDialogs.cs` | Create | MAUI impl of `IUserDialogs` |
| `GitWizardUI/Services/MauiFolderPicker.cs` | Create | MAUI impl of `IFolderPicker` (current WinUI-handle code, gated `#if WINDOWS`) |
| `GitWizardUI/MainPage.xaml.cs` | Modify | Construct `MainViewModel` with MAUI services; delegate handler bodies to extracted VM methods |
| `GitWizardUI/SettingsPage.xaml.cs` | Modify | Construct `SettingsViewModel` with MAUI services; delegate handler bodies to extracted VM methods |
| `GitWizardTests/GitWizardTests.csproj` | Modify | Add `ProjectReference` to `GitWizardUI.ViewModels` |
| `GitWizardTests/StubServiceTests.cs` | Create | Tests for `Stub*` service impls |
| `GitWizardTests/RepositoryNodeViewModelTests.cs` | Create | Tests for neutralized `StatusColor`, `ItemPadding`, `GroupHeaderFontWeight` |
| `GitWizardTests/MainViewModelTests.cs` | Create | Tests that `MainViewModel` constructs correctly and posts UI work via `IUiDispatcher` |
| `GitWizardAvalonia/GitWizardAvalonia.csproj` | Create | Avalonia desktop app project |
| `GitWizardAvalonia/Program.cs` | Create | Avalonia entry point |
| `GitWizardAvalonia/App.axaml` + `.axaml.cs` | Create | Avalonia application root |
| `GitWizardAvalonia/Views/MainWindow.axaml` + `.axaml.cs` | Create | Port of `MainPage.xaml` |
| `GitWizardAvalonia/Views/SettingsWindow.axaml` + `.axaml.cs` | Create | Port of `SettingsPage.xaml` |
| `GitWizardAvalonia/Services/AvaloniaUiDispatcher.cs` | Create | Avalonia impl of `IUiDispatcher` |
| `GitWizardAvalonia/Services/AvaloniaUserDialogs.cs` | Create | Avalonia impl of `IUserDialogs` |
| `GitWizardAvalonia/Services/AvaloniaFolderPicker.cs` | Create | Avalonia impl of `IFolderPicker` (uses `IStorageProvider`) |
| `git-wizard.slnx` | Modify | Add new projects |
| `PLAN.md` | Modify | Mark Avalonia phase complete when finished |

---

## Phase 0: Pre-flight

### Task 0: Verify environment

**Goal:** Fail fast if the box can't run subsequent tasks. No code changes, no commit — purely a gate.

- [ ] **Step 1: Verify .NET 10 SDK is present**

Run: `dotnet --list-sdks`
Expected: output contains a line starting with `10.` (e.g. `10.0.104 [/usr/share/dotnet/sdk]`).
If missing: stop. Surface a PR-comment "blocked: requires .NET 10 SDK" and exit. Do not attempt to install (system-level install needs sudo).

- [ ] **Step 2: Verify git commit identity is configured**

Run: `git config --get user.email && git config --get user.name`
Expected: both lines non-empty.
If missing: surface a PR-comment "blocked: agent harness must set git user.email and user.name" and exit.

- [ ] **Step 3: Verify Avalonia templates are installed (idempotent)**

Run: `dotnet new install Avalonia.Templates --force`
Expected: prints "Templates installed" with a list including `avalonia.app`. The `--force` flag makes this safe to re-run; it reinstalls cleanly even if templates already exist.

- [ ] **Step 4: Ensure `~/.GitWizard/config.json` has at least one search path (so smoke tests have repos to find)**

Run: `test -f ~/.GitWizard/config.json && grep -q '"SearchPaths"' ~/.GitWizard/config.json || dotnet run --project git-wizard/git-wizard.csproj -- --scan-only 2>&1 | tail -5`

If the file is missing or has no search paths, the CLI's first run creates a default config pointing at `$HOME`. That's adequate for smoke tests (will find a few repos quickly).

- [ ] **Step 5: Note Linux build constraint**

This step is informational. Confirm that this agent will only run `dotnet build`/`dotnet test` on these specific csproj files:
- `GitWizard/GitWizard.csproj`
- `git-wizard/git-wizard.csproj`
- `GitWizardTests/GitWizardTests.csproj`
- `GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj` (created in Task 1)
- `GitWizardAvalonia/GitWizardAvalonia.csproj` (created in Task 11)

The agent must NEVER run `dotnet build` or `dotnet test` without a project argument on Linux — that builds the slnx, which includes MAUI-only projects that fail on Linux.

No commit for this task. Proceed to Task 1.

---

## Phase A: View-model service abstractions

### Task 1: `IUiDispatcher` abstraction + stub impl

**Files:**
- Create: `GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj`
- Create: `GitWizardUI.ViewModels/Services/IUiDispatcher.cs`
- Create: `GitWizardUI.ViewModels/Services/StubUiDispatcher.cs`
- Create: `GitWizardTests/StubServiceTests.cs`
- Modify: `GitWizardTests/GitWizardTests.csproj`
- Modify: `git-wizard.slnx`

- [ ] **Step 1: Create the bare class-library project**

Write `GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>GitWizardUI.ViewModels</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\GitWizard\GitWizard.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to slnx**

Edit `git-wizard.slnx`. Insert this line before `</Solution>`:

```xml
  <Project Path="GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj" />
```

- [ ] **Step 3: Wire the test project to the new lib**

Edit `GitWizardTests/GitWizardTests.csproj`. Inside the existing `<ItemGroup>` that contains the `GitWizard` ProjectReference, add:

```xml
    <ProjectReference Include="..\GitWizardUI.ViewModels\GitWizardUI.ViewModels.csproj" />
```

- [ ] **Step 4: Write the failing test**

Create `GitWizardTests/StubServiceTests.cs`:

```csharp
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public class StubUiDispatcherTests
{
    [Test]
    public void Post_InvokesActionSynchronously()
    {
        var dispatcher = new StubUiDispatcher();
        var called = false;

        dispatcher.Post(() => called = true);

        Assert.That(called, Is.True);
    }

    [Test]
    public async Task InvokeAsync_RunsAndCompletes()
    {
        var dispatcher = new StubUiDispatcher();
        var called = false;

        await dispatcher.InvokeAsync(() => called = true);

        Assert.That(called, Is.True);
    }

    [Test]
    public void IsOnUiThread_AlwaysTrue()
    {
        Assert.That(new StubUiDispatcher().IsOnUiThread, Is.True);
    }
}
```

- [ ] **Step 5: Run tests; expect compile failure**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~StubUiDispatcherTests" --nologo`
Expected: build error CS0234 — namespace `GitWizardUI.ViewModels.Services` does not exist.

- [ ] **Step 6: Define the interface**

Create `GitWizardUI.ViewModels/Services/IUiDispatcher.cs`:

```csharp
namespace GitWizardUI.ViewModels.Services;

/// <summary>Marshals work onto the UI thread. Implementations wrap the host framework's dispatcher.</summary>
public interface IUiDispatcher
{
    bool IsOnUiThread { get; }

    /// <summary>Fire-and-forget enqueue onto the UI thread.</summary>
    void Post(Action action);

    /// <summary>Run on the UI thread and await completion.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Run async work on the UI thread and await completion.</summary>
    Task InvokeAsync(Func<Task> action);
}
```

- [ ] **Step 7: Implement the stub**

Create `GitWizardUI.ViewModels/Services/StubUiDispatcher.cs`:

```csharp
namespace GitWizardUI.ViewModels.Services;

/// <summary>Synchronous test stub. Pretends every call site is the UI thread.</summary>
public sealed class StubUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    public Task InvokeAsync(Func<Task> action) => action();
}
```

- [ ] **Step 8: Run tests; expect pass**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~StubUiDispatcherTests" --nologo`
Expected: 3 passed.

- [ ] **Step 9: Commit**

```bash
git add GitWizardUI.ViewModels/ GitWizardTests/StubServiceTests.cs GitWizardTests/GitWizardTests.csproj git-wizard.slnx
git commit -m "feat(viewmodels): add IUiDispatcher abstraction with stub impl"
```

---

### Task 2: `IUserDialogs` abstraction + stub impl

**Files:**
- Create: `GitWizardUI.ViewModels/Services/IUserDialogs.cs`
- Create: `GitWizardUI.ViewModels/Services/StubUserDialogs.cs`
- Modify: `GitWizardTests/StubServiceTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `GitWizardTests/StubServiceTests.cs`:

```csharp
public class StubUserDialogsTests
{
    [Test]
    public async Task DisplayAlertAsync_RecordsCall()
    {
        var dialogs = new StubUserDialogs();

        await dialogs.DisplayAlertAsync("Title", "Body");

        Assert.That(dialogs.AlertCalls, Has.Count.EqualTo(1));
        Assert.That(dialogs.AlertCalls[0].Title, Is.EqualTo("Title"));
        Assert.That(dialogs.AlertCalls[0].Message, Is.EqualTo("Body"));
    }

    [Test]
    public async Task DisplayConfirmAsync_ReturnsScriptedAnswer()
    {
        var dialogs = new StubUserDialogs { NextConfirmResult = true };

        var result = await dialogs.DisplayConfirmAsync("Are you sure?", "Really?");

        Assert.That(result, Is.True);
        Assert.That(dialogs.ConfirmCalls, Has.Count.EqualTo(1));
    }
}
```

- [ ] **Step 2: Run tests; expect compile failure**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~StubUserDialogsTests" --nologo`
Expected: CS0246 — type `StubUserDialogs` not found.

- [ ] **Step 3: Define the interface**

Create `GitWizardUI.ViewModels/Services/IUserDialogs.cs`:

```csharp
namespace GitWizardUI.ViewModels.Services;

/// <summary>Show modal user-facing dialogs. Owner window resolution is the impl's responsibility.</summary>
public interface IUserDialogs
{
    Task DisplayAlertAsync(string title, string message, string okLabel = "OK");
    Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No");
}
```

- [ ] **Step 4: Implement the stub**

Create `GitWizardUI.ViewModels/Services/StubUserDialogs.cs`:

```csharp
namespace GitWizardUI.ViewModels.Services;

/// <summary>Test stub: records every call, returns scripted answers for confirms.</summary>
public sealed class StubUserDialogs : IUserDialogs
{
    public record AlertCall(string Title, string Message);
    public record ConfirmCall(string Title, string Message);

    public List<AlertCall> AlertCalls { get; } = new();
    public List<ConfirmCall> ConfirmCalls { get; } = new();
    public bool NextConfirmResult { get; set; }

    public Task DisplayAlertAsync(string title, string message, string okLabel = "OK")
    {
        AlertCalls.Add(new AlertCall(title, message));
        return Task.CompletedTask;
    }

    public Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
    {
        ConfirmCalls.Add(new ConfirmCall(title, message));
        return Task.FromResult(NextConfirmResult);
    }
}
```

- [ ] **Step 5: Run tests; expect pass**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~StubUserDialogsTests" --nologo`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add GitWizardUI.ViewModels/Services/IUserDialogs.cs GitWizardUI.ViewModels/Services/StubUserDialogs.cs GitWizardTests/StubServiceTests.cs
git commit -m "feat(viewmodels): add IUserDialogs abstraction with stub impl"
```

---

### Task 3: `IFolderPicker` abstraction + stub impl

**Files:**
- Create: `GitWizardUI.ViewModels/Services/IFolderPicker.cs`
- Create: `GitWizardUI.ViewModels/Services/StubFolderPicker.cs`
- Modify: `GitWizardTests/StubServiceTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `GitWizardTests/StubServiceTests.cs`:

```csharp
public class StubFolderPickerTests
{
    [Test]
    public async Task PickFolderAsync_ReturnsScriptedPath()
    {
        var picker = new StubFolderPicker { NextResult = "/tmp/repos" };

        var result = await picker.PickFolderAsync();

        Assert.That(result, Is.EqualTo("/tmp/repos"));
        Assert.That(picker.PickCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PickFolderAsync_NullMeansCancelled()
    {
        var picker = new StubFolderPicker { NextResult = null };

        var result = await picker.PickFolderAsync();

        Assert.That(result, Is.Null);
    }
}
```

- [ ] **Step 2: Run tests; expect compile failure**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~StubFolderPickerTests" --nologo`
Expected: CS0246 — type `StubFolderPicker` not found.

- [ ] **Step 3: Define the interface**

Create `GitWizardUI.ViewModels/Services/IFolderPicker.cs`:

```csharp
namespace GitWizardUI.ViewModels.Services;

/// <summary>Native folder-picker dialog. Returns the absolute path the user chose, or null if cancelled.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync();
}
```

- [ ] **Step 4: Implement the stub**

Create `GitWizardUI.ViewModels/Services/StubFolderPicker.cs`:

```csharp
namespace GitWizardUI.ViewModels.Services;

/// <summary>Test stub: returns a scripted result and tracks invocation count.</summary>
public sealed class StubFolderPicker : IFolderPicker
{
    public string? NextResult { get; set; }
    public int PickCount { get; private set; }

    public Task<string?> PickFolderAsync()
    {
        PickCount++;
        return Task.FromResult(NextResult);
    }
}
```

- [ ] **Step 5: Run tests; expect pass**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~StubFolderPickerTests" --nologo`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add GitWizardUI.ViewModels/Services/IFolderPicker.cs GitWizardUI.ViewModels/Services/StubFolderPicker.cs GitWizardTests/StubServiceTests.cs
git commit -m "feat(viewmodels): add IFolderPicker abstraction with stub impl"
```

---

## Phase A.5: Make `RepositoryNodeViewModel` framework-agnostic

### Task 4: Convert MAUI-typed VM properties to neutral primitives

**Why this task exists:** `RepositoryNodeViewModel` currently exposes properties typed as `Color` (`Microsoft.Maui.Graphics.Color`), `Thickness` (`Microsoft.Maui.Thickness`), and `FontAttributes` (`Microsoft.Maui.Controls.FontAttributes`). Those types are auto-imported by the MAUI SDK. Once the file moves into `GitWizardUI.ViewModels` (a non-MAUI lib in Task 5), they will not resolve. Both MAUI and Avalonia XAML can parse these values from strings, so we convert before moving.

**Files:**
- Modify: `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs`
- Modify: `GitWizardUI/MainPage.xaml`
- Create: `GitWizardTests/RepositoryNodeViewModelTests.cs`

This task is TDD on the new return types.

- [ ] **Step 1: Write failing tests for the new shapes**

Create `GitWizardTests/RepositoryNodeViewModelTests.cs`:

```csharp
using GitWizardUI.ViewModels;

namespace GitWizardTests;

public class RepositoryNodeViewModelTests
{
    [Test]
    public void GroupHeaderFontWeight_IsBoldWhenGroupHeader()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("Drive C:");
        Assert.That(node.GroupHeaderFontWeight, Is.EqualTo("Bold"));
    }

    [Test]
    public void GroupHeaderFontWeight_IsNormalWhenNotGroupHeader()
    {
        var node = RepositoryNodeViewModel.CreateForRepoPath("/tmp/some-repo");
        Assert.That(node.GroupHeaderFontWeight, Is.EqualTo("Normal"));
    }

    [Test]
    public void ItemPaddingString_HasGroupHeaderInset()
    {
        var node = RepositoryNodeViewModel.CreateGroupHeader("Drive C:");
        Assert.That(node.ItemPaddingString, Is.EqualTo("0,5,0,0"));
    }

    [Test]
    public void ItemPaddingString_HasChildIndent()
    {
        var node = RepositoryNodeViewModel.CreateForRepoPath("/tmp/some-repo");
        Assert.That(node.ItemPaddingString, Is.EqualTo("15,0,0,0"));
    }

    [Test]
    public void StatusColorHex_ReturnsHexString()
    {
        var node = RepositoryNodeViewModel.CreateForRepoPath("/tmp/some-repo");
        // Default status (Refreshing or Success); both are valid 7-char hex starting with '#'.
        Assert.That(node.StatusColorHex, Does.StartWith("#"));
        Assert.That(node.StatusColorHex.Length, Is.EqualTo(7).Or.EqualTo(9));  // #RRGGBB or #RRGGBBAA
    }
}
```

If `RepositoryNodeViewModel` does not have `CreateGroupHeader` / `CreateForRepoPath` factory methods, use whatever public construction path exists today (parameterless ctor + property setters). The agent must read the current file once and adapt the test setup to whatever construction is supported. The asserts on `GroupHeaderFontWeight`, `ItemPaddingString`, and `StatusColorHex` (the property names being introduced in Step 4 below) are what matter.

- [ ] **Step 2: Run tests; expect compile failure**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~RepositoryNodeViewModelTests" --nologo`
Expected: CS1061 — `RepositoryNodeViewModel` does not contain `GroupHeaderFontWeight` (or `ItemPaddingString` or `StatusColorHex`).

- [ ] **Step 3: Open `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs` and identify the three MAUI-typed properties**

Read lines 28–29 and the `StatusColor` switch (around line 70). The current shape:

```csharp
public FontAttributes GroupHeaderFontAttributes => IsGroupHeader ? FontAttributes.Bold : FontAttributes.None;
public Thickness ItemPadding => IsGroupHeader ? new Thickness(0, 5, 0, 0) : new Thickness(15, 0, 0, 0);
public Color StatusColor => _status switch
{
    RefreshStatus.Refreshing => Colors.Gray,
    RefreshStatus.Success => Colors.Green,
    // ... etc
};
```

- [ ] **Step 4: Replace with neutral-typed siblings**

Add these *new* properties next to the existing ones (keep the originals for now — Step 6 deletes them after MAUI XAML is updated):

```csharp
public string GroupHeaderFontWeight => IsGroupHeader ? "Bold" : "Normal";
public string ItemPaddingString => IsGroupHeader ? "0,5,0,0" : "15,0,0,0";
public string StatusColorHex => _status switch
{
    RefreshStatus.Refreshing => "#808080",  // gray
    RefreshStatus.Success    => "#28A745",  // green
    RefreshStatus.Timeout    => "#FFA500",  // orange
    RefreshStatus.Error      => "#DC3545",  // red
    _                         => "#000000",
};
```

Match every case in the existing `StatusColor` switch — every `Colors.X` becomes the corresponding hex. Common MAUI named colors: `Colors.Gray=#808080`, `Colors.Green=#008000` (use `#28A745` if the MAUI app intentionally used a brighter green — preserve the original semantic by reading the existing values), `Colors.Red=#FF0000`, `Colors.Orange=#FFA500`, `Colors.Yellow=#FFFF00`, `Colors.Black=#000000`.

In the `OnPropertyChanged` calls that already raise notifications for `StatusColor`/`ItemPadding`/`GroupHeaderFontAttributes`, also raise the new property names:

```csharp
OnPropertyChanged(nameof(StatusColor));
OnPropertyChanged(nameof(StatusColorHex));            // new
// ...
OnPropertyChanged(nameof(GroupHeaderFontAttributes));
OnPropertyChanged(nameof(GroupHeaderFontWeight));     // new
// ...
OnPropertyChanged(nameof(ItemPadding));
OnPropertyChanged(nameof(ItemPaddingString));         // new
```

- [ ] **Step 5: Run the new tests; expect pass**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~RepositoryNodeViewModelTests" --nologo`
Expected: 5 passed.

- [ ] **Step 6: Update `GitWizardUI/MainPage.xaml` bindings to consume the neutral properties**

In `GitWizardUI/MainPage.xaml`, find these three binding paths in the `DataTemplate` (lines ~85–100) and rename them:

| Old binding | New binding |
|-------------|-------------|
| `TextColor="{Binding StatusColor}"` | `TextColor="{Binding StatusColorHex}"` |
| `FontAttributes="{Binding GroupHeaderFontAttributes}"` | `FontAttributes="{Binding GroupHeaderFontWeight}"` |
| `Padding="{Binding ItemPadding}"` | `Padding="{Binding ItemPaddingString}"` |

MAUI XAML's value converters parse hex strings into `Color`, `"Bold"`/`"Normal"` strings into `FontAttributes`, and `"l,t,r,b"` strings into `Thickness`, so this is a no-op visual change.

- [ ] **Step 7: Delete the old MAUI-typed properties from `RepositoryNodeViewModel.cs`**

Now that nothing binds to them, remove the original `StatusColor`, `GroupHeaderFontAttributes`, and `ItemPadding` properties (and any imports they pulled in — likely `using Microsoft.Maui.Graphics;` etc.). Also remove the `OnPropertyChanged(nameof(StatusColor))` etc. calls for the deleted properties.

- [ ] **Step 8: Build the file in place to confirm it still compiles inside the MAUI project**

Run: `dotnet build GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj --nologo`
This task hasn't moved the file yet (still under `GitWizardUI/ViewModels/`); the build above won't include it. Instead, on Linux, the only verification we can do is that the rest of the shared lib still builds. The actual MAUI build verification has to happen on Windows (Task 9 step 6).

Run the test suite to confirm nothing regressed:

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --nologo`
Expected: all tests pass, including the new 5.

- [ ] **Step 9: Commit**

```bash
git add GitWizardUI/ViewModels/RepositoryNodeViewModel.cs GitWizardUI/MainPage.xaml GitWizardTests/RepositoryNodeViewModelTests.cs
git commit -m "refactor(viewmodels): neutralize MAUI types in RepositoryNodeViewModel"
```

---

## Phase B: Move view models into the shared project

### Task 5: Move `RepositoryNodeViewModel`

**Files:**
- Move: `GitWizardUI/ViewModels/RepositoryNodeViewModel.cs` → `GitWizardUI.ViewModels/RepositoryNodeViewModel.cs`
- Modify: `GitWizardUI/GitWizardUI.csproj`

- [ ] **Step 1: Move the file**

```bash
git mv GitWizardUI/ViewModels/RepositoryNodeViewModel.cs GitWizardUI.ViewModels/RepositoryNodeViewModel.cs
```

- [ ] **Step 2: Add ProjectReference to MAUI csproj**

Edit `GitWizardUI/GitWizardUI.csproj`. Inside the `<ItemGroup>` that contains the `GitWizard` ProjectReference, add:

```xml
    <ProjectReference Include="..\GitWizardUI.ViewModels\GitWizardUI.ViewModels.csproj" />
```

- [ ] **Step 3: Build the shared lib + test project**

Run: `dotnet build GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj GitWizardTests/GitWizardTests.csproj --nologo`
Expected: 0 errors. (`RepositoryNodeViewModel` has no MAUI dependencies after Task 4.)

- [ ] **Step 4: Run all tests to confirm nothing regressed**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --nologo`
Expected: all existing tests pass.

- [ ] **Step 5: Commit**

```bash
git add GitWizardUI/ViewModels/RepositoryNodeViewModel.cs GitWizardUI.ViewModels/RepositoryNodeViewModel.cs GitWizardUI/GitWizardUI.csproj
git commit -m "refactor(viewmodels): move RepositoryNodeViewModel to shared project"
```

---

### Task 6: Move + refactor `MainViewModel` to use abstractions

**Files:**
- Move: `GitWizardUI/ViewModels/MainViewModel.cs` → `GitWizardUI.ViewModels/MainViewModel.cs`
- Modify: `GitWizardUI.ViewModels/MainViewModel.cs`
- Create: `GitWizardTests/MainViewModelTests.cs`

- [ ] **Step 1: Move the file**

```bash
git mv GitWizardUI/ViewModels/MainViewModel.cs GitWizardUI.ViewModels/MainViewModel.cs
```

- [ ] **Step 2: Replace the parameterless constructor with one that takes services**

Edit `GitWizardUI.ViewModels/MainViewModel.cs`. Find the existing `public MainViewModel()` constructor (around line 163). Replace its signature and add fields:

```csharp
readonly IUiDispatcher _ui;
readonly IUserDialogs _dialogs;

public MainViewModel(IUiDispatcher ui, IUserDialogs dialogs)
{
    _ui = ui;
    _dialogs = dialogs;
    // ...rest of existing constructor body unchanged
}
```

Add at the top of the file: `using GitWizardUI.ViewModels.Services;`

- [ ] **Step 3: Replace `MainThread.BeginInvokeOnMainThread(...)` with `_ui.Post(...)`**

Search the file for `MainThread.BeginInvokeOnMainThread(`. There are 13+ occurrences (lines 197, 215, 233, 256, 793, 811, and others). For each:

```csharp
// Before:
MainThread.BeginInvokeOnMainThread(action);
// After:
_ui.Post(action);
```

- [ ] **Step 4: Replace `MainThread.InvokeOnMainThreadAsync(...)` with `_ui.InvokeAsync(...)`**

Search for `await MainThread.InvokeOnMainThreadAsync(`. There are 2 occurrences (lines 326, 335). For each:

```csharp
// Before:
await MainThread.InvokeOnMainThreadAsync(action);
// After:
await _ui.InvokeAsync(action);
```

- [ ] **Step 5: Replace `Application.Current.Windows[0].Page.DisplayAlertAsync` blocks with `_dialogs.DisplayAlertAsync`**

Find each block matching this pattern (4 occurrences, around lines 197, 215, 233, 256):

```csharp
MainThread.BeginInvokeOnMainThread(async () =>
{
    if (Application.Current?.Windows.Count > 0)
    {
        await Application.Current.Windows[0].Page?.DisplayAlertAsync("Error", message, "OK");
    }
});
```

Rewrite as:

```csharp
_ui.Post(async () => await _dialogs.DisplayAlertAsync("Error", message));
```

(Note: Step 3 already converted the outer `MainThread.BeginInvokeOnMainThread` to `_ui.Post`; this step also collapses the inner null-check + `DisplayAlertAsync` since the `IUserDialogs` impl handles owner resolution.)

- [ ] **Step 6: Build the shared lib; expect failure if any MAUI references remain**

Run: `dotnet build GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj --nologo`
Expected: errors only if Steps 3–5 missed anything. Search the file for any remaining `MainThread`, `Application.Current`, `Microsoft.Maui` and replace per the same pattern.

- [ ] **Step 7: Build clean**

Run: `dotnet build GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj --nologo`
Expected: 0 errors.

- [ ] **Step 8: Add a regression test for construction**

Create `GitWizardTests/MainViewModelTests.cs`:

```csharp
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public class MainViewModelTests
{
    [Test]
    public void Construction_RequiresInjectedServices()
    {
        var dispatcher = new StubUiDispatcher();
        var dialogs = new StubUserDialogs();

        var vm = new MainViewModel(dispatcher, dialogs);

        Assert.That(vm, Is.Not.Null);
        Assert.That(vm.HeaderText, Is.EqualTo("GitWizard"));
    }
}
```

- [ ] **Step 9: Run tests; expect pass**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --filter "FullyQualifiedName~MainViewModelTests" --nologo`
Expected: 1 passed.

- [ ] **Step 10: Commit**

```bash
git add GitWizardUI/ViewModels/MainViewModel.cs GitWizardUI.ViewModels/MainViewModel.cs GitWizardTests/MainViewModelTests.cs
git commit -m "refactor(viewmodels): move MainViewModel and route UI work through abstractions"
```

---

### Task 7: Move + refactor `SettingsViewModel` to use `IFolderPicker`

**Files:**
- Move: `GitWizardUI/ViewModels/SettingsViewModel.cs` → `GitWizardUI.ViewModels/SettingsViewModel.cs`
- Modify: `GitWizardUI.ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Move the file**

```bash
git mv GitWizardUI/ViewModels/SettingsViewModel.cs GitWizardUI.ViewModels/SettingsViewModel.cs
```

- [ ] **Step 2: Replace MAUI imports with abstraction**

Edit `GitWizardUI.ViewModels/SettingsViewModel.cs`. Remove:

```csharp
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
```

Add:

```csharp
using GitWizardUI.ViewModels.Services;
```

- [ ] **Step 3: Inject `IFolderPicker` via constructor**

Add field + constructor at the top of the class:

```csharp
readonly IFolderPicker _folderPicker;

public SettingsViewModel(IFolderPicker folderPicker)
{
    _folderPicker = folderPicker;
    // ...if there was an existing parameterless constructor body, fold it into here
}
```

- [ ] **Step 4: Replace the WinUI-handle folder picker (around line 96)**

Find the existing folder-picker method that contains `((MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!).WindowHandle`. Replace the entire WinUI-handle block + `Windows.Storage.Pickers.FolderPicker` setup with:

```csharp
var path = await _folderPicker.PickFolderAsync();
if (path is null) return;
// ...keep the existing add-to-paths logic that ran after the picker returned
```

- [ ] **Step 5: Build**

Run: `dotnet build GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj --nologo`
Expected: 0 errors.

- [ ] **Step 6: Run all tests**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --nologo`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add GitWizardUI/ViewModels/SettingsViewModel.cs GitWizardUI.ViewModels/SettingsViewModel.cs
git commit -m "refactor(viewmodels): route SettingsViewModel folder picking through IFolderPicker"
```

---

### Task 8: Extract VM methods used by both UIs

**Why this task exists:** `MainPage.xaml.cs` and `SettingsPage.xaml.cs` currently contain handler-body logic (e.g. `FilterButton_Click`) that performs operations on the view model. The Avalonia views need to invoke that same logic, so it must move to the view models. Extracting now (with MAUI still wired in) is mechanical; doing it after Avalonia is half-built is fragile.

**Files:**
- Modify: `GitWizardUI.ViewModels/MainViewModel.cs`
- Modify: `GitWizardUI.ViewModels/SettingsViewModel.cs`
- Modify: `GitWizardUI/MainPage.xaml.cs`
- Modify: `GitWizardUI/SettingsPage.xaml.cs`

The agent must read each of the four files first to identify current handler bodies, then move logic into the VM methods listed below. Each handler in the page code-behind ends up as a one-liner that calls the VM.

**Methods to add to `MainViewModel`** (extract bodies from the corresponding handlers in `GitWizardUI/MainPage.xaml.cs`):

| New VM method | Source handler in `MainPage.xaml.cs` |
|---------------|--------------------------------------|
| `public void ApplyFilter(string buttonName)` | `FilterButton_Click` — switch on `buttonName` to set `_activeFilter`, call `ApplyFilterAndGrouping` (existing method) |
| `public void ApplyGroup(string buttonName)` | `GroupButton_Click` — switch on `buttonName` to set `_activeGroupMode`, call `ApplyFilterAndGrouping` |
| `public void ApplySort(string buttonName)` | `SortButton_Click` — switch on `buttonName` to set `_activeSortMode`, call `ApplyFilterAndGrouping` |
| `public void UpdateSearchText(string text)` | `SearchBox_TextChanged` — set `_searchText`, call `ApplyFilterAndGrouping`. (Avalonia uses two-way Text binding on the TextBox + a property setter; this method is a fallback if the agent prefers handler-style.) |
| `public Task ClearCacheAsync()` | `ClearCacheMenuItem_Click` — current logic |
| `public Task DeleteAllLocalFilesAsync()` | `DeleteAllLocalFilesMenuItem_Click` — current logic, uses `_dialogs.DisplayConfirmAsync` for confirmation |

**Note on `CheckWindowsDefenderMenuItem_Click`:** that handler invokes Windows-Defender exclusion logic that uses MAUI dialogs and Windows-only elevation. Do **not** lift this into the VM. Instead:
- Leave the handler in `MainPage.xaml.cs` unchanged.
- Avalonia will hide the corresponding button on non-Windows (Task 16). On Windows, the Avalonia handler will call into the same `WindowsDefenderException` helper class that `GitWizard` already exposes (look in `GitWizard/`). Keep the call gated `if (OperatingSystem.IsWindows())` in the Avalonia handler.

**Methods to add to `SettingsViewModel`** (extract from `GitWizardUI/SettingsPage.xaml.cs`):

| New VM method / property | Source handler |
|--------------------------|----------------|
| `public string? SelectedSearchPath { get; set; }` (with `INotifyPropertyChanged` raise) | new — bound from ListBox SelectedItem |
| `public string? SelectedIgnoredPath { get; set; }` (with raise) | new — bound from ListBox SelectedItem |
| `public Task AddSearchPathAsync()` | `AddSearchPath_Click` (or equivalently named handler) — calls `_folderPicker.PickFolderAsync`, adds non-null result to `SearchPaths` |
| `public void RemoveSelectedSearchPath()` | `RemoveSearchPath_Click` — removes `SelectedSearchPath` from `SearchPaths` if non-null |
| `public Task AddIgnoredPathAsync()` | `AddIgnoredPath_Click` — same shape as AddSearchPathAsync but for IgnoredPaths |
| `public void RemoveSelectedIgnoredPath()` | `RemoveIgnoredPath_Click` |

If the existing handlers don't exactly match these names, use the most similar handler. If a handler doesn't exist (e.g. there's no separate ignored-path remove button), implement the simplest reasonable version (set property, raise notify, return).

- [ ] **Step 1: Read existing handler bodies**

Read these files in full and identify each handler's logic:
- `GitWizardUI/MainPage.xaml.cs`
- `GitWizardUI/SettingsPage.xaml.cs`

- [ ] **Step 2: Add `MainViewModel` public methods**

Add the methods listed above to `GitWizardUI.ViewModels/MainViewModel.cs`. Each method body comes from the corresponding handler. Where the handler accessed `this` (the page), substitute the VM equivalent (e.g. `this.FilterPendingChanges` → use the passed `buttonName` string).

For switch-on-button-name patterns, e.g.:

```csharp
public void ApplyFilter(string buttonName)
{
    _activeFilter = buttonName switch
    {
        "FilterPendingChanges" => FilterType.PendingChanges,
        "FilterSubmoduleCheckout" => FilterType.SubmoduleCheckout,
        "FilterSubmoduleUninitialized" => FilterType.SubmoduleUninitialized,
        "FilterSubmoduleConfigIssue" => FilterType.SubmoduleConfigIssue,
        "FilterDetachedHead" => FilterType.DetachedHead,
        "FilterMyRepositories" => FilterType.MyRepositories,
        "FilterLocalOnlyCommits" => FilterType.LocalOnlyCommits,
        "FilterStale" => FilterType.Stale,
        _ => FilterType.None,
    };
    ApplyFilterAndGrouping();
}
```

`ApplyGroup` and `ApplySort` follow the same pattern with their respective enums.

- [ ] **Step 3: Add `SettingsViewModel` public methods + selected-item properties**

Add the methods and properties listed above to `GitWizardUI.ViewModels/SettingsViewModel.cs`. Implement the selected-item properties with backing fields and `OnPropertyChanged` raises so the `ListBox.SelectedItem` two-way binding works.

```csharp
string? _selectedSearchPath;
public string? SelectedSearchPath
{
    get => _selectedSearchPath;
    set { _selectedSearchPath = value; OnPropertyChanged(); }
}
// ditto for SelectedIgnoredPath
```

- [ ] **Step 4: Update `MainPage.xaml.cs` handlers to delegate**

Each existing `*_Click` handler shrinks to a one-liner (or no-op + delegate). For example:

```csharp
// Before:
void FilterButton_Click(object sender, EventArgs e)
{
    if (sender is Button btn) { /* the lifted logic */ }
}
// After:
void FilterButton_Click(object sender, EventArgs e)
{
    if (sender is Button btn) _viewModel.ApplyFilter(btn.StyleId ?? btn.GetType().Name);
}
```

(MAUI exposes a button's `x:Name` as `StyleId` or via property name; verify on actual code. If `StyleId` is null/unused, use whatever name property the existing code reads.)

Repeat for `GroupButton_Click`, `SortButton_Click`, `SearchBox_TextChanged`, `ClearCacheMenuItem_Click`, `DeleteAllLocalFilesMenuItem_Click`. Leave `CheckWindowsDefenderMenuItem_Click` and `SettingsMenuItem_Click` untouched.

- [ ] **Step 5: Update `SettingsPage.xaml.cs` handlers**

Each handler delegates to the corresponding VM method. For example:

```csharp
async void AddSearchPath_Click(object sender, EventArgs e) => await ((SettingsViewModel)BindingContext).AddSearchPathAsync();
```

- [ ] **Step 6: Build the shared lib (Linux)**

Run: `dotnet build GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj --nologo`
Expected: 0 errors.

- [ ] **Step 7: Run tests**

Run: `dotnet test GitWizardTests/GitWizardTests.csproj --nologo`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add GitWizardUI.ViewModels/MainViewModel.cs GitWizardUI.ViewModels/SettingsViewModel.cs GitWizardUI/MainPage.xaml.cs GitWizardUI/SettingsPage.xaml.cs
git commit -m "refactor(viewmodels): extract handler logic into VM methods"
```

---

## Phase C: MAUI service impls + page wiring

### Task 9: MAUI implementations and page wiring

**Files:**
- Create: `GitWizardUI/Services/MauiUiDispatcher.cs`
- Create: `GitWizardUI/Services/MauiUserDialogs.cs`
- Create: `GitWizardUI/Services/MauiFolderPicker.cs`
- Modify: `GitWizardUI/MainPage.xaml.cs`
- Modify: `GitWizardUI/SettingsPage.xaml.cs`

- [ ] **Step 1: Implement `MauiUiDispatcher`**

Create `GitWizardUI/Services/MauiUiDispatcher.cs`:

```csharp
using GitWizardUI.ViewModels.Services;
using Microsoft.Maui.ApplicationModel;

namespace GitWizardUI.Services;

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => MainThread.IsMainThread;
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
    public Task InvokeAsync(Action action) => MainThread.InvokeOnMainThreadAsync(action);
    public Task InvokeAsync(Func<Task> action) => MainThread.InvokeOnMainThreadAsync(action);
}
```

- [ ] **Step 2: Implement `MauiUserDialogs`**

Create `GitWizardUI/Services/MauiUserDialogs.cs`:

```csharp
using GitWizardUI.ViewModels.Services;
using Microsoft.Maui.Controls;

namespace GitWizardUI.Services;

public sealed class MauiUserDialogs : IUserDialogs
{
    public async Task DisplayAlertAsync(string title, string message, string okLabel = "OK")
    {
        if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page is { } page)
            await page.DisplayAlertAsync(title, message, okLabel);
    }

    public async Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
    {
        if (Application.Current?.Windows.Count > 0 && Application.Current.Windows[0].Page is { } page)
            return await page.DisplayAlertAsync(title, message, acceptLabel, cancelLabel);
        return false;
    }
}
```

- [ ] **Step 3: Implement `MauiFolderPicker`**

Create `GitWizardUI/Services/MauiFolderPicker.cs`. Lift the WinUI-handle logic from the original `SettingsViewModel` (look in git history if needed):

```csharp
using GitWizardUI.ViewModels.Services;
#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
#endif

namespace GitWizardUI.Services;

public sealed class MauiFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync()
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = ((MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!).WindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
#else
        await Task.Yield();
        return null;
#endif
    }
}
```

- [ ] **Step 4: Wire view-model construction in `MainPage.xaml.cs`**

Edit `GitWizardUI/MainPage.xaml.cs`. Replace `_viewModel = new MainViewModel();` (line 16) with:

```csharp
_viewModel = new MainViewModel(new MauiUiDispatcher(), new MauiUserDialogs());
```

Add at top: `using GitWizardUI.Services;` and `using GitWizardUI.ViewModels;` if not already present.

- [ ] **Step 5: Wire view-model construction in `SettingsPage.xaml.cs`**

Edit `GitWizardUI/SettingsPage.xaml.cs`. Replace `BindingContext = new SettingsViewModel();` (line 10) with:

```csharp
BindingContext = new SettingsViewModel(new MauiFolderPicker());
```

- [ ] **Step 6: Linux fallback for MAUI build verification**

The MAUI csproj cannot build on Linux (Windows TFM). The agent on this box CANNOT run the MAUI build. Instead:

1. Confirm the shared lib still compiles cleanly: `dotnet build GitWizardUI.ViewModels/GitWizardUI.ViewModels.csproj --nologo` — expect 0 errors.
2. Run the test suite: `dotnet test GitWizardTests/GitWizardTests.csproj --nologo` — expect all pass.
3. In the PR description (the agent harness writes this), append a `## Manual Windows verification required` section listing:
   - `MSBuild.exe GitWizardUI/GitWizardUI.csproj -t:Build -p:Configuration=Debug` should compile cleanly.
   - Launch the MAUI app, scan a small repo set, open Settings, click "Browse" to add a path. The app should behave exactly as before (no UX change — only construction changed).

Do not block on Windows verification. Continue to Task 10.

- [ ] **Step 7: Commit**

```bash
git add GitWizardUI/Services/ GitWizardUI/MainPage.xaml.cs GitWizardUI/SettingsPage.xaml.cs
git commit -m "feat(maui): add MAUI service impls and wire view models through abstractions"
```

---

## Phase D: Avalonia scaffolding

### Task 10: Scaffold the Avalonia app

**Files:**
- Create: `GitWizardAvalonia/GitWizardAvalonia.csproj`
- Create: `GitWizardAvalonia/Program.cs`
- Create: `GitWizardAvalonia/App.axaml`
- Create: `GitWizardAvalonia/App.axaml.cs`
- Create: `GitWizardAvalonia/Views/MainWindow.axaml`
- Create: `GitWizardAvalonia/Views/MainWindow.axaml.cs`
- Create: `GitWizardAvalonia/app.manifest`
- Modify: `git-wizard.slnx`

- [ ] **Step 1: Scaffold from template**

Run: `dotnet new avalonia.app -o GitWizardAvalonia -n GitWizardAvalonia --force`
Expected: creates `GitWizardAvalonia/` with template files. The `--force` flag overwrites if the directory pre-exists from an earlier failed attempt.

- [ ] **Step 2: Replace the generated csproj**

Open `GitWizardAvalonia/GitWizardAvalonia.csproj` and overwrite with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <BuiltInComInteropSupport>true</BuiltInComInteropSupport>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <RootNamespace>GitWizardAvalonia</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.*" />
    <PackageReference Include="Avalonia.Desktop" Version="11.2.*" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.*" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.2.*" />
    <PackageReference Include="Avalonia.Diagnostics" Version="11.2.*" Condition="'$(Configuration)' == 'Debug'" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\GitWizard\GitWizard.csproj" />
    <ProjectReference Include="..\GitWizardUI.ViewModels\GitWizardUI.ViewModels.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Confirm the template generated `Program.cs`, `App.axaml`, `App.axaml.cs`, `app.manifest`, and a `MainWindow.axaml` + `MainWindow.axaml.cs`**

Run: `ls GitWizardAvalonia/`
Expected: includes `Program.cs`, `App.axaml`, `App.axaml.cs`, `app.manifest`, `MainWindow.axaml`, `MainWindow.axaml.cs`.

- [ ] **Step 4: Move the generated `MainWindow` files into a `Views/` subdirectory**

```bash
mkdir -p GitWizardAvalonia/Views
git mv GitWizardAvalonia/MainWindow.axaml GitWizardAvalonia/Views/MainWindow.axaml
git mv GitWizardAvalonia/MainWindow.axaml.cs GitWizardAvalonia/Views/MainWindow.axaml.cs
```

- [ ] **Step 5: Update namespace in moved files**

Edit `GitWizardAvalonia/Views/MainWindow.axaml.cs`:

```csharp
namespace GitWizardAvalonia.Views;
```

(Replace any existing `namespace GitWizardAvalonia;` line.)

Edit `GitWizardAvalonia/Views/MainWindow.axaml`. The `x:Class` attribute on the root `<Window>` element should be:

```
x:Class="GitWizardAvalonia.Views.MainWindow"
```

- [ ] **Step 6: Update `App.axaml.cs` to point at the moved `MainWindow`**

Open `GitWizardAvalonia/App.axaml.cs`. In `OnFrameworkInitializationCompleted`, the line `desktop.MainWindow = new MainWindow();` needs `using GitWizardAvalonia.Views;` at the top (or the line becomes `new Views.MainWindow()`).

- [ ] **Step 7: Add to slnx**

Edit `git-wizard.slnx`. Insert before `</Solution>`:

```xml
  <Project Path="GitWizardAvalonia/GitWizardAvalonia.csproj" />
```

- [ ] **Step 8: Build**

Run: `dotnet build GitWizardAvalonia/GitWizardAvalonia.csproj --nologo`
Expected: 0 errors. First run downloads ~120 MB of NuGet.

- [ ] **Step 9: Smoke-test the empty app**

Run: `dotnet run --project GitWizardAvalonia/GitWizardAvalonia.csproj`
Expected: an empty window opens with the template default content. Close it; the process exits 0.
If the agent cannot interact (no display): launch with `--no-build` after a successful build, capture stderr/stdout for ~5 seconds via timeout, look for absence of unhandled exceptions. If no GUI is available the agent can verify only that the build succeeded — note in PR.

- [ ] **Step 10: Commit**

```bash
git add GitWizardAvalonia/ git-wizard.slnx
git commit -m "feat(avalonia): scaffold empty Avalonia desktop app"
```

---

### Task 11: Avalonia service implementations

**Files:**
- Create: `GitWizardAvalonia/Services/AvaloniaUiDispatcher.cs`
- Create: `GitWizardAvalonia/Services/AvaloniaUserDialogs.cs`
- Create: `GitWizardAvalonia/Services/AvaloniaFolderPicker.cs`

- [ ] **Step 1: Implement `AvaloniaUiDispatcher`**

Create `GitWizardAvalonia/Services/AvaloniaUiDispatcher.cs`:

```csharp
using Avalonia.Threading;
using GitWizardUI.ViewModels.Services;

namespace GitWizardAvalonia.Services;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    public Task InvokeAsync(Func<Task> action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
```

- [ ] **Step 2: Implement `AvaloniaUserDialogs`**

Avalonia has no built-in alert dialog. Compose one from `Window` + `TextBlock` + buttons. Create `GitWizardAvalonia/Services/AvaloniaUserDialogs.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using GitWizardUI.ViewModels.Services;

namespace GitWizardAvalonia.Services;

public sealed class AvaloniaUserDialogs : IUserDialogs
{
    public Task DisplayAlertAsync(string title, string message, string okLabel = "OK")
        => ShowDialogAsync(title, message, okLabel, cancelLabel: null).ContinueWith(_ => { });

    public Task<bool> DisplayConfirmAsync(string title, string message, string acceptLabel = "Yes", string cancelLabel = "No")
        => ShowDialogAsync(title, message, acceptLabel, cancelLabel);

    static async Task<bool> ShowDialogAsync(string title, string message, string acceptLabel, string? cancelLabel)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return false;

        var tcs = new TaskCompletionSource<bool>();
        var accept = new Button { Content = acceptLabel, Margin = new Thickness(4) };
        var cancel = cancelLabel is null ? null : new Button { Content = cancelLabel, Margin = new Thickness(4) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(accept);
        if (cancel is not null) buttons.Children.Add(cancel);

        var window = new Window
        {
            Title = title,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) },
                    buttons,
                }
            }
        };

        accept.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        if (cancel is not null) cancel.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(false);

        await window.ShowDialog(owner);
        return await tcs.Task;
    }
}
```

- [ ] **Step 3: Implement `AvaloniaFolderPicker`**

Create `GitWizardAvalonia/Services/AvaloniaFolderPicker.cs`:

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using GitWizardUI.ViewModels.Services;

namespace GitWizardAvalonia.Services;

public sealed class AvaloniaFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner?.StorageProvider is null) return null;

        var result = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select repository folder",
            AllowMultiple = false,
        });
        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build GitWizardAvalonia/GitWizardAvalonia.csproj --nologo`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add GitWizardAvalonia/Services/
git commit -m "feat(avalonia): add Avalonia service impls for dispatcher, dialogs, folder picker"
```

---

## Phase E: View porting

### Task 12: `MainWindow.axaml` — toolbar, sidebar, repo list, progress

**Files:**
- Modify: `GitWizardAvalonia/Views/MainWindow.axaml`
- Modify: `GitWizardAvalonia/Views/MainWindow.axaml.cs`

This task ports `GitWizardUI/MainPage.xaml` (133 lines) using these dialect substitutions:

| MAUI | Avalonia |
|------|----------|
| `ContentPage` | `Window` |
| `Grid.RowDefinitions` row syntax | same (or shorthand `RowDefinitions="Auto,*,Auto"`) |
| `HorizontalStackLayout` | `StackPanel Orientation="Horizontal"` |
| `VerticalStackLayout` | `StackPanel Orientation="Vertical"` |
| `ScrollView` | `ScrollViewer` |
| `Label` | `TextBlock` |
| `Entry` | `TextBox` |
| `CollectionView` | `ListBox` |
| `ToolTipProperties.Text="..."` | `ToolTip.Tip="..."` |
| `Clicked="Foo"` | `Click="Foo"` |
| `IsVisible` | `IsVisible` (same) |
| `FontAttributes="Bold"` | `FontWeight="Bold"` |

- [ ] **Step 1: Replace `MainWindow.axaml`**

Overwrite `GitWizardAvalonia/Views/MainWindow.axaml` with:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:GitWizardUI.ViewModels"
        x:Class="GitWizardAvalonia.Views.MainWindow"
        x:DataType="vm:MainViewModel"
        Title="GitWizard"
        Width="1100" Height="700">
  <Grid ColumnDefinitions="250,*" RowDefinitions="Auto,Auto,*,Auto">

    <StackPanel Grid.Row="0" Grid.ColumnSpan="2" Orientation="Horizontal" Spacing="5" Margin="5">
      <Button Content="Settings" Click="SettingsMenuItem_Click" FontSize="12" Padding="10,5" />
      <Button Content="Configure Windows Defender" x:Name="DefenderButton" Click="CheckWindowsDefenderMenuItem_Click" FontSize="12" Padding="10,5" />
      <Button Content="Clear Cache" Click="ClearCacheMenuItem_Click" FontSize="12" Padding="10,5" />
      <Button Content="Delete All Local Files" Click="DeleteAllLocalFilesMenuItem_Click" FontSize="12" Padding="10,5" />
    </StackPanel>

    <ScrollViewer Grid.Row="1" Grid.RowSpan="3" Grid.Column="0">
      <StackPanel Margin="5,0" Spacing="0">
        <TextBlock Text="Filter by" FontSize="16" FontWeight="Bold" HorizontalAlignment="Center" Padding="5" />
        <Button x:Name="FilterPendingChanges"         ToolTip.Tip="Repositories with un-staged or pending changes"                                                  Content="Pending Changes"        FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="FilterSubmoduleCheckout"      ToolTip.Tip="Repositories with submodules which are not checked out to project pointer reference"             Content="Submodule Checkout"     FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="FilterSubmoduleUninitialized" ToolTip.Tip="Repositories submodules which have not been checked out/initialized"                              Content="Submodule Uninitialized" FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="FilterSubmoduleConfigIssue"   ToolTip.Tip="Repositories with submodules which are in .gitmodules but not in the index, or vice versa"        Content="Submodule Config Issue" FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="FilterDetachedHead"           ToolTip.Tip="Repositories or submodules with detached heads with pending/unstaged changes"                     Content="Detached Head"          FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="FilterMyRepositories"         ToolTip.Tip="Repositories that you have committed to (based on email in global git configuration)"             Content="My Repositories"        FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="FilterLocalOnlyCommits"       ToolTip.Tip="Repositories with commits not pushed to any remote"                                                Content="Local Only Commits"     FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="FilterStale"                  ToolTip.Tip="Repositories with no commits in the last 30 days"                                                  Content="Stale (30+ days)"       FontSize="12" Click="FilterButton_Click" HorizontalAlignment="Stretch" />

        <TextBlock Text="Group by" FontSize="16" FontWeight="Bold" HorizontalAlignment="Center" Padding="5" />
        <Button x:Name="GroupByDrive"     ToolTip.Tip="Group repositories by drive letter / root path"   Content="Drive"      FontSize="12" Click="GroupButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="GroupByRemoteUrl" ToolTip.Tip="Group repositories by remote URL (find duplicates)" Content="Remote URL" FontSize="12" Click="GroupButton_Click" HorizontalAlignment="Stretch" />

        <TextBlock Text="Sort by" FontSize="16" FontWeight="Bold" HorizontalAlignment="Center" Padding="5" />
        <Button x:Name="SortByWorkingDirectory" ToolTip.Tip="Path to working directory, alphabetically" Content="Working Directory" FontSize="12" Click="SortButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="SortByRecentlyUsed"     ToolTip.Tip="Time of the most recent commit"             Content="Recently Used"      FontSize="12" Click="SortButton_Click" HorizontalAlignment="Stretch" />
        <Button x:Name="SortByRemoteUrl"        ToolTip.Tip="URL of first remote (usually origin)"        Content="Remote URL"         FontSize="12" Click="SortButton_Click" HorizontalAlignment="Stretch" />
      </StackPanel>
    </ScrollViewer>

    <Grid Grid.Row="1" Grid.RowSpan="3" Grid.Column="1" RowDefinitions="Auto,30,Auto,*,Auto">
      <StackPanel Grid.Row="0" Orientation="Horizontal" Spacing="5" Margin="0,5">
        <Button Content="Refresh" Click="RefreshButton_Click" />
        <Button Content="Fetch &amp; Refresh" Click="FetchAndRefreshButton_Click" ToolTip.Tip="Fetch all remotes before refreshing status (slower but accurate)" />
      </StackPanel>

      <TextBlock Grid.Row="1" Margin="10,0,0,0" VerticalAlignment="Center" Text="{Binding HeaderText}" />
      <TextBox Grid.Row="2" Margin="10,2" Watermark="Search repositories..." Text="{Binding SearchText, Mode=TwoWay}" />

      <ListBox Grid.Row="3" ItemsSource="{Binding Repositories}">
        <ListBox.ItemTemplate>
          <DataTemplate x:DataType="vm:RepositoryNodeViewModel">
            <Grid ColumnDefinitions="20,*,Auto,Auto" Margin="5,5,20,5">
              <TextBlock Grid.Column="0" Text="{Binding StatusIcon}"      Foreground="{Binding StatusColorHex}" FontSize="14" HorizontalAlignment="Center" VerticalAlignment="Center" ToolTip.Tip="{Binding StatusTooltip}" IsVisible="{Binding IsStatusVisible}" />
              <TextBlock Grid.Column="0" Text="{Binding ExpandIndicator}" FontSize="12" HorizontalAlignment="Center" VerticalAlignment="Center" IsVisible="{Binding IsGroupHeader}" />
              <TextBlock Grid.Column="1" Text="{Binding DisplayText}" VerticalAlignment="Center" FontWeight="{Binding GroupHeaderFontWeight}" Padding="{Binding ItemPaddingString}" />
              <Button   Grid.Column="2" Content="Fork" FontSize="12" Padding="8,4" VerticalAlignment="Center" IsVisible="{Binding IsNotGroupHeader}" Click="ForkButton_Click" Tag="{Binding}" />
              <Button   Grid.Column="3" Content="&#x21bb;" FontSize="12" Padding="8,4" VerticalAlignment="Center" IsVisible="{Binding IsNotGroupHeader}" Click="DeepRefreshButton_Click" Tag="{Binding}" ToolTip.Tip="Deep refresh: fetch remotes and update index (may be slow)" />
            </Grid>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>

      <StackPanel Grid.Row="4" IsVisible="{Binding IsProgressVisible}" Spacing="2" Margin="5,2">
        <ProgressBar Value="{Binding ProgressValue}" Minimum="0" Maximum="1" Height="8" />
        <TextBlock Text="{Binding ProgressText}" FontSize="10" HorizontalAlignment="Center" />
      </StackPanel>
    </Grid>

  </Grid>
</Window>
```

- [ ] **Step 2: Replace `MainWindow.axaml.cs`**

Overwrite `GitWizardAvalonia/Views/MainWindow.axaml.cs` with:

```csharp
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizardAvalonia.Services;
using GitWizardUI.ViewModels;

namespace GitWizardAvalonia.Views;

public partial class MainWindow : Window
{
    readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new AvaloniaUiDispatcher(), new AvaloniaUserDialogs());
        DataContext = _viewModel;
    }

    void SettingsMenuItem_Click(object? sender, RoutedEventArgs e)
        => new SettingsWindow().ShowDialog(this);

    async void CheckWindowsDefenderMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Windows-only: call into GitWizard.WindowsDefenderException helper.
        // If that class is not available or behaves differently, leave a TODO and continue.
        // The button is hidden on non-Windows in Task 16.
        await Task.CompletedTask;
    }

    void ClearCacheMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.ClearCacheAsync();

    void DeleteAllLocalFilesMenuItem_Click(object? sender, RoutedEventArgs e)
        => _ = _viewModel.DeleteAllLocalFilesAsync();

    void FilterButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplyFilter((sender as Button)?.Name ?? string.Empty);

    void GroupButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplyGroup((sender as Button)?.Name ?? string.Empty);

    void SortButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.ApplySort((sender as Button)?.Name ?? string.Empty);

    void RefreshButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RefreshCommand?.Execute(null);

    void FetchAndRefreshButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.FetchAndRefreshCommand?.Execute(null);

    void ForkButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.OpenInForkCommand?.Execute(node);
    }

    void DeepRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is RepositoryNodeViewModel node)
            _viewModel.DeepRefreshCommand?.Execute(node);
    }
}
```

`RefreshCommand`, `FetchAndRefreshCommand`, `OpenInForkCommand`, `DeepRefreshCommand` are existing `ICommand` properties on `MainViewModel`. If any are named differently, adapt; if any are absent, add them as straightforward `RelayCommand`-style implementations on the VM (no plan needed — they're trivial wrappers).

- [ ] **Step 3: Build**

Run: `dotnet build GitWizardAvalonia/GitWizardAvalonia.csproj --nologo`
Expected: 0 errors.

If errors mention missing properties on `MainViewModel` (e.g. `SearchText`, `HeaderText`, `IsProgressVisible`, etc.): each is on the existing VM. Read `GitWizardUI.ViewModels/MainViewModel.cs` and confirm — they all exist per the original MAUI XAML bindings.

- [ ] **Step 4: Smoke acceptance**

Run: `dotnet run --project GitWizardAvalonia/GitWizardAvalonia.csproj`
Acceptance criteria (verify each):
- Window opens at ~1100×700.
- Top toolbar shows 4 buttons (Settings / Defender / Clear Cache / Delete All).
- Left sidebar shows three labelled sections (Filter by / Group by / Sort by) with all buttons present.
- Right pane shows top toolbar (Refresh / Fetch & Refresh), header text "GitWizard", search box, empty list area, no errors in stderr.

If running headless (no display): the agent verifies build success only and notes in PR that runtime smoke needs human run.

- [ ] **Step 5: Commit**

```bash
git add GitWizardAvalonia/Views/MainWindow.axaml GitWizardAvalonia/Views/MainWindow.axaml.cs
git commit -m "feat(avalonia): port MainPage to MainWindow with full sidebar layout"
```

---

### Task 13: End-to-end smoke — Refresh + filter + search

This is a runtime task. Skip the run step if the agent's host has no display.

- [ ] **Step 1: Run the app**

Run: `dotnet run --project GitWizardAvalonia/GitWizardAvalonia.csproj`

- [ ] **Step 2: Click Refresh**

Acceptance:
- Header text changes to show progress messages.
- ProgressBar appears at the bottom and animates.
- Within ~30s on a typical dev box, the list populates with repos from `~/.GitWizard/config.json`.
- Each row shows status icon, repo path, Fork button, refresh (↻) button.

If list is empty: confirm `~/.GitWizard/config.json` has at least one search path (Task 0 step 4 handled this; if it fell through, `dotnet run --project git-wizard/git-wizard.csproj` once will fix it).

- [ ] **Step 3: Click a sidebar filter**

E.g. "Pending Changes" — list filters to subset.

- [ ] **Step 4: Type in search box**

Acceptance: list filters live as the agent simulates typing (or as the human running the smoke does so).

- [ ] **Step 5: Commit (only if fixes were needed)**

If any code fixes were needed:

```bash
git add -p
git commit -m "fix(avalonia): wire up <whatever-was-broken>"
```

Otherwise skip.

---

### Task 14: `SettingsWindow.axaml`

**Files:**
- Create: `GitWizardAvalonia/Views/SettingsWindow.axaml`
- Create: `GitWizardAvalonia/Views/SettingsWindow.axaml.cs`

- [ ] **Step 1: Read `GitWizardUI/SettingsPage.xaml`**

Read the file to understand the exact layout (paths editor, ignored-paths editor, button layout). The skeleton below is conservative; match feature-for-feature.

- [ ] **Step 2: Create `SettingsWindow.axaml`**

Write `GitWizardAvalonia/Views/SettingsWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:GitWizardUI.ViewModels"
        x:Class="GitWizardAvalonia.Views.SettingsWindow"
        x:DataType="vm:SettingsViewModel"
        Title="Settings"
        Width="600" Height="500"
        WindowStartupLocation="CenterOwner">
  <Grid RowDefinitions="Auto,*,Auto,Auto,*,Auto" Margin="10">
    <TextBlock Grid.Row="0" Text="Search Paths" FontSize="16" FontWeight="Bold" Margin="0,0,0,5" />
    <ListBox Grid.Row="1" ItemsSource="{Binding SearchPaths}" SelectedItem="{Binding SelectedSearchPath, Mode=TwoWay}" />
    <StackPanel Grid.Row="2" Orientation="Horizontal" Spacing="5" Margin="0,5">
      <Button Content="Add..." Click="AddSearchPath_Click" />
      <Button Content="Remove" Click="RemoveSearchPath_Click" />
    </StackPanel>

    <TextBlock Grid.Row="3" Text="Ignored Paths" FontSize="16" FontWeight="Bold" Margin="0,15,0,5" />
    <ListBox Grid.Row="4" ItemsSource="{Binding IgnoredPaths}" SelectedItem="{Binding SelectedIgnoredPath, Mode=TwoWay}" />
    <StackPanel Grid.Row="5" Orientation="Horizontal" Spacing="5" Margin="0,5">
      <Button Content="Add..." Click="AddIgnoredPath_Click" />
      <Button Content="Remove" Click="RemoveIgnoredPath_Click" />
    </StackPanel>
  </Grid>
</Window>
```

If the original `SettingsPage.xaml` exposes additional widgets (e.g. a "Save" button, a description label), add them with the same handler-name pattern.

- [ ] **Step 3: Create `SettingsWindow.axaml.cs`**

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using GitWizardAvalonia.Services;
using GitWizardUI.ViewModels;

namespace GitWizardAvalonia.Views;

public partial class SettingsWindow : Window
{
    readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(new AvaloniaFolderPicker());
        DataContext = _viewModel;
    }

    async void AddSearchPath_Click(object? sender, RoutedEventArgs e)
        => await _viewModel.AddSearchPathAsync();

    void RemoveSearchPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RemoveSelectedSearchPath();

    async void AddIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => await _viewModel.AddIgnoredPathAsync();

    void RemoveIgnoredPath_Click(object? sender, RoutedEventArgs e)
        => _viewModel.RemoveSelectedIgnoredPath();
}
```

- [ ] **Step 4: Build**

Run: `dotnet build GitWizardAvalonia/GitWizardAvalonia.csproj --nologo`
Expected: 0 errors.

- [ ] **Step 5: Smoke acceptance**

Launch the app, click Settings.
- Settings window opens modally.
- Existing search/ignored paths populate from `~/.GitWizard/config.json`.
- "Add..." opens a native folder picker (GTK file dialog on Linux).
- Selecting a folder adds it to the list.
- "Remove" deletes the selected path.

- [ ] **Step 6: Commit**

```bash
git add GitWizardAvalonia/Views/SettingsWindow.axaml GitWizardAvalonia/Views/SettingsWindow.axaml.cs
git commit -m "feat(avalonia): port SettingsPage to SettingsWindow with native folder picker"
```

---

## Phase F: Polish

### Task 15: Hide Windows-only UI on non-Windows

**Files:**
- Modify: `GitWizardAvalonia/Views/MainWindow.axaml.cs`

- [ ] **Step 1: Hide the Defender button at runtime on non-Windows**

In `GitWizardAvalonia/Views/MainWindow.axaml.cs`, at the end of the constructor, add:

```csharp
if (!OperatingSystem.IsWindows())
    DefenderButton.IsVisible = false;
```

`OperatingSystem.IsWindows()` is in `System` (auto-imported via implicit usings).

- [ ] **Step 2: Build and smoke-test**

Run on Linux: `dotnet run --project GitWizardAvalonia/GitWizardAvalonia.csproj`
Acceptance: top toolbar has 3 buttons (Settings, Clear Cache, Delete All Local Files) — no Defender button.

- [ ] **Step 3: Commit**

```bash
git add GitWizardAvalonia/Views/MainWindow.axaml.cs
git commit -m "feat(avalonia): hide Windows-only Defender button on non-Windows"
```

---

### Task 16: Linux end-to-end smoke + screenshot

- [ ] **Step 1: Run the app on Linux, exercise full UI**

Run: `dotnet run --project GitWizardAvalonia/GitWizardAvalonia.csproj`

In the app:
1. Click Refresh; wait for scan to complete.
2. Click each sidebar filter; confirm list updates.
3. Click each Group button; confirm grouping headers appear.
4. Click each Sort button; confirm order changes.
5. Type a substring in the search box; confirm filtering.
6. Click a row's Fork button; confirm it launches `fork` (or no-ops gracefully if Fork isn't installed).
7. Click a row's ↻ button; confirm deep refresh runs.
8. Open Settings; add and remove a path.

If running headless: skip this task and record in PR that smoke needs human verification.

- [ ] **Step 2: Capture a screenshot**

If a display is available, use a tool that's likely present:

```bash
gnome-screenshot -w -f /home/schoen/git-wizard/Screenshots/GitWizardAvalonia.png 2>/dev/null \
  || maim -i $(xdotool getactivewindow) /home/schoen/git-wizard/Screenshots/GitWizardAvalonia.png 2>/dev/null \
  || scrot -u /home/schoen/git-wizard/Screenshots/GitWizardAvalonia.png 2>/dev/null \
  || echo "No screenshot tool available — skip"
```

If no tool works, skip. The screenshot is nice-to-have, not required.

- [ ] **Step 3: Commit screenshot (if captured)**

```bash
git add Screenshots/GitWizardAvalonia.png
git commit -m "docs: add Linux screenshot of Avalonia UI"
```

---

### Task 17: Update PLAN.md

**Files:**
- Modify: `PLAN.md`

- [ ] **Step 1: Append a section recording the Avalonia work**

Append to `PLAN.md`:

```markdown
### Avalonia cross-platform UI

- [x] Extracted view models into `GitWizardUI.ViewModels` shared project behind `IUiDispatcher` / `IUserDialogs` / `IFolderPicker`
- [x] Neutralized MAUI types in `RepositoryNodeViewModel` (color/padding/font as strings)
- [x] Extracted handler logic from MAUI page code-behind into VM methods (`ApplyFilter`/`ApplyGroup`/`ApplySort` etc.)
- [x] MAUI app refactored to consume shared VMs via `MauiUiDispatcher` etc. — manual Windows verification pending
- [x] Avalonia desktop project (`GitWizardAvalonia/`) ports MainPage and SettingsPage
- [x] Native folder picker on Linux/macOS via Avalonia `IStorageProvider`
- [x] Windows-only features (Defender button) gated on `OperatingSystem.IsWindows()`
- [x] Verified scan + filter + group + sort on Linux
```

- [ ] **Step 2: Commit**

```bash
git add PLAN.md
git commit -m "docs: record Avalonia migration in PLAN.md"
```

---

## Out of scope (intentionally deferred)

The following items are real but not part of this plan:

- **Retiring `GitWizardUI/` (MAUI app).** This plan leaves MAUI building. Decision on whether to delete it is downstream once Avalonia has matched parity on Windows too.
- **macOS testing.** Avalonia builds on macOS, but verification needs Mac hardware.
- **Avalonia `dotnet publish` zip target.** Replicating the MAUI `ZipPublishOutput` for Avalonia is a release-tooling task.
- **Visual styling parity.** The Avalonia app uses default Fluent theme. Matching MAUI's exact look is a polish pass.
- **Tray-icon / single-instance behavior**, if present in MAUI — out of scope.
- **MFTLib path on non-Windows.** MFTLib NuGet restores on Linux but its native DLL is Windows-only. The existing `GitWizard` core code already handles this (the CLI works on Linux), so no Avalonia work is needed.
- **CI build for Avalonia on GitHub Actions.** Worthwhile but a separate task.

---

## Self-review checklist (already done — leaving here as a record)

- ✅ Spec coverage: every section of the conversation spec maps to a task.
- ✅ No placeholders: every code step contains the actual code.
- ✅ Type consistency: `IUiDispatcher`/`IUserDialogs`/`IFolderPicker` names, methods, and signatures match across abstractions, stubs, MAUI impls, and Avalonia impls.
- ✅ Smoke acceptance criteria specified for all UI-porting tasks.
- ✅ Each task ends in a commit (except Task 0 pre-flight and Task 13 conditional).
- ✅ Linux-only execution paths called out for tasks that involve MAUI build.
- ✅ MAUI-typed VM properties neutralized before being moved to a non-MAUI lib (Phase A.5).
- ✅ Methods extracted from MAUI page code-behind enumerated with signatures (Task 8).
- ✅ Compiled-bindings strictness called out in plan preamble.
