# Cavernize V4

Download the verified Windows x64 GitHub Actions artifact and extract the inner release ZIP into a fresh test directory such as `C:\AtmosMetaData\Cavernize-V4`. Start `CavernizeGUI.exe` from its root or run `CLI\CavernizeCLI.exe` in PowerShell. The package includes the .NET 8 runtimes and the TrueHDD 0.6.3 decoder under `Tools\TrueHDD`. The GUI uses the WebView2 Evergreen Runtime. MKV media export also needs FFmpeg on PATH or at the configured location. Keep the V4 installation separate from any existing Cavernize copy.

## Integrated analysis

Open a direct `.atmos` master or a `.mkv` or `.mka` container. Cavernize selects the single available TrueHD audio track automatically. If several exist, choose a track in the GUI dropdown or pass its **zero-based audio index** with `--track`. This index excludes video and subtitles. The CSV also records the separate Matroska track number. TrueHDD is invoked within Cavernize. Channel-only TrueHD can be analyzed with grading. Peak-event verification requires an Atmos presentation.

In the GUI, set the primary target using the main layout selector. Under Settings, enable **Report only**, **Quality analysis and grading**, and **Verify PCM peak events (Atmos)**. Open **Analysis workspace and layouts** to set a local SSD directory, select additional layouts, or retain raw verification JSON. Start the render and check the final report path in the status bar. A custom TrueHDD executable can be selected there for analysis. CLI overrides use `--truehdd`.

The default Windows analysis workspace is `C:\AtmosMetaData\Work`. For analysis-only jobs, Cavernize creates an isolated subdirectory on the chosen local volume and checks estimated free space. The estimate reserves a typical 16-channel Atmos payload plus extraction and diagnostics. The selected volume must be an SSD. The default path and volume check establish storage location and capacity. Drive hardware detection depends on the Windows storage configuration, so select the SSD explicitly when appropriate. TrueHDD inputs, decoded masters, logs, and evidence stay under that workspace. Successful jobs delete intermediate media, keeping JSON only when requested. Failed jobs report the diagnostic directory.

Media exports keep their own paths. TrueHDD media-export work stays beside the input, where the previous conversion already worked. Rendered MKV staging stays beside the chosen output, and existing media export settings still apply. The analysis workspace setting is ignored whenever a media file is exported, including exports with grading or peak verification.

## One movie and batch commands

```powershell
$cli = 'C:\AtmosMetaData\Cavernize-V4\CLI\CavernizeCLI.exe'
$source = 'V:\Movies (UHD)\Ready Player One (2018) (UHD) (Atmos).mkv'
$work = 'C:\AtmosMetaData\Work'

& $cli --input $source --layout '5.1.6 with top sides' --layout '5.1.2 side' --report-only --quality --verify-peak-events --work-dir $work
if ($LASTEXITCODE -ne 0) { throw "Analysis failed ($LASTEXITCODE)" }

& $cli --input $source --layout '5.1.2 side' --report-only --quality --verify-peak-events --listener-gain-db 0 --work-dir $work --keep-verification-json
```

The first command generates a single CSV with both layouts. The second adds an explicit unity-gain result without replacing the automatic 0.707 result. The existing `--render-gain` still multiplies the automatic gain, and cannot be combined with the absolute override. The limiter stays active.

Repeat `--input` for several sources or use a UTF-8 list with one path per line. Relative entries are resolved from the list's directory. The batch continues across failures and returns a failure code if any source fails.

```powershell
& $cli --input-list 'C:\AtmosMetaData\movies.txt' --layout '5.1.6 with top sides' --report-only --quality --verify-peak-events --work-dir $work
if ($LASTEXITCODE -ne 0) { throw "One or more sources failed ($LASTEXITCODE)" }
```

Exit codes are 0 for a completed requested operation, 1 for processing, verification, or publication failure, and 2 for a single invocation with invalid options or ambiguous track selection. `--list-layouts` lists exact names. `--legacy-report-files` requests the V3 single-layout naming convention for direct Atmos regression only.

## Reports

The integrated CSV uses the exact original basename and directory, for example `Ready Player One (2018) (UHD) (Atmos).csv`. Completed results retain other layout, track and gain configurations for the same source revision. Each result has an analysis ID, a layout ID, a settings ID and structured settings. The CSV contains original report bed positions and labels, dynamic and unused target counts, all applicable grades, both PCM taps, peak events and float bits, RMS, and activity. A same-name unrelated CSV or one for a different source revision is preserved. Move it aside deliberately if the new result should replace it. Publication is atomic. Source revisions use file length and modification time, including the applicable DAMF companions.

`source_coverage_unverified` can accompany a complete render of a TrueHDD-decoded payload. Cavernize checks the decoder summary and decoded frame counts. This does not establish the integrity of the original multiplexed source. The separate peak verification status records whether the independent scan and reconstruction matched. `--keep-verification-json` retains the full evidence under the analysis SSD job path. Media-export evidence requested through the CLI goes beside the explicit output.

## MKV media export

Select the normal GUI export action with Report only off. Choose a new output file. The CLI can export a Matroska input as a speaker-rendered MKV.

```powershell
& $cli --input $source --layout '5.1.2 side' --output 'C:\AtmosMetaData\RPO Speaker PCM.mkv' -f PCM_Float --quality --verify-peak-events
& $cli --input $source --layout '5.1.2 side' --output 'C:\AtmosMetaData\RPO Speaker FLAC.mkv' -f FLAC --quality
```

The PCM route uses Cavernize's native Matroska PCM writer. The encoded route feeds a rendered WAV to FFmpeg. Both use a final metadata-preserving remux, because the old native writer copied only video from the source. Video, subtitles, and attachments are copied, chapter data and global metadata are mapped, and original audio tracks are replaced by the rendered speaker track. The new track carries its source language and a `CAVERNIZE_CHANNEL_ORDER` tag. Player layouts can be inferred from the codec or channel count, so confirm routing for nonstandard height layouts. The remux stream-copies Dolby Vision and HDR video packets. Container-level Dolby Vision signalling and player behavior should be checked on the actual source and playback chain.

The export is rendered speaker PCM or its selected encoding. It does not carry Atmos objects. The automated media test compares complete audio against an independent decode of the matching WAV export, confirms packet payloads and timings, checks chapters, subtitles and attachment contents, and verifies the original source SHA-256.

## Ready Player One and JRiver MC36 acceptance

From a V4 feature checkout, run the comparison helper on the local full movie. Select the same source version used for the reference and use the same decoder presentation and render settings.

```powershell
.\Tests\Integration\Test-CavernizeRpo.ps1 -Executable $cli -Source 'C:\AtmosMetaData\RPO.atmos' -WorkDirectory $work
```

The check uses 403,084,680 frames at 48 kHz, frame 235,083,282, V3 gain, peak and RMS values, and all applicable legacy metric IDs. Compare the detailed V4 quality results with the saved V3 report for the same source. The full ready-player soundtrack is not part of the automated fixture.

For JRiver MC36, import one movie into a disposable test library and record its track list. Run an integrated analysis with the SSD outside the watched movie folder. Rescan and confirm how your import rules handle the companion CSV before processing more movies. Open the exported MKV separately, start playback, seek near the start, middle, chapter boundaries and end, and select each intended subtitle. Use your multichannel output settings to confirm the decoded sample rate, routing and height channel order against the export tag. If applicable, confirm HDR and Dolby Vision playback on the actual display. Compare the source file SHA-256 before and after the test. GitHub's automated fixture tests do not exercise JRiver playback.
