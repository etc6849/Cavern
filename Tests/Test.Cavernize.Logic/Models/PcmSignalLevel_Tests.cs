using Cavernize.Logic.Models;

namespace Test.Cavernize.Logic.Models;

[TestClass]
public class PcmSignalLevel_Tests {
    [TestMethod]
    public void ConstantOversAndSilenceHaveIndependentRmsAndActivity() {
        const int frames = 125;
        const int channels = 3;
        PcmSamplePeakAccumulator meter = new(channels, 1000);
        AddProgram(meter, channels, frames, 29, (_, slot) => slot switch {
            0 => 1.25f,
            1 => -.5f,
            _ => 0
        });

        Assert.AreEqual((long)frames, meter.ObservedFrames);
        Assert.AreEqual(1.25, meter.ChannelLevel(0).Rms.Value, 1e-12);
        Assert.AreEqual(1.93820026, meter.ChannelLevel(0).RmsDbfs.Value, 1e-8);
        Assert.AreEqual(.5, meter.ChannelLevel(1).Rms.Value, 1e-12);
        Assert.AreEqual(0d, meter.ChannelLevel(2).Rms);
        Assert.AreEqual(double.NegativeInfinity, meter.ChannelLevel(2).RmsDbfs);
        Assert.AreEqual(0d, meter.ChannelLevel(2).Active60);
        Assert.AreEqual(0d, meter.ChannelLevel(2).Active40);
        Assert.AreEqual(100d, meter.ChannelLevel(0).Active40);
        Assert.AreEqual(Math.Sqrt((1.25 * 1.25 + .25) / 3), meter.OverallLevel().Rms.Value, 1e-12);
        Assert.IsNull(meter.OverallLevel().Active60);
    }

    [DataTestMethod]
    [DataRow(6)]
    [DataRow(12)]
    [DataRow(16)]
    public void PerChannelPowerIsIndependentAcrossVectorGroupsAndScalarRemainders(int channels) {
        PcmSamplePeakAccumulator meter = new(channels, 1000);
        const int frames = 237;
        AddProgram(meter, channels, frames, 23, (frame, slot) => (slot + 1) / 16f * (frame % 2 == 0 ? 1 : -.5f));
        double squaredFactor = (119 + 118 * .25) / frames;
        double summedSquares = 0;
        for (int slot = 0; slot < channels; slot++) {
            double amplitude = (slot + 1) / 16d;
            Assert.AreEqual(amplitude * Math.Sqrt(squaredFactor), meter.ChannelLevel(slot).Rms.Value, 1e-12);
            summedSquares += amplitude * amplitude;
        }
        Assert.AreEqual(Math.Sqrt(squaredFactor * summedSquares / channels), meter.OverallLevel().Rms.Value, 1e-12);
    }

    [TestMethod]
    public void SineRmsMatchesAnalyticSolutionAcrossRenderBlocks() {
        const int sampleRate = 48000;
        PcmSamplePeakAccumulator meter = new(1, sampleRate);
        AddProgram(meter, 1, sampleRate, 127, (frame, _) => (float)(.5 * Math.Sin(2 * Math.PI * frame / 48)));
        Assert.AreEqual(.5 / Math.Sqrt(2), meter.ChannelLevel(0).Rms.Value, 1e-8);
        Assert.AreEqual(100d, meter.ChannelLevel(0).Active60);
        Assert.AreEqual(100d, meter.ChannelLevel(0).Active40);
    }

