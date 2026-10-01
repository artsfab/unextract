<#
.SYNOPSIS
    Lists (default) or removes the test fixtures left under tests/*/bin/*/*/fixtures.

.DESCRIPTION
    The tests never delete their own fixtures (docs/PLAN_TESTS.md). This script is the manual
    cleanup for developers. Without -Execute it only lists what it would do and changes nothing.

    With -Execute it runs, in this order, and stops at the first failure:
      1. In P03_* fixtures, removes the DENY ACEs of the current user's SID (icacls /remove:d).
      2. Removes every junction / directory link one by one with "rmdir" (no /s), and checks that
         the link is gone and that its target (if it existed) still exists.
      3. Removes each fixtures directory with "cmd /c rmdir /s /q", and checks that it is gone.
    Remove-Item -Recurse is never used.

    Only directories matching tests/*/bin/*/*/fixtures relative to the repository root are
    handled. Each one (and each path component below the repository root) must be a real
    directory (not a reparse point) under <repo>\tests, otherwise the script stops with an error.

    This file is ASCII only so that Windows PowerShell 5.1 parses it correctly without a BOM.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\clean-test-fixtures.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\clean-test-fixtures.ps1 -Execute
#>
[CmdletBinding()]
param(
    [switch]$Execute
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$ReparsePoint = [System.IO.FileAttributes]::ReparsePoint

function Fail([string]$message) {
    throw "clean-test-fixtures: $message"
}

function Test-Reparse([System.IO.FileSystemInfo]$info) {
    return (($info.Attributes -band $ReparsePoint) -ne 0)
}

# True if the name exists. GetAttributes does not follow reparse points, so a dangling junction counts
# as existing (Test-Path would report it as missing).
function Test-Entry([string]$path) {
    try {
        $null = [System.IO.File]::GetAttributes($path)
        return $true
    } catch [System.IO.FileNotFoundException], [System.IO.DirectoryNotFoundException] {
        return $false
    }
}

# Repository root = parent of this script's directory. Fixtures must be under <repo>\tests\.
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'tests'))
$testsPrefix = $testsRoot.TrimEnd('\') + '\'

function Assert-UnderTests([string]$path) {
    $full = [System.IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($testsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "resolved path is outside $testsRoot : $full"
    }
    return $full
}

# Child directories of $dir that are real directories (reparse points are rejected, not followed).
function Get-RealSubdirectories([string]$dir) {
    $result = @()
    foreach ($child in ([System.IO.DirectoryInfo]::new($dir)).EnumerateDirectories()) {
        if (Test-Reparse $child) {
            Fail "unexpected reparse point on the fixtures path: $($child.FullName)"
        }
        $result += $child
    }
    return $result
}

# Resolve tests/*/bin/*/*/fixtures without following any reparse point.
function Find-FixtureRoots {
    $testsInfo = [System.IO.DirectoryInfo]::new($testsRoot)
    if (-not $testsInfo.Exists) {
        Fail "tests directory not found: $testsRoot"
    }
    if (Test-Reparse $testsInfo) {
        Fail "tests directory is a reparse point: $testsRoot"
    }

    $roots = @()
    foreach ($project in (Get-RealSubdirectories $testsRoot)) {
        $bin = [System.IO.DirectoryInfo]::new((Join-Path $project.FullName 'bin'))
        if (-not $bin.Exists) { continue }
        if (Test-Reparse $bin) { Fail "bin is a reparse point: $($bin.FullName)" }
        foreach ($configuration in (Get-RealSubdirectories $bin.FullName)) {
            foreach ($tfm in (Get-RealSubdirectories $configuration.FullName)) {
                $fixtures = [System.IO.DirectoryInfo]::new((Join-Path $tfm.FullName 'fixtures'))
                if (-not $fixtures.Exists) { continue }
                if (Test-Reparse $fixtures) { Fail "fixtures is a reparse point: $($fixtures.FullName)" }
                $roots += (Assert-UnderTests $fixtures.FullName)
            }
        }
    }
    return $roots
}

# Walk a tree without entering reparse points. Returns @{ Items; Links; Errors }.
function Get-Tree([string]$root) {
    $items = 0
    $links = @()
    $errors = @()
    $stack = New-Object System.Collections.Stack
    $stack.Push($root)
    while ($stack.Count -gt 0) {
        $dir = [string]$stack.Pop()
        try {
            $children = @(([System.IO.DirectoryInfo]::new($dir)).EnumerateFileSystemInfos())
        } catch {
            $errors += "cannot enumerate $dir : $($_.Exception.Message)"
            continue
        }
        foreach ($child in $children) {
            $items++
            if ($child -is [System.IO.DirectoryInfo]) {
                if (Test-Reparse $child) {
                    $links += $child.FullName
                } else {
                    $stack.Push($child.FullName)
                }
            }
        }
    }
    return @{ Items = $items; Links = $links; Errors = $errors }
}

function Get-LinkTarget([string]$link) {
    $target = (Get-Item -LiteralPath $link -Force).Target
    if ($null -eq $target) { return $null }
    return [string](@($target)[0])
}

$currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$sidType = [System.Security.Principal.SecurityIdentifier]

# Visit a P03_* fixture (itself and everything below it, without following links). With -Restore, the
# DENY ACEs of the current user are removed from each item before its children are enumerated (a DENY on a
# directory can prevent listing it). Returns @{ Denies; Restored; Errors }.
function Invoke-P03Walk([string]$p03, [switch]$Restore) {
    $denies = 0
    $restored = @()
    $errors = @()
    $stack = New-Object System.Collections.Stack
    $stack.Push($p03)
    while ($stack.Count -gt 0) {
        $path = [string]$stack.Pop()
        $count = Get-OwnDenyCount $path
        $denies += $count
        if ($Restore -and $count -gt 0) {
            $null = Assert-UnderTests $path
            & icacls.exe $path /remove:d "*$currentSid" | Out-Null
            if ($LASTEXITCODE -ne 0) { Fail "icacls /remove:d failed (exit $LASTEXITCODE): $path" }
            if ((Get-OwnDenyCount $path) -ne 0) { Fail "DENY ACE still present after icacls: $path" }
            $restored += $path
        }
        $info = [System.IO.DirectoryInfo]::new($path)
        if ((-not $info.Exists) -or (Test-Reparse $info)) { continue }
        try {
            $children = @($info.EnumerateFileSystemInfos())
        } catch {
            if ($Restore) { Fail "cannot enumerate $path : $($_.Exception.Message)" }
            $errors += "cannot enumerate $path : $($_.Exception.Message)"
            continue
        }
        foreach ($child in $children) {
            $stack.Push($child.FullName)
        }
    }
    return @{ Denies = $denies; Restored = $restored; Errors = $errors }
}

function Get-OwnDenyCount([string]$path) {
    $acl = Get-Acl -LiteralPath $path
    $count = 0
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne 'Deny') { continue }
        try {
            $sid = $rule.IdentityReference.Translate($sidType).Value
        } catch {
            continue
        }
        if ($sid -eq $currentSid) { $count++ }
    }
    return $count
}

