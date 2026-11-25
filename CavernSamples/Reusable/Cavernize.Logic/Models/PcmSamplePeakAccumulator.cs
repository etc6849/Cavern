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

/// <summary>
/// Tracks finite sample magnitude maxima and valid sample-frame coverage for one PCM tap.
/// </summary>
internal sealed class PcmSamplePeakAccumulator {
    readonly float[] peaks;
    readonly float[] blockPeaks;
    readonly PcmPeakEvent[] events;
    readonly PcmPeakEvent[] blockEvents;
    int stagedFrames;
    bool hasStagedBlock;

    internal PcmSamplePeakAccumulator(int channels) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
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

    internal void Stage(ReadOnlySpan<float> pcm, int blockFrames, int validFrames, long startFrame = 0) {
        DiscardBlock();
        int channels = peaks.Length;
        if (validFrames <= 0 || validFrames > blockFrames || pcm.Length % channels != 0 ||
            pcm.Length / channels != blockFrames) {
            Issues |= PcmPeakIssue.InvalidBuffer;
            return;
        }

        hasStagedBlock = true;
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
        ObservedFrames += stagedFrames;
        DiscardBlock();
    }

    internal void DiscardBlock() {
        stagedFrames = 0;
        hasStagedBlock = false;
    }

    internal void Flag(PcmPeakIssue issue) => Issues |= issue;
}
