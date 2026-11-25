using System.Globalization;
using System.Text.Json;
using Cavern;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Microsoft.VisualBasic.FileIO;

namespace Test.Cavernize.Logic.Models;

[TestClass, DoNotParallelize]
public class PcmPeakExport_Tests {
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CsvUsesActualOrderAndKeepsCompletedFileWhenAnalysisFails(bool heightLayout) {
        Channel[] old = Listener.Channels;
        CultureInfo current = CultureInfo.CurrentCulture;
        string dir = Directory.CreateTempSubdirectory().FullName;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            ReferenceChannel[] channels = heightLayout ? ChannelPrototype.ref516 : ChannelPrototype.ref510;
            string name = heightLayout ? "5.1.6 with top sides" : "5.1 side";
            RenderTarget target = new(name, channels);
            target.Apply(heightLayout);
            Listener listener = new(false) { SampleRate = 48000, UpdateRate = 16 };
            PcmSamplePeakAnalysis analysis = new(listener, 1, target);
            using MemoryStream stream = new(Convert.FromHexString(
                "636166660001000064657363000000000000002040E77000000000006C70636D0000000200000002000000010000000100000010646174610000000000000006000000000000"));
            using CavernizeTrack track = new(new CoreAudioFormatReader(stream), Codec.PCM_LE, 0);
            analysis.CheckSource(track, true);
            analysis.BeginBlock(0);
            float[] raw = new float[channels.Length * 16];
            raw[4] = -1.25f;
            raw[channels.Length] = 5; // Beyond the valid final PCM frame.
            analysis.ObservePreLimiter(raw);
            float[] limited = (float[])raw.Clone();
            limited[4] = -.9f;
            analysis.ObservePostLimiter(limited, new(1.25f, .72f, true));
            analysis.AcceptRenderedBlock(limited);

            string filename = Path.Combine(dir, "RPO, Film.atmos");
            var quality = new PostRenderReport.QualityMetric("Grade, \"quoted\"", -6.25f, "A");
            string csv = PcmPeakExport.SaveCsv(filename, analysis, [quality]);
            string[][] table = ReadCsv(csv);
            Assert.AreEqual(1 + channels.Length + 2, table.Length);
            Assert.IsTrue(table.All(row => row.Length == table[0].Length));
            int Col(string header) => Array.IndexOf(table[0], header);
            string[] channel = table[5];
            Assert.AreEqual("5", channel[Col("slot")]);
            Assert.AreEqual(heightLayout ? "Top front left" : "Side left", channel[Col("speaker")]);
            Assert.AreEqual("RPO, Film.atmos", channel[Col("source_file")]);
            Assert.AreEqual("complete", channel[Col("status")]);
            Assert.AreEqual("0", channel[Col("pre_frame")]);
            Assert.AreEqual("-1.25", channel[Col("pre_signed_sample")]);
            Assert.AreEqual("0xBFA00000", channel[Col("pre_float_bits")]);
            Assert.AreEqual("-0.9", channel[Col("pre_same_sample_after_limiter")]);
            Assert.AreEqual("True", channel[Col("pre_block_attack")]);
            Assert.AreEqual(1.93820026, double.Parse(channel[Col("pre_dbfs")], CultureInfo.InvariantCulture), 1e-8);
            Assert.AreEqual("-Infinity", table[1][Col("pre_dbfs")]);
            Assert.AreEqual("5", table[^2][Col("pre_slot")]);
            Assert.AreEqual("1.25", channel[Col("pre_rms_linear")]);
            Assert.AreEqual((double).9f, double.Parse(channel[Col("post_rms_linear")], CultureInfo.InvariantCulture), 1e-12);
            Assert.AreEqual("100", channel[Col("pre_active_gt_minus40_percent")]);
            Assert.AreEqual("0", table[1][Col("pre_active_gt_minus60_percent")]);
            Assert.AreEqual("-Infinity", table[1][Col("pre_rms_dbfs")]);
            Assert.AreEqual("100", channel[Col("activity_window_ms")]);
            Assert.AreEqual("-60", channel[Col("activity_threshold_60_dbfs")]);
            Assert.AreEqual("-40", channel[Col("activity_threshold_40_dbfs")]);
            Assert.AreEqual(1.25 / Math.Sqrt(channels.Length),
                double.Parse(table[^2][Col("pre_rms_linear")], CultureInfo.InvariantCulture), 1e-12);
            Assert.AreEqual("", table[^2][Col("pre_active_gt_minus60_percent")]);
            PostRenderReport report = new(listener);
            report.Generate(new Cavern.Utilities.RenderStats(listener), analysis);
            string[][] display = report.Report.Split('\n').Where(row => row.StartsWith("5. "))
                .Select(row => row.Split('|')).ToArray();
            Assert.AreEqual(2, display.Length);
            Assert.AreEqual(double.Parse(channel[Col("pre_rms_dbfs")], CultureInfo.InvariantCulture).ToString("+0.00"),
                display[0][2].Trim().Split(' ')[0]);
            Assert.AreEqual(double.Parse(channel[Col("post_rms_dbfs")], CultureInfo.InvariantCulture).ToString("0.00"),
                display[1][2].Trim().Split(' ')[0]);
            Assert.AreEqual(channel[Col("pre_active_gt_minus40_percent")] + "%", display[0][4].Trim());
            Assert.AreEqual(channel[Col("post_active_gt_minus60_percent")] + "%", display[1][3].Trim());
            Assert.AreEqual(quality.Name, table[^1][Col("quality_metric")]);
            Assert.AreEqual("-6.25", table[^1][Col("quality_db")]);
            Assert.AreEqual(csv, PcmPeakExport.SaveCsv(filename, analysis, []));
            string completed = File.ReadAllText(csv);

            analysis.Flag(PcmPeakIssue.Interrupted);
            string incomplete = PcmPeakExport.SaveCsv(filename, analysis, []);
            StringAssert.EndsWith(incomplete, "-Incomplete.csv");
            Assert.AreNotEqual(csv, incomplete);
            Assert.AreEqual(completed, File.ReadAllText(csv));
        } finally {
            CultureInfo.CurrentCulture = current;
            Listener.ReplaceChannels(old);
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void AtomicWritePreservesExistingReportAndCleansTemporaryFileWhenWriterFails() {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try {
            string path = Path.Combine(dir, "analysis.csv");
            File.WriteAllText(path, "complete");
            Assert.ThrowsException<IOException>(() => PcmPeakExport.WriteAtomic(path, stream => {
                stream.WriteByte(42);
                throw new IOException("disk error");
            }));
            Assert.AreEqual("complete", File.ReadAllText(path));
            CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(dir));
            PcmPeakExport.WriteAtomic(path, stream => stream.WriteByte(42));
            CollectionAssert.AreEqual(new byte[] { 42 }, File.ReadAllBytes(path));
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void IndependentScanReconstructsSourceSumAndPreservesPcm(bool lfe) {
        Channel[] saved = Listener.Channels;
        string dir = Directory.CreateTempSubdirectory().FullName;
        try {
            RenderTarget target = new("Test", ChannelPrototype.ref516);
            target.Apply(false);
            int slot = lfe ? 3 : 4;
            int[] baseline = null;
            foreach (string mode in new[] { "baseline", "metrics", "verification" }) {
                bool trace = mode == "verification";
                Listener listener = new(false) { SampleRate = 48000, UpdateRate = 64,
                    LFESeparation = true, AudioQuality = QualityModes.Perfect };
                ReferenceChannel first = lfe ? ReferenceChannel.ScreenLFE : ReferenceChannel.SideLeft;
                ReferenceChannel second = lfe ? ReferenceChannel.ScreenLFE : ReferenceChannel.RearLeft;
                Source[] sources = [Attach(listener, first, lfe), Attach(listener, second, lfe)];
                PcmPeakDiagnostics diagnostics = trace ? new(12, sources, [first], [14, 15]) : null;
                PcmSamplePeakAnalysis analysis = new(listener, 128, target, diagnostics);
                listener.TracePcmSources = trace;
                if (mode != "baseline") {
                    listener.ObservePreLimiterPcm = analysis.ObservePreLimiter;
                    listener.ObservePostLimiterPcm = analysis.ObservePostLimiter;
                }
                List<int> bits = new();
                for (int frame = 0; frame < 128; frame += 64) {
                    if (mode != "baseline") {
                        analysis.BeginBlock(frame);
                    }
                    float[] pcm = listener.Render();
                    if (mode != "baseline") {
                        analysis.AcceptRenderedBlock(pcm);
                    }
                    bits.AddRange(pcm.Select(BitConverter.SingleToInt32Bits));
                }
                if (mode == "baseline") {
                    baseline = bits.ToArray();
                    continue;
                }
                CollectionAssert.AreEqual(baseline, bits.ToArray());
                if (!trace) {
                    continue;
                }
                PcmPeakEvent peak = analysis.PreLimiter.GetEvent(slot).Value;
                if (!lfe) {
                    Assert.AreEqual(71L, peak.Frame);
                    Assert.AreEqual(-1.85f, peak.Sample, .000001f);
                    Assert.AreEqual(-.9f, peak.PostLimiterSample.Value, .000001f);
                    Assert.IsTrue(peak.Limiter.Value.Attacked);
                    Assert.AreEqual(0L, analysis.Processed.GetEvent(slot).Value.Frame);
                }

                string json = PcmPeakExport.SaveEvidence(Path.Combine(dir, "Synthetic.atmos"), analysis);
                using JsonDocument result = JsonDocument.Parse(File.ReadAllText(json));
                JsonElement evidence = result.RootElement.GetProperty("Evidence");
                Assert.IsTrue(evidence.GetProperty("Matches").GetBoolean());
                JsonElement highest = evidence.GetProperty("Highest");
                Assert.AreEqual(slot + 1, highest.GetProperty("Slot").GetInt32());
                Assert.AreEqual(peak.Frame, highest.GetProperty("Frame").GetInt64());
                Assert.AreEqual(peak.Frame / 48000d, evidence.GetProperty("HighestTimestampSeconds").GetDouble(), 1e-12);
                Assert.AreEqual(peak.Sample, highest.GetProperty("Reconstructed").GetSingle(), .000001f);
                JsonElement contributions = highest.GetProperty("Contributions");
                Assert.AreEqual(2, contributions.GetArrayLength());
                Assert.AreEqual(14, contributions[0].GetProperty("AtmosSourceId").GetInt32());
                Assert.AreEqual(15, contributions[1].GetProperty("AtmosSourceId").GetInt32());
                if (lfe) {
                    Assert.IsTrue(highest.GetProperty("LfeInputPrefix").GetArrayLength() > 0);
                    Assert.IsTrue(highest.GetProperty("LfeInitialState").GetProperty("B0").GetSingle() > 0);
                }

                // Demonstrate that agreement is checked against the independently scanned PCM.
                float[] inconsistent = new float[12];
                inconsistent[slot] = -9;
                analysis.PreLimiter.Stage(inconsistent, 1, 1, 140);
                analysis.PreLimiter.CommitBlock();
                using JsonDocument disagreement = JsonDocument.Parse(File.ReadAllText(
                    PcmPeakExport.SaveEvidence(Path.Combine(dir, "Synthetic.atmos"), analysis)));
                JsonElement checkedEvents = disagreement.RootElement.GetProperty("Evidence");
                Assert.IsFalse(checkedEvents.GetProperty("Matches").GetBoolean());
                Assert.IsFalse(checkedEvents.GetProperty("Channels")[slot].GetProperty("Matches").GetBoolean());
                Assert.AreEqual(lfe, highest.GetProperty("LfeLowpass").GetBoolean());
            }
        } finally {
            Listener.ReplaceChannels(saved);
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void DamfExportUsesMasterFilenameInsteadOfPcmCompanionName() {
        string dir = Directory.CreateTempSubdirectory().FullName;
        Channel[] saved = Listener.Channels;
        try {
            string source = Path.Combine(dir, "RPO.atmos");
            File.WriteAllText(source, """
                presentations:
                  - type: home
                    bedInstances:
                      - channels:
                          - channel: L
                            ID: 0
                """);
            File.WriteAllText(source + ".metadata", """
                events:
                  - ID: 0
                    samplePos: 0
                    pos: [-1, 1, 0]
                """);
            File.WriteAllBytes(source + ".audio", Convert.FromHexString(
                "636166660001000064657363000000000000002040E77000000000006C70636D0000000200000002000000010000000100000010646174610000000000000006000000000020"));

            using AudioFile audio = new(source);
            CavernizeTrack track = audio.Tracks.Single();
            Assert.AreEqual(source + ".audio", track.Path);
            Assert.AreEqual(source, track.AtmosSourcePath);
            RenderTarget target = new("5.1.6 with top sides", ChannelPrototype.ref516);
            target.Apply(false);
            Listener listener = new(false) { SampleRate = 48000, UpdateRate = 64, AudioQuality = QualityModes.Perfect };
            track.Attach(listener, new Cavern.CavernSettings.UpmixingSettings(false));
            PcmPeakDiagnostics diagnostics = new(Listener.Channels.Length, track);
            PcmSamplePeakAnalysis analysis = new(listener, track.Length, target, diagnostics);
            listener.TracePcmSources = true;
            listener.ObservePreLimiterPcm = analysis.ObservePreLimiter;
            listener.ObservePostLimiterPcm = analysis.ObservePostLimiter;
            analysis.CheckSource(track, true);
            analysis.BeginBlock(0);
            analysis.AcceptRenderedBlock(listener.Render());

            string csv = PcmPeakExport.SaveCsv(track.AtmosSourcePath, analysis, []);
            Assert.AreEqual(Path.Combine(dir, "RPO-5.1.6-with-top-sides-PeakAnalysis.csv"), csv);
            Assert.AreEqual(1L, analysis.Processed.ObservedFrames);
            using JsonDocument evidence = JsonDocument.Parse(File.ReadAllText(PcmPeakExport.SaveEvidence(source, analysis)));
            JsonElement verification = evidence.RootElement.GetProperty("Evidence");
            Assert.IsTrue(verification.GetProperty("Matches").GetBoolean());

        } finally {
            Listener.ReplaceChannels(saved);
            Directory.Delete(dir, true);
        }
    }

    static Source Attach(Listener listener, ReferenceChannel channel, bool lfe) {
        float[] signal = new float[128];
        signal[0] = .49f;
        signal[71] = -.925f;
        if (lfe) {
            Array.Fill(signal, -.35f, 64, 64);
        }
        Source source = new() {
            Clip = new Clip(signal, 1, 48000), LFE = lfe, VolumeRolloff = Rolloffs.Disabled,
            Position = ChannelPrototype.AlternativePositions[(int)channel] * Listener.EnvironmentSize
        };
        listener.AttachSource(source);
        return source;
    }

    static string[][] ReadCsv(string file) {
        using TextFieldParser parser = new(file);
        parser.SetDelimiters(",");
        parser.TrimWhiteSpace = false;
        List<string[]> rows = new();
        while (!parser.EndOfData) {
            rows.Add(parser.ReadFields());
        }
        return rows.ToArray();
    }
}
