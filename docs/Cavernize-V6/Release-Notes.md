# Cavernize V6 Beta 1

This community-maintained Windows x64 pre-release corrects the Atmos source inventory in the GUI and CLI. It retains the V5 TrueHD extraction fix and decoder diagnostics. The ZIP includes self-contained GUI and CLI builds with .NET 8, the unmodified TrueHDD 0.6.3 executable, third-party notices, and a build manifest. [Installation and CLI examples](https://github.com/etc6849/Cavern/blob/cavernize-v6.0.0-beta.1/docs/Cavernize-V6-Beta-README.md)

## Source identification

- For a Dolby Atmos Master Format (DAMF) presentation, the report reads declared bed names and IDs from `bedInstances` and the object inventory from the `objects` list. For TrueHD, the inventory describes the DAMF presentation decoded by TrueHDD. TrueHDD 0.6.3 derives its DAMF bed assignments from TrueHD Object Audio Metadata (OAMD). It can declare only an LFE bed even when other rendered channels carry audio. [TrueHDD bed assignment](https://github.com/truehdd/truehdd/blob/0.6.3/src/damf.rs)
- Authored object movement is grouped by distinct explicit metadata positions. One position is stationary, two through fifteen are semi-static, and sixteen or more are dynamic. Missing positions and IDs that cannot be matched to the renderer mapping have unverified motion. The inventory reports the total declared objects independently of these categories.
- An inactive object source had a complete decoded PCM observation with no nonzero samples at its input to the mix. Activity is unverified if the source could not be observed completely and no signal was established. Silent object sources are distinct from silent speaker outputs. The report marks source types without a verified DAMF inventory as unavailable and retains the old coordinate observations as diagnostics.

## Ready Player One and rendering

V5 called `RenderStats.GetStaticOrSemiStaticPositions()`, deduplicated positions, and tried to name each one with an exact reference-coordinate match. This treated some objects and default positions as bed channels. The legacy unused count then subtracted overlapping motion categories and a count of distinct positions from a count of attached sources. V6 uses declared source identity independently of position and tracks activity independently of speaker layout.

The reported positions `(0, 0, 0)` and `(-0.8064516, 0.4, 0.12903225)` have no matching reference channel in the lookup that V5 used. In the TrueHD test fixture, a gain-only event for the declared `ScreenLFE` bed (ID 3) has no authored position. The DAMF renderer applies the default zero position for that event, while the source still has its LFE routing flag. This reproduces how an actual bed can appear as `Unknown` at the origin. An unpositioned slot or an explicitly authored object can also produce zero. The second coordinate is off the reference grid. Identifying the movie's presentation and assigning IDs to these positions requires its decoded root and object movement metadata.

The maintainer reported that the full-length V5 5.1.6 and 5.1.4 processed peaks and RMS matched previous FFmpeg measurements to about 0.000001 dB. The 5.1.6 top rear outputs were silent in both runs. For 5.1.2, earlier runs used the 0.707 automatic multiplier, and the V5 comparison used absolute unity gain. Align listener gain and speaker mapping before comparing. A gain-only correction cannot reproduce the output of a limiter that responds differently across the two runs. The full-length movie and original independent logs were unavailable for automated release validation.

The Windows release checks compare V6 against the published V5 executable on a short TrueHD Atmos fixture. They cover 5.1.2, 5.1.4, and 5.1.6, check the exported float PCM bytes and PCM measurement fields, and use FFmpeg `astats` to check processed peak and RMS for each speaker. Synthetic 5.1 and 7.1 masters check encoded LFE and right-surround identities, stationary and moving objects, and silence. Windows CI also checks GUI/CLI report parity, real laced TrueHD extraction/regression fixtures, decoder errors, and packaged GUI startup and CLI analysis. GUI testing in CI is unattended.

A separate V5-era placement issue was reproduced with a synthetic DAMF bed. The prototype helper used by the renderer selects alternative positions by ordinal in the passed array and already applies environment scale. The DAMF setup then applies environment scale again. For a subset bed this can produce coordinates different from the declared speaker roles. V6 preserves both placement paths. It records reference and last observed positions for diagnostic comparison. The short TrueHD fixture produced nonzero output in both top rears on the same 5.1.6 preset. The cause of the full-length movie's silent top rears remains unverified. [Prototype helper](https://github.com/etc6849/Cavern/blob/cavernize-v6.0.0-beta.1/Cavern/Channels/ChannelPrototype.cs#L230-L242) · [DAMF renderer](https://github.com/etc6849/Cavern/blob/cavernize-v6.0.0-beta.1/Cavern.Format/Renderers/CoreAudioFormatRenderer.cs#L53-L92)

## CSV compatibility

Integrated CSVs keep the existing 74 columns in order and add a final `source_inventory` column. The `overall` row stores JSON with provenance, bed labels and IDs, object IDs, motion, input PCM activity, and positions. The schema identifier remains `Cavernize V4`. V6 accepts existing 74-column files for the same source revision. It replaces matching configurations and keeps older configurations with a blank inventory field. Retain a copy of the original CSV if you plan to reopen it with V5.

- `bed_count` and `bed_positions` report the declared bed and its reference coordinates. `source_inventory` also stores observed coordinates when available.
- `dynamic_object_count` counts objects with verified dynamic metadata movement. The inventory also carries the total declared objects and identifies motion uncertainty.
- `unused_target_count` counts inactive object sources with verified decoded silence. It is blank if any object's PCM activity is unverified. For unsupported source metadata, the bed and object summary fields are blank.
- Sample peaks, timestamps, RMS, activity, coverage checks, and quality statistics retain their previous definitions.

Original Cavern by Bence Sgánetz. [Creator](http://en.sbence.hu) · [Upstream source and license](https://github.com/VoidXH/Cavern)
