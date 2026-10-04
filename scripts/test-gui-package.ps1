param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [switch]$ExpectMissingCli
)

# Startup smoke only: no archive/target arguments and no analyze/delete jobs.
$ErrorActionPreference = 'Stop'
$directory = [System.IO.Path]::GetFullPath($PackageDirectory)
$exe = Join-Path $directory 'unextract-gui.exe'
$cli = Join-Path $directory 'cli/unextract.exe'
$guiProcess = $null

try {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing GUI: $exe" }
    if ($ExpectMissingCli) {
        if (Test-Path -LiteralPath $cli) { throw "Expected CLI to be absent: $cli" }
    }
    elseif (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Missing CLI: $cli" }

    $guiProcess = Start-Process -FilePath $exe -WorkingDirectory $directory -WindowStyle Hidden -PassThru
    if (-not $guiProcess.WaitForInputIdle(30000)) { throw 'GUI did not become idle within 30 seconds.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $guiProcess.Refresh()
        if ($guiProcess.HasExited) { throw "GUI exited during startup: $($guiProcess.ExitCode)" }
        if ($guiProcess.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($guiProcess.MainWindowHandle -eq 0 -or $guiProcess.MainWindowTitle -ne 'unextract GUI') {
        throw 'The WPF main window was not created.'
    }
    if (-not $guiProcess.CloseMainWindow()) { throw 'Cannot request normal GUI shutdown.' }
    if (-not $guiProcess.WaitForExit(30000)) { throw 'GUI did not exit normally within 30 seconds.' }
    if ($guiProcess.ExitCode -ne 0) { throw "GUI exit code: $($guiProcess.ExitCode)" }
    Write-Output "GUI startup/shutdown passed: $exe"
    $global:LASTEXITCODE = 0
}
catch {
    [Console]::Error.WriteLine("test-gui-package: $_")
    exit 1
}
finally {
    # A failed smoke never kills an application process. In later units it
    # could own a delete job; request normal shutdown only.
    if ($null -ne $guiProcess) {
        if (-not $guiProcess.HasExited) { $null = $guiProcess.CloseMainWindow() }
        $guiProcess.Dispose()
    }
}
