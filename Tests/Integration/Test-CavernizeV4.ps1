param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Decoder,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [switch]$RequireMkvMerge
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Checked([string]$Tool, [string[]]$Arguments, [int]$ExpectedExit = 0) {
    $lines = & $Tool @Arguments 2>&1
    $code = $LASTEXITCODE
    $message = $lines -join [Environment]::NewLine
    if ($code -ne $ExpectedExit) {
        throw "Expected exit $ExpectedExit from $Tool, got $code. $Arguments $message"
    }
    return $message
}

function Assert-Equal($Actual, $Expected, [string]$Field) {
    if ($Actual -cne $Expected) { throw "$Field expected [$Expected], observed [$Actual]" }
}

function Overall([string]$Csv) {
    return @(Import-Csv -LiteralPath $Csv | Where-Object row_type -eq 'overall')
}

function Probe([string]$Path, [string[]]$Arguments) {
    Invoke-Checked 'ffprobe' (@('-v', 'error') + $Arguments + @('-of', 'json', $Path)) | ConvertFrom-Json
}

function AudioBytes([string]$InputFile, [string]$OutputFile, [string]$Format) {
    $null = Invoke-Checked 'ffmpeg' @('-v', 'error', '-xerror', '-i', $InputFile, '-map', '0:a:0', '-f', $Format, $OutputFile)
    return (Get-FileHash -LiteralPath $OutputFile).Hash
}

$movieDir = Join-Path $WorkDirectory 'Movies (Fixture)'
$ssd = Join-Path $WorkDirectory 'SSD workspace'
$outputDir = Join-Path $WorkDirectory 'Media exports'
$null = New-Item -ItemType Directory -Path $movieDir, $outputDir
$stream = Join-Path $WorkDirectory 'atmos.thd'
$version = '08c295919a8f516ba28d4099f30e0aecb4bd0eca'
Invoke-WebRequest "https://raw.githubusercontent.com/truehdd/truehdd/$version/truehd/tests/assets/fba_atmos_dimtrim.mlp" -OutFile $stream
Assert-Equal (Get-FileHash $stream).Hash.ToLowerInvariant() 'e39283e66ad93106137db1d2f8b436ce47167983fbb8bf0de0d300e85de5d3c1' 'Atmos fixture SHA-256'

$subtitle = Join-Path $WorkDirectory 'subtitle.srt'
$chapters = Join-Path $WorkDirectory 'chapters.txt'
$attachment = Join-Path $WorkDirectory 'attachment.txt'
@('1', '00:00:00,050 --> 00:00:00,225', 'Cavernize test subtitle') | Set-Content -Encoding ascii $subtitle
@(';FFMETADATA1', 'title=Cavernize fixture', '[CHAPTER]', 'TIMEBASE=1/1000', 'START=50',
    'END=275', 'title=First chapter') | Set-Content -Encoding ascii $chapters
'Cavernize attachment' | Set-Content -Encoding ascii $attachment
$mkv = Join-Path $movieDir 'Movie (UHD) Ω.mkv'
$mka = Join-Path $movieDir 'Audio (Atmos).mka'
$ambiguous = Join-Path $movieDir 'Two TrueHD tracks.mka'
$sine = 'sine=frequency=401:sample_rate=48000:d=0.333'
$null = Invoke-Checked 'ffmpeg' @('-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=s=96x48:r=25:d=0.32', '-f', 'lavfi', '-i', $sine,
    '-i', $stream, '-i', $subtitle, '-f', 'ffmetadata', '-i', $chapters, '-map', '0:v', '-map', '1:a', '-map', '2:a', '-map', '3:s',
    '-map_metadata', '4', '-map_chapters', '4', '-c:v', 'libx265', '-pix_fmt', 'yuv420p10le', '-bf', '0',
    '-x265-params', 'pools=1:frame-threads=1:repeat-headers=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc',
    '-color_primaries', 'bt2020', '-color_trc', 'smpte2084',
    '-colorspace', 'bt2020nc', '-c:a:0', 'pcm_s16le', '-c:a:1', 'copy', '-c:s', 'srt',
    '-metadata:s:a:1', 'language=eng', '-metadata:s:a:1', 'title=Atmos fixture', '-attach', $attachment,
    '-metadata:s:t:0', 'mimetype=text/plain', $mkv)
