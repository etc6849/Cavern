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

/// <summary>
/// Tracks finite sample magnitude maxima and valid sample-frame coverage for one PCM tap.
/// </summary>
internal sealed class PcmSamplePeakAccumulator {
    readonly float[] peaks;
    readonly float[] blockPeaks;
    int stagedFrames;
    bool hasStagedBlock;

    internal PcmSamplePeakAccumulator(int channels) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        peaks = new float[channels];
        blockPeaks = new float[channels];
        Array.Fill(peaks, -1);
    }

    internal ReadOnlySpan<float> Peaks => peaks;
    internal long ObservedFrames { get; private set; }
    internal PcmPeakIssue Issues { get; private set; }

    internal void Stage(ReadOnlySpan<float> pcm, int blockFrames, int validFrames) {
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
                float magnitude = MathF.Abs(pcm[sample++]);
                if (float.IsFinite(magnitude)) {
                    if (magnitude > blockPeaks[channel]) {
                        blockPeaks[channel] = magnitude;
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

    internal void CommitBlock() {
        if (!hasStagedBlock) {
            return;
        }
        for (int i = 0; i < peaks.Length; i++) {
            peaks[i] = Math.Max(peaks[i], blockPeaks[i]);
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
