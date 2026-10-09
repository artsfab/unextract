<#
.SYNOPSIS
    Shared helpers for the result directories of verify.ps1 and run-gui-ui-tests.ps1 (dot-source this file).
.DESCRIPTION
    Result directories are outside the repository:
      %TEMP%\unextract-verify\<checkout id>\latest           standard verification (verify.ps1)
      %TEMP%\unextract-verify\<checkout id>\ui-debug-latest  run-gui-ui-tests.ps1 run on its own
    The checkout id is derived from the normalized checkout path, so separate checkouts never share a directory.
    An entry point takes the named mutex of its result directory, then empties only that directory, and keeps the
    mutex until it has written its summary. A directory in use is reported and left unchanged. The mutex is not a
    lock over the tests themselves. Test fixtures are never touched here.
    ASCII only for Windows PowerShell 5.1 compatibility.
#>

Set-StrictMode -Version 3.0

function Get-ResultsParent([string]$RepoRoot) {
    $normalized = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized))
    }
    finally { $sha.Dispose() }
    $id = -join ($hash[0..7] | ForEach-Object { $_.ToString('x2') })
    $leaf = [System.IO.Path]::GetFileName($normalized) -replace '[^a-z0-9._-]', '_'
    $temp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    return [System.IO.Path]::Combine($temp, 'unextract-verify', "$leaf-$id")
}

# Every existing component of the path, from the drive root down, must be an ordinary directory.
function Assert-NoReparseOnPath([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $current = $full
    while (-not [string]::IsNullOrEmpty($current)) {
        if ([System.IO.Directory]::Exists($current) -or [System.IO.File]::Exists($current)) {
            $attributes = [System.IO.File]::GetAttributes($current)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point on the result path: $current"
            }
            if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0) {
                throw "Not a directory on the result path: $current"
            }
        }
        $current = [System.IO.Path]::GetDirectoryName($current)
    }
}

# Takes the mutex of the result directory and recreates it empty. Returns the held mutex.
# Throws when another entry point holds it; the directory is then left unchanged.
function Enter-ResultsDirectory([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($full.ToLowerInvariant())) }
    finally { $sha.Dispose() }
    $name = 'Global\unextract-results-' + (-join ($hash[0..11] | ForEach-Object { $_.ToString('x2') }))
    $mutex = New-Object System.Threading.Mutex($false, $name)
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [System.Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) {
            throw "The result directory is in use by another run (left unchanged): $full"
        }
        Assert-NoReparseOnPath $full
        if ([System.IO.Directory]::Exists($full)) {
            # Directory.Delete(recursive) removes links under the directory without entering them.
            [System.IO.Directory]::Delete($full, $true)
        }
        $null = [System.IO.Directory]::CreateDirectory($full)
        Assert-NoReparseOnPath $full
        return $mutex
    }
    catch {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
        throw
    }
}

function Exit-ResultsDirectory($Mutex) {
    if ($null -eq $Mutex) { return }
    try { $Mutex.ReleaseMutex() } finally { $Mutex.Dispose() }
}

# Environment-dependent observations that the tests report as precondition not met and that the
# standard verification counts separately as "not observed": test method name -> required prefix of the reason.
# Any other precondition report fails the verification. The prefix is built from code points (ASCII-only file).
$script:NotMetPrefix = (-join @([char]0x524D, [char]0x63D0, [char]0x4E0D, [char]0x6210, [char]0x7ACB)) + ': '
$script:AllowedNotMet = @(
    @{ Method = 'Unextract.Windows.Tests.DirectoryEnumeratorTests.Enumerate_DoesNotReturnShortNames'; Reason = '' },
    @{ Method = 'Unextract.Windows.Tests.Integration.ClassificationIntegrationTests.T06_ShortNameOnly_IsMissing'; Reason = 'T06 (8.3' },
    @{ Method = 'Unextract.Windows.Tests.Integration.ClassificationIntegrationTests.T08_CompressedAndSparse_AreMatched'; Reason = 'T08 (' },
    @{ Method = 'Unextract.Windows.Tests.Integration.ClassificationIntegrationTests.T15_CaseSensitiveDirectory'; Reason = 'T15: ' }
)

