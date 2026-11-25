param(
    [Parameter(Mandatory)][string]$PublishedDirectory,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$TestFixture,
    [Parameter(Mandatory)][string]$SourceCommit
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$version = '5.0.0-beta.1'
$zipName = 'Cavernize-V5-Beta-win-x64.zip'
$cliDirectory = Join-Path $PublishedDirectory 'CLI'

function Read-Runtime([string]$Directory, [string]$App) {
    $config = Get-Content -LiteralPath (Join-Path $Directory "$App.runtimeconfig.json") -Raw | ConvertFrom-Json
    $frameworks = $config.runtimeOptions.includedFrameworks
    if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks -or
        @($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' -and $_.version -like '8.0.*' }).Count -ne 1) {
        throw "$App did not publish as a self-contained .NET 8 application."
    }
    return $frameworks
}

function Assert-X64([string]$Path, [string]$ExpectedVersion) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Missing DOS header in $Path" }
        $reader.BaseStream.Position = 0x3c
        $reader.BaseStream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw "Expected a Windows x64 PE executable at $Path"
        }
    } finally {
        $reader.Dispose()
    }
    if ($ExpectedVersion) {
        $actual = (Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
        if ($actual -cne $ExpectedVersion) {
            throw "Unexpected product version for $Path (expected $ExpectedVersion, found '$actual')."
        }
    }
}

function Copy-Notices([string]$Package, [string]$Version, [string[]]$Files) {
    $source = Join-Path $nuget "$($Package.ToLowerInvariant())/$Version"
    $destination = New-Item -ItemType Directory -Path (Join-Path $PublishedDirectory "Licenses/$Package/$Version") -Force
    foreach ($file in $Files) {
        Copy-Item -LiteralPath (Join-Path $source $file) -Destination $destination.FullName
    }
}

$guiRuntime = @(Read-Runtime $PublishedDirectory 'CavernizeGUI')
$cliRuntime = @(Read-Runtime $cliDirectory 'CavernizeCLI')
$desktop = $guiRuntime | Where-Object name -eq 'Microsoft.WindowsDesktop.App'
if (!$desktop -or $desktop.version -notlike '8.0.*') { throw 'Missing .NET 8 Windows Desktop runtime.' }
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$coreVersions = @($guiRuntime + $cliRuntime | Where-Object name -eq 'Microsoft.NETCore.App' |
    Select-Object -ExpandProperty version -Unique)
