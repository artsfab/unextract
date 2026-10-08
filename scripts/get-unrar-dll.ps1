<#
.SYNOPSIS
    Downloads the pinned UnRAR.dll 7.23 (x64) for development and CI tests.
.DESCRIPTION
    UnRAR64.dll is not stored in the repository nor shipped with the product
    (docs/spec/rar.md#pinning). Tests that need the real DLL read it from
    UNEXTRACT_TEST_UNRAR_DLL, or from the default location below when the
    variable is not set (docs/TESTING.md#rar). This script prepares that file.

    Steps: download rarlab's unrardll-723.exe (or use -InstallerPath), check
    its SHA-256, extract only x64\UnRAR64.dll with 7-Zip or UnRAR.exe (the
    self-extracting exe is never run), check the DLL's SHA-256 and place it in
    the output directory. Nothing is written inside the repository.

    The output directory must not be inside the repository. An existing
    UnRAR64.dll with the pinned SHA-256 is kept; a different file is an error
    (it is not overwritten).
    Prints the absolute path of the DLL. ASCII only for Windows PowerShell 5.1.
.PARAMETER OutputDirectory
    Destination folder. Default: %LOCALAPPDATA%\unextract-dev\unrar-7.23
.PARAMETER InstallerPath
    An already downloaded unrardll-723.exe (no network access).
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\get-unrar-dll.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$InstallerPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Pinned values (docs/RATIONALE.md#rar-dll-usage, src/Unextract.Windows/Rar/UnrarLibrary.cs).
$installerUrl = 'https://www.rarlab.com/rar/unrardll-723.exe'
$installerSha256 = '68b064b34691988158c4126d3cf422f4e74a7d1d618c26bafc93b4e502b88b55'
$dllSha256 = '894b7d2db8d6363eb12f30c7b89f48eab9e71963b8b438675bdd64c12dd59bcc'
$dllName = 'UnRAR64.dll'

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Find-Extractor {
    foreach ($candidate in @(
            (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
            (Join-Path $env:ProgramFiles 'WinRAR\UnRAR.exe'))) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    throw 'Neither 7-Zip (7z.exe) nor WinRAR UnRAR.exe was found.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if ([string]::IsNullOrEmpty($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:LOCALAPPDATA 'unextract-dev\unrar-7.23'
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($output.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be outside the repository: $output"
}

$destination = Join-Path $output $dllName
if (Test-Path -LiteralPath $destination) {
    if ((Get-Sha256 $destination) -ne $dllSha256) {
        throw "An existing file does not match the pinned SHA-256 (not overwritten): $destination"
    }
    Write-Output $destination
    exit 0
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('unextract-unrar-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ([string]::IsNullOrEmpty($InstallerPath)) {
        $installer = Join-Path $work 'unrardll-723.exe'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $installerUrl -OutFile $installer -UseBasicParsing
    }
    else {
        $installer = [System.IO.Path]::GetFullPath($InstallerPath)
    }
    if ((Get-Sha256 $installer) -ne $installerSha256) {
        throw "unrardll-723.exe does not match the pinned SHA-256: $installer"
    }

    $extractor = Find-Extractor
    $extracted = Join-Path $work 'extracted'
    New-Item -ItemType Directory -Path $extracted | Out-Null
    if ([System.IO.Path]::GetFileName($extractor) -ieq '7z.exe') {
        & $extractor x -y "-o$extracted" $installer 'x64\UnRAR64.dll' | Out-Null
    }
    else {
        & $extractor x -y -inul $installer 'x64\UnRAR64.dll' ($extracted + '\') | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "Extraction failed ($extractor, exit $LASTEXITCODE)." }

    $dll = Join-Path $extracted "x64\$dllName"
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "x64\$dllName was not extracted." }
    if ((Get-Sha256 $dll) -ne $dllSha256) { throw "$dllName does not match the pinned SHA-256." }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item -LiteralPath $dll -Destination $destination
    if ((Get-Sha256 $destination) -ne $dllSha256) { throw "Copied $dllName does not match the pinned SHA-256." }
    Write-Output $destination
}
finally {
    # Only the files this invocation created in its own GUID work directory.
    foreach ($file in @((Join-Path $work 'extracted\x64\UnRAR64.dll'), (Join-Path $work 'unrardll-723.exe'))) {
        if (Test-Path -LiteralPath $file -PathType Leaf) { [System.IO.File]::Delete($file) }
    }
    foreach ($dir in @((Join-Path $work 'extracted\x64'), (Join-Path $work 'extracted'), $work)) {
        if ((Test-Path -LiteralPath $dir -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $dir -Force | Select-Object -First 1)) {
            [System.IO.Directory]::Delete($dir)
        }
    }
}