$null = Invoke-Checked 'ffmpeg' @('-v', 'error', '-f', 'lavfi', '-i', $sine, '-i', $stream, '-map', '0:a', '-map', '1:a',
    '-c:a:0', 'pcm_s16le', '-c:a:1', 'copy', $mka)
$null = Invoke-Checked 'ffmpeg' @('-v', 'error', '-i', $stream, '-map', '0:a', '-map', '0:a', '-c', 'copy', $ambiguous)

$sourceHash = (Get-FileHash -LiteralPath $mkv).Hash
$mkaHash = (Get-FileHash -LiteralPath $mka).Hash
$decoderFlag = @('--truehdd', $Decoder)
$analysis = @('--report-only', '--quality', '--verify-peak-events', '--work-dir', $ssd) + $decoderFlag
$side = @('--layout', '5.1.2 side')
$top = @('--layout', '5.1.6 with top sides')
$null = Invoke-Checked $Executable (@('--input', $mkv, '--keep-verification-json') + $analysis + $side + $top)
$movieReport = [IO.Path]::ChangeExtension($mkv, '.csv')
$rows = @(Overall $movieReport)
Assert-Equal $rows.Count 2 'Combined overall rows'
foreach ($row in $rows) {
    Assert-Equal $row.original_source_path $mkv 'Original MKV path'
    Assert-Equal $row.source_codec 'TrueHD' 'Source codec'
    Assert-Equal $row.audio_track_index '1' 'Automatic TrueHD selection'
    Assert-Equal $row.container_track_number '3' 'Container track number'
    Assert-Equal $row.decoder_version '0.6.3' 'Decoder version'
    Assert-Equal $row.status 'source_coverage_unverified' 'Coverage provenance'
    Assert-Equal $row.pre_frames '16000' 'Decoded frame count'
    Assert-Equal $row.post_frames '16000' 'Rendered frame count'
    Assert-Equal $row.verification 'matched' 'Atmos reconstruction'
    Assert-Equal ([double]$row.listener_gain_effective_linear) $(if ($row.layout_id -eq '5.1.2-side') { 0.707 } else { 1.0 }) 'Layout gain'
    if (!$row.bed_positions -or !$row.render_settings) { throw 'Missing original report or render settings' }
}
$evidence = @(Get-ChildItem -LiteralPath $ssd -Recurse -Filter '*-Verification.json' -File)
Assert-Equal $evidence.Count 2 'Retained SSD diagnostics'
foreach ($item in $evidence) {
    $json = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json
    Assert-Equal $json.SourceFile ([IO.Path]::GetFileName($mkv)) 'Diagnostic source identity'
    if (!$json.Evidence.Matches -or $json.AnalysisId -notin $rows.analysis_id) { throw 'Unmatched diagnostic evidence' }
}
Assert-Equal @(Get-ChildItem -LiteralPath $ssd -Recurse -File).Count 2 'Clean successful SSD jobs'
Assert-Equal @(Get-ChildItem -LiteralPath $movieDir -File).Count 4 'Movie-side writes after first analysis'

$inputList = Join-Path $WorkDirectory 'inputs.txt'
@($mka, $mkv) | Set-Content -Encoding utf8 $inputList
$null = Invoke-Checked $Executable (@('--input-list', $inputList, '--listener-gain-db', '0') + $analysis + $side)
Assert-Equal @(Overall $movieReport).Count 3 'Retained gain variants'
Assert-Equal @(Overall ([IO.Path]::ChangeExtension($mka, '.csv')))[0].verification 'matched' 'MKA verification'
Assert-Equal @(Get-ChildItem -LiteralPath $ssd -Directory).Count 1 'Routine jobs delete their workspaces'
$badChoice = Invoke-Checked $Executable (@('--input', $ambiguous) + $analysis + $side) 2
if ($badChoice -notmatch 'Multiple TrueHD') { throw 'Ambiguous input must ask for an explicit track' }
$null = Invoke-Checked $Executable (@('--input', $ambiguous, '--track', '1') + $analysis + $side)
Assert-Equal @(Overall ([IO.Path]::ChangeExtension($ambiguous, '.csv')))[0].audio_track_index '1' 'Explicit selection'

