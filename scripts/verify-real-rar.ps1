<#
.SYNOPSIS
    Local-only check of unextract against real RAR archives made by WinRAR.
.DESCRIPTION
    Real WinRAR fixtures are local verification only: they are not stored in
    the repository and CI does not run this script (docs/TESTING.md#rar).
    Rar.exe of an installed WinRAR (trial period included, within its license
    terms) creates RAR5 archives from data generated here, UnRAR.exe extracts
    them into targets, and the given unextract.exe is run with --jsonl.

    Checks: compressed (-m3/-m5), stored, BLAKE2 (-htb), -rr/-qo/-ts, -md1g
    are accepted (Strict MATCHED, Fast SAME_SIZE, 1-bit change MODIFIED);
    Strict delete removes only matched files; Fast deletes a same-size
    different file; --entries with a Japanese name; corrupted compressed data
    is never MATCHED (FATAL/STOP); NTFS streams (-os) give ADS skip or
    CONTENT_TOO_LONG; Solid, header/file encryption, every volume, SFX, -oi
    and -oh are rejected with the same FATAL in both operations and modes
    without touching the target. RAR4 is not covered (WinRAR 7 cannot
    create it).

    The exe must have the pinned UnRAR64.dll next to it (README#rar-dll).
    Everything is written under a new work directory outside the repository;
    nothing is cleaned up (deletions happen only through unextract on the
    targets this script created). Exit code 0 when every check passed.
    ASCII only for Windows PowerShell 5.1.
.PARAMETER Exe
    unextract.exe to test (for example a publish output outside the repository).
.PARAMETER WorkDirectory
    New directory for archives and targets. Default: %TEMP%\unextract-real-rar-<guid>
.PARAMETER WinRarDirectory
    WinRAR installation with Rar.exe and UnRAR.exe.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-real-rar.ps1 -Exe <publish>\unextract.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Exe,
    [string]$WorkDirectory,
    [string]$WinRarDirectory = (Join-Path $env:ProgramFiles 'WinRAR')
)

$ErrorActionPreference = 'Stop'

$rarExe = Join-Path $WinRarDirectory 'Rar.exe'
$unrarExe = Join-Path $WinRarDirectory 'UnRAR.exe'
foreach ($tool in @($rarExe, $unrarExe)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Not found: $tool" }
}
$Exe = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $Exe) 'UnRAR64.dll') -PathType Leaf)) {
    throw "UnRAR64.dll is not next to the exe: $Exe"
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if ([string]::IsNullOrEmpty($WorkDirectory)) {
    $WorkDirectory = Join-Path $env:TEMP ('unextract-real-rar-' + [guid]::NewGuid().ToString('N'))
}
$Work = [System.IO.Path]::GetFullPath($WorkDirectory).TrimEnd('\')
if ($Work.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $Work.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "WorkDirectory must be outside the repository: $Work"
}
if (Test-Path -LiteralPath $Work) { throw "WorkDirectory already exists: $Work" }
New-Item -ItemType Directory -Path $Work | Out-Null

$script:failures = New-Object System.Collections.Generic.List[string]
$script:log = New-Object System.Collections.Generic.List[string]
function Note([string]$message) { $script:log.Add($message); Write-Host $message }
function Check([bool]$condition, [string]$what) {
    if ($condition) { Note "  ok   $what" } else { Note "  FAIL $what"; $script:failures.Add($what) }
}

# A non-ASCII name built at run time (the script stays ASCII).
$jp = (-join ([char]0x65E5, [char]0x672C, [char]0x8A9E)) + '.txt'
$files = @('a.txt', "sub\$jp", 'empty.txt', 'b.bin')

function New-Source([string]$dir) {
    New-Item -ItemType Directory -Path $dir, "$dir\sub", "$dir\emptydir" | Out-Null
    [IO.File]::WriteAllText("$dir\a.txt", "alpha`r`n" * 1000)
    [IO.File]::WriteAllText("$dir\sub\$jp", 'nihongo ' * 500, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllBytes("$dir\empty.txt", [byte[]]@())
    # 3 MiB, deterministic: compressible blocks mixed with LCG bytes.
    $bytes = New-Object byte[] (3MB)
    $x = [uint32]12345
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if (($i -shr 12) % 3 -eq 0) { $bytes[$i] = [byte]($i % 251) }
        else { $x = [uint32](([uint64]$x * 1103515245 + 12345) % 4294967296); $bytes[$i] = [byte]($x -shr 24) }
    }
    [IO.File]::WriteAllBytes("$dir\b.bin", $bytes)
}
function New-Rar([string]$archive, [string]$source, [string[]]$switches) {
    $arguments = @('a', '-idq', '-r', '-ep1') + $switches + @($archive, "$source\*")
    & $rarExe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Rar.exe failed ($LASTEXITCODE): $switches" }
}
function Expand-Rar([string]$archive, [string]$destination) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    & $unrarExe x -idq -o+ $archive "$destination\" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "UnRAR.exe failed ($LASTEXITCODE): $archive" }
}
function Invoke-Unextract([string[]]$arguments) {
    $lines = & $Exe @arguments 2>$null
    $code = $LASTEXITCODE
    $records = @($lines | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    [pscustomobject]@{
        Code    = $code
        Entries = @($records | Where-Object { $_.type -eq 'entry' })
        Result  = ($records | Where-Object { $_.type -eq 'result' } | Select-Object -Last 1)
        Run     = ($records | Where-Object { $_.type -eq 'run' } | Select-Object -First 1)
    }
}
function Status($run, [string]$name) { ($run.Entries | Where-Object { $_.name -eq $name } | Select-Object -First 1).status }
function Statuses($run) { ($run.Entries | ForEach-Object { "$($_.name)=$($_.status)" }) -join ', ' }
function ErrorCode($run) { if ($run.Result -and $run.Result.error) { $run.Result.error.code } }
function Set-SameSizeChange([string]$path) {
    [IO.File]::WriteAllText($path, ("alpha`r`n" * 999) + "alphA`r`n")
}

$src = Join-Path $Work 'src'
New-Source $src
Note "Rar.exe $((Get-Item $rarExe).VersionInfo.ProductVersion); unextract $Exe; work $Work"

# Accepted archives.
$accepted = [ordered]@{
    'rar5-m3'        = @('-m3')
    'rar5-m0'        = @('-m0')
    'rar5-m5-blake2' = @('-m5', '-htb')
    'rar5-rr-qo-ts'  = @('-m3', '-rr5%', '-qo+', '-ts+')
    'rar5-md1g'      = @('-m3', '-md1g')
}
foreach ($name in $accepted.Keys) {
    Note "[$name] $($accepted[$name] -join ' ')"
    $archive = Join-Path $Work "$name.rar"
    New-Rar $archive $src $accepted[$name]
    $target = Join-Path $Work "$name-target"
    Expand-Rar $archive $target
    $r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--jsonl')
    $allMatched = @($files | Where-Object { (Status $r $_) -ne 'MATCHED' }).Count -eq 0
    Check ($r.Code -eq 0 -and $allMatched -and (Status $r 'sub\') -eq 'DIRECTORY' -and (Status $r 'emptydir\') -eq 'DIRECTORY') "strict analyze: all MATCHED/DIRECTORY ($(Statuses $r))"
    $r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--fast', '--jsonl')
    Check ($r.Code -eq 0 -and @($files | Where-Object { (Status $r $_) -ne 'SAME_SIZE' }).Count -eq 0) 'fast analyze: all SAME_SIZE'
    $bytes = [IO.File]::ReadAllBytes("$target\b.bin"); $bytes[2MB + 7] = $bytes[2MB + 7] -bxor 1; [IO.File]::WriteAllBytes("$target\b.bin", $bytes)
    $r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--jsonl')
    Check ($r.Code -eq 0 -and (Status $r 'b.bin') -eq 'MODIFIED' -and (Status $r 'a.txt') -eq 'MATCHED') 'strict analyze: 1-bit change in b.bin is MODIFIED'
}

# Strict delete: only matched files go; the same-size change, an extra file and directories stay.
foreach ($name in @('rar5-m3', 'rar5-m5-blake2')) {
    Note "[$name strict delete]"
    $archive = Join-Path $Work "$name.rar"
    $target = Join-Path $Work "$name-delete"
    Expand-Rar $archive $target
    Set-SameSizeChange "$target\a.txt"
    [IO.File]::WriteAllText("$target\extra.txt", 'not in the archive')
    $r = Invoke-Unextract @('delete', $archive, '--target', $target, '--yes', '--jsonl')
    Check ($r.Code -eq 0) "delete exit 0 ($(Statuses $r))"
    Check ((Status $r 'a.txt') -eq 'MODIFIED' -and (Test-Path -LiteralPath "$target\a.txt")) 'same-size change kept'
    Check (-not (Test-Path -LiteralPath "$target\b.bin") -and -not (Test-Path -LiteralPath "$target\sub\$jp") -and -not (Test-Path -LiteralPath "$target\empty.txt")) 'matched files deleted'
    Check ((Test-Path -LiteralPath "$target\extra.txt") -and (Test-Path -LiteralPath "$target\sub") -and (Test-Path -LiteralPath "$target\emptydir")) 'extra file and directories kept'
}

Note '[rar5-m3 fast delete]'
$archive = Join-Path $Work 'rar5-m3.rar'
$target = Join-Path $Work 'rar5-m3-fast-delete'
Expand-Rar $archive $target
Set-SameSizeChange "$target\a.txt"
[IO.File]::WriteAllText("$target\empty.txt", 'x')
$r = Invoke-Unextract @('delete', $archive, '--target', $target, '--fast', '--yes', '--jsonl')
Check ($r.Code -eq 0 -and -not (Test-Path -LiteralPath "$target\a.txt") -and (Test-Path -LiteralPath "$target\empty.txt") -and (Status $r 'empty.txt') -eq 'MODIFIED') 'fast: same-size different file deleted, different size kept'

Note '[rar5-m3 --entries]'
$target = Join-Path $Work 'rar5-m3-entries'
Expand-Rar $archive $target
$r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--jsonl')
$selected = ($r.Entries | Where-Object { $_.name -like 'sub\*' -and -not $_.directory }).name
$entries = Join-Path $Work 'entries.txt'
[IO.File]::WriteAllText($entries, "$selected`n", (New-Object Text.UTF8Encoding $false))
$r = Invoke-Unextract @('delete', $archive, '--target', $target, '--entries', $entries, '--yes', '--jsonl')
Check ($r.Code -eq 0 -and $r.Entries.Count -eq 1 -and -not (Test-Path -LiteralPath "$target\sub\$jp") -and (Test-Path -LiteralPath "$target\a.txt") -and (Test-Path -LiteralPath "$target\b.bin")) 'entries: only the selected Japanese-named file deleted'

# Corrupted compressed data: never MATCHED, Strict FATAL/STOP, file kept.
foreach ($name in @('rar5-m3', 'rar5-m5-blake2')) {
    Note "[$name corrupted]"
    $original = Join-Path $Work "$name.rar"
    $archive = Join-Path $Work "$name-corrupted.rar"
    $bytes = [IO.File]::ReadAllBytes($original)
    $position = [int]($bytes.Length * 0.6); $bytes[$position] = $bytes[$position] -bxor 0x10
    [IO.File]::WriteAllBytes($archive, $bytes)
    $target = Join-Path $Work "$name-corrupted-target"
    Expand-Rar $original $target
    $r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--jsonl')
    Check ($r.Code -eq 1 -and $r.Result.outcome -eq 'fatal' -and @($r.Entries | Where-Object { $_.status -eq 'MATCHED' -and $_.name -eq 'b.bin' }).Count -eq 0) "strict analyze FATAL, b.bin not MATCHED (code=$(ErrorCode $r), byte $position)"
    $r = Invoke-Unextract @('delete', $archive, '--target', $target, '--yes', '--jsonl')
    Check ($r.Code -eq 1 -and $r.Result.outcome -eq 'stopped' -and (Test-Path -LiteralPath "$target\b.bin")) "strict delete STOP, b.bin kept (code=$(ErrorCode $r))"
}

Note '[rar5-os NTFS streams]'
$sourceStreams = Join-Path $Work 'src-os'
New-Source $sourceStreams
Set-Content -LiteralPath "$sourceStreams\a.txt" -Stream 'extra' -Value 'stream data'
$archive = Join-Path $Work 'rar5-os.rar'
New-Rar $archive $sourceStreams @('-m3', '-os')
$target = Join-Path $Work 'rar5-os-target-ads'
Expand-Rar $archive $target
Check (@(Get-Item -LiteralPath "$target\a.txt" -Stream * | Where-Object { $_.Stream -ne ':$DATA' }).Count -gt 0) 'extracted a.txt has an ADS'
$r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--jsonl')
$a = $r.Entries | Where-Object { $_.name -eq 'a.txt' }
Check ($r.Code -eq 0 -and $a.status -eq 'SKIPPED_SPECIAL_FILE' -and $a.skip_reason -eq 'ADS' -and (Status $r 'b.bin') -eq 'MATCHED') 'target with ADS: SKIPPED_SPECIAL_FILE (ADS), others MATCHED'
$target = Join-Path $Work 'rar5-os-target-plain'
Expand-Rar (Join-Path $Work 'rar5-m3.rar') $target
$r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--jsonl')
Check ($r.Code -eq 1 -and $r.Result.outcome -eq 'fatal' -and (ErrorCode $r) -eq 'CONTENT_TOO_LONG' -and $r.Result.error.entry_name -eq 'a.txt') 'target without ADS: strict FATAL CONTENT_TOO_LONG at a.txt'
$r = Invoke-Unextract @('delete', $archive, '--target', $target, '--yes', '--jsonl')
Check ($r.Code -eq 1 -and $r.Result.outcome -eq 'stopped' -and (Test-Path -LiteralPath "$target\a.txt")) 'strict delete STOP at a.txt, a.txt kept'
$r = Invoke-Unextract @('analyze', $archive, '--target', $target, '--fast', '--jsonl')
Check ($r.Code -eq 0 -and (Status $r 'a.txt') -eq 'SAME_SIZE') 'fast analyze: SAME_SIZE (content not read)'

# Rejected archives: the same FATAL for analyze/delete x Strict/Fast, no run record, target unchanged.
function Test-Rejected([string]$label, [string]$archive, [string]$expected) {
    $target = Join-Path $Work "$label-target"
    Expand-Rar (Join-Path $Work 'rar5-m3.rar') $target
    $before = (Get-ChildItem -LiteralPath $target -Recurse -Force | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
    foreach ($operation in @('analyze', 'delete')) {
        foreach ($mode in @('strict', 'fast')) {
            $arguments = @($operation, $archive, '--target', $target, '--jsonl')
            if ($operation -eq 'delete') { $arguments += '--yes' }
            if ($mode -eq 'fast') { $arguments += '--fast' }
            $r = Invoke-Unextract $arguments
            Check ($r.Code -eq 1 -and $r.Result.outcome -eq 'fatal' -and (ErrorCode $r) -eq $expected -and $null -eq $r.Run) "$label $operation/$mode -> $expected (got $(ErrorCode $r))"
        }
    }
    $after = (Get-ChildItem -LiteralPath $target -Recurse -Force | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }) -join "`n"
    Check ($before -eq $after) "$label target unchanged"
}
Note '[rejected]'
New-Rar (Join-Path $Work 'rar5-solid.rar') $src @('-m3', '-s')
Test-Rejected 'solid' (Join-Path $Work 'rar5-solid.rar') 'ARCHIVE_SOLID'
New-Rar (Join-Path $Work 'rar5-hp.rar') $src @('-m3', '-hpTestPass1')
Test-Rejected 'header-encrypted' (Join-Path $Work 'rar5-hp.rar') 'ARCHIVE_ENCRYPTED'
New-Rar (Join-Path $Work 'rar5-p.rar') $src @('-m3', '-pTestPass1')
Test-Rejected 'file-encrypted' (Join-Path $Work 'rar5-p.rar') 'ENTRY_ENCRYPTED'
New-Rar (Join-Path $Work 'rar5-vol.rar') $src @('-m0', '-v1m')
foreach ($volume in (Get-ChildItem -LiteralPath $Work -Filter 'rar5-vol.part*.rar' | Sort-Object Name)) {
    Test-Rejected $volume.BaseName $volume.FullName 'ARCHIVE_MULTI_VOLUME'
}
New-Rar (Join-Path $Work 'sfx.exe') $src @('-m3', '-sfx')
Copy-Item -LiteralPath (Join-Path $Work 'sfx.exe') -Destination (Join-Path $Work 'rar5-sfx.rar')
Test-Rejected 'sfx' (Join-Path $Work 'rar5-sfx.rar') 'ARCHIVE_NOT_RAR'
$sourceCopies = Join-Path $Work 'src-identical'
New-Source $sourceCopies
Copy-Item -LiteralPath "$sourceCopies\a.txt" -Destination "$sourceCopies\a-copy.txt"
New-Rar (Join-Path $Work 'rar5-oi.rar') $sourceCopies @('-m3', '-oi1:1')
Test-Rejected 'identical-as-reference' (Join-Path $Work 'rar5-oi.rar') 'ENTRY_REDIRECTION'
$sourceLinks = Join-Path $Work 'src-hardlink'
New-Source $sourceLinks
New-Item -ItemType HardLink -Path "$sourceLinks\a-link.txt" -Target "$sourceLinks\a.txt" | Out-Null
New-Rar (Join-Path $Work 'rar5-oh.rar') $sourceLinks @('-m3', '-oh')
Test-Rejected 'hardlink' (Join-Path $Work 'rar5-oh.rar') 'ENTRY_REDIRECTION'

Note ''
Note "failures: $($script:failures.Count)"
$script:failures | ForEach-Object { Note "  $_" }
Note "work: $Work (not cleaned up)"
$script:log | Set-Content -LiteralPath (Join-Path $Work 'summary.txt') -Encoding UTF8
Get-ChildItem -LiteralPath $Work -Filter '*.rar' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $Work 'sha256.txt')
if ($script:failures.Count -gt 0) { exit 1 }
exit 0
