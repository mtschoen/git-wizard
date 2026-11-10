# Helpers for scripts/cli-verb-matrix.ps1, dot-sourced by it and by ci/script-tests/cli-verb-matrix.tests.ps1.
# Nothing here elevates, prompts or touches the real git-wizard state folder: every child process
# gets the environment its caller passes in, and every launch is owned until it is torn down, so
# neither a timeout nor a parent that exits before its children can leave a process behind.
# Kept together despite its size so the native launch lifetime and matrix helpers share one source.

$script:MatrixStartedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$script:MatrixJobType = $null

function Get-MatrixJobType {
    # Compiled once per session. A job object is the only handle on a descendant whose parent has
    # already exited: Kill(true) walks live children, so it cannot reach an orphan, and a cleanup
    # gated on that parent's HasExited skips its subtree entirely.
    if (-not $script:MatrixJobType) {
        $source = @'
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CliVerbMatrix
{
    public sealed class MatrixJobObject : IDisposable
    {
        // KILL_ON_JOB_CLOSE is only accepted through the extended information class; the basic one
        // rejects it with ERROR_INVALID_PARAMETER.
        const int ExtendedLimitInformationClass = 9;
        const int KillOnJobClose = 0x2000;
        const int CreateSuspended = 0x4;
        const int CreateUnicodeEnvironment = 0x400;
        const int CreateNoWindow = 0x08000000;
        const int UseStandardHandles = 0x100;

        public Process Process { get; private set; }
        public StreamWriter StandardInput { get; private set; }
        public StreamReader StandardOutput { get; private set; }
        public StreamReader StandardError { get; private set; }
        IntPtr primaryThread;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInformation
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation
        {
            public IntPtr Process, Thread;
            public int ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, int flags, IntPtr environment, string directory,
            ref StartupInformation startup, out ProcessInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        // Windows argv quoting: backslashes double only before a quote or the closing delimiter.
        static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                result.Append(character);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        public static MatrixJobObject StartSuspended(ProcessStartInfo startInfo)
        {
            var launch = new MatrixJobObject();
            AnonymousPipeServerStream input = null, output = null, error = null;
            IntPtr environment = IntPtr.Zero;
            ProcessInformation information = default;
            try
            {
                input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                var startup = new StartupInformation
                {
                    Size = Marshal.SizeOf<StartupInformation>(), Flags = UseStandardHandles,
                    StandardInput = input.ClientSafePipeHandle.DangerousGetHandle(),
                    StandardOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
                    StandardError = error.ClientSafePipeHandle.DangerousGetHandle()
                };
                string executable = startInfo.FileName;
                string command = Quote(executable) + " " + string.Join(" ", startInfo.ArgumentList.Select(Quote));
                if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    executable = Environment.GetEnvironmentVariable("ComSpec");
                    command = Quote(executable) + " /d /s /c \"" + command + "\"";
                }
                string variables = string.Join("\0", startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
                environment = Marshal.StringToHGlobalUni(variables);
                if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow, environment, null, ref startup, out information))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
                }
                launch.Process = Process.GetProcessById(information.ProcessId);
                // Retain a managed handle before closing the native creation handle.
                _ = launch.Process.Handle;
                launch.primaryThread = information.Thread;
                input.DisposeLocalCopyOfClientHandle();
                output.DisposeLocalCopyOfClientHandle();
                error.DisposeLocalCopyOfClientHandle();
                launch.StandardInput = new StreamWriter(input);
                launch.StandardOutput = new StreamReader(output, startInfo.StandardOutputEncoding);
                launch.StandardError = new StreamReader(error, startInfo.StandardErrorEncoding);
                return launch;
            }
            catch
            {
                if (information.Process != IntPtr.Zero)
                {
                    TerminateProcess(information.Process, 1);
                    WaitForSingleObject(information.Process, 5000);
                }
                if (information.Thread != IntPtr.Zero) { CloseHandle(information.Thread); }
                launch.Process?.Dispose();
                input?.Dispose();
                output?.Dispose();
                error?.Dispose();
                throw;
            }
            finally
            {
                if (information.Process != IntPtr.Zero) { CloseHandle(information.Process); }
                if (environment != IntPtr.Zero) { Marshal.FreeHGlobal(environment); }
            }
        }

        public void Resume()
        {
            if (ResumeThread(primaryThread) == uint.MaxValue)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");
            }
            CloseHandle(primaryThread);
            primaryThread = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (primaryThread != IntPtr.Zero) { CloseHandle(primaryThread); primaryThread = IntPtr.Zero; }
            StandardInput?.Dispose();
            StandardOutput?.Dispose();
            StandardError?.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public int LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public int ActiveProcessLimit;
            public IntPtr Affinity;
            public int PriorityClass;
            public int SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public IntPtr ProcessMemoryLimit;
            public IntPtr JobMemoryLimit;
            public IntPtr PeakProcessMemoryUsed;
            public IntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformation information, int informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        // Kill-on-job-close also reaps the job when this host dies without running its cleanup,
        // because the operating system closes the handle for it.
        public static IntPtr Create()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { throw new InvalidOperationException("CreateJobObject failed with Win32 error " + Marshal.GetLastWin32Error()); }
            ExtendedLimitInformation limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, Marshal.SizeOf(typeof(ExtendedLimitInformation))))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(job);
                throw new InvalidOperationException("SetInformationJobObject failed with Win32 error " + error);
            }
            return job;
        }

        public static void Assign(IntPtr job, IntPtr process)
        {
            if (!AssignProcessToJobObject(job, process)) { throw new InvalidOperationException("AssignProcessToJobObject failed with Win32 error " + Marshal.GetLastWin32Error()); }
        }

        public static void Stop(IntPtr job)
        {
            if (job == IntPtr.Zero) { return; }
            TerminateJobObject(job, 1);
            CloseHandle(job);
        }
    }
}
'@
        # Add-Type -PassThru can return more than the one public type; select it by name.
        $script:MatrixJobType = @(Add-Type -TypeDefinition $source -Language CSharp -PassThru) |
            Where-Object { $_.FullName -eq 'CliVerbMatrix.MatrixJobObject' } |
            Select-Object -First 1
    }
    return $script:MatrixJobType
}