$csvHash = (Get-FileHash -LiteralPath $movieReport).Hash
$failed = Invoke-Checked $Executable (@('--input', $mkv, '--report-only', '--work-dir', $ssd,
    '--truehdd', (Join-Path $WorkDirectory 'missing.exe')) + $side) 1
if ($failed -notmatch 'Analysis diagnostics' -or !(Get-ChildItem -LiteralPath $ssd -Recurse -Filter 'failure.log')) {
    throw 'Decoder failure left no SSD diagnostics'
}
Assert-Equal (Get-FileHash -LiteralPath $movieReport).Hash $csvHash 'Valid report after failed decode'
Assert-Equal @(Get-ChildItem -LiteralPath $movieDir -Filter '_extracted*' -File).Count 0 'Report-only isolation from the movie drive'

$referenceStem = Join-Path $WorkDirectory 'reference'
$null = Invoke-Checked $Decoder @('decode', '--json', $stream, '--output-path', $referenceStem)
$reference = "$referenceStem.atmos"
$nativeWav = Join-Path $outputDir 'Reference float.wav'
$encodedWav = Join-Path $outputDir 'Reference int16.wav'
$native = Join-Path $outputDir 'Rendered native.mkv'
$encoded = Join-Path $outputDir 'Rendered encoded.mkv'
$unusedSsd = Join-Path $WorkDirectory 'Must never be created during media export'
$mediaArgs = @('--layout', '5.1.2 side', '--listener-gain-db', '0', '--work-dir', $unusedSsd)
$null = Invoke-Checked $Executable (@('--input', $reference, '--output', $nativeWav, '-f', 'PCM_Float') + $mediaArgs)
$null = Invoke-Checked $Executable (@('--input', $reference, '--output', $encodedWav, '-f', 'PCM_LE') + $mediaArgs)
$null = Invoke-Checked $Executable (@('--input', $mkv, '--output', $native, '-f', 'PCM_Float', '--keep-temp-files',
    '--quality', '--verify-peak-events') + $decoderFlag + $mediaArgs)
$mediaIntermediates = @(Get-ChildItem -LiteralPath $movieDir -Filter '_extracted-*' -File)
Assert-Equal $mediaIntermediates.Count 6 'TrueHDD media intermediates beside the original source'
$null = Invoke-Checked $Executable (@('--input', $mkv, '--output', $encoded, '-f', 'FLAC',
    '--quality', '--verify-peak-events') + $decoderFlag + $mediaArgs)
Assert-Equal @(Get-ChildItem -LiteralPath $movieDir -Filter '_extracted-*' -File).Count 6 'Media export cleans only its own intermediates'
$mkaWav = Join-Path $outputDir 'Rendered from MKA.wav'
$null = Invoke-Checked $Executable (@('--input', $mka, '--output', $mkaWav, '-f', 'PCM_Float', '--quality',
    '--verify-peak-events') + $decoderFlag + $mediaArgs)
$mkaMedia = @(Overall ([IO.Path]::ChangeExtension($mka, '.csv')) | Where-Object {
    ($_.render_settings | ConvertFrom-Json).ExportCodec -eq 'PCM_Float'
})
Assert-Equal $mkaMedia.Count 1 'Integrated CSV written for MKA media export'
Assert-Equal $mkaMedia[0].verification 'matched' 'MKA media export verified'
if (Test-Path -LiteralPath $unusedSsd) { throw 'Media export used the analysis SSD' }

$videoPackets = @('-select_streams', 'v:0', '-show_packets', '-show_data_hash', 'sha256',
    '-show_entries', 'packet=pts_time,dts_time,duration_time,data_hash')