# Runs one cmd.exe command line. The argument string is passed verbatim (no PowerShell quoting rules);
# /s makes cmd strip exactly the outer quotes, /d skips AutoRun. Output goes to the console.
function Invoke-Cmd([string]$command) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c `"$command`""
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    $process.WaitForExit()
    return $process.ExitCode
}

# ---------------------------------------------------------------------------------------------

$mode = if ($Execute) { 'EXECUTE' } else { 'LIST ONLY (nothing is changed; use -Execute to clean)' }
Write-Output "Mode: $mode"
Write-Output "Repository: $repoRoot"
Write-Output "Current user SID: $currentSid"

$fixtureRoots = @(Find-FixtureRoots)
if ($fixtureRoots.Count -eq 0) {
    Write-Output 'No fixtures directories found.'
    exit 0
}

$p03Dirs = @()
foreach ($root in $fixtureRoots) {
    foreach ($child in ([System.IO.DirectoryInfo]::new($root)).EnumerateDirectories('P03_*')) {
        if (Test-Reparse $child) { Fail "P03 fixture is a reparse point: $($child.FullName)" }
        $p03Dirs += (Assert-UnderTests $child.FullName)
    }
}

# --- Listing (always) ---
$totalLinks = 0
foreach ($root in $fixtureRoots) {
    $tree = Get-Tree $root
    $fixtureCount = @(([System.IO.DirectoryInfo]::new($root)).EnumerateDirectories()).Count
    Write-Output ''
    Write-Output "[fixtures] $root"
    Write-Output "  fixture directories: $fixtureCount, items (not following links): $($tree.Items), links: $($tree.Links.Count)"
    foreach ($link in $tree.Links) {
        $target = Get-LinkTarget $link
        $exists = ($null -ne $target) -and (Test-Entry $target)
        Write-Output "  link: $link -> $target (target exists: $exists)"
    }
    foreach ($e in $tree.Errors) {
        Write-Output "  WARNING: $e"
    }
    $totalLinks += $tree.Links.Count
}