    [TestMethod]
    public void IntermittentChannelsWeightTheFinalPartialWindowByItsActualDuration() {
        const int frames = 237;
        const float loud = .02f, quiet = .005f;
        PcmSamplePeakAccumulator meter = new(2, 1000);
        AddProgram(meter, 2, frames, 19, (frame, slot) => slot == 0 ?
            frame < 100 ? loud : frame < 200 ? quiet : 0 : frame >= 200 ? -loud : 0);

        PcmSignalLevel first = meter.ChannelLevel(0), second = meter.ChannelLevel(1);
        Assert.AreEqual(100d * 200 / frames, first.Active60.Value, 1e-12);
        Assert.AreEqual(100d * 100 / frames, first.Active40.Value, 1e-12);
        Assert.AreEqual(100d * 37 / frames, second.Active60.Value, 1e-12);
        Assert.AreEqual(second.Active60, second.Active40);
        double firstPower = 100 * ((double)loud * loud + (double)quiet * quiet);
        double secondPower = 37 * (double)loud * loud;
        Assert.AreEqual(Math.Sqrt(firstPower / frames), first.Rms.Value, 1e-12);
        Assert.AreEqual(Math.Sqrt(secondPower / frames), second.Rms.Value, 1e-12);
        Assert.AreEqual(Math.Sqrt((firstPower + secondPower) / (2 * frames)), meter.OverallLevel().Rms.Value, 1e-12);
        Assert.AreEqual(first, meter.ChannelLevel(0)); // Reading a partial window does not finalize it twice.
    }

    [TestMethod]
    public void WindowBoundariesFollowSampleClockWhenRateIsNotDivisibleByTen() {
        PcmSamplePeakAccumulator meter = new(1, 101);
        AddProgram(meter, 1, 25, 6, (frame, _) => frame < 11 ? .02f : 0);
        Assert.AreEqual(44d, meter.ChannelLevel(0).Active60);
        Assert.AreEqual(44d, meter.ChannelLevel(0).Active40);
    }

    [TestMethod]
    public void AdjacentFloatAmplitudesFallOnTheCorrectSidesOfBothThresholds() {
        PcmSamplePeakAccumulator meter = new(1, 100);
        float[] amplitudes = [MathF.BitDecrement(.001f), MathF.BitIncrement(.001f),
            MathF.BitDecrement(.01f), MathF.BitIncrement(.01f)];
        AddProgram(meter, 1, 40, 13, (frame, _) => amplitudes[frame / 10]);
        Assert.AreEqual(75d, meter.ChannelLevel(0).Active60);
        Assert.AreEqual(25d, meter.ChannelLevel(0).Active40);
    }

    [TestMethod]
    public void InvalidFramesAndRejectedBlocksCannotAlterCommittedPower() {
        PcmSamplePeakAccumulator meter = new(2, 100);
        Assert.IsNull(meter.ChannelLevel(0).Rms);
        meter.Stage([.5f, 0, 100, float.NaN, -.5f, 0], 3, 3);
        meter.CommitBlock();
        meter.Stage([20, 25, 30, 35], 2, 2, 3);
        meter.DiscardBlock();
        meter.Stage([9, 9, 9], 2, 2, 5);
        meter.CommitBlock();

        Assert.AreEqual(2L, meter.ObservedFrames);
        Assert.AreEqual(.5, meter.ChannelLevel(0).Rms);
        Assert.AreEqual(0d, meter.ChannelLevel(1).Rms);
        Assert.AreEqual(Math.Sqrt(.125), meter.OverallLevel().Rms.Value, 1e-12);
        Assert.AreEqual(100f, meter.Peaks[0]); // Preserve the existing finite-sample peak policy.
        Assert.AreEqual(PcmPeakIssue.NonFinitePcm | PcmPeakIssue.InvalidBuffer, meter.Issues);
    }

    static void AddProgram(PcmSamplePeakAccumulator meter, int channels, int frames, int blockSize, Func<int, int, float> sample) {
        float[] block = new float[blockSize * channels];
        for (int start = 0; start < frames; start += blockSize) {
            int valid = Math.Min(blockSize, frames - start);
            Array.Fill(block, 99);
            for (int i = 0; i < valid; i++) {
                for (int slot = 0; slot < channels; slot++) {
                    block[i * channels + slot] = sample(start + i, slot);
                }
            }
            meter.Stage(block, blockSize, valid, start);
            meter.CommitBlock();
        }
    }
}