$originalVideo = (Probe $mkv $videoPackets).packets | ConvertTo-Json -Depth 10 -Compress
$original = Probe $mkv @('-show_format', '-show_chapters', '-show_streams', '-show_data_hash', 'sha256')
foreach ($item in @(@($native, 'pcm_f32le', 'f32le', $nativeWav, 4), @($encoded, 'flac', 's16le', $encodedWav, 2))) {
    $path, $codec, $format, $referenceWav, $sampleBytes = $item
    $probe = Probe $path @('-show_format', '-show_chapters', '-show_streams', '-show_data_hash', 'sha256')
    Assert-Equal $probe.streams.Count 4 'Stream inventory'
    $audio = @($probe.streams | Where-Object codec_type -eq 'audio')
    Assert-Equal $audio.Count 1 'Only the new speaker audio track'
    Assert-Equal $audio[0].codec_name $codec 'Encoded audio codec'
    Assert-Equal ([string]$audio[0].sample_rate) '48000' 'Export sample rate'
    Assert-Equal $audio[0].channels 8 'Export channel count'
    Assert-Equal $audio[0].tags.language 'eng' 'Language'
    $originalAtmos = $original.streams | Where-Object codec_name -eq 'truehd'
    if ([Math]::Abs([double]$audio[0].start_time - [double]$originalAtmos.start_time) -gt 0.002) {
        throw 'Rendered audio start does not align with the original selected track.'
    }
    Assert-Equal $audio[0].tags.CAVERNIZE_CHANNEL_ORDER 'Front left,Front right,Front center,LFE,Side left,Side right,Top rear left,Top rear right' 'Applied channel order'
    Assert-Equal $probe.format.tags.title $original.format.tags.title 'Title'
    Assert-Equal $probe.streams[0].color_primaries 'bt2020' 'HDR colour primaries'
    Assert-Equal $probe.streams[0].color_transfer 'smpte2084' 'HDR transfer'
    Assert-Equal $probe.streams[0].color_space 'bt2020nc' 'HDR colour space'
    Assert-Equal $probe.chapters[0].tags.title $original.chapters[0].tags.title 'Chapter title'
    Assert-Equal $probe.chapters[0].start_time $original.chapters[0].start_time 'Chapter timestamp'
    Assert-Equal $probe.chapters[0].end_time $original.chapters[0].end_time 'Chapter end'
    $originalAttachment = $original.streams | Where-Object codec_type -eq 'attachment'
    $outputAttachment = $probe.streams | Where-Object codec_type -eq 'attachment'
    Assert-Equal $outputAttachment.extradata_hash $originalAttachment.extradata_hash 'Attachment content'
    Assert-Equal $outputAttachment.tags.filename $originalAttachment.tags.filename 'Attachment name'
    Assert-Equal ((Probe $path $videoPackets).packets | ConvertTo-Json -Depth 10 -Compress) $originalVideo 'Copied video packets and timestamps'
    $subtitlePacketArgs = @('-select_streams', 's:0', '-show_packets', '-show_data_hash', 'sha256',
        '-show_entries', 'packet=pts_time,duration_time,data_hash')
    $expectedSubtitle = (Probe $mkv $subtitlePacketArgs).packets | ConvertTo-Json -Depth 10 -Compress
    Assert-Equal ((Probe $path $subtitlePacketArgs).packets | ConvertTo-Json -Depth 10 -Compress) $expectedSubtitle 'Subtitle packets'
    $raw = "$path.$format"
    $referenceRaw = "$referenceWav.$format"
    Assert-Equal (AudioBytes $path $raw $format) (AudioBytes $referenceWav $referenceRaw $format) 'Bit-identical full rendered audio'
    Assert-Equal (Get-Item -LiteralPath $raw).Length (16000 * 8 * $sampleBytes) 'Full rendered audio duration'
    if (Get-Command mkvmerge -ErrorAction SilentlyContinue) {
        $structure = Invoke-Checked 'mkvmerge' @('-J', $path) | ConvertFrom-Json
        Assert-Equal $structure.tracks.Count 3 'MKVToolNix track inventory'
        Assert-Equal $structure.attachments.Count 1 'MKVToolNix attachment inventory'
        Assert-Equal $structure.chapters[0].num_entries 1 'MKVToolNix chapters'
    } elseif ($RequireMkvMerge) {
        throw 'mkvmerge was required for independent Matroska validation.'
    }
}

