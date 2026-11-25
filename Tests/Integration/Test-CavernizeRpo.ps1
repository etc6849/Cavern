param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$WorkDirectory = 'C:\AtmosMetaData\Work'
)

$ErrorActionPreference = 'Stop'
$layouts = @('5.1.6 with top sides', '5.1.2 side')
& $Executable --input $Source --layout $layouts[0] --layout $layouts[1] --report-only --quality --verify-peak-events --work-dir $WorkDirectory --keep-verification-json
if ($LASTEXITCODE -ne 0) { throw "Ready Player One analysis failed with exit $LASTEXITCODE." }

$csv = [IO.Path]::ChangeExtension($Source, '.csv')
$all = @(Import-Csv -LiteralPath $csv)
function Near([double]$Actual, [double]$Expected, [double]$Tolerance, [string]$Metric) {
    if ([double]::IsNaN($Actual) -or [double]::IsInfinity($Actual) -or [Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw "$Metric expected $Expected +/- $Tolerance, observed $Actual"
    }
}
foreach ($layout in $layouts) {
    $result = @($all | Where-Object {
        $_.row_type -eq 'overall' -and $_.speaker_configuration -eq $layout -and $_.verification -eq 'matched' -and
        $_.listener_gain_mode -eq 'Automatic' -and ($_.render_settings | ConvertFrom-Json).ExportCodec -eq $null
    })[-1]
    if (!$result -or $result.sample_rate_hz -ne '48000' -or $result.pre_frames -ne '403084680' -or $result.post_frames -ne '403084680') {
        throw "Ready Player One coverage mismatch in $layout."
    }
    if ($result.status -ne 'complete' -and $result.status -ne 'source_coverage_unverified') { throw 'Incomplete result' }
    $channels = @($all | Where-Object { $_.row_type -eq 'channel' -and $_.analysis_id -eq $result.analysis_id })
    $highest = $channels | Where-Object slot -eq $result.pre_slot
    if ($highest.speaker -ne 'Side left' -or $result.pre_frame -ne '235083282') { throw "Peak event moved in $layout." }
    if ($layout -eq $layouts[0]) {
        Near $result.listener_gain_effective_linear 1 0.0000001 'Automatic listener gain'
        Near $result.pre_dbfs 5.342746785633084 0.00001 'Pre-limiter peak'
        Near $result.post_dbfs (-0.0026511277) 0.00001 'Processed peak'
        Near $result.pre_rms_dbfs (-28.043428) 0.00001 'Pre-limiter RMS'
        Near $result.post_rms_dbfs (-28.159846) 0.00001 'Processed RMS'
        $bits = '0x{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes([float]-1.8498535), 0)
        if ($result.pre_float_bits -ne $bits) { throw "Ready Player One peak bits mismatch. Expected $bits." }
    } else {
        Near $result.listener_gain_effective_linear 0.707 0.0000001 'Automatic listener gain'
        Near $result.pre_dbfs 2.331135 0.00001 'Pre-limiter peak'
        Near $result.post_dbfs (-0.0066479893) 0.00001 'Processed peak'
        Near $result.pre_rms_dbfs (-29.2941255333) 0.00001 'Pre-limiter RMS'
        Near $result.post_rms_dbfs (-29.3306287763) 0.00001 'Processed RMS'
    }

    $quality = @($all | Where-Object { $_.row_type -eq 'quality' -and $_.analysis_id -eq $result.analysis_id })
    $required = @('PeaGa', 'RMSGa', 'MacDy', 'MicDy', 'PeaLF', 'RMSLF', 'MacLF', 'MicLF', 'CheSl', 'SurUs', 'HeiUs')
    foreach ($metric in $required) {
        if ($metric -notin $quality.quality_metric_id) { throw "Missing legacy quality row $metric in $layout." }
    }
    Near ($quality | Where-Object quality_metric_id -eq 'PeaGa').quality_db $(if ($layout -eq $layouts[0]) { -5.66 } else { -6.05 }) 0.01 'Legacy peak audio frame level'
    Write-Host "$layout matches the supplied PCM reference. Compare the bed/object/unused counts and legacy grades with your saved V3 report."
}
