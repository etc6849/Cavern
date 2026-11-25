using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Cavern.Filters;

namespace Cavernize.Logic.Models;

[Flags]
internal enum PcmPeakIssue {
    None = 0,
    MissingBlock = 1,
    InvalidBuffer = 2,
    NonFinitePcm = 4,
    RenderError = 8,
    LayoutChanged = 16,
    SourceLengthMismatch = 32,
    InvalidSource = 64,
    Interrupted = 128,
    MeasurementError = 256
}

internal readonly record struct PcmPeakEvent(long Frame, float Sample) {
    internal double Dbfs => 20 * Math.Log10(Math.Abs((double)Sample));
    internal float? PostLimiterSample { get; init; }
    internal Normalizer.BlockState? Limiter { get; init; }
}

internal readonly record struct PcmSignalLevel(double? Rms, double? Active60, double? Active40) {
    internal double? RmsDbfs => Rms is double value ? 20 * Math.Log10(value) : null;
}

/// <summary>
/// Tracks peaks, whole-program power and duration-weighted window activity for one PCM tap.
/// </summary>
internal sealed class PcmSamplePeakAccumulator {
    internal const int ActivityWindowMs = 100;
    internal const int ActivityThreshold60 = -60;
    internal const int ActivityThreshold40 = -40;
    const int windowsPerSecond = 1000 / ActivityWindowMs;
    static readonly double thresholdPower60 = Math.Pow(10, ActivityThreshold60 / 10d);
    static readonly double thresholdPower40 = Math.Pow(10, ActivityThreshold40 / 10d);

    struct SignalPower {
        internal double CompletedPower, WindowPower;
        internal long Active60, Active40;

        internal void CloseWindow(int frames) {
            CompletedPower += WindowPower;
            if (WindowPower > thresholdPower60 * frames) {
                Active60 += frames;
            }
            if (WindowPower > thresholdPower40 * frames) {
                Active40 += frames;
            }
            WindowPower = 0;
        }
    }

    readonly int sampleRate;
    SignalPower[] signals, stagedSignals;
    int windowFrames, stagedWindowFrames;
    long windowEnd, stagedWindowEnd;
    readonly float[] peaks;
    readonly float[] blockPeaks;
    readonly PcmPeakEvent[] events;
    readonly PcmPeakEvent[] blockEvents;
    int stagedFrames;
    bool hasStagedBlock;