function New-MatrixJob {
    # IntPtr.Zero where job objects do not exist, leaving Kill(true) as the only owner.
    if (-not $IsWindows) { return [System.IntPtr]::Zero }
    return (Get-MatrixJobType)::Create()
}

function Stop-MatrixJob {
    param([System.IntPtr]$Job = [System.IntPtr]::Zero)
    if ($Job -ne [System.IntPtr]::Zero) { (Get-MatrixJobType)::Stop($Job) }
}

function Stop-MatrixProcessTree {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process)
    try {
        if (-not $Process.HasExited) { $Process.Kill($true) }
    }
    catch [System.InvalidOperationException] { }
    catch [System.ComponentModel.Win32Exception] { }
}

function Stop-MatrixStartedProcesses {
    # Backstop for anything still tracked, reached from the script's own finally block.
    foreach ($process in @($script:MatrixStartedProcesses)) {
        Stop-MatrixProcessTree $process
        $null = $process.WaitForExit(5000)
        $process.Dispose()
    }
    $script:MatrixStartedProcesses.Clear()
}

function Invoke-MatrixProcess {
    # Runs one executable with a hard timeout. stdin is closed at once so a prompt such as
    # "Press Enter to exit" ends instead of hanging. The launch owns a job object holding every
    # descendant, so teardown reaches children whose parent has already exited; it runs before the
    # output is drained, because a descendant that inherited the pipe keeps the reads open.
    param(
        [Parameter(Mandatory)][string]$ExecutablePath,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{},
        [ValidateRange(1, 86400)][int]$TimeoutSeconds = 120
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($ExecutablePath)
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    foreach ($name in $Environment.Keys) {
        if ($null -eq $Environment[$name]) { $null = $startInfo.Environment.Remove($name) }
        else { $startInfo.Environment[$name] = [string]$Environment[$name] }
    }

    $job = New-MatrixJob
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $launch = $null
    try {
        if ($job -ne [System.IntPtr]::Zero) {
            $launch = (Get-MatrixJobType)::StartSuspended($startInfo)
            $process.Dispose()
            $process = $launch.Process
            $capture = $launch
        }
        else {
            $null = $process.Start()
            $capture = $process
        }
        $started = $true
        $script:MatrixStartedProcesses.Add($process)
        # CREATE_SUSPENDED prevents any child code from running before job membership exists.
        if ($job -ne [System.IntPtr]::Zero) {
            (Get-MatrixJobType)::Assign($job, $process.Handle)
            $launch.Resume()
        }
        $capture.StandardInput.Close()
        $outputTask = $capture.StandardOutput.ReadToEndAsync()
        $errorTask = $capture.StandardError.ReadToEndAsync()

        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        $exitCode = if ($timedOut) { $null } else { $process.ExitCode }

        # Ownership ends here on every exit path. The job goes first: it terminates descendants
        # whose parent has already exited, which the tree kill cannot reach, and closing their
        # inherited pipe ends is what lets the two reads below complete.
        Stop-MatrixJob $job
        $job = [System.IntPtr]::Zero
        Stop-MatrixProcessTree $process

        $tasks = [System.Threading.Tasks.Task[]]@($outputTask, $errorTask)
        $drained = [System.Threading.Tasks.Task]::WaitAll($tasks, 10000)
        $output = if ($outputTask.IsCompletedSuccessfully) { $outputTask.Result } else { '' }
        $errorOutput = if ($errorTask.IsCompletedSuccessfully) { $errorTask.Result } else { '' }

        return [pscustomobject]@{
            ExitCode = $exitCode
            TimedOut = $timedOut
            OutputTruncated = -not $drained
            Output = $output
            ErrorOutput = $errorOutput
            Combined = $output + "`n" + $errorOutput
        }
    }
    finally {
        Stop-MatrixJob $job
        if ($started) {
            Stop-MatrixProcessTree $process
            $null = $process.WaitForExit(5000)
        }
        if ($launch) { $launch.Dispose() }
        $process.Dispose()
        $null = $script:MatrixStartedProcesses.Remove($process)
    }
}

function Expand-MatrixText {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][hashtable]$Context)
    foreach ($name in $Context.Keys) { $Text = $Text.Replace('{' + $name + '}', [string]$Context[$name]) }
    return $Text
}