Write-Output ''
Write-Output "P03 fixtures: $($p03Dirs.Count)"
foreach ($p03 in $p03Dirs) {
    $walk = Invoke-P03Walk $p03
    Write-Output "  $p03 (DENY ACEs of current user: $($walk.Denies))"
    foreach ($e in $walk.Errors) {
        Write-Output "  WARNING: $e"
    }
}

if (-not $Execute) {
    Write-Output ''
    Write-Output "Would run: (1) icacls /remove:d on $($p03Dirs.Count) P03 fixture(s), (2) rmdir on $totalLinks link(s), (3) rmdir /s /q on $($fixtureRoots.Count) fixtures directory(ies)."
    Write-Output 'Nothing was changed.'
    exit 0
}

# --- (1) Remove own DENY ACEs in P03_* ---
Write-Output ''
Write-Output '(1) Removing DENY ACEs of the current user in P03 fixtures'
foreach ($p03 in $p03Dirs) {
    $walk = Invoke-P03Walk $p03 -Restore
    foreach ($p in $walk.Restored) {
        Write-Output "  restored: $p"
    }
}

# --- (2) Remove links one by one (rmdir without /s) ---
Write-Output ''
Write-Output '(2) Removing links (rmdir without /s)'
foreach ($root in $fixtureRoots) {
    $tree = Get-Tree $root
    if ($tree.Errors.Count -gt 0) { Fail ($tree.Errors -join '; ') }
    foreach ($link in $tree.Links) {
        $null = Assert-UnderTests $link
        $target = Get-LinkTarget $link
        $targetExisted = ($null -ne $target) -and (Test-Entry $target)
        $code = Invoke-Cmd "rmdir `"$link`""
        if ($code -ne 0) { Fail "rmdir failed (exit $code): $link" }
        if (Test-Entry $link) { Fail "link still exists after rmdir: $link" }
        if ($targetExisted -and -not (Test-Entry $target)) {
            Fail "link target disappeared after removing the link: $link -> $target"
        }
        Write-Output "  removed link: $link (target kept: $target)"
    }
}

# --- (3) Remove the fixtures directories (cmd /c rmdir /s /q) ---
Write-Output ''
Write-Output '(3) Removing fixtures directories (rmdir /s /q)'
foreach ($root in $fixtureRoots) {
    $tree = Get-Tree $root
    if ($tree.Errors.Count -gt 0) { Fail ($tree.Errors -join '; ') }
    if ($tree.Links.Count -gt 0) { Fail "links remain under $root ; not removing" }
    $null = Assert-UnderTests $root
    $code = Invoke-Cmd "rmdir /s /q `"$root`""
    if ($code -ne 0) { Fail "rmdir /s /q failed (exit $code): $root" }
    if (Test-Entry $root) { Fail "directory still exists after rmdir /s /q: $root" }
    Write-Output "  removed: $root"
}

Write-Output ''
Write-Output 'Done.'
