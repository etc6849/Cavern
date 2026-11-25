# Cavernize V4 requirements and evidence

Baseline `c2b67b7e1998e20789f052b7f0327f94f70d792d`. Development branch `feature/cavernize-v4-integrated-analysis`.

| Specification | Baseline finding | Required verification |
| --- | --- | --- |
| 1, 12. V3 rendering and Ready Player One | GUI and CLI share the speaker render loop and analysis taps. RPO is unavailable here. | Re-run V3 Logic and Windows CLI parity. Use the published V4 against the full local soundtrack and compare both layouts with established values. |
| 2, 3. Direct Atmos, MKV and MKA | GUI invokes TrueHDD but only publishes for a direct Atmos source. Headless CLI rejects TrueHD. | Use TrueHD-containing MKV and MKA, ambiguous and explicit tracks, direct Atmos fixtures and real TrueHDD. Check source identity and diagnostics. |
| 4, 8, 13. SSD and safety | TrueHDD uses a fixed source-adjacent stem and ignores process status. | Check space preflight, job isolation, intermediate routing, successful cleanup, failure diagnostics, unchanged media and source coverage. |
| 5, 6, 7. Comprehensive CSV | Existing CSV is layout-specific and omits bed/object/unused header fields. | Check exact Unicode basename, structured report fields, layout and gain consolidation, atomic publication and retention after failures. |
| 9. GUI, CLI and batch | V3 defaults to one layout and one source. | Exercise the same complete job in GUI and CLI and verify unattended batch behavior. |
| 10, 11. Media exports | Native Matroska copies only video tracks. FFmpeg mapping omits attachments and the CLI only exports WAV. | Verify native PCM and FFmpeg-encoded MKV routes independently with stream probes, decoded audio, checksums, metadata and timestamps. Run final JRiver acceptance locally. |
| 14. Windows delivery | The V3 workflow publishes and verifies both Windows executables for the V3 branch. | Build and run both from the V4 branch and validate the final downloaded archive. |