function Get-MatrixOptionName {
    param([Parameter(Mandatory)][string]$Argument)
    if ($Argument -match '^(-{1,2}(?:[A-Za-z][\w-]*|\?))') { return $Matches[1] }
    return $null
}


function Get-MatrixRowTokens {
    # Options before the subcommand belong to the root scope; later options use the subcommand
    # scope. Skip values using the CLI's fixed value-option spellings, not their contents.
    param([Parameter(Mandatory)][hashtable]$Row, [hashtable]$Aliases = @{})
    $arguments = @($Row.Arguments)
    $tokens = [System.Collections.Generic.List[string]]::new()
    $prefix = ''
    $expectsValue = $false
    $valueOptions = @('--paths', '-p', '-paths', '--filter', '-f', '--save-path', '-save-path',
        '--config-path', '--match', '-m', '--not', '--not-merged', '--base')
    foreach ($argument in $arguments) {
        if ($expectsValue) { $expectsValue = $false; continue }
        if ($argument -eq '--') { break }
        if (-not $argument.StartsWith('-')) {
            if (-not $prefix) { $prefix = $argument + ' '; $tokens.Add($argument) }
            continue
        }
        $name = Get-MatrixOptionName $argument
        if (-not $name) { continue }
        $spelled = $prefix + $name
        $tokens.Add($(if ($Aliases.ContainsKey($spelled)) { $Aliases[$spelled] } else { $spelled }))
        $expectsValue = $name -in $valueOptions -and -not $argument.Contains('=')
    }
    return @($tokens | Select-Object -Unique)
}