$bytes = [IO.File]::ReadAllBytes("$native.f32le")
$max = 0.0
$sum = 0.0
for ($offset = 0; $offset -lt $bytes.Length; $offset += 4) {
    $sample = [double][BitConverter]::ToSingle($bytes, $offset)
    $max = [Math]::Max($max, [Math]::Abs($sample))
    $sum += $sample * $sample
}
$summary = @(Overall $movieReport | Where-Object { $_.layout_id -eq '5.1.2-side' -and $_.listener_gain_requested_db -eq '0' })[-1]
if ($max -le 0 -or [Math]::Abs($max - [double]$summary.post_abs_linear) -gt 0.00000001 -or
    [Math]::Abs([Math]::Sqrt($sum / ($bytes.Length / 4)) - [double]$summary.post_rms_linear) -gt 0.00000001) {
    throw 'Independent MKV peak and RMS did not match Cavernize'
}
$null = Invoke-Checked $Executable @('--input', $mkv, '--output', $mkv) 2
Assert-Equal (Get-FileHash -LiteralPath $mkv).Hash $sourceHash 'Preserved source MKV'
Assert-Equal (Get-FileHash -LiteralPath $mka).Hash $mkaHash 'Preserved source MKA'

# Change the parity nibble of an access unit without changing its size or the Matroska structure.
$rawAtmos = [IO.File]::ReadAllBytes($stream)
$damaged = [IO.File]::ReadAllBytes($mka)
$offset = 0
for ($frame = 0; $frame -lt 20; $frame++) {
    $offset += (([int]$rawAtmos[$offset] * 256 + [int]$rawAtmos[$offset + 1]) -band 0xfff) * 2
}
$frameSize = (([int]$rawAtmos[$offset] * 256 + [int]$rawAtmos[$offset + 1]) -band 0xfff) * 2
$expectedFrame = [Convert]::ToHexString($rawAtmos, $offset, $frameSize)
$payloadOffset = [Convert]::ToHexString($damaged).IndexOf($expectedFrame, [StringComparison]::Ordinal)
if ($payloadOffset -lt 0 -or $payloadOffset % 2 -ne 0) { throw 'Fixture access unit could not be located in the MKA' }
$index = [int]($payloadOffset / 2)
$damaged[$index] = $damaged[$index] -bxor 0x10
$corruptDirectory = Join-Path $WorkDirectory 'Corrupt input'
$null = New-Item -ItemType Directory -Path $corruptDirectory
$corrupt = Join-Path $corruptDirectory 'Damaged TrueHD.mka'
[IO.File]::WriteAllBytes($corrupt, $damaged)
$diagnosticRoot = Join-Path $WorkDirectory 'Decoder failure'
$failure = Invoke-Checked $Executable (@('--input', $corrupt, '--report-only', '--work-dir', $diagnosticRoot) + $decoderFlag + $side) 1
if ($failure -notmatch 'Decoder log:' -or $failure -notmatch 'parity check failed' -or $failure.Length -gt 1600) {
    throw "Missing or unbounded analysis diagnostic: $failure"
}
$rawLogs = @(Get-ChildItem -LiteralPath $diagnosticRoot -Recurse -Filter 'truehdd.log' -File)
Assert-Equal $rawLogs.Count 1 'Full analysis decoder log'
if (!(Select-String -LiteralPath $rawLogs[0].FullName -Pattern 'parity check failed')) { throw 'Missing raw parity warning' }
$failedOutput = Join-Path $outputDir 'Failed decode.wav'
$failure = Invoke-Checked $Executable (@('--input', $corrupt, '--output', $failedOutput) + $decoderFlag + $side) 1
if ($failure -notmatch 'Decoder log:' -or $failure.Length -gt 1600 -or (Test-Path -LiteralPath $failedOutput)) {
    throw "Media decode did not fail before output: $failure"
}
$mediaLogs = @(Get-ChildItem -LiteralPath $corruptDirectory -Filter '_extracted-*.thd.truehdd.log' -File)
Assert-Equal $mediaLogs.Count 1 'Media failure diagnostics beside the source'
if (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($corrupt, '.csv'))) { throw 'A failed decode published a CSV' }

Write-Host 'V4 passed real TrueHD/Atmos MKV and MKA analysis, SSD and export isolation, report protection, and both media routes.'
exit 0
