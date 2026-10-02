<#
.SYNOPSIS
    Publishes a temporary single-file exe, runs E2E (including PTY), and cleans up.
.DESCRIPTION
    Always uses its own publish, temporarily overriding UNEXTRACT_E2E_EXE in this
    process only. Existing executables, build outputs and test fixtures are not
    removed. PTY tests clean up their own fixtures; other fixtures follow the
    existing manual cleanup policy.
    Returns the publish/test exit code. A wrapper or cleanup error returns 1
    when no publish/test failure has already occurred.
    ASCII only for Windows PowerShell 5.1 compatibility.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-e2e-tests.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
# Keep native failures as exit codes, also when invoked from PowerShell 7.
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
$name = 'unextract-e2e-publish-' + [Guid]::NewGuid().ToString('N')
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $tempRoot $name))
$createdDirectory = $null
$originalExe = [Environment]::GetEnvironmentVariable('UNEXTRACT_E2E_EXE', 'Process')
$exitCode = 1
$stage = 'setup'

try {
    # New-Item fails if the directory already exists. Ownership starts only
    # after successful creation; never derive a cleanup path from the exe env.
    $createdDirectory = (New-Item -ItemType Directory -Path $publishDirectory -ErrorAction Stop).FullName
    Write-Output "Temporary publish: $createdDirectory"

    $stage = 'publish'
    & dotnet publish (Join-Path $repoRoot 'src/Unextract.Cli') -c Release -p:PublishProfile=win-x64 -o $createdDirectory
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        $stage = 'exe validation'
        $exe = Join-Path $createdDirectory 'unextract.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
            throw "Published exe does not exist: $exe"
        }
        [Environment]::SetEnvironmentVariable('UNEXTRACT_E2E_EXE', $exe, 'Process')
        $stage = 'test'
        & dotnet test (Join-Path $repoRoot 'tests/Unextract.E2E.Tests') -c Release --logger 'console;verbosity=detailed'
        $exitCode = $LASTEXITCODE
    }
}
catch {
    [Console]::Error.WriteLine("run-e2e-tests: $stage failed: $_")
    if ($exitCode -eq 0) { $exitCode = 1 }
}
finally {
    [Environment]::SetEnvironmentVariable('UNEXTRACT_E2E_EXE', $originalExe, 'Process')
    if ($null -ne $createdDirectory) {
        try {
            # Require the exact directory this invocation created, directly
            # under the captured OS temp root, with this invocation's GUID.
            $full = [System.IO.Path]::GetFullPath($createdDirectory)
            if (-not [string]::Equals($full, $publishDirectory, [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals([System.IO.Path]::GetDirectoryName($full), $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
                [System.IO.Path]::GetFileName($full) -ne $name) {
                throw "Cannot establish cleanup ownership: $full"
            }
            # Refuse reparse points without traversing them. Publish only
            # creates ordinary files/directories; do not follow unexpected links.
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
            Write-Output "Removed temporary publish: $full"
        }
        catch {
            [Console]::Error.WriteLine("run-e2e-tests: cleanup failed: $createdDirectory; $stage exit code before cleanup: $exitCode; $_")
            if ($exitCode -eq 0) { $exitCode = 1 }
        }
    }
}

exit $exitCode