function Get-MatrixUsage {
    # Every command and option the CLI's own help text lists, and the alias map from each short or
    # consumer spelling to the option's long name. The root text comes from --help; each subcommand's
    # options come from its own --help, passed under the subcommand's name. An option shared by
    # several scopes is a separate token per scope, so each subcommand must exercise its own.
    param([Parameter(Mandatory)][hashtable]$HelpTexts)
    $tokens = [System.Collections.Generic.List[string]]::new()
    $aliases = @{}
    foreach ($scope in $HelpTexts.Keys) {
        $prefix = if ($scope) { "$scope " } else { '' }
        $section = ''
        foreach ($line in ($HelpTexts[$scope] -split "\r?\n")) {
            if ($line -eq 'Options:') { $section = 'options'; continue }
            if ($line -eq 'Commands:') { $section = 'commands'; continue }
            if ($line -match '^\S') { $section = '' }
            if ($section -eq 'options' -and $line -match '^  (-[^\s|]+(?:\|-[^\s|]+)*)(?: <[^>]+>)?\s{2,}\S') {
                $names = $Matches[1] -split '\|'
                $long = @(@($names | Where-Object { $_.StartsWith('--') }) + $names[0])[0]
                $tokens.Add($prefix + $long)
                foreach ($name in $names) { if ($name -ne $long) { $aliases[$prefix + $name] = $prefix + $long } }
            }
            elseif ($section -eq 'commands' -and $line -match '^  ([a-z][a-z-]*)\s{2,}\S') { $tokens.Add($Matches[1]) }
        }
    }
    # Spellings NormalizeConsumerAliases rewrites before parsing; the help text does not list them.
    foreach ($consumer in @(@('-paths', '--paths'), @('-save-path', '--save-path'), @('-silent', '--silent'))) {
        if ($tokens -contains $consumer[1]) { $aliases[$consumer[0]] = $consumer[1] }
    }
    return [pscustomobject]@{ Tokens = @($tokens | Select-Object -Unique); Aliases = $aliases }
}

function Test-MatrixCoverage {
    # Compares what the usage text lists with what the matrix runs or explicitly excludes, both ways, so
    # a new verb without a row and a row or exclusion for a verb that no longer exists are each caught.
    param(
        [Parameter(Mandatory)][string[]]$UsageTokens,
        [Parameter(Mandatory)][hashtable[]]$Rows,
        [Parameter(Mandatory)][hashtable[]]$Exclusions,
        [hashtable]$Aliases = @{}
    )
    $covered = [System.Collections.Generic.HashSet[string]]::new()
    $rowTokens = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in $Rows) {
        if ($row.ContainsKey('Probe') -and $row.Probe) { continue }
        foreach ($token in (Get-MatrixRowTokens $row $Aliases)) { $null = $covered.Add($token); $null = $rowTokens.Add($token) }
    }
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($token in $UsageTokens) {
        $excluded = $Exclusions | Where-Object { $token -like $_.Token }
        if (-not $covered.Contains($token) -and -not $excluded) {
            $problems.Add("The usage text lists '$token' but the matrix neither runs nor excludes it. Add a row to Get-MatrixRows or an exclusion with a reason to Get-MatrixExclusions in scripts/cli-verb-matrix.rows.ps1.")
        }
    }
    foreach ($token in $rowTokens) {
        if ($token -notin $UsageTokens) {
            $problems.Add("A matrix row exercises '$token' but the usage text no longer lists it. Fix or remove the row.")
        }
    }
    foreach ($exclusion in $Exclusions) {
        if (-not ($UsageTokens | Where-Object { $_ -like $exclusion.Token })) {
            $problems.Add("The exclusion for '$($exclusion.Token)' matches nothing in the usage text. Remove it.")
        }
    }
    return [pscustomobject]@{ Ok = ($problems.Count -eq 0); Problems = @($problems) }
}

function Assert-MatrixRowScope {
    # Refuses, at definition time, any row that could elevate or reach beyond the throwaway tree.
    # A discovering command needs --no-mft (an MFT scan elevates and covers every drive), before or
    # after its subcommand. Without --paths, discovery uses only the isolated home's seeded search
    # paths. Watch and Defender setup elevate.
    param([Parameter(Mandatory)][hashtable]$Row)
    $arguments = @($Row.Arguments)
    $names = @($arguments | ForEach-Object { Get-MatrixOptionName $_ } | Where-Object { $_ })
    foreach ($forbidden in @('--watch', '--setup-defender')) {
        if ($forbidden -in $names) { throw "Matrix row '$($Row.Name)' uses $forbidden, which elevates. Exclude it instead." }
    }
    $withoutDiscovery = @('-h', '-?', '--help', '--version', '--db-size', '--index-cache-status', '--clear-index-cache')
    foreach ($name in $names) { if ($name -in $withoutDiscovery) { return } }
    if ('--no-mft' -notin $names) {
        throw "Matrix row '$($Row.Name)' is unscoped: a discovering command without --no-mft scans the real drives and elevates."
    }
}

