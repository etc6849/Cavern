param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$FixtureDirectory,
    [Parameter(Mandatory = $true)][string]$WorkDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[void](New-Item -ItemType Directory -Path $WorkDirectory)
Copy-Item -Path (Join-Path $FixtureDirectory 'ListenerGain.atmos*') -Destination $WorkDirectory
$source = Join-Path $WorkDirectory 'ListenerGain.atmos'

function Invoke-Cli([int]$ExpectedExit, [string[]]$Arguments) {
    $lines = & $Executable @Arguments 2>&1
    $exit = $LASTEXITCODE
    $text = $lines -join "`n"
    if ($exit -ne $ExpectedExit) {
        throw "Expected exit $ExpectedExit, got $exit. Arguments: $Arguments`n$text"
    }
    return $text
}

function Assert-Near([double]$Actual, [double]$Expected, [string]$Description, [double]$Tolerance = 0.000001) {
    if ([double]::IsNaN($Actual) -or [Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw "$Description expected $Expected, got $Actual"
    }
}

function Assert-Report([string]$LayoutId, [double]$Safe, [double]$Effective, [int]$Channels, [string]$Mode) {
    $base = Join-Path $WorkDirectory "ListenerGain-$LayoutId-PeakAnalysis"
    $rows = @(Import-Csv "$base.csv")
    $overall = @($rows | Where-Object row_type -eq 'overall')
    $speakers = @($rows | Where-Object row_type -eq 'channel')
    $grades = @($rows | Where-Object { $_.row_type -eq 'quality' -and $_.quality_grade })
    if ($overall.Count -ne 1 -or $speakers.Count -ne $Channels -or $grades.Count -lt 1) {
        throw "Unexpected CSV shape or missing grades in $base"
    }
    $summary = $overall[0]
    if ($summary.status -ne 'complete' -or $summary.verification -ne 'matched' -or $summary.listener_gain_mode -ne $Mode -or
        $summary.pre_frames -ne '5381' -or $summary.post_frames -ne '5381' -or $summary.sample_rate_hz -ne '48000') {
        throw "Unexpected status, gain mode, or frame count in $base"
    }
    Assert-Near ([double]$summary.listener_automatic_safe_gain) $Safe 'Safety gain'
    Assert-Near ([double]$summary.listener_gain_effective_linear) $Effective 'Effective gain'
    Assert-Near ([double]$summary.listener_gain_effective_db) (20 * [Math]::Log10($Effective)) 'Effective gain dB' 0.00001
    Assert-Near ([double]$summary.pre_abs_linear) ($Effective * 16384 / 32767) 'Peak after exactly one gain operation'
    Assert-Near ([double]$summary.pre_rms_linear) ($Effective * 16384 / 32767 * [Math]::Sqrt(5317.0 / 5381 / $Channels)) 'Multichannel RMS'
    if ($speakers[0].speaker -ne 'Front left' -or $speakers[0].pre_frame -ne '64' -or
        $speakers[1].pre_dbfs -ne '-Infinity' -or $speakers[1].pre_active_gt_minus60_percent -ne '0') {
        throw "Unexpected peak event or silent-channel data in $base"
    }
    $json = Get-Content "$base-Verification.json" -Raw | ConvertFrom-Json
    if ($json.Status -ne 'complete' -or !$json.Evidence.Matches -or !$json.Evidence.Highest.Matches -or
        $json.AnalysisId -ne $summary.analysis_id -or $json.Evidence.Highest.Frame -ne 64) {
        throw "Peak verification did not match the CSV in $base"
    }
    Assert-Near $json.Evidence.Highest.Reconstructed ([double]$summary.pre_signed_sample) 'Reconstruction'
    return $summary
}

$helpText = Invoke-Cli 0 @('--help')
if (!$helpText.Contains('--listener-gain-db') -or !$helpText.Contains('--report-only')) { throw 'Incomplete help' }
$list = Invoke-Cli 0 @('--list-layouts')
if ($list -notmatch '(?m)^5\.1\.2 side\r?$' -or $list -notmatch '(?m)^5\.1\.6 with top sides\r?$') { throw 'Missing layout' }
$common = @('--report-only', '--quality', '--verify-peak-events')
$null = Invoke-Cli 0 (@('--input', $source, '--layout', '5.1.6 with top sides') + $common)
$null = Assert-Report '5.1.6-with-top-sides' 1 1 12 'Automatic'
$null = Invoke-Cli 0 (@('--input', $source, '--layout', '5.1.2 side') + $common)
$null = Assert-Report '5.1.2-side' .707 .707 8 'Automatic'
$null = Invoke-Cli 0 (@('--input', $source, '--layout', '5.1.2 side') + $common + @('--listener-gain-db', '0'))
$null = Assert-Report '5.1.2-side-ListenerGain-0dB' .707 1 8 'Explicit override'
$null = Invoke-Cli 0 (@('--listener-gain-db', '-3', '--input', $source, '--layout', '5.1.2 side') + $common)
$null = Assert-Report '5.1.2-side-ListenerGain-m3dB' .707 ([Math]::Pow(10, -3.0 / 20)) 8 'Explicit override'
$null = Invoke-Cli 0 (@('--input', $source, '--layout', '5.1.2 side', '--listener-gain-db', '8') + $common)
$boost = Assert-Report '5.1.2-side-ListenerGain-8dB' .707 ([Math]::Pow(10, 8.0 / 20)) 8 'Explicit override'
if ($boost.pre_block_attack -ne 'True' -or [double]$boost.post_abs_linear -gt 1) { throw 'Limiter did not engage' }
if (@(Get-ChildItem $WorkDirectory -Include *.wav, *.mka, *.mkv -File -Recurse).Count -ne 0) { throw 'Report Only created audio output' }
$null = Invoke-Cli 2 @('--input', $source, '--layout', '5.1.6', '--report-only')
$null = Invoke-Cli 2 @('--input', $source, '--layout', 'no-such-layout', '--report-only')
$null = Invoke-Cli 2 @('--input', $source, '--listener-gain-db', 'NaN', '--report-only')
$null = Invoke-Cli 2 @('--input', $source, '--listener-gain-db', '0', '--render-gain', '0', '--report-only')
$null = Invoke-Cli 1 @('--input', (Join-Path $WorkDirectory 'missing.atmos'), '--report-only')

Write-Host 'Published CLI passed Atmos analysis, JSON verification, both layouts, absolute gain, and failure cases.'
exit 0
