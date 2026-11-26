# Cavernize V6 Beta 1 for Windows x64

Download the ZIP and its checksum from [Cavernize V6 Beta 1](https://github.com/etc6849/Cavern/releases/tag/cavernize-v6.0.0-beta.1). Compare the SHA-256 from PowerShell with the first field in the checksum file.

```powershell
Get-FileHash .\Cavernize-V6-Beta-win-x64.zip -Algorithm SHA256
Get-Content .\Cavernize-V6-Beta-win-x64.zip.sha256
```

Extract the entire ZIP to a separate directory such as `C:\AtmosMetaData\Cavernize-V6-Beta`. Start `CavernizeGUI.exe` in the extracted directory, or run `CLI\CavernizeCLI.exe` in PowerShell. Keep the runtime and decoder files in place. Windows 10 and 11 x64 are the target systems. The package includes .NET 8 and the Windows Desktop runtime. No .NET installation, SDK, Visual Studio, or WebView2 Runtime is needed.

TrueHDD 0.6.3 is included. If Windows reports that `VCRUNTIME140.dll` is missing, install Microsoft's [Visual C++ Redistributable for x64](https://aka.ms/vc14/vc_redist.x64.exe). FFmpeg and FFprobe are separate tools for MKV media export. Analysis-only jobs can use the bundled decoder without them.

## Analyze a movie

In the GUI, open a TrueHD MKV/MKA or a direct `.atmos` master. Choose the track and speaker layout. Under the rendering settings, enable **Report only**, **Quality analysis and grading**, and **Verify PCM peak events (Atmos)** as needed. **Analysis workspace and layouts** configures the local SSD and additional layouts. The integrated report is saved as CSV beside the source. Keep optional verification JSON with `--keep-verification-json` or the corresponding GUI option.

```powershell
$cli = 'C:\AtmosMetaData\Cavernize-V6-Beta\CLI\CavernizeCLI.exe'
& $cli --list-layouts
& $cli --input 'D:\Movies\Ready Player One (2018) (UHD) (Atmos).mkv' `
    --layout '5.1.6 with top sides' --layout '5.1.4' --layout '5.1.2 side' `
    --report-only --quality --verify-peak-events --listener-gain-db 0 `
    --work-dir 'C:\AtmosMetaData\Work'
```

The example requests unity listener gain in all three presets. Omit the override to use each preset's automatic gain. Use the same gain, source track, renderer, and speaker mapping when comparing with another run. The two stock presets with fewer heights use rear coordinates for their last pair, including the preset labeled `5.1.2 side`.

Use `--track` with the zero-based audio-track index if there is more than one TrueHD track. See [Release-Notes.md](Release-Notes.md) for the new source inventory and CSV compatibility. For other inherited CLI and GUI options, see the [V4 user guide](https://github.com/etc6849/Cavern/blob/b646f4b3584e7a87ab7a28434d90df4f7e8ac07e/docs/Cavernize%20V4.md).

## Licenses and attribution

This community-maintained beta is based on Cavern by [Bence Sgánetz](http://en.sbence.hu). The [upstream Cavern repository](https://github.com/VoidXH/Cavern) is the original source. `LICENSE.md` contains the Cavern license. The unmodified TrueHDD decoder from [TrueHDD 0.6.3](https://github.com/truehdd/truehdd/tree/0.6.3) is redistributed under Apache 2.0 with `LICENSE` and `NOTICE` in `Tools\TrueHDD`. .NET and its dependencies have license and notice files under `Licenses`. The GUI's **About** dialog and `BUILD-INFO.json` identify the build.