function Get-MatrixGitEnvironment {
    # Isolates fixture git children from global/system config, command-scope config overrides
    # (GIT_CONFIG_COUNT/KEY_*/VALUE_*, GIT_CONFIG_PARAMETERS) and repository/index routing variables.
    $environment = @{ GIT_CONFIG_GLOBAL = 'NUL'; GIT_CONFIG_NOSYSTEM = '1'; GIT_TERMINAL_PROMPT = '0' }
    foreach ($name in @(
            'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_COMMON_DIR',
            'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_IMPLICIT_WORK_TREE',
            'GIT_PREFIX', 'GIT_GRAFT_FILE', 'GIT_SHALLOW_FILE', 'GIT_NO_REPLACE_OBJECTS',
            'GIT_REPLACE_REF_BASE', 'GIT_CONFIG', 'GIT_CONFIG_PARAMETERS', 'GIT_CONFIG_COUNT')) {
        $environment[$name] = $null
    }
    foreach ($item in (Get-ChildItem Env:)) {
        if ($item.Name -match '^GIT_CONFIG_(?:KEY|VALUE)_') { $environment[$item.Name] = $null }
    }
    return $environment
}

function New-MatrixFixture {
    # Builds the throwaway git repositories the rows run against, each with its own bare remote:
    # clean (pushed, one pushed and one local-only tag), dirty (modified tracked file, an unpushed
    # branch and a stash) and prunable (a merged branch, which prune can delete, and an unmerged one).
    # Every git call runs through Invoke-MatrixProcess with a sanitized git environment.
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$GitPath)
    $environment = Get-MatrixGitEnvironment
    $identity = @('-c', 'user.name=Matrix', '-c', 'user.email=matrix@example.invalid', '-c', 'commit.gpgsign=false', '-c', 'core.autocrlf=false')
    $git = {
        param([string]$Directory, [string[]]$GitArguments)
        $run = Invoke-MatrixProcess -ExecutablePath $GitPath -Arguments (@('-C', $Directory) + $identity + $GitArguments) -Environment $environment -TimeoutSeconds 60
        if ($run.TimedOut -or $run.ExitCode -ne 0) { throw "git $($GitArguments -join ' ') failed in ${Directory}: $($run.Combined)" }
    }.GetNewClosure()
    $write = { param([string]$Directory, [string]$Name, [string]$Content) [System.IO.File]::WriteAllText((Join-Path $Directory $Name), "$Content`n") }
    $repositories = Join-Path $Root 'repos'
    $remotes = Join-Path $Root 'remotes'
    # An explicit empty template prevents inherited templates from installing config or hooks.
    $template = Join-Path $Root 'empty-template'
    foreach ($directory in @($repositories, $remotes, $template)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    foreach ($name in 'clean', 'dirty', 'prunable') {
        $remote = Join-Path $remotes "$name.git"
        $repository = Join-Path $repositories $name
        & $git $Root @('init', '-q', '--bare', '-b', 'main', "--template=$template", $remote)
        & $git $Root @('init', '-q', '-b', 'main', "--template=$template", $repository)
        & $write $repository 'a.txt' 'one'
        & $git $repository @('add', '.')
        & $git $repository @('commit', '-q', '-m', 'one')
        & $git $repository @('remote', 'add', 'origin', $remote)
        & $git $repository @('push', '-q', 'origin', 'main')
    }
    $clean = Join-Path $repositories 'clean'
    & $git $clean @('tag', 'archive/one')
    & $git $clean @('push', '-q', 'origin', 'archive/one')
    & $git $clean @('tag', 'local/two')

    $dirty = Join-Path $repositories 'dirty'
    & $git $dirty @('checkout', '-q', '-b', 'wip')
    & $write $dirty 'w.txt' 'wip'
    & $git $dirty @('add', '.')
    & $git $dirty @('commit', '-q', '-m', 'wip')
    & $git $dirty @('checkout', '-q', 'main')
    & $write $dirty 'a.txt' 'stashed'
    & $git $dirty @('stash', 'push', '-q')
    & $write $dirty 'a.txt' 'dirty'

    $prunable = Join-Path $repositories 'prunable'
    & $git $prunable @('checkout', '-q', '-b', 'done')
    & $write $prunable 'd.txt' 'done'
    & $git $prunable @('add', '.')
    & $git $prunable @('commit', '-q', '-m', 'done')
    & $git $prunable @('checkout', '-q', 'main')
    & $git $prunable @('merge', '-q', '--no-ff', 'done', '-m', 'merge done')
    & $git $prunable @('push', '-q', 'origin', 'main')
    & $git $prunable @('checkout', '-q', '-b', 'open')
    & $write $prunable 'o.txt' 'open'
    & $git $prunable @('add', '.')
    & $git $prunable @('commit', '-q', '-m', 'open')
    & $git $prunable @('checkout', '-q', 'main')
    return [pscustomobject]@{ Repositories = $repositories; Clean = $clean; Dirty = $dirty; Prunable = $prunable }
}

