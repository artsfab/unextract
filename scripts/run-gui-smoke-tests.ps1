$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
# Keep this script ASCII for Windows PowerShell 5.1's BOM-less source decoding.
$japanese = -join @([char]0x65E5, [char]0x672C, [char]0x8A9E, ' ', [char]0x7A7A, [char]0x767D)
$name = 'unextract-gui-smoke ' + $japanese + '-' + [Guid]::NewGuid().ToString('N')
$fixture = [IO.Path]::GetFullPath((Join-Path $tempRoot $name))
$created = $null
$exitCode = 1
$cliProcess = $null

try {
    $created = (New-Item -ItemType Directory -Path $fixture -ErrorAction Stop).FullName
    $package = Join-Path $created 'package'
    & (Join-Path $PSScriptRoot 'publish-gui.ps1') -OutputDirectory $package
    if ($LASTEXITCODE -ne 0) { throw "GUI publish failed: $LASTEXITCODE" }

    $runtime = Get-Content -Raw (Join-Path $package 'unextract-gui.runtimeconfig.json') | ConvertFrom-Json
    if ($null -ne $runtime.runtimeOptions.framework -or $null -ne $runtime.runtimeOptions.frameworks) {
        throw 'The GUI distribution requires an installed framework.'
    }
    $deps = Get-Content -Raw (Join-Path $package 'unextract-gui.deps.json') | ConvertFrom-Json
    foreach ($dependency in $deps.libraries.PSObject.Properties.Name) {
        if ($dependency -match '^(unextract|Unextract.Core|Unextract.Windows)/') {
            throw "Unexpected GUI assembly dependency: $dependency"
        }
    }
    & (Join-Path $PSScriptRoot 'test-gui-package.ps1') -PackageDirectory $package
    if ($LASTEXITCODE -ne 0) { throw "GUI startup smoke failed: $LASTEXITCODE" }

    # Execute only a usage error. No target or archive is passed to the CLI.
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $package 'cli/unextract.exe'
    $startInfo.Arguments = '--jsonl'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $cliProcess = [Diagnostics.Process]::Start($startInfo)
    $stdout = $cliProcess.StandardOutput.ReadToEndAsync()
    $stderr = $cliProcess.StandardError.ReadToEndAsync()
    if (-not $cliProcess.WaitForExit(30000)) { throw 'Bundled CLI did not exit.' }
    $record = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
    $diagnostic = $stderr.GetAwaiter().GetResult()
    if ($cliProcess.ExitCode -ne 1 -or $record.v -ne 1 -or $record.type -ne 'result' -or
        $record.outcome -ne 'input_error' -or $record.exit_code -ne 1) {
        throw "Bundled CLI usage smoke failed: $diagnostic"
    }
    Write-Output 'Bundled CLI JSONL startup passed.'

    $missing = Join-Path $created 'missing-cli'
    New-Item -ItemType Directory -Path $missing -ErrorAction Stop | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $package) {
        if ($item.Name -ne 'cli') { Copy-Item -LiteralPath $item.FullName -Destination $missing -Recurse }
    }
    & (Join-Path $PSScriptRoot 'test-gui-package.ps1') -PackageDirectory $missing -ExpectMissingCli
    if ($LASTEXITCODE -ne 0) { throw "Missing-CLI startup smoke failed: $LASTEXITCODE" }
    $exitCode = 0
}
catch {
    [Console]::Error.WriteLine("run-gui-smoke-tests: $_")
}
finally {
    if ($null -ne $cliProcess) { $cliProcess.Dispose() }
    if ($null -ne $created) {
        try {
            $full = [IO.Path]::GetFullPath($created)
            if (-not [string]::Equals($full, $fixture, [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals([IO.Path]::GetDirectoryName($full), $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($full) -ne $name) {
                throw "Cannot establish cleanup ownership: $full"
            }
            $pending = New-Object 'System.Collections.Generic.Stack[string]'
            $pending.Push($full)
            while ($pending.Count -gt 0) {
                $path = $pending.Pop()
                $attributes = [IO.File]::GetAttributes($path)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unexpected reparse point: $path" }
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Push($child) }
                }
            }
            [IO.Directory]::Delete($full, $true)
            Write-Output "Removed temporary GUI publish: $full"
        }
        catch {
            [Console]::Error.WriteLine("run-gui-smoke-tests: cleanup failed: $created; $_")
            $exitCode = 1
        }
    }
}
exit $exitCode