foreach ($coreVersion in $coreVersions) {
    Copy-Notices 'Microsoft.NETCore.App.Runtime.win-x64' $coreVersion @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')
}
Copy-Notices 'Microsoft.WindowsDesktop.App.Runtime.win-x64' $desktop.version @('LICENSE')
$guiDeps = Get-Content -LiteralPath (Join-Path $PublishedDirectory 'CavernizeGUI.deps.json') -Raw | ConvertFrom-Json
foreach ($dependency in @('System.Drawing.Common', 'Microsoft.Win32.SystemEvents')) {
    $key = @($guiDeps.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$dependency/*" })
    if ($key.Count -ne 1) { throw "Cannot resolve the published $dependency package version." }
    Copy-Notices $dependency ($key[0].Split('/')[1]) @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')
}

$manifest = [ordered]@{
    product = 'Cavernize V5 Beta'
    version = $version
    tag = "cavernize-v$version"
    sourceBranch = 'feature/cavernize-v4-truehd-fix'
    commit = $SourceCommit
    buildUtc = [DateTime]::UtcNow.ToString('o')
    configuration = 'Release'
    runtimeIdentifier = 'win-x64'
    guiRuntime = $guiRuntime
    cliRuntime = $cliRuntime
    truehddVersion = '0.6.3'
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PublishedDirectory 'BUILD-INFO.json')
Copy-Item -LiteralPath 'LICENSE.md', 'docs/Cavernize-V5-Beta-README.md', 'docs/Cavernize-V5-Beta-Release-Notes.md' -Destination $PublishedDirectory
Rename-Item -LiteralPath (Join-Path $PublishedDirectory 'Cavernize-V5-Beta-README.md') -NewName 'README.md'

$zip = Join-Path $PackageDirectory $zipName
[IO.Compression.ZipFile]::CreateFromDirectory($PublishedDirectory, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $required = @(
        'CavernizeGUI.exe', 'CavernizeGUI.dll', 'CavernizeGUI.dll.config', 'CavernizeGUI.deps.json', 'CavernizeGUI.runtimeconfig.json',
        'Cavern.WPF.dll', 'PresentationFramework.dll', 'WindowsBase.dll', 'wpfgfx_cor3.dll',
        'de/PresentationFramework.resources.dll',
        'CLI/CavernizeCLI.exe', 'CLI/CavernizeCLI.dll', 'CLI/CavernizeCLI.deps.json', 'CLI/CavernizeCLI.runtimeconfig.json',
        'Tools/TrueHDD/truehdd.exe', 'Tools/TrueHDD/LICENSE', 'Tools/TrueHDD/NOTICE',
        'README.md', 'LICENSE.md', 'BUILD-INFO.json', 'Cavernize-V5-Beta-Release-Notes.md'
    )
    foreach ($prefix in @('', 'CLI/')) {
        foreach ($file in @('Cavern.dll', 'Cavern.Format.dll', 'Cavernize.Logic.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
            $required += "$prefix$file"
        }
    }
    foreach ($name in $required) {
        $entry = $archive.GetEntry($name)
        if (!$entry -or !$entry.Length) { throw "Missing or empty published file: $name" }
    }
    if ($archive.Entries.Name -like '*WebView2*') { throw 'Unexpected WebView2 dependency in the V5 Beta ZIP.' }
} finally {
    $archive.Dispose()
}

$expanded = Join-Path $PackageDirectory 'Unpacked Cavernize V5 Beta'
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $expanded)
$gui = Join-Path $expanded 'CavernizeGUI.exe'
$cli = Join-Path $expanded 'CLI/CavernizeCLI.exe'
$truehdd = Join-Path $expanded 'Tools/TrueHDD/truehdd.exe'
Assert-X64 $gui "$version+$SourceCommit"
Assert-X64 $cli "$version+$SourceCommit"
Assert-X64 $truehdd
$decoderVersion = & $truehdd --version
if ($decoderVersion -notmatch '\b0\.6\.3\b') { throw "Unexpected bundled decoder version: $decoderVersion" }

[xml]$guiConfig = Get-Content -LiteralPath (Join-Path $expanded 'CavernizeGUI.dll.config') -Raw
$settings = @($guiConfig.configuration.userSettings.'CavernizeGUI.Resources.Settings'.setting)
foreach ($name in @('ffmpegLocation', 'renderTarget', 'outputCodec', 'checkUpdates')) {
    if (@($settings | Where-Object name -eq $name).Count -ne 1) { throw "Missing GUI setting: $name" }
}

$stdout = Join-Path $PackageDirectory 'GUI-help.txt'
$stderr = Join-Path $PackageDirectory 'GUI-stderr.txt'
$process = Start-Process -FilePath $gui -ArgumentList '-help' -WorkingDirectory $expanded -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    if (!$process.WaitForExit(30000)) {
        $process.Kill($true)
        throw 'Packaged GUI help startup timed out.'
    }
    if ($process.ExitCode -ne 0 -or (Get-Content -LiteralPath $stdout -Raw) -notmatch '(?m)^-help \(-h\)\s*:') {
        throw "Packaged GUI help startup failed. $(Get-Content -LiteralPath $stderr -Raw)"
    }
} finally {
    $process.Dispose()
}

# Exercise the same relative install paths an end user receives, including decoder discovery.
$testMedia = Join-Path $PackageDirectory 'Packaged analysis.mkv'
Copy-Item -LiteralPath $TestFixture -Destination $testMedia
Push-Location $PackageDirectory
try {
    & $cli --input $testMedia --layout '5.1.6 with top sides' --report-only --quality --verify-peak-events `
        --work-dir (Join-Path $PackageDirectory 'Analysis workspace')
} finally {
    Pop-Location
}
$rows = @(Import-Csv -LiteralPath ([IO.Path]::ChangeExtension($testMedia, '.csv')) | Where-Object row_type -eq 'overall')
if ($rows.Count -ne 1 -or $rows[0].decoder_version -ne '0.6.3' -or $rows[0].pre_frames -ne '16000' -or
    $rows[0].post_frames -ne '16000' -or $rows[0].verification -ne 'matched') {
    throw 'Packaged CLI did not decode and verify the full fixture with the bundled TrueHDD.'
}

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $zipName" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
"Verified both x64 executables, GUI startup, and CLI analysis with the bundled decoder." | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
"SHA-256 $hash" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
Write-Host "::notice title=Cavernize V5 Beta ZIP SHA-256::$hash"
