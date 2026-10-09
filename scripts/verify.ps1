<#
.SYNOPSIS
    The standard verification: runs every stage, every time, and reports one pass/fail verdict.
.DESCRIPTION
    Stages, in order (there is no option to choose stages):
      1. build      Release build of the solution (warnings are errors)
      2. solution   dotnet test of the solution (Release, the current build; UNEXTRACT_E2E_EXE is unset meanwhile)
      3. e2e        temporary single-file publish and the E2E tests against it (run-e2e-tests.ps1)
      4. gui-smoke  GUI publish and startup smoke (run-gui-smoke-tests.ps1)
      5. ui-e2e     all GUI UI E2E tests (run-gui-ui-tests.ps1); needs an unlocked interactive desktop
    When a stage fails, the later stages are not run; the results of the stages that ran are still collected.
    The verdict is PASSED only when every stage (except ui-e2e under -Ci) and the result collection pass.
    "PASSED (not observed: N)" means the same, with fixed environment-dependent cases that could not be observed
    here (listed in the summary; they are not counted as observed). A missing prerequisite (interactive desktop,
    UnRAR.dll, ...), an aborted test host, a missing TRX or an unknown precondition report fails the verification.

    Results: %TEMP%\unextract-verify\<checkout id>\latest (summary.txt, TRX files and diagnostics per stage).
    The directory is emptied at the start under a named mutex; a second run on the same checkout is refused and the
    existing results are left unchanged. Test fixtures are not handled here (the tests delete their own).

    -Ci is accepted only when GITHUB_ACTIONS=true. It excludes ui-e2e (reported as "excluded in CI").
    ASCII only for Windows PowerShell 5.1 compatibility.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1
#>
[CmdletBinding()]
param(
    [switch]$Ci
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ($Ci -and $env:GITHUB_ACTIONS -ne 'true') {
    [Console]::Error.WriteLine('verify: -Ci is accepted only in GitHub Actions (GITHUB_ACTIONS=true). Run without it locally.')
    exit 2
}

. (Join-Path $PSScriptRoot 'test-results.ps1')
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$powershell = (Get-Process -Id $PID).Path
$results = Join-Path (Get-ResultsParent $repoRoot) 'latest'
try {
    $mutex = Enter-ResultsDirectory $results
}
catch {
    [Console]::Error.WriteLine("verify: $_")
    exit 1
}

$stages = [System.Collections.Generic.List[object]]::new()
foreach ($name in @('build', 'solution', 'e2e', 'gui-smoke', 'ui-e2e')) {
    $stages.Add([pscustomobject]@{ Name = $name; Status = 'not run'; Seconds = 0; Counts = @(); NotObserved = @(); Problems = @(); Notes = @() })
}
$started = [DateTime]::Now

function Get-Stage([string]$name) { return @($stages | Where-Object { $_.Name -eq $name })[0] }

function Invoke-Script([string]$script, [string[]]$arguments) {
    # Out-Host keeps the child's output on the console instead of in the return value.
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments | Out-Host
    return $LASTEXITCODE
}

# Reads the TRX files of a stage. Each expected assembly must appear exactly once. The files are renamed per assembly.
function Collect-Trx($stage, [string]$directory, [string[]]$expected) {
    $found = @{}
    $files = @()
    if ([System.IO.Directory]::Exists($directory)) { $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.trx' -File) }
    foreach ($file in $files) {
        try { $result = Read-TrxResult $file.FullName }
        catch { $stage.Problems += "unreadable TRX $($file.FullName): $_"; continue }
        $assembly = if ($result.Assemblies.Count -eq 1) { $result.Assemblies[0] } else { '(' + ($result.Assemblies -join ',') + ')' }
        if ($found.ContainsKey($assembly)) { $stage.Problems += "two TRX files for $assembly"; continue }
        $found[$assembly] = $result
        $stage.Problems += @(Test-TrxResult $result $assembly)
        if ($expected -notcontains $assembly) { $stage.Problems += "unexpected TRX: $($file.FullName) ($assembly)" }
        $stage.Counts += ('{0}: {1} executed, {2} passed, {3} failed ({4})' -f $assembly, $result.Executed, $result.Passed, $result.Failed, $result.Outcome)
        $stage.NotObserved += $result.NotObserved
        $target = Join-Path $directory "$assembly.trx"
        if ($file.FullName -ne $target -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $file.FullName -Destination $target }
    }
    foreach ($assembly in $expected) {
        if (-not $found.ContainsKey($assembly)) { $stage.Problems += "no TRX for $assembly (not run, or the test host aborted)" }
    }
}

function Test-InteractiveDesktop {
    if (-not [Environment]::UserInteractive) { return 'the session is not interactive' }
    if ((Get-Process -Id $PID).SessionId -eq 0) { return 'running in session 0 (service session)' }
    if (-not ('UnextractVerifyDesktop' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class UnextractVerifyDesktop
{
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, int length, out int needed);
    // Returns the name of the input desktop, or null when it cannot be opened (for example, the workstation is locked).
    public static string InputDesktopName()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 0x0001 | 0x0100);
        if (desktop == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(256);
            int needed;
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed) ? name.ToString() : null;
        }
        finally { CloseDesktop(desktop); }
    }
}
'@
    }
    $desktop = [UnextractVerifyDesktop]::InputDesktopName()
    if ($null -eq $desktop) { return 'the input desktop cannot be opened (locked workstation, or no interactive desktop)' }
    if ($desktop -ne 'Default') { return "the input desktop is '$desktop', not 'Default'" }
    return $null
}