# Reads one TRX file. Returns the assembly, counters, failures, and precondition reports split into the fixed
# "not observed" cases and unknown ones.
function Read-TrxResult([string]$Path) {
    [xml]$report = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $run = $report.TestRun
    $assemblies = @()
    $methods = @{}
    if ($null -ne $run.PSObject.Properties['TestDefinitions'] -and $null -ne $run.TestDefinitions) {
        foreach ($unitTest in @($run.TestDefinitions.UnitTest)) {
            $assemblies += [System.IO.Path]::GetFileNameWithoutExtension([string]$unitTest.TestMethod.codeBase)
            $methods[[string]$unitTest.id] = ([string]$unitTest.TestMethod.className) + '.' + ([string]$unitTest.TestMethod.name)
        }
    }
    $assemblies = @($assemblies | Sort-Object -Unique)
    $counters = $run.ResultSummary.Counters
    $failures = @()
    $notObserved = @()
    $unknown = @()
    if ($null -ne $run.PSObject.Properties['Results'] -and $null -ne $run.Results) {
        foreach ($result in @($run.Results.UnitTestResult)) {
            if ([string]$result.outcome -ne 'Passed') { $failures += "$($result.testName): $($result.outcome)" }
            $stdout = $null
            if ($null -ne $result.PSObject.Properties['Output'] -and $null -ne $result.Output -and
                $null -ne $result.Output.PSObject.Properties['StdOut']) { $stdout = [string]$result.Output.StdOut }
            if ([string]::IsNullOrEmpty($stdout)) { continue }
            foreach ($line in ($stdout -split "`r?`n")) {
                $index = $line.IndexOf($script:NotMetPrefix, [StringComparison]::Ordinal)
                if ($index -lt 0) { continue }
                $reason = $line.Substring($index + $script:NotMetPrefix.Length)
                $method = $methods[[string]$result.testId]
                $allowed = @($script:AllowedNotMet | Where-Object { $_.Method -eq $method -and $reason.StartsWith($_.Reason, [StringComparison]::Ordinal) })
                if ($allowed.Count -gt 0 -and [string]$result.outcome -eq 'Passed') { $notObserved += "$($result.testName): $reason" }
                else { $unknown += "$($result.testName): $reason" }
            }
        }
    }
    return [pscustomobject]@{
        Path = $Path
        Assemblies = $assemblies
        Outcome = [string]$run.ResultSummary.outcome
        Total = [int]$counters.total
        Executed = [int]$counters.executed
        Passed = [int]$counters.passed
        Failed = [int]$counters.failed
        Failures = $failures
        NotObserved = $notObserved
        UnknownNotMet = $unknown
    }
}

# Checks one TRX result. Returns a list of problems (empty when the result passes).
function Test-TrxResult($Result, [string]$ExpectedAssembly) {
    $problems = @()
    if ($Result.Assemblies.Count -ne 1 -or $Result.Assemblies[0] -ne $ExpectedAssembly) {
        $problems += "unexpected assemblies in $($Result.Path): $($Result.Assemblies -join ', ')"
    }
    if ($Result.Executed -eq 0) { $problems += "$ExpectedAssembly executed no test" }
    if ($Result.Outcome -ne 'Completed') { $problems += "$ExpectedAssembly run outcome: $($Result.Outcome) (test host aborted or failed)" }
    if ($Result.Failed -ne 0 -or $Result.Passed -ne $Result.Executed -or $Result.Total -ne $Result.Executed) {
        $problems += "$ExpectedAssembly total=$($Result.Total) executed=$($Result.Executed) passed=$($Result.Passed) failed=$($Result.Failed)"
    }
    foreach ($failure in $Result.Failures) { $problems += "$ExpectedAssembly failed: $failure" }
    foreach ($item in $Result.UnknownNotMet) { $problems += "$ExpectedAssembly unknown precondition report: $item" }
    return $problems
}
