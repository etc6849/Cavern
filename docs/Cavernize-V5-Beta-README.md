# Cavernize V5 Beta for Windows x64

## Install

1. Download `Cavernize-V5-Beta-win-x64.zip` and its `.sha256` file from the [V5 Beta release](https://github.com/etc6849/Cavern/releases/tag/cavernize-v5.0.0-beta.1).
2. Compare the SHA-256 from PowerShell with the first field in the checksum file.

   ```powershell
   Get-FileHash .\Cavernize-V5-Beta-win-x64.zip -Algorithm SHA256
   Get-Content .\Cavernize-V5-Beta-win-x64.zip.sha256
   ```

3. Extract the entire ZIP to a new directory such as `C:\AtmosMetaData\Cavernize-V5-Beta`. Keep any earlier Cavernize installation in its own directory. Run `CavernizeGUI.exe` from the extracted folder, or use `CLI\CavernizeCLI.exe` from PowerShell. Keep the `CLI`, `Tools`, and runtime files in their supplied locations.

The package targets Windows 10 or 11 on x64. Both apps include their .NET 8 runtime, and the GUI includes the Windows Desktop runtime. No .NET installation, SDK, Visual Studio, or WebView2 Runtime is needed. The portable package keeps the GUI, CLI, and external TrueHDD decoder as separate executables with their dependencies.

The bundled TrueHDD 0.6.3 Windows executable uses the Microsoft Visual C++ v14 runtime. If it cannot start because `VCRUNTIME140.dll` is missing, install the [Microsoft Visual C++ Redistributable for x64](https://aka.ms/vc14/vc_redist.x64.exe). MKV media export requires FFmpeg and FFprobe on `PATH` or at the configured location. Analysis-only jobs can use the bundled decoder without FFmpeg.

## Analyze an Atmos track

Open an MKV or MKA with a TrueHD track or open a direct `.atmos` master. If there are several TrueHD tracks, select the intended one. Choose your speaker layout. Under the rendering settings, enable **Report only**, **Quality analysis and grading**, and **Verify PCM peak events (Atmos)** as needed. **Analysis workspace and layouts** allows you to choose a local SSD for analysis intermediates and select additional layouts. Ensure the SSD has enough free space for the extracted and decoded title and that the source directory is writable for CSV output.

```powershell
$cli = 'C:\AtmosMetaData\Cavernize-V5-Beta\CLI\CavernizeCLI.exe'
& $cli --list-layouts
& $cli --input 'D:\Movies\A Few Good Men (1992).mkv' `
    --layout '5.1.6 with top sides' --report-only --quality --verify-peak-events `
    --work-dir 'C:\AtmosMetaData\Work'
```

Use `--track` with the zero-based audio-track index to select among multiple tracks, or `--keep-verification-json` to retain verification evidence. The CSV is written beside the source. If the decoder fails, the diagnostic message points to the log and decode summary. A media-export job continues to use its existing temporary-file locations.

Use `BUILD-INFO.json` or the GUI's **About** dialog to identify the build. The [V4 user guide](https://github.com/etc6849/Cavern/blob/b646f4b3584e7a87ab7a28434d90df4f7e8ac07e/docs/Cavernize%20V4.md) covers inherited analysis and media export options. Use this README for V5 setup and dependencies.

## Licenses and attribution

This community-maintained beta is based on Cavern by [Bence Sgánetz](http://en.sbence.hu). The [upstream Cavern repository](https://github.com/VoidXH/Cavern) is the original source. `LICENSE.md` contains the Cavern license. The unmodified TrueHDD 0.6.3 decoder from [TrueHDD](https://github.com/truehdd/truehdd/tree/0.6.3) is redistributed under Apache 2.0 with its `LICENSE` and `NOTICE` in `Tools\TrueHDD`. .NET and its dependencies have their license and notice files under `Licenses`.