$failed = $false
$originalExe = [Environment]::GetEnvironmentVariable('UNEXTRACT_E2E_EXE', 'Process')
try {
    foreach ($stage in $stages) {
        if ($failed) { continue }
        if ($stage.Name -eq 'ui-e2e' -and $Ci) { $stage.Status = 'excluded in CI'; continue }
        $directory = Join-Path $results $stage.Name
        $null = New-Item -ItemType Directory -Path $directory
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        Write-Output ''
        Write-Output "=== verify: $($stage.Name) ==="
        try {
            switch ($stage.Name) {
                'build' {
                    & dotnet build (Join-Path $repoRoot 'unextract.sln') -c Release
                    if ($LASTEXITCODE -ne 0) { $stage.Problems += "dotnet build exit code $LASTEXITCODE" }
                }
                'solution' {
                    [Environment]::SetEnvironmentVariable('UNEXTRACT_E2E_EXE', $null, 'Process')
                    try {
                        $exe = Join-Path $repoRoot 'src\Unextract.Cli\bin\Release\net10.0-windows\unextract.exe'
                        $stage.Notes += "E2E exe: $exe"
                        & dotnet test (Join-Path $repoRoot 'unextract.sln') -c Release --no-build --results-directory $directory --logger trx
                        if ($LASTEXITCODE -ne 0) { $stage.Problems += "dotnet test exit code $LASTEXITCODE" }
                    }
                    finally { [Environment]::SetEnvironmentVariable('UNEXTRACT_E2E_EXE', $originalExe, 'Process') }
                    Collect-Trx $stage $directory @('Unextract.Core.Tests', 'Unextract.Cli.Tests', 'Unextract.Windows.Tests', 'Unextract.E2E.Tests', 'Unextract.Gui.Tests')
                }
                'e2e' {
                    $code = Invoke-Script 'run-e2e-tests.ps1' @('-ResultsDirectory', $directory)
                    if ($code -ne 0) { $stage.Problems += "run-e2e-tests.ps1 exit code $code" }
                    $exeFile = Join-Path $directory 'exe.txt'
                    if (Test-Path -LiteralPath $exeFile) { $stage.Notes += "E2E exe: $([System.IO.File]::ReadAllText($exeFile))" }
                    Collect-Trx $stage $directory @('Unextract.E2E.Tests')
                }
                'gui-smoke' {
                    $code = Invoke-Script 'run-gui-smoke-tests.ps1' @()
                    if ($code -ne 0) { $stage.Problems += "run-gui-smoke-tests.ps1 exit code $code" }
                }
                'ui-e2e' {
                    $reason = Test-InteractiveDesktop
                    if ($null -ne $reason) {
                        $stage.Problems += "prerequisite not met: $reason (UI E2E is part of the standard verification)"
                        break
                    }
                    Write-Output 'UI E2E starts now. Do not touch the mouse or keyboard until it finishes.'
                    $code = Invoke-Script 'run-gui-ui-tests.ps1' @('-ResultsDirectory', $directory)
                    if ($code -ne 0) { $stage.Problems += "run-gui-ui-tests.ps1 exit code $code" }
                    Collect-Trx $stage $directory @('Unextract.Gui.UiTests')
                }
            }
        }
        catch {
            $stage.Problems += "stage error: $_"
        }
        finally {
            $stage.Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
            if ($stage.Problems.Count -gt 0) { $stage.Status = 'FAILED'; $failed = $true } else { $stage.Status = 'passed' }
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('UNEXTRACT_E2E_EXE', $originalExe, 'Process')
    $notObserved = @($stages | ForEach-Object { $_.NotObserved })
    $verdict = if ($failed) { 'FAILED' } elseif ($notObserved.Count -gt 0) { "PASSED (not observed: $($notObserved.Count))" } else { 'PASSED' }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("unextract standard verification: $verdict")
    $lines.Add("checkout: $repoRoot")
    $lines.Add("started: $($started.ToString('yyyy-MM-dd HH:mm:ss')), total $([Math]::Round(([DateTime]::Now - $started).TotalSeconds, 1)) s")
    $lines.Add("mode: $(if ($Ci) { 'CI (ui-e2e excluded)' } else { 'local (all stages)' })")
    $lines.Add('')
    foreach ($stage in $stages) {
        $lines.Add("[$($stage.Status)] $($stage.Name)$(if ($stage.Seconds -gt 0) { " ($($stage.Seconds) s)" })")
        foreach ($item in $stage.Notes) { $lines.Add("    $item") }
        foreach ($item in $stage.Counts) { $lines.Add("    $item") }
        foreach ($item in $stage.NotObserved) { $lines.Add("    not observed: $item") }
        foreach ($item in $stage.Problems) { $lines.Add("    problem: $item") }
    }
    $lines.Add('')
    $lines.Add("results: $results")
    try {
        [System.IO.File]::WriteAllLines((Join-Path $results 'summary.txt'), $lines, (New-Object System.Text.UTF8Encoding($false)))
    }
    catch {
        $lines.Add("problem: cannot write summary.txt: $_")
        $verdict = 'FAILED'
        $failed = $true
    }
    Write-Output ''
    foreach ($line in $lines) { Write-Output $line }
    if ($Ci -and $env:GITHUB_OUTPUT) { [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, "results=$results`n") }
    Exit-ResultsDirectory $mutex
}

if ($failed) { exit 1 }
exit 0
