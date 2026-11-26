param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$Fixture,
    [Parameter(Mandatory)][string]$WorkDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Checked([string]$Tool, [string[]]$Arguments) {
    $output = & $Tool @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed with exit ${LASTEXITCODE}. $($output -join [Environment]::NewLine)" }
    return $output
}

function Assert-Db([string]$Expected, [string]$Measured, [string]$Context) {
    if ($Expected -eq '-Infinity' -and $Measured -eq '-inf') { return }
    $expectedDb = [double]::Parse($Expected, [Globalization.CultureInfo]::InvariantCulture)
    $measuredDb = [double]::Parse($Measured, [Globalization.CultureInfo]::InvariantCulture)
    if (![double]::IsFinite($expectedDb) -or ![double]::IsFinite($measuredDb) -or
        [Math]::Abs($expectedDb - $measuredDb) -gt 0.000001) {
        throw "$Context expected $expectedDb dBFS, FFmpeg observed $measuredDb dBFS."
    }
}

New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
$inputFile = Join-Path $WorkDirectory 'Atmos fixture.mkv'
Copy-Item -LiteralPath $Fixture -Destination $inputFile
$inputHash = (Get-FileHash -LiteralPath $inputFile).Hash
$csv = [IO.Path]::ChangeExtension($inputFile, '.csv')
$layouts = @('5.1.6 with top sides', '5.1.4', '5.1.2 side')
$common = @('--input', $inputFile, '--report-only', '--quality', '--verify-peak-events',
    '--work-dir', (Join-Path $WorkDirectory 'Analysis workspace'))
$auto = $common + @('--layout', '5.1.2 side')
$analysis = $common + @('--listener-gain-db', '0')
foreach ($layout in $layouts) { $analysis += @('--layout', $layout) }

$null = Invoke-Checked $Baseline $auto
$null = Invoke-Checked $Baseline $analysis
$v5 = @(Import-Csv -LiteralPath $csv | Sort-Object settings_id, row_type, slot, quality_metric_id)
# Publish V6 into the same CSV, testing that it accepts the V5 header and replaces the matching configurations.
$text = (Invoke-Checked $Candidate $analysis) -join [Environment]::NewLine
$preserved = @(Import-Csv -LiteralPath $csv | Where-Object { $_.row_type -eq 'overall' -and $_.listener_gain_mode -eq 'Automatic' })
if ($preserved.Count -ne 1 -or $preserved[0].source_inventory -ne '' -or $preserved[0].listener_gain_effective_linear -ne '0.707') {
    throw 'The V5 automatic-gain configuration was not retained when migrating the CSV.'
}
$null = Invoke-Checked $Candidate $auto
$v6 = @(Import-Csv -LiteralPath $csv | Sort-Object settings_id, row_type, slot, quality_metric_id)
if ($v5.Count -ne $v6.Count -or $v6.Count -eq 0 -or $v6[0].PSObject.Properties.Name.Count -ne 75) {
    throw 'Unexpected CSV shape or duplicate configurations after the V5 to V6 update.'
}
$excluded = @('analysis_id', 'bed_count', 'dynamic_object_count', 'unused_target_count', 'bed_positions')
$stable = @($v5[0].PSObject.Properties.Name | Where-Object { $_ -notin $excluded })
for ($row = 0; $row -lt $v5.Count; $row++) {
    foreach ($column in $stable) {
        if ($v5[$row].$column -cne $v6[$row].$column) {
            throw "CSV row $row field $column changed between V5 and V6: $($v5[$row].$column) vs $($v6[$row].$column)"
        }
    }
}

if ($text -notmatch 'Encoded bed channels: 1 \(ScreenLFE\)' -or $text -notmatch 'Encoded Atmos objects: 11') {
    throw 'The CLI report did not identify the declared bed and objects in the fixture.'
}
foreach ($row in @($v6 | Where-Object row_type -eq 'overall')) {
    $inventory = $row.source_inventory | ConvertFrom-Json
    if ($inventory.Provenance -ne 'decoded_damf' -or $inventory.Beds.Count -ne 1 -or
        $inventory.Beds[0].Channel -ne 'ScreenLFE' -or $inventory.Beds[0].Id -ne 3 -or
        ($inventory.Objects.DeclaredId -join ',') -ne '10,11,12,13,14,15,16,17,18,19,20' -or
        $row.verification -ne 'matched' -or $row.post_frames -ne '16000') {
        throw 'The TrueHDD presentation inventory was not preserved across layouts.'
    }
}

foreach ($layout in $layouts) {
    $outputName = $layout -replace '[^0-9.]', ''
    $v5Mkv = Join-Path $WorkDirectory "V5-$outputName.mkv"
    $v6Mkv = Join-Path $WorkDirectory "V6-$outputName.mkv"
    $v5Pcm = Join-Path $WorkDirectory "V5-$outputName.f32"
    $v6Pcm = Join-Path $WorkDirectory "V6-$outputName.f32"
    $export = @('--input', $inputFile, '--layout', $layout, '--listener-gain-db', '0', '-f', 'PCM_Float')
    $null = Invoke-Checked $Baseline ($export + @('--output', $v5Mkv))
    $null = Invoke-Checked $Candidate ($export + @('--output', $v6Mkv))
    $pcmFormat = @('-map', '0:a:0', '-c:a', 'pcm_f32le', '-f', 'f32le')
    $null = Invoke-Checked 'ffmpeg' (@('-nostdin', '-v', 'error', '-i', $v5Mkv) + $pcmFormat + @($v5Pcm))
    $null = Invoke-Checked 'ffmpeg' (@('-nostdin', '-v', 'error', '-i', $v6Mkv) + $pcmFormat + @($v6Pcm))
    if ((Get-FileHash -LiteralPath $v5Pcm).Hash -cne (Get-FileHash -LiteralPath $v6Pcm).Hash) {
        throw "V5 and V6 float PCM bytes differ for $layout."
    }

    $statistics = Invoke-Checked 'ffmpeg' @('-nostdin', '-hide_banner', '-i', $v6Mkv, '-map', '0:a:0',
        '-af', 'astats=metadata=0:reset=0', '-f', 'null', '-')
    $slots = @{}
    $slot = 0
    foreach ($line in $statistics) {
        if ($line -match '\] Channel: (\d+)\s*$') {
            $slot = [int]$Matches[1]
            $slots[$slot] = @{}
        } elseif ($line -match '\] Overall\s*$') {
            $slot = 0
        } elseif ($slot -and $line -match '\] (Peak|RMS) level dB: (\S+)\s*$') {
            $slots[$slot][$Matches[1]] = $Matches[2]
        }
    }
    $rows = @($v6 | Where-Object {
        $_.row_type -eq 'channel' -and $_.speaker_configuration -eq $layout -and $_.listener_gain_mode -eq 'Explicit override'
    })
    if ($slots.Count -ne $rows.Count -or $rows.Count -eq 0) { throw "Unexpected FFmpeg channel count for $layout." }
    foreach ($row in $rows) {
        $astats = $slots[[int]$row.slot]
        Assert-Db $row.post_dbfs $astats['Peak'] "$layout $($row.speaker) peak"
        Assert-Db $row.post_rms_dbfs $astats['RMS'] "$layout $($row.speaker) RMS"
    }
}
if ((Get-FileHash -LiteralPath $inputFile).Hash -ne $inputHash) { throw 'The source MKV changed.' }
Write-Host 'V5 and V6 PCM and measurements match across all three layouts. FFmpeg astats confirms every processed peak and RMS.'
