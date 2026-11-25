using Cavern;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Utilities;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

namespace Test.Cavernize.Logic.Models;

[TestClass, DoNotParallelize]
public class PcmSamplePeak_Tests {
    [TestMethod]
    public void InterleavedPeaksIncludeFirstBlockAndOnlyValidTail() {
        PcmSamplePeakAccumulator meter = new(5);
        float[] first = [1.25f, -1, .125f, .25f, 0, -.25f, 0, .25f, -.125f, 0];
        int[] bits = Array.ConvertAll(first, BitConverter.SingleToInt32Bits);
        meter.Stage(first, 2, 2);
        meter.CommitBlock();
        meter.Stage([0, 0, -.5f, -1, 0, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6], 3, 1);
        meter.CommitBlock();
        CollectionAssert.AreEqual(new float[] { 1.25f, 1, .5f, 1, 0 }, meter.Peaks.ToArray());
        CollectionAssert.AreEqual(bits, Array.ConvertAll(first, BitConverter.SingleToInt32Bits));
        Assert.AreEqual(3L, meter.ObservedFrames);
        Assert.AreEqual(PcmPeakIssue.None, meter.Issues);
    }

    [TestMethod]
    public void NonFiniteSamplesAndMalformedBlocksDoNotCountAsSilence() {
        PcmSamplePeakAccumulator meter = new(3);
        meter.Stage([float.NaN, float.PositiveInfinity, float.NegativeInfinity, -.5f, 0, 0], 2, 2);
        meter.CommitBlock();
        meter.Stage([5, 5], 1, 1);
        meter.CommitBlock();
        meter.Stage([5, 5, 5], 1, 2);
        meter.CommitBlock();
        CollectionAssert.AreEqual(new float[] { .5f, 0, 0 }, meter.Peaks.ToArray());
        Assert.AreEqual(1L, meter.ObservedFrames);
        Assert.AreEqual(PcmPeakIssue.NonFinitePcm | PcmPeakIssue.InvalidBuffer, meter.Issues);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReportShowsIndependentPeaksAndPreservesLegacyText(bool grading) {
        Channel[] original = Listener.Channels;
        try {
            RenderTarget target = new("Test", [ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                ReferenceChannel.TopSideLeft, ReferenceChannel.ScreenLFE, ReferenceChannel.TopSideRight, ReferenceChannel.FrontLeft]);
            target.Apply(false);
            Listener listener = new(false) { UpdateRate = 1 };
            PcmSamplePeakAnalysis peaks = new(listener, 1, target);
            peaks.BeginBlock(0);
            peaks.ObservePreLimiter([1.25f, -1, .5f, 0, .25f, MathF.BitIncrement(1)]);
            peaks.AcceptRenderedBlock([.5f, -.5f, .25f, 0, .125f, 0]);

            PostRenderReport report = new(listener);
            RenderStats legacy = grading ? new RenderStatsEx(listener) : new RenderStats(listener);
            if (grading) {
                legacy.Update([.25f, .25f, .25f, .25f, .25f, .25f]);
            }
            report.Generate(legacy);
            string oldText = report.Report;
            report.Generate(legacy, peaks);
            Assert.IsTrue(report.Report.StartsWith(oldText, StringComparison.Ordinal));
            Assert.AreEqual(grading, oldText.Contains("Peak audio frame level", StringComparison.Ordinal));
            StringAssert.Contains(report.Report, "Overall sample peak: +1.94 dBFS");
            StringAssert.Contains(report.Report, "Overall sample peak: -6.02 dBFS");
            StringAssert.Contains(report.Report, "2. LFE: 0.00 dBFS");
            StringAssert.Contains(report.Report, "3. Top side left: -6.02 dBFS");
            StringAssert.Contains(report.Report, "4. LFE: −∞ dBFS");
            StringAssert.Contains(report.Report, "6. Front left: +0.000001 dBFS");
            StringAssert.Contains(report.Report, "source coverage unverified (1 / 1 PCM frames observed / expected)");
        } finally {
            Listener.ReplaceChannels(original);
        }
    }

    [TestMethod]
    public void RejectedRenderedBlockAndLayoutReorderKeepOnlyValidObservations() {
        Channel[] original = Listener.Channels;
        try {
            RenderTarget target = new("Test", ChannelPrototype.ref516);
            target.Apply(true);
            Listener listener = new(false) { UpdateRate = 1 };
            PcmSamplePeakAnalysis peaks = new(listener, 3, target);
            Assert.AreEqual("Top front left", peaks.Labels[4]);
            Assert.AreEqual("Side left", peaks.Labels[6]);
            Assert.AreEqual("Top side left", peaks.Labels[8]);
            peaks.BeginBlock(0);
            peaks.ObservePreLimiter(new float[12]);
            peaks.AcceptRenderedBlock(new float[12]);
            peaks.BeginBlock(1);
            peaks.ObservePreLimiter(Enumerable.Repeat(2f, 12).ToArray());
            peaks.RejectRenderedBlock();
            peaks.BeginBlock(2);
            Channel[] changed = (Channel[])Listener.Channels.Clone();
            (changed[0], changed[1]) = (changed[1], changed[0]);
            Listener.ReplaceChannels(changed);
            peaks.ObservePreLimiter(Enumerable.Repeat(3f, 12).ToArray());
            peaks.AcceptRenderedBlock(new float[12]);

            Assert.AreEqual(1L, peaks.PreLimiter.ObservedFrames);
            Assert.AreEqual(1L, peaks.Processed.ObservedFrames);
            Assert.AreEqual(PcmPeakIssue.RenderError | PcmPeakIssue.LayoutChanged, peaks.Processed.Issues);
            CollectionAssert.AreEqual(new float[12], peaks.PreLimiter.Peaks.ToArray());
        } finally {
            Listener.ReplaceChannels(original);
        }
    }

    [TestMethod]
    public void MissingTapAndEarlyTerminationAreReportedIndependently() {
        Channel[] original = Listener.Channels;
        try {
            Listener.ReplaceChannels([new Channel(13, 23)]);
            Listener listener = new(false) { UpdateRate = 2 };
            PcmSamplePeakAnalysis peaks = new(listener, 4, null);
            peaks.BeginBlock(0);
            peaks.AcceptRenderedBlock([0, 0]);
            PostRenderReport report = new(listener);
            report.Generate(new RenderStats(listener), peaks);
            StringAssert.Contains(report.Report, "incomplete analysis (0 / 4 PCM frames observed / expected)");
            StringAssert.Contains(report.Report, "incomplete analysis (2 / 4 PCM frames observed / expected)");
            StringAssert.Contains(report.Report, "Azimuth 23°, elevation 13°");
            StringAssert.Contains(report.Report, "Overall sample peak: Unavailable");
            StringAssert.Contains(report.Report, "Overall sample peak: −∞ dBFS");
        } finally {
            Listener.ReplaceChannels(original);
        }
    }

    [DataTestMethod]
    [DataRow(true, false, "Complete program")]
    [DataRow(false, false, "source coverage unverified")]
    [DataRow(true, true, "incomplete analysis")]
    public void SourceCoverageDistinguishesDirectCompleteCafFromUnverifiedOrTruncated(
        bool original, bool truncated, string status) {
        // A complete CAF with one silent 16-bit, 48 kHz mono sample.
        byte[] caf = Convert.FromHexString("636166660001000064657363000000000000002040E77000000000006C70636D0000000200000002000000010000000100000010646174610000000000000006000000000000");
        using MemoryStream stream = new(truncated ? caf[..^2] : caf);
        using CavernizeTrack track = new(new CoreAudioFormatReader(stream), Codec.PCM_LE, 0);

        Channel[] saved = Listener.Channels;
        try {
            Listener.ReplaceChannels([new Channel(0, 0)]);
            Listener listener = new(false) { UpdateRate = 16, SampleRate = 48000 };
            PcmSamplePeakAnalysis peaks = new(listener, track.Length, null);
            peaks.CheckSource(track, original);
            peaks.BeginBlock(0);
            peaks.ObservePreLimiter(new float[16]);
            peaks.AcceptRenderedBlock(new float[16]);

            PostRenderReport report = new(listener);
            report.Generate(new RenderStats(listener), peaks);
            StringAssert.Contains(report.Report, status);
            Assert.AreEqual(1L, peaks.Processed.ObservedFrames);
            Assert.AreEqual(truncated ? PcmPeakIssue.InvalidSource : PcmPeakIssue.None, peaks.Processed.Issues);
        } finally {
            Listener.ReplaceChannels(saved);
        }
    }
}
