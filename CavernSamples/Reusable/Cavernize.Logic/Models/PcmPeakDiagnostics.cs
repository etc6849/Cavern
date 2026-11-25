using Cavern;
using Cavern.Channels;
using Cavern.Filters;
using Cavern.Format.Renderers;

namespace Cavernize.Logic.Models;

/// <summary>
/// Independent channel-major scan. Only a new overall record needs a source snapshot.
/// </summary>
internal sealed class PcmPeakDiagnostics(int channels, IReadOnlyList<Source> sources, ReferenceChannel[] bed,
    IReadOnlyList<int> atmosSourceIds = null) {
    internal sealed record Contribution(int? SourceIndex, int? AtmosSourceId, string Identity, float X, float Y, float Z,
        float SourceVolume, float SpatialBlend, float Size, float? DecodedSample, float RenderedContribution);
    internal sealed record Reconstruction(long Frame, int Slot, float Observed, float SourceSum, float Reconstructed,
        float Tolerance, bool Matches, float ListenerVolume, float LfeVolume, bool LfeLowpass,
        IReadOnlyList<Contribution> Contributions, float[] LfeInputPrefix, BiquadFilter.PcmFilterState? LfeInitialState);
    internal sealed record Verification(int Slot, string Speaker, bool Matches, long? Frame, float? SignedSample,
        string FloatBits, double? Dbfs);

    internal PcmPeakDiagnostics(int channels, CavernizeTrack track) : this(channels, track.Renderer.Objects,
        track.Renderer.GetChannels(), (track.Renderer as CoreAudioFormatRenderer)?.AtmosSourceIds) { }

    readonly PcmPeakEvent?[] committed = new PcmPeakEvent?[channels];
    readonly PcmPeakEvent?[] staged = new PcmPeakEvent?[channels];
    Reconstruction best, pending;
    Exception scanError;

    internal void TryScan(ReadOnlySpan<float> pcm, long blockStart, int validFrames, Listener listener) {
        if (scanError != null) {
            return;
        }
        try {
            Scan(pcm, blockStart, validFrames, listener);
        } catch (Exception e) {
            DiscardBlock();
            scanError = e;
        }
    }

    void Scan(ReadOnlySpan<float> pcm, long blockStart, int validFrames, Listener listener) {
        float largest = best == null ? -1 : Math.Abs(best.Observed);
        int bestFrame = 0, bestSlot = -1;
        for (int slot = 0; slot < channels; slot++) {
            float magnitude = committed[slot] is PcmPeakEvent prior ? Math.Abs(prior.Sample) : -1;
            for (int frame = 0, index = slot; frame < validFrames; frame++, index += channels) {
                float sample = pcm[index];
                float absolute = Math.Abs(sample);
                if (!float.IsFinite(sample) || absolute <= magnitude) {
                    continue;
                }
                magnitude = absolute;
                staged[slot] = new PcmPeakEvent(blockStart + frame, sample);
                if (absolute > largest || (absolute == largest && bestSlot >= 0 &&
                    (frame < bestFrame || (frame == bestFrame && slot < bestSlot)))) {
                    largest = absolute;
                    bestSlot = slot;
                    bestFrame = frame;
                }
            }
        }
        if (bestSlot >= 0) {
            float observed = pcm[bestFrame * channels + bestSlot];
            pending = Explain(listener, blockStart, bestFrame, bestSlot, observed);
        }
    }

    Reconstruction Explain(Listener listener, long blockStart, int frame, int slot, float observed) {
        List<Contribution> contributions = new();
        float sum = 0;
        IReadOnlyList<Source> collected = listener.TracedPcmSources;
        for (int i = 0; i < collected.Count; i++) {
            Source source = collected[i];
            float contribution = listener.GetSourcePcmContribution(i, frame, slot);
            sum += contribution;
            // Preserve zero contributors on LFE because the filter can carry prior-frame energy.
            if (contribution == 0 && !Listener.Channels[slot].LFE) {
                continue;
            }
            int sourceIndex = -1;
            for (int j = 0; j < sources.Count; j++) {
                if (ReferenceEquals(sources[j], source)) {
                    sourceIndex = j;
                    break;
                }
            }
            string identity = sourceIndex < 0 ? "Listener source " + (i + 1) :
                sourceIndex < bed.Length ? "Bed " + bed[sourceIndex] : "Object " + (sourceIndex - bed.Length + 1);
            float? decoded = source.Rendered is { Channels: 1 } waveform && waveform[0].Length == listener.UpdateRate ?
                waveform[0][frame] : null;
            int? atmosId = atmosSourceIds != null && sourceIndex >= 0 ? atmosSourceIds[sourceIndex] : null;
            contributions.Add(new(sourceIndex < 0 ? null : sourceIndex, atmosId, identity,
                source.Position.X, source.Position.Y, source.Position.Z, source.Volume, source.SpatialBlend,
                source.Size, decoded, contribution));
        }
        float rebuilt = listener.ReconstructPcmSample(frame, slot, sum, out float[] lfePrefix, out var lfeState);
        float tolerance = 1e-6f * Math.Max(1, Math.Abs(observed));
        return new(blockStart + frame, slot + 1, observed, sum, rebuilt, tolerance,
            Math.Abs(rebuilt - observed) <= tolerance, listener.Volume, listener.LFEVolume,
            Listener.Channels[slot].LFE && !listener.DirectLFE, contributions, lfePrefix, lfeState);
    }

    internal void CommitBlock() {
        for (int slot = 0; slot < channels; slot++) {
            if (staged[slot].HasValue) {
                committed[slot] = staged[slot];
            }
        }
        best = pending ?? best;
    }

    internal void DiscardBlock() {
        Array.Clear(staged);
        pending = null;
    }

    internal bool Matches(PcmSamplePeakAnalysis analysis) {
        if (scanError != null || best?.Matches != true) {
            return false;
        }
        for (int slot = 0; slot < channels; slot++) {
            if (!SameEvent(committed[slot], analysis.PreLimiter.GetEvent(slot))) {
                return false;
            }
        }
        return true;
    }

    static bool SameEvent(PcmPeakEvent? a, PcmPeakEvent? b) =>
        a?.Frame == b?.Frame && (a == null ||
            BitConverter.SingleToUInt32Bits(a.Value.Sample) == BitConverter.SingleToUInt32Bits(b.Value.Sample));

    internal object Evidence(PcmSamplePeakAnalysis analysis) {
        Verification[] verification = new Verification[channels];
        for (int slot = 0; slot < channels; slot++) {
            PcmPeakEvent? independent = committed[slot], measured = analysis.PreLimiter.GetEvent(slot);
            bool matches = SameEvent(independent, measured);
            verification[slot] = new(slot + 1, analysis.Labels[slot], matches, independent?.Frame,
                independent?.Sample, Bits(independent?.Sample), independent?.Dbfs);
        }
        return new {
            Matches = Matches(analysis),
            Error = scanError?.Message,
            Channels = verification,
            Highest = best,
            HighestSpeaker = best == null ? null : analysis.Labels[best.Slot - 1],
            HighestTimestampSeconds = best?.Frame / (double)analysis.SampleRate,
            CorrespondingMeasuredEvent = best == null ? null : Describe(analysis.PreLimiter.GetEvent(best.Slot - 1))
        };
    }

    static object Describe(PcmPeakEvent? value) => value is PcmPeakEvent peak ? new {
        peak.Frame, peak.Sample, FloatBits = Bits(peak.Sample), peak.Dbfs, peak.PostLimiterSample, peak.Limiter
    } : null;

    internal static string Bits(float? sample) => sample is float value ?
        $"0x{BitConverter.SingleToUInt32Bits(value):X8}" : null;
}
