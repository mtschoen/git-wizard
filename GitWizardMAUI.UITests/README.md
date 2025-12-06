# GitWizard MAUI UI Tests

Automated UI tests that launch the MAUI app and capture screenshots for documentation.

## Prerequisites

### 1. Install WinAppDriver

Download and install from: https://github.com/microsoft/WinAppDriver/releases

Latest version: WinAppDriver v1.2.1

### 2. Enable Developer Mode (Windows)

1. Open **Settings** > **Update & Security** > **For developers**
2. Turn on **Developer mode**

## Running the Tests

### Option 1: From Visual Studio

1. Open Test Explorer (Test > Test Explorer)
2. **Start WinAppDriver** (run as Administrator):
   ```
   C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe
   ```
3. Build the solution
4. In Test Explorer, right-click and select "Run All Tests"

### Option 2: From Command Line

1. **Start WinAppDriver** in one terminal (as Administrator):
   ```cmd
   cd "C:\Program Files (x86)\Windows Application Driver"
   WinAppDriver.exe
   ```

2. **Run tests** in another terminal:
   ```bash
   dotnet test GitWizardMAUI.UITests\GitWizardMAUI.UITests.csproj
   ```

## Available Tests

### `CaptureMainWindowScreenshot`
- Launches the app
- Captures a screenshot
- Saves to `maui-ui.png` in repo root
- Perfect for updating documentation screenshots

### `VerifyMainUIElements`
- Verifies key UI elements exist (Refresh button, Settings button, etc.)
- Takes error screenshot on failure: `maui-ui-error.png`

### `CaptureAfterRefresh`
- Clicks the Refresh button
- Waits for repositories to load
- Captures screenshot with data: `maui-ui-refreshed.png`

## Automating Screenshot Updates

### PowerShell Script

Create `update-screenshots.ps1`:

```powershell
# Start WinAppDriver
Start-Process -FilePath "C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe"
Start-Sleep -Seconds 2

# Build and run tests
dotnet build GitWizardMAUI\GitWizardMAUI.csproj
dotnet test GitWizardMAUI.UITests\GitWizardMAUI.UITests.csproj --filter "FullyQualifiedName~CaptureMainWindowScreenshot"

# Stop WinAppDriver
Stop-Process -Name "WinAppDriver" -Force
```

### VS Test Configuration

Add to `.runsettings`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>1</MaxCpuCount>
  </RunConfiguration>
  <MSTest>
    <Parallelize>
      <Workers>1</Workers>
      <Scope>ClassLevel</Scope>
    </Parallelize>
  </MSTest>
</RunSettings>
```

## CI/CD Integration

### GitHub Actions Example

```yaml
- name: Setup WinAppDriver
  run: choco install winappdriver

- name: Start WinAppDriver
  run: Start-Process "C:\Program Files (x86)\Windows Application Driver\WinAppDriver.exe"
  shell: powershell

- name: Run UI Tests
  run: dotnet test GitWizardMAUI.UITests

- name: Upload Screenshots
  uses: actions/upload-artifact@v3
  with:
    name: ui-screenshots
    path: "*.png"
```

## Troubleshooting

### "Failed to start WinAppDriver session"
- Ensure WinAppDriver.exe is running as Administrator
- Check that Developer Mode is enabled in Windows Settings
- Verify the app path in MainWindowTests.cs matches your build output

### "Element not found"
- The UI might be loading slowly - increase wait times in tests
- Check element names match the actual UI controls
- View error screenshots in repo root for debugging

### Tests hang or timeout
- Make sure only one instance of WinAppDriver is running
- Close any existing instances of the GitWizard MAUI app
- Try increasing `ms:waitForAppLaunch` timeout in Setup method

## Screenshot Output

Screenshots are saved to the repo root:
- `maui-ui.png` - Main window screenshot
- `maui-ui-refreshed.png` - After refresh with data loaded
- `maui-ui-error.png` - Screenshot captured on test failure
