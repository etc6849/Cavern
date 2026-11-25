# Cavernize V5 Beta: TrueHD Extraction Fix

This community-maintained Windows x64 pre-release contains the TrueHD extraction correction from `feature/cavernize-v4-truehd-fix`. The ZIP includes the GUI, CLI, self-contained .NET 8 runtimes, TrueHDD 0.6.3, [installation instructions](https://github.com/etc6849/Cavern/blob/cavernize-v5.0.0-beta.1/docs/Cavernize-V5-Beta-README.md), licenses, and a build manifest.

## TrueHD extraction and diagnostics

- Cavern now writes only the encoded payload bytes of each Matroska frame. It accounts for the lace headers and size descriptors, with bounds checks for frame sizes and offsets. The original V4 extraction could produce a same-size stream with incorrect bytes, leading to repeated frame-parity errors in TrueHDD. [Extraction correction](https://github.com/etc6849/Cavern/commit/6833f07b)
- TrueHDD output is retained in the on-disk decoder log and JSON decode summary. Cavernize reports the error count, a bounded excerpt of the first error when available, and the diagnostic paths. A failed process, unusable summary, reported errors or recognized integrity warnings, skipped or concealed frames, invalid branches, or failed protection checks causes decode rejection before analysis. Repetitive decoder output stays in the log. [Diagnostic correction](https://github.com/etc6849/Cavern/commit/d0389a2c)

## Analysis

Integrated Atmos analysis includes channel and speaker layout reporting, pre-limiter and post-processing PCM sample peaks, independent peak-event verification, quality measurements, and CSV output beside the source. The existing renderer, layouts, measurement definitions, and CSV schema are unchanged. Reported peaks describe Cavern's speaker render under the selected settings.

## Full-length Windows acceptance test

The maintainer tested the corrected build on the UHD MKV release of *A Few Good Men (1992)* with Dolby TrueHD Atmos. The extracted stream was **4,031,471,458 bytes**, with this full-file SHA-256 matching MKVToolNix:

```text
54017F8F2B07AA451C4A51743B35B850E40A95B40BD6ACF0538AAB1DC5DBBCD7
```

TrueHDD 0.6.3 decoded the complete stream. Integrated analysis at **5.1.6 with top sides** produced a 74-column CSV beside the MKV. Both PCM taps analyzed all **397,501,160** expected frames, peak verification reported `Matched`, and neither tap reported PCM analysis issues.

The Windows workflows use short fixtures to exercise laced extraction, TrueHD decode and diagnostics, the CLI, GUI analysis parity, and both MKV export routes. The packaged GUI has an unattended startup check. Interactive testing on a Windows desktop and additional full-length titles, other containers, and other layouts remain to be validated.

Original Cavern by [Bence Sgánetz](http://en.sbence.hu). [Upstream source and license](https://github.com/VoidXH/Cavern)
