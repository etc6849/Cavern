# Cavernize V4 TrueHD extraction investigation

The rollback build is `db69cf928309be2d13394f86ecb5ae1a3e7bd8f0` on `feature/cavernize-v4-integrated-analysis`. The read-only MKVToolNix reference is `749dea4c263627af8a9768d8af2a7d5718983c54` on `etc6849/MKVToolNix` main.

## Evidence before the fix

[MKVToolNix extracts each frame in file order from both BlockGroup and SimpleBlock](https://github.com/etc6849/MKVToolNix/blob/749dea4c263627af8a9768d8af2a7d5718983c54/src/extract/tracks.cpp#L225). [Its ordinary TrueHD extractor reverses track content encoding and writes the frame bytes](https://github.com/etc6849/MKVToolNix/blob/749dea4c263627af8a9768d8af2a7d5718983c54/src/extract/xtr_base.cpp#L82). Codec-private data and block-level timing are separate from the ordinary TrueHD output. [TrueHD extractor selection](https://github.com/etc6849/MKVToolNix/blob/749dea4c263627af8a9768d8af2a7d5718983c54/src/extract/xtr_base.cpp#L160)

In Cavern V4, [Block.cs records the first-frame offset immediately after the lace count](https://github.com/etc6849/Cavern/blob/db69cf928309be2d13394f86ecb5ae1a3e7bd8f0/Cavern.Format/Container/Matroska/Block.cs#L77). Fixed-size lacing has no following size table, so that offset is correct for it. Xiph and EBML both read the remaining lace sizes after the saved offset. Their last frame size is calculated from the correct payload length, after allowing for the size table. The reader then [reads precisely that many bytes starting at the old offset](https://github.com/etc6849/Cavern/blob/db69cf928309be2d13394f86ecb5ae1a3e7bd8f0/Cavern.Format/Container/Matroska/Block.cs#L150). For a table of N bytes the result includes N metadata bytes and omits the last N payload bytes. [MatroskaReader reads the block](https://github.com/etc6849/Cavern/blob/db69cf928309be2d13394f86ecb5ae1a3e7bd8f0/Cavern.Format/Input/Container/MatroskaReader.cs#L64) and [the extractor writes those bytes unchanged](https://github.com/etc6849/Cavern/blob/db69cf928309be2d13394f86ecb5ae1a3e7bd8f0/Cavern.Format/Operations/ExtractTrackFromContainer.cs#L33).

The same genuine 55,410-byte TrueHD/Atmos stream was wrapped three ways and extracted through V4's analysis path. Every result had the same length. The unlaced result matched the original SHA-256 and decoded all 16,000 samples. Both laced results differed from the original. TrueHDD 0.6.3 returned exit code zero but reported 520 samples with five skipped and four concealed frames for Xiph, and 4,160 samples with five skipped and 95 concealed frames for EBML. Both also reported one invalid branch. The user's same-size/different-hash movie result is consistent with this defect. The movie file and its block structure were unavailable for inspection.

| V4 extraction | SHA-256 |
| --- | --- |
| Unlaced | `e39283e66ad93106137db1d2f8b436ce47167983fbb8bf0de0d300e85de5d3c1` |
| Xiph | `5db6f3181cc58c0fdfe1148d1dcd7c18baf61b568d54909d7403139fb43fbddc` |
| EBML | `22dbb0c9425246122f310f86ca72eefc8103e763736cac18b0f3f5ded5d03094` |

## Related failure modes

- Fixed-size headers and frame iteration use a byte index. It wraps when the valid count is 256. The parser also accepted a fixed-size payload with a remainder, lacked bounds for lace headers, and did not validate signed-size results.
- Cluster reading starts at the EBML tree scan cursor after looking up the timestamp. Earlier blocks can disappear if a timestamp follows another child. The indexed tree lookup also rejected valid children ending exactly at their parent's boundary, which can include a final cluster.
- Playback `Seek(0)` selects the last block at or before zero. It can skip early packets, including consecutive packets rounded to timestamp zero. The raw CLI extraction command also started at the current cursor after track probing. A whole-track extract must start at the first physical block.
- BlockGroup metadata such as BlockAdditions is separate from the Block child. It does not explain the demonstrated same-size substitution. MKVToolNix also reverses track content encodings. Cavern's raw extraction cannot safely return compressed, header-stripped, or encrypted bytes as an elementary stream. Multi-segment track identity also requires separate handling.
- The audio pipeline and PCM calculations are outside this fix. No code from MKVToolNix was copied.

## Decoder diagnostics

Analysis and media export both request the TrueHDD JSON decode summary and write the decoder's messages to a log. An unacceptable exit status, integrity diagnostic, skipped or concealed frames, invalid branch, failed protection check, or incomplete decoded payload rejects the decode. Ordinary informational messages and non-integrity warnings remain in the log. The GUI shows one failure summary with counts, one representative message and the two diagnostic paths. Tests include 4,000 repeated messages and an intentionally damaged access unit processed by the real decoder.

On an analysis failure, look for `truehdd.log` and `truehdd.json` in the reported analysis workspace. On a media-export failure, look for the per-extraction `.truehdd.log` and `.truehdd.json` beside the source. Media placement follows V4. Success removes the diagnostics as part of normal cleanup unless media temporary files were explicitly retained.

## Full-movie Windows check

Extract the test ZIP into a separate directory and leave the V4 baseline installation available. Use MKVToolNix's track identification to find the intended TrueHD track. In the example below, set `$track` to its zero-based **container** track index for raw extraction, including video and other track types in the count.

```powershell
$movie = 'V:\Movies (UHD)\A Few Good Men.mkv'
$cli = 'C:\AtmosMetaData\Cavernize-V4-TrueHD-Fix\CLI\CavernizeCLI.exe'
$work = 'C:\AtmosMetaData\Work'
$before = (Get-FileHash -Algorithm SHA256 -LiteralPath $movie).Hash
mkvmerge -J $movie
$track = 1 # Replace with the selected TrueHD track's container index.
$reference = Join-Path $work 'A Few Good Men reference.thd'
$actual = Join-Path $work 'A Few Good Men Cavern.thd'
mkvextract $movie tracks "${track}:$reference"
if ($LASTEXITCODE -ne 0) { throw 'MKVToolNix extraction failed' }
& $cli --input $movie --extract $track $actual
if ($LASTEXITCODE -ne 0) { throw 'Cavernize extraction failed' }
if ((Get-FileHash -LiteralPath $reference).Hash -ne (Get-FileHash -LiteralPath $actual).Hash) {
    throw 'TrueHD extraction hashes differ'
}
if ((Get-FileHash -LiteralPath $movie).Hash -ne $before) { throw 'Source hash changed' }
```

Use an SSD with room for both 4,031,471,458-byte extracts plus the integrated analysis job. Then run `--report-only --quality --verify-peak-events --layout '5.1.6 with top sides' --work-dir $work`. If the movie has multiple TrueHD tracks, pass the matching **zero-based audio** index via `--track` for analysis. Check whether TrueHDD completes cleanly and the post-render CSV reports the full decoded duration. If the hashes differ, preserve both extracts and the decoder logs to locate the first differing block.