    internal PcmSamplePeakAccumulator(int channels, int sampleRate = 48000) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        this.sampleRate = Math.Max(1, sampleRate); // Invalid listener settings are flagged by the analysis.
        signals = new SignalPower[channels];
        stagedSignals = new SignalPower[channels];
        peaks = new float[channels];
        blockPeaks = new float[channels];
        events = new PcmPeakEvent[channels];
        blockEvents = new PcmPeakEvent[channels];
        Array.Fill(peaks, -1);
    }

    internal ReadOnlySpan<float> Peaks => peaks;
    internal PcmPeakEvent? GetEvent(int slot) => peaks[slot] >= 0 ? events[slot] : null;
    internal long ObservedFrames { get; private set; }
    internal PcmPeakIssue Issues { get; private set; }

    internal PcmSignalLevel ChannelLevel(int slot) {
        if (ObservedFrames == 0) {
            return default;
        }
        SignalPower signal = signals[slot];
        signal.CloseWindow(windowFrames);
        return new(Math.Sqrt(signal.CompletedPower / ObservedFrames),
            100d * signal.Active60 / ObservedFrames, 100d * signal.Active40 / ObservedFrames);
    }

    internal PcmSignalLevel OverallLevel() {
        if (ObservedFrames == 0) {
            return default;
        }
        double totalPower = 0;
        foreach (SignalPower signal in signals) {
            totalPower += signal.CompletedPower + signal.WindowPower;
        }
        return new(Math.Sqrt(totalPower / (ObservedFrames * (double)signals.Length)), null, null);
    }

    void AdvanceWindow(long frame) {
        for (int slot = 0; slot < stagedSignals.Length; slot++) {
            stagedSignals[slot].CloseWindow(stagedWindowFrames);
        }
        stagedWindowFrames = 0;
        // Use absolute PCM positions to avoid drift at rates not divisible by ten.
        long window = frame * windowsPerSecond / sampleRate;
        stagedWindowEnd = ((window + 1) * sampleRate + windowsPerSecond - 1) / windowsPerSecond;
    }

    internal void Stage(ReadOnlySpan<float> pcm, int blockFrames, int validFrames, long startFrame = 0) {
        DiscardBlock();
        int channels = peaks.Length;
        if (validFrames <= 0 || validFrames > blockFrames || pcm.Length % channels != 0 ||
            pcm.Length / channels != blockFrames) {
            Issues |= PcmPeakIssue.InvalidBuffer;
            return;
        }

        hasStagedBlock = true;
        Array.Copy(signals, stagedSignals, channels);
        stagedWindowFrames = windowFrames;
        stagedWindowEnd = windowEnd;
        Array.Fill(blockPeaks, -1);
        bool allFinite = true;
        int sample = 0;
        for (int frame = 0; frame < validFrames; frame++) {
            bool finiteFrame = true;
            for (int channel = 0; channel < channels; channel++) {
                float value = pcm[sample++];
                float magnitude = MathF.Abs(value);
                if (float.IsFinite(magnitude)) {
                    if (magnitude > blockPeaks[channel]) {
                        blockPeaks[channel] = magnitude;
                        if (magnitude > peaks[channel]) {
                            blockEvents[channel] = new PcmPeakEvent(startFrame + frame, value);
                        }
                    }
                } else {
                    finiteFrame = false;
                }
            }
            stagedFrames += finiteFrame ? 1 : 0;
            allFinite &= finiteFrame;
        }
        if (!allFinite) {
            Issues |= PcmPeakIssue.NonFinitePcm;
        }
        MeasurePower(pcm, startFrame, validFrames, allFinite);
    }

    void MeasurePower(ReadOnlySpan<float> pcm, long startFrame, int validFrames, bool allFinite) {
        int channels = peaks.Length;
        for (int first = 0; first < validFrames;) {
            long absoluteFrame = startFrame + first;
            if (absoluteFrame >= stagedWindowEnd) {
                AdvanceWindow(absoluteFrame);
            }
            int count = (int)Math.Min(validFrames - first, stagedWindowEnd - absoluteFrame);
            ReadOnlySpan<float> windowPart = pcm.Slice(first * channels, count * channels);
            if (allFinite) {
                AccumulatePower(windowPart, channels);
                stagedWindowFrames += count;
            } else {
                AccumulateFiniteFrames(windowPart, channels);
            }
            first += count;
        }
    }

    void AccumulatePower(ReadOnlySpan<float> pcm, int channels) {
        int slot = 0;
        if (Vector128.IsHardwareAccelerated) {
            // The block is whole interleaved frames, and each load stays inside its channel group.
            ref float first = ref MemoryMarshal.GetReference(pcm);
            int lanes = Vector128<float>.Count;
            for (; slot <= channels - lanes; slot += lanes) {
                Vector128<double> lower = Vector128<double>.Zero, upper = Vector128<double>.Zero;
                for (int sample = slot; sample < pcm.Length; sample += channels) {
                    var (lo, hi) = Vector128.Widen(Vector128.LoadUnsafe(ref first, (nuint)sample));
                    lower += lo * lo;
                    upper += hi * hi;
                }
                for (int lane = 0; lane < Vector128<double>.Count; lane++) {
                    stagedSignals[slot + lane].WindowPower += lower[lane];
                    stagedSignals[slot + lane + Vector128<double>.Count].WindowPower += upper[lane];
                }
            }
        }
        for (; slot < channels; slot++) {
            double power = 0;
            for (int sample = slot; sample < pcm.Length; sample += channels) {
                double value = pcm[sample];
                power += value * value;
            }
            stagedSignals[slot].WindowPower += power;
        }
    }

    // Peak coverage keeps its existing finite-sample policy. RMS uses only whole finite frames.
    void AccumulateFiniteFrames(ReadOnlySpan<float> pcm, int channels) {
        for (int frame = 0; frame < pcm.Length; frame += channels) {
            bool valid = true;
            for (int slot = 0; slot < channels; slot++) {
                valid &= float.IsFinite(pcm[frame + slot]);
            }
            if (valid) {
                for (int slot = 0; slot < channels; slot++) {
                    double value = pcm[frame + slot];
                    stagedSignals[slot].WindowPower += value * value;
                }
                stagedWindowFrames++;
            }
        }
    }

    internal void PairLimiterBlock(ReadOnlySpan<float> pcm, long startFrame, Normalizer.BlockState state) {
        if (!hasStagedBlock) {
            return;
        }
        for (int slot = 0; slot < peaks.Length; slot++) {
            if (blockPeaks[slot] > peaks[slot]) {
                int frame = (int)(blockEvents[slot].Frame - startFrame);
                blockEvents[slot] = blockEvents[slot] with {
                    PostLimiterSample = pcm[frame * peaks.Length + slot], Limiter = state
                };
            }
        }
    }

    internal void CommitBlock(Normalizer.BlockState? limiter = null) {
        if (!hasStagedBlock) {
            return;
        }
        for (int i = 0; i < peaks.Length; i++) {
            if (blockPeaks[i] > peaks[i]) {
                peaks[i] = blockPeaks[i];
                events[i] = limiter.HasValue ? blockEvents[i] with { Limiter = limiter } : blockEvents[i];
            }
        }
        (signals, stagedSignals) = (stagedSignals, signals);
        windowFrames = stagedWindowFrames;
        windowEnd = stagedWindowEnd;
        ObservedFrames += stagedFrames;
        DiscardBlock();
    }

    internal void DiscardBlock() {
        stagedFrames = 0;
        hasStagedBlock = false;
    }

    internal void Flag(PcmPeakIssue issue) => Issues |= issue;
}
