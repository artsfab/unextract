param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$destination = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')

try {
    if ([string]::Equals($destination, $repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $destination.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GUI publish output must be outside the repository.'
    }
    if (Test-Path -LiteralPath $destination) {
        throw "Output already exists; specify a new directory: $destination"
    }
    New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null

    & dotnet publish (Join-Path $repoRoot 'src/Unextract.Gui') -c Release -p:PublishProfile=win-x64 -o $destination
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # CLI retains its own existing publish profile and is published directly
    # into the fixed subdirectory. Nothing is copied from GUI build's cli/.
    $cliDirectory = Join-Path $destination 'cli'
    & dotnet publish (Join-Path $repoRoot 'src/Unextract.Cli') -c Release -p:PublishProfile=win-x64 -o $cliDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    foreach ($exe in @((Join-Path $destination 'unextract-gui.exe'), (Join-Path $cliDirectory 'unextract.exe'))) {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing published executable: $exe" }
    }
    # The package is only the GUI plus cli\unextract.exe, the same relative CLI location as the normal build.
    # Test-only components must never reach a distribution.
    $testOnly = @(Get-ChildItem -LiteralPath $destination -Recurse -File |
        Where-Object { $_.Name -match '^(unextract-fake-cli|Unextract\.Gui\.(FakeCli|UiTests|Tests))|^FlaUI\.' })
    if ($testOnly.Count -gt 0) { throw "Test-only file in the distribution: $($testOnly[0].FullName)" }
    Write-Output "GUI distribution: $destination"
}
catch {
    [Console]::Error.WriteLine("publish-gui: $_")
    exit 1
}
