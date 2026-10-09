<#
.SYNOPSIS
    Publishes the GUI (and a copy with a fake CLI) into a temporary directory, runs the GUI UI E2E tests, and cleans up.
.DESCRIPTION
    The only entry point of tests/Unextract.Gui.UiTests (FlaUI UIA3). The tests are opt-in: the project is built
    by the solution but is a test project only with -p:UnextractUiTests=true, so "dotnet test unextract.sln" never
    runs it. This script
      1. publishes the GUI distribution with scripts/publish-gui.ps1 into a new temporary directory (real CLI),
      2. copies it without cli\ and publishes the fake CLI (tests/Unextract.Gui.FakeCli) as cli\unextract.exe,
      3. passes both package paths through UNEXTRACT_UI_GUI_PACKAGE / UNEXTRACT_UI_FAKE_PACKAGE (restored afterwards),
      4. runs the UI tests and fails when no test was executed,
      5. removes only the temporary directory it created (the results are kept).
    Results (ui.trx and per-test diagnostics) go to -ResultsDirectory when verify.ps1 passes it (verify.ps1 owns
    that directory and its mutex), otherwise to %TEMP%\unextract-verify\<checkout id>\ui-debug-latest, which this
    script empties under its named mutex (see test-results.ps1). The standard verification result is never overwritten.
    -Shots also saves pictures of the OS message boxes that the STA render tests cannot draw (UNEXTRACT_GUI_SHOTS =
    <results>\shots); ordinary runs take none (docs/TESTING.md#gui-review).
    Requirements: an unlocked interactive desktop. Do not touch the mouse or keyboard while it runs. Remote desktop
    sessions that are minimized, service sessions and running several UI test sessions at once are not supported.
    The tests do not publish anything themselves.
    ASCII only for Windows PowerShell 5.1 compatibility.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-gui-ui-tests.ps1
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [switch]$Detailed,
    [string]$ResultsDirectory,
    [switch]$Shots
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
# Keep native failures as exit codes, also when invoked from PowerShell 7.
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
$name = 'unextract-ui-tests-' + [Guid]::NewGuid().ToString('N')
$workDirectory = [System.IO.Path]::GetFullPath((Join-Path $tempRoot $name))
$createdDirectory = $null
$variables = @('UNEXTRACT_UI_GUI_PACKAGE', 'UNEXTRACT_UI_FAKE_PACKAGE', 'UNEXTRACT_FAKE_CLI_SCENARIO', 'UNEXTRACT_GUI_TEST_DATA_ROOT', 'UNEXTRACT_UI_RESULTS', 'UNEXTRACT_GUI_SHOTS')
$originals = @{}
foreach ($variable in $variables) { $originals[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
$exitCode = 1
$stage = 'setup'
$resultsMutex = $null
. (Join-Path $PSScriptRoot 'test-results.ps1')

try {
    if ($ResultsDirectory) {
        $results = [System.IO.Path]::GetFullPath($ResultsDirectory)
        if (-not [System.IO.Directory]::Exists($results)) { throw "Results directory does not exist: $results" }
    }
    else {
        $results = Join-Path (Get-ResultsParent $repoRoot) 'ui-debug-latest'
        $resultsMutex = Enter-ResultsDirectory $results
    }
    Write-Output "Results: $results"

    # New-Item fails if the directory already exists. Ownership starts only after successful creation.
    $createdDirectory = (New-Item -ItemType Directory -Path $workDirectory -ErrorAction Stop).FullName
    Write-Output "Temporary directory: $createdDirectory"

    $stage = 'publish GUI'
    $real = Join-Path $createdDirectory 'real'
    & (Join-Path $PSScriptRoot 'publish-gui.ps1') -OutputDirectory $real
    if ($LASTEXITCODE -ne 0) { throw "GUI publish failed: $LASTEXITCODE" }

    $stage = 'fake package'
    $fake = Join-Path $createdDirectory 'fake'
    New-Item -ItemType Directory -Path $fake -ErrorAction Stop | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $real) {
        if ($item.Name -ne 'cli') { Copy-Item -LiteralPath $item.FullName -Destination $fake -Recurse }
    }
    & dotnet publish (Join-Path $repoRoot 'tests/Unextract.Gui.FakeCli') -c Release -p:PublishProfile=win-x64 -p:AssemblyName=unextract -o (Join-Path $fake 'cli')
    if ($LASTEXITCODE -ne 0) { throw "Fake CLI publish failed: $LASTEXITCODE" }
    foreach ($exe in @((Join-Path $real 'unextract-gui.exe'), (Join-Path $real 'cli\unextract.exe'),
                       (Join-Path $fake 'unextract-gui.exe'), (Join-Path $fake 'cli\unextract.exe'))) {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
    }

    $stage = 'test'
    [Environment]::SetEnvironmentVariable('UNEXTRACT_UI_GUI_PACKAGE', $real, 'Process')
    [Environment]::SetEnvironmentVariable('UNEXTRACT_UI_FAKE_PACKAGE', $fake, 'Process')
    # The tests set these per GUI process; never inherit them from the caller.
    [Environment]::SetEnvironmentVariable('UNEXTRACT_FAKE_CLI_SCENARIO', $null, 'Process')
    [Environment]::SetEnvironmentVariable('UNEXTRACT_GUI_TEST_DATA_ROOT', $null, 'Process')
    [Environment]::SetEnvironmentVariable('UNEXTRACT_UI_RESULTS', $results, 'Process')
    [Environment]::SetEnvironmentVariable('UNEXTRACT_GUI_SHOTS', $(if ($Shots) { Join-Path $results 'shots' } else { $null }), 'Process')
    $arguments = @((Join-Path $repoRoot 'tests/Unextract.Gui.UiTests'), '-c', 'Release', '-p:UnextractUiTests=true',
                   '--results-directory', $results, '--logger', 'trx;LogFileName=ui.trx', '--logger', ('console;verbosity=' + $(if ($Detailed) { 'detailed' } else { 'normal' })))
    if ($Filter) { $arguments += @('--filter', $Filter) }
    & dotnet test @arguments
    $exitCode = $LASTEXITCODE

    $stage = 'result check'
    $trx = Join-Path $results 'ui.trx'
    if (-not (Test-Path -LiteralPath $trx -PathType Leaf)) { throw 'No test result file was written.' }
    [xml]$report = Get-Content -Raw -LiteralPath $trx
    $counters = $report.TestRun.ResultSummary.Counters
    Write-Output ("UI tests: total={0} executed={1} passed={2} failed={3}" -f $counters.total, $counters.executed, $counters.passed, $counters.failed)
    if ([int]$counters.executed -eq 0) { throw 'No UI test was executed.' }
    if ($exitCode -eq 0 -and ([int]$counters.failed -ne 0 -or [int]$counters.passed -ne [int]$counters.executed)) {
        throw 'The result file reports failures or skipped tests.'
    }
}
catch {
    [Console]::Error.WriteLine("run-gui-ui-tests: $stage failed: $_")
    if ($exitCode -eq 0) { $exitCode = 1 }
}
finally {
    foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, $originals[$variable], 'Process') }
    if ($null -ne $createdDirectory) {
        try {
            # Require the exact directory this invocation created, directly under the captured OS temp root.
            $full = [System.IO.Path]::GetFullPath($createdDirectory)
            if (-not [string]::Equals($full, $workDirectory, [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals([System.IO.Path]::GetDirectoryName($full), $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
                [System.IO.Path]::GetFileName($full) -ne $name) {
                throw "Cannot establish cleanup ownership: $full"
            }
            # Refuse reparse points without traversing them. Publish only creates ordinary files and directories.
            $pending = [System.Collections.Generic.Stack[string]]::new()
            $pending.Push($full)
            while ($pending.Count -gt 0) {
                $path = $pending.Pop()
                $attributes = [System.IO.File]::GetAttributes($path)
                if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Unexpected reparse point: $path"
                }
                if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    foreach ($child in [System.IO.Directory]::EnumerateFileSystemEntries($path)) {
                        $pending.Push($child)
                    }
                }
            }
            [System.IO.Directory]::Delete($full, $true)
            Write-Output "Removed temporary directory: $full"
        }
        catch {
            [Console]::Error.WriteLine("run-gui-ui-tests: cleanup failed: $createdDirectory; $stage exit code before cleanup: $exitCode; $_")
            if ($exitCode -eq 0) { $exitCode = 1 }
        }
    }
    Exit-ResultsDirectory $resultsMutex
}

exit $exitCode
