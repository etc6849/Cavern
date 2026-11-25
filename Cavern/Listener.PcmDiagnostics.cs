using System;
using System.Collections.Generic;
using Cavern.Filters;

namespace Cavern {
    partial class Listener {
        internal delegate void LimitedPcmObserver(ReadOnlySpan<float> pcm, Normalizer.BlockState state);
        internal LimitedPcmObserver ObservePostLimiterPcm { get; set; }
        internal bool TracePcmSources { get; set; }

        readonly List<Source> tracedSources = new List<Source>();
        BiquadFilter.PcmFilterState[] traceFilterStates = Array.Empty<BiquadFilter.PcmFilterState>();

        void CapturePcmFilterStates() {
            if (traceFilterStates.Length != channelCount) {
                traceFilterStates = new BiquadFilter.PcmFilterState[channelCount];
            }
            for (int slot = 0; slot < channelCount; slot++) {
                if (Channels[slot].LFE && !DirectLFE) {
                    traceFilterStates[slot] = new BiquadFilter.PcmFilterState(lowpasses[slot]);
                }
            }
        }

        // Only valid synchronously at the pre-limiter tap. Source buffers are reused on the next update.
        internal IReadOnlyList<Source> TracedPcmSources => tracedSources;

        internal float GetSourcePcmContribution(int source, int frame, int slot) {
            float[] block = results[source];
            int index = frame * channelCount + slot;
            return index < block.Length ? block[index] : 0;
        }

        internal float ReconstructPcmSample(int frame, int slot, float sourceSum,
            out float[] lfeInput, out BiquadFilter.PcmFilterState? lfeState) {
            lfeInput = null;
            lfeState = null;
            bool lfe = Channels[slot].LFE;
            if (lfe && !DirectLFE) {
                BiquadFilter.PcmFilterState filter = traceFilterStates[slot];
                lfeState = filter;
                lfeInput = new float[frame + 1];
                for (int i = 0; i <= frame; i++) {
                    float input = 0;
                    for (int source = 0; source < results.Count; source++) {
                        input += GetSourcePcmContribution(source, i, slot);
                    }
                    lfeInput[i] = input;
                    sourceSum = filter.Process(input);
                }
            }
            return sourceSum * (lfe ? LFEVolume * Volume : Volume);
        }

        internal void ClearPcmSourceTrace() {
            TracePcmSources = false;
            tracedSources.Clear();
        }
    }
}
