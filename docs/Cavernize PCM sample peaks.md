# Cavernize PCM sample peaks

The post-render report includes two sections for channel renders, including Report only with quality grading on or off. Each section lists the overall maximum and one row per listener output slot.

- **Before the listener limiter** measures spatially rendered and summed speaker PCM after LFE filtering and listener gain. It can report positive dBFS when a sample exceeds unity.
- **Processed listener PCM before export** measures the buffer returned by the listener after limiting and any listener-side convolution. Export rematrixing, export virtualization and its separate limiter, quantization, and encoding are downstream.

When export virtualization is selected, the measurements describe its virtual speaker inputs at these same taps. A headphone render therefore lists the applied virtual speakers, before the later stereo fold-down.

These are discrete sample peaks relative to unity. The overall value is the largest channel peak. A valid silent channel reports `−∞ dBFS`. The report keeps the previous RMS-based fields and grades. Neither sample-peak section measures the output of an external processor such as the Trinnov AltitudeCI.

The rows follow the actual listener order. For **5.1.6 with top sides**, the default order is front left/right/center, LFE, side left/right, top front left/right, top side left/right, and top rear left/right. The existing surround swap exchanges 1-based slots 5 and 7 and slots 6 and 8. On this preset that swaps the side and top-front pairs.

A complete result requires every expected rendered frame and a structurally complete direct CAF/DAMF PCM payload. The CAF check verifies its declared sample alignment and byte extent. Other inputs, including externally decoded TrueHD, report source coverage as unverified even if every expected rendered frame arrived. A known render or source failure produces an incomplete observation and explains the reason. Zero PCM samples alone never indicate a coverage problem.

## Build and package a separate Windows test copy

Use **Windows x64**. Download the `Cavernize-Test-win-x64-Release` artifact from the feature branch's GitHub Actions run, then extract the inner `Cavernize-Test-win-x64-Release.zip` into a fresh `C:\AtmosMetaData\Cavernize-Test` directory. Keep all files together, with `CavernizeGUI.exe` at the root and `CLI\CavernizeCLI.exe` in its own folder. The existing installation at `C:\AtmosMetaData\Cavernize` is separate. A separate Windows user profile or VM also isolates saved GUI and Cavern Driver settings.

Both published applications bundle their .NET 8 runtime. The GUI's About panel uses the Microsoft Edge WebView2 Evergreen Runtime. If you use the optional native `CavernAmp.dll`, use the same matching x64 version for comparisons. Direct DAMF analysis needs no FFmpeg or external decoder. The GUI still uses its existing FFmpeg and `truehdd.exe` integrations for formats that require them.

Compare the hash of the inner ZIP to the accompanying `.zip.sha256` file before extracting.

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\Cavernize-Test-win-x64-Release.zip'
```

To publish locally, use a .NET 8 SDK from the feature checkout root in PowerShell.

```powershell
$testDir = 'C:\AtmosMetaData\Cavernize-Test'
if (Test-Path -LiteralPath $testDir) {
    throw "Use an empty destination. Move the existing test directory aside first."
}
dotnet publish .\CavernSamples\CavernizeGUI\CavernizeGUI.csproj -c Release -r win-x64 --self-contained true -o $testDir
if ($LASTEXITCODE -ne 0) { throw 'Cavernize GUI publish failed.' }
dotnet publish .\CavernSamples\CavernizeCLI\CavernizeCLI.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $testDir 'CLI')
if ($LASTEXITCODE -ne 0) { throw 'Cavernize CLI publish failed.' }
```

## GUI and headless analysis

In the GUI, open the `.atmos` master with its matching `.atmos.audio` and `.atmos.metadata` companions beside it. Select the exact render target. In Settings, enable **Report only**, **Quality analysis and grading**, and **Verify PCM peak events (Atmos)** as needed, then start the render. Use **Show post-render report** to review the results. The CLI equivalents can all be enabled in one invocation, with no audio output file or interactive prompt.

```powershell
$cli = 'C:\AtmosMetaData\Cavernize-Test\CLI\CavernizeCLI.exe'
$source = 'C:\AtmosMetaData\RPO.atmos'
& $cli --help
& $cli --list-layouts

# A. 5.1.6 with top sides, automatic gain
& $cli --input $source --layout '5.1.6 with top sides' --report-only --quality --verify-peak-events

# B. 5.1.2 side, automatic gain
& $cli --input $source --layout '5.1.2 side' --report-only --quality --verify-peak-events

