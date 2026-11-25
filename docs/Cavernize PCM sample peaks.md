# Cavernize PCM sample peaks

The post-render report includes two sections for channel renders, including Report only with quality grading on or off. Each section lists the overall maximum and one row per listener output slot.

- **Before the listener limiter** measures spatially rendered and summed speaker PCM after LFE filtering and listener gain. It can report positive dBFS when a sample exceeds unity.
- **Processed listener PCM before export** measures the buffer returned by the listener after limiting and any listener-side convolution. Export rematrixing, export virtualization and its separate limiter, quantization, and encoding are downstream.

These are discrete sample peaks relative to unity. The overall value is the largest channel peak. A valid silent channel reports `−∞ dBFS`. The report keeps the previous RMS-based fields and grades. Neither sample-peak section measures the output of an external processor such as the Trinnov AltitudeCI.

The rows follow the actual listener order. For **5.1.6 with top sides**, the default order is front left/right/center, LFE, side left/right, top front left/right, top side left/right, and top rear left/right. The existing surround swap exchanges 1-based slots 5 and 7 and slots 6 and 8. On this preset that swaps the side and top-front pairs.

A complete result requires every expected rendered frame and a structurally complete direct CAF/DAMF PCM payload. The CAF check verifies its declared sample alignment and byte extent. Other inputs, including externally decoded TrueHD, report source coverage as unverified even if every expected rendered frame arrived. A known render or source failure produces an incomplete observation and explains the reason. Zero PCM samples alone never indicate a coverage problem.

## Build and package a separate Windows test copy

Use **Windows x64**, the **.NET 8 SDK** to build, the **.NET 8 x64 Desktop Runtime** to run a framework-dependent build, and the **Microsoft Edge WebView2 Evergreen Runtime** for the About panel. The existing native `CavernAmp.dll` is optional. Use the same matching x64 version when comparing against an installation that uses it. FFmpeg is needed for exports that request an FFmpeg codec. Direct DAMF Report only does not need a decoder download. Opening TrueHD directly uses Cavernize’s existing `truehdd.exe` integration.

From the feature checkout root in PowerShell, publish to a fresh test directory.

```powershell
$project = '.\CavernSamples\CavernizeGUI\CavernizeGUI.csproj'
$testDir = 'C:\AtmosMetaData\Cavernize-Test'
if (Test-Path -LiteralPath $testDir) {
    throw "Use an empty destination. Move the existing test directory aside first."
}
dotnet publish $project -c Release -r win-x64 --self-contained false -o $testDir
if ($LASTEXITCODE -ne 0) { throw 'Cavernize publish failed.' }
Start-Process (Join-Path $testDir 'CavernizeGUI.exe') -WorkingDirectory $testDir
```

Keep the entire publish directory together when copying or zipping it. It contains `CavernizeGUI.exe`, the application DLL, `.deps.json`, `.runtimeconfig.json`, configuration, the Cavern and WebView2 assemblies, and their runtime assets. The original application directory `C:\AtmosMetaData\Cavernize` is separate from this output. A separate Windows user profile or VM also isolates saved application settings. Seed that profile with the same settings used for the original report.

## Windows verification

1. Run the Windows test project with `dotnet test .\Tests\Test.CavernizeGUI\Test.CavernizeGUI.csproj -c Release`.
2. Open `RPO.atmos` with its matching audio and metadata files. Select **5.1.6 with top sides**, **Report only**, and **quality grading**. Use the same dependency versions, gain, layout, room correction, surround swap and virtualization settings as the original run.
3. Compare all legacy metrics and grades with the original report. Check that both new sections contain 12 correctly labeled output rows and one overall maximum each, with full frame coverage for the direct DAMF input.
4. Repeat with grading disabled. Start each comparison with equivalent listener state by restarting the app if needed. Confirm that legacy grading follows the setting and that both sample-peak sections are still present and agree across equivalent runs.
5. Time identical Report only runs on the original and modified Windows builds, after one warmup, to measure full-program elapsed-time impact. Record the machine, build, dependency versions, and preset with the results.

## Version 2 analysis files

Opening an `.atmos` master and rendering to speaker channels automatically saves a CSV beside the master, including in **Report only** mode. For example, the default top-sides layout writes `RPO-5.1.6-with-top-sides-PeakAnalysis.csv`. A remapped or custom layout adds an identifier derived from the applied speaker positions and order. A completed analysis replaces its previous CSV for that layout. Interrupted results use the `-Incomplete` suffix, and results with unverified source coverage use `-SourceUnverified`.

The CSV lists every output slot in listener order, then an overall row and any available legacy quality rows. The overall row can name different pre-limiter and processed slots and frames. Event frames are zero-based, speaker slots are one-based, and the timestamp uses the listener sample rate. The signed sample and hexadecimal IEEE-754 bits describe the winning sample. The matching immediate post-limiter value on each pre-limiter row is taken before any listener convolution. The processed maximum has its own event location and block limiter metadata. `-Infinity` denotes silence.

For verification, enable **Verify PCM peak events (Atmos)** in the same menu as **Report only** before starting the analysis. This writes a compact `-Verification.json` beside the CSV. The independent scan starts at the beginning of the program. It compares each channel’s peak index and signed sample bits against the main meter. It also captures and sums the effective rendered source contributions at the highest pre-limiter event, applies listener gain, and compares the reconstruction to the actual buffer. For an LFE event it replays the captured filter state and input prefix before applying gain. Decoded samples are included when the decoded block aligns with the output sample position. The JSON reports mismatches explicitly. A mismatch also marks the CSV incomplete and preserves the previous completed report. Match the analysis IDs when comparing a CSV and JSON, since a later run with verification disabled updates the CSV alone.