function Invoke-MatrixRow {
    param(
        [Parameter(Mandatory)][hashtable]$Row,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][hashtable]$Context,
        [hashtable]$Environment = @{},
        [int]$TimeoutSeconds = 120
    )
    $arguments = @($Row.Arguments | ForEach-Object { Expand-MatrixText $_ $Context })
    $commandLine = "git-wizard $($arguments -join ' ')"
    $expectedExit = if ($Row.ContainsKey('ExitCode')) { [int]$Row.ExitCode } else { 0 }
    $problems = [System.Collections.Generic.List[string]]::new()
    $result = $null
    try {
        if ($Row.ContainsKey('Setup')) { & $Row.Setup $Context }
        $result = Invoke-MatrixProcess -ExecutablePath $ExecutablePath -Arguments $arguments -Environment $Environment -TimeoutSeconds $TimeoutSeconds
    }
    catch {
        $problems.Add("could not run: $($_.Exception.Message)")
    }
    if ($null -ne $result) {
        if ($result.TimedOut) { $problems.Add("timed out after $TimeoutSeconds seconds (process tree killed)") }
        elseif ($result.ExitCode -ne $expectedExit) { $problems.Add("exit code $($result.ExitCode), expected $expectedExit") }
        # Incomplete capture is a failure on its own: the substring checks below would otherwise
        # judge a row against text that is missing whatever never drained.
        if ($result.OutputTruncated) { $problems.Add('output capture did not drain, so the captured text is incomplete') }
        foreach ($expected in @($Row['Contains'])) {
            if ($expected -and -not $result.Combined.Contains((Expand-MatrixText $expected $Context))) { $problems.Add("output lacks '$expected'") }
        }
        foreach ($unexpected in @($Row['NotContains'])) {
            if ($unexpected -and $result.Combined.Contains((Expand-MatrixText $unexpected $Context))) { $problems.Add("output contains '$unexpected'") }
        }
        if ($Row.ContainsKey('Verify') -and -not $result.TimedOut) {
            $message = & $Row.Verify $Context $result
            if ($message) { $problems.Add([string]$message) }
        }
    }
    $excerpt = if ($null -ne $result) { (($result.Combined -replace "\r", "`n") -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 8) -join "`n" } else { '' }
    return [pscustomobject]@{
        Name = $Row.Name
        Status = if ($problems.Count -eq 0) { 'PASS' } else { 'FAIL' }
        Problems = @($problems)
        CommandLine = $commandLine
        Excerpt = $excerpt
        Fatal = ($Row.ContainsKey('Fatal') -and [bool]$Row.Fatal)
    }
}

function Write-MatrixResult {
    param([Parameter(Mandatory)]$Result)
    if ($Result.Status -eq 'PASS') {
        Write-Host "PASS     $($Result.Name)" -ForegroundColor Green
        return
    }
    Write-Host "FAIL     $($Result.Name)" -ForegroundColor Red
    foreach ($problem in $Result.Problems) { Write-Host "           $problem" -ForegroundColor Red }
    Write-Host "           command: $($Result.CommandLine)" -ForegroundColor Red
    foreach ($line in ($Result.Excerpt -split "`n")) { Write-Host "           | $line" -ForegroundColor DarkGray }
}

function Remove-MatrixDirectory {
    # Deletes the throwaway folder, retrying briefly because a just-killed process can still hold a handle.
    param([Parameter(Mandatory)][string]$Path)
    for ($attempt = 1; $attempt -le 5 -and (Test-Path -LiteralPath $Path); $attempt++) {
        try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop }
        catch { Start-Sleep -Milliseconds (200 * $attempt) }
    }
    if (Test-Path -LiteralPath $Path) { Write-Warning "Could not delete the temporary folder: $Path" }
}