# C. 5.1.2 side, absolute unity gain
& $cli --input $source --layout '5.1.2 side' --report-only --quality --verify-peak-events --listener-gain-db 0
```

| Option | Purpose |
| --- | --- |
| `--input`, `-i` | Select the source master. The first positional argument can also be the input. |
| `--layout`, `-t` | Use one exact name from `--list-layouts`, quoted if it contains spaces. Layout shorthand is accepted when unambiguous. The default is `5.1.2 side`. |
| `--report-only` | Analyze a DAMF master without exporting a WAV or container. |
| `--quality` | Add the original quality statistics and grades. PCM analysis also runs without this switch. |
| `--verify-peak-events` | Enable independent peak verification and write its JSON evidence. |
| `--listener-gain-db` | Set the absolute final listener gain in dB. |
| `--render-gain` | Apply additional gain to the automatic gain. Mutually exclusive with `--listener-gain-db`. |
| `--surround-swap` | Use Cavernize's existing surround remapping. |
| `--output`, `-o` | Optional PCM WAV export with a WAVE-compatible layout. Use `-f PCM_Float` for floating-point PCM. |
| `--list-layouts`, `--help` | Print supported layouts or command syntax without an input. |

The CLI prints render progress and reports to the console. Exit code `0` means success. Exit code `1` means a source, render, coverage, verification, or report-write failure. Exit code `2` means invalid or incompatible arguments. Check `$LASTEXITCODE` after each invocation in scripts.

```powershell
# D. Batch analysis with automatic gain and separate results for each layout
$layouts = @('5.1.6 with top sides', '5.1.2 side')
foreach ($master in (Get-ChildItem -LiteralPath 'C:\AtmosMetaData' -Filter '*.atmos' -File | Sort-Object Name)) {
    foreach ($layout in $layouts) {
        & $cli --input $master.FullName --layout $layout --report-only --quality --verify-peak-events
        if ($LASTEXITCODE -ne 0) { throw "Analysis failed for $($master.Name) using $layout (exit $LASTEXITCODE)." }
    }
}
```

### Listener gain

The GUI uses its existing automatic setting. The CLI defaults to the same automatic gain calculation. For object-based tracks it multiplies the render gain setting by the selected target's safety multiplier. For example, the automatic multiplier is 1.0 for **5.1.6 with top sides** and 0.707 for **5.1.2 side**. The absolute override uses `10^(dB/20)` at the listener gain stage. On **5.1.2 side**, `--listener-gain-db 0` gives 1.0 and `--render-gain 0` gives 0.707. The limiter remains active.

The post-render report and CSV record the mode, requested dB when specified, original automatic safety multiplier, and applied listener gain in linear units and dB. Compare the actual gain and layout when interpreting differences in peaks, RMS, or activity. Speaker routing and downstream limiting can also differ between layouts.

## Analysis files

Opening an `.atmos` master and rendering to speaker channels automatically saves a CSV beside the master, including in **Report only** mode. For example, the default top-sides layout writes `RPO-5.1.6-with-top-sides-PeakAnalysis.csv`. A remapped or custom layout adds an identifier derived from the applied speaker positions and order. A completed analysis replaces its previous CSV for that layout. Interrupted results use the `-Incomplete` suffix, and results with unverified source coverage use `-SourceUnverified`.

An explicit override adds the requested gain to both filenames, so an absolute 0 dB run of **5.1.2 side** writes `RPO-5.1.2-side-ListenerGain-0dB-PeakAnalysis.csv` and, with verification enabled, `RPO-5.1.2-side-ListenerGain-0dB-PeakAnalysis-Verification.json`. A negative gain such as −3 dB uses `ListenerGain-m3dB`. The source directory must be writable. Reports are individually replaced atomically, and a failed or source-unverified observation uses a separate filename from a completed observation.

The CSV lists every output slot in listener order, then an overall row and any available legacy quality rows. The overall row can name different pre-limiter and processed slots and frames. Event frames are zero-based, speaker slots are one-based, and the timestamp uses the listener sample rate. The signed sample and hexadecimal IEEE-754 bits describe the winning sample. The matching immediate post-limiter value on each pre-limiter row is taken before any listener convolution. The processed maximum has its own event location and block limiter metadata. `-Infinity` denotes silence.

For verification, enable **Verify PCM peak events (Atmos)** in the same menu as **Report only** before starting the analysis. This writes a compact `-Verification.json` beside the CSV. The independent scan starts at the beginning of the program. It compares each channel’s peak index and signed sample bits against the main meter. It also captures and sums the effective rendered source contributions at the highest pre-limiter event, applies listener gain, and compares the reconstruction to the actual buffer. For an LFE event it replays the captured filter state and input prefix before applying gain. Decoded samples are included when the decoded block aligns with the output sample position. The JSON reports mismatches explicitly. A mismatch also marks the CSV incomplete and preserves the previous completed report. Match the analysis IDs when comparing a CSV and JSON, since a later run with verification disabled updates the CSV alone.

## Whole-program level and speaker activity

Both PCM sections display the same per-channel peak measurements and tap locations described above, plus whole-program RMS and speaker activity. Whole-program channel RMS is the square root of that channel's sum of squared PCM amplitudes divided by the valid program-frame count. Overall RMS takes the square root of total squared amplitude divided by valid frames times the number of configured output channels. Silent channels and silent passages are included, and floating-point samples above unity are preserved. The original graded RMS and dynamics statistics retain their existing definitions.

Speaker activity uses nonoverlapping **100 ms RMS windows** on the actual PCM sample clock. A window counts as active when its RMS is **strictly greater** than the stated threshold, either −60 or −40 dBFS. The percentage is the duration of active windows divided by the valid analyzed duration. A final partial window uses its actual frame count for both RMS and duration. Completely silent channels report 0% activity and negative infinity dBFS. These measurements describe signal activity.

The CSV retains the preceding peak, quality and coverage columns and appends linear and dBFS RMS, activity percentages, window length and threshold definitions. RMS and activity use only fully finite, successfully rendered PCM frames. Known gaps or nonfinite frames make the result incomplete, and the existing peak meter continues to report its finite sample observations. The optional peak-verification JSON retains its existing schema and behavior.

## Windows regression reference

The Windows build workflow runs the published CLI against a small DAMF fixture. For a movie regression, use equivalent GUI settings and run commands A and B on the original full `RPO.atmos`. The previously validated Windows measurements below cover 403,084,680 frames at 48 kHz and put the highest pre-limiter sample at frame 235,083,282 in both layouts. The entire movie is required to repeat that comparison.

| Automatic layout | Gain | Pre-limiter peak (dBFS) | Processed peak (dBFS) | Pre-limiter RMS (dBFS) | Processed RMS (dBFS) |
| --- | ---: | ---: | ---: | ---: | ---: |
| 5.1.6 with top sides | 1.0 | +5.3427467856 | −0.0026511277 | −28.0434277055 | −28.1598455894 |
| 5.1.2 side | 0.707 | +2.3311350046 | −0.0066479893 | −29.2941255333 | −29.3306287763 |
