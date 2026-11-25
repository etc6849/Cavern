using System;

using Cavern.Utilities;

namespace Cavern.Filters {
    /// <summary>
    /// Makes sure the content always stays as close to 0 dB as possible.
    /// </summary>
    public partial class Normalizer : Filter {
        /// <inheritdoc/>
        public override bool LinearTimeInvariant => false;

        /// <summary>
        /// Gain increment per frame, should be decay rate * update rate / sample rate.
        /// </summary>
        public float decayFactor;

        /// <summary>
        /// Don't go over 0 dB gain. If true, the normalizer will act as a clipping protector.
        /// </summary>
        public bool limiterOnly;

        /// <summary>
        /// Last normalizer gain.
        /// </summary>
        float lastGain = 1;

        /// <summary>
        /// Create a normalizer or limiter.
        /// </summary>
        public Normalizer(bool limiterOnly) => this.limiterOnly = limiterOnly;

        internal readonly struct BlockState {
            public float Maximum { get; }
            public float Gain { get; }
            public bool Attacked { get; }

            internal BlockState(float maximum, float gain, bool attacked) {
                Maximum = maximum;
                Gain = gain;
                Attacked = attacked;
            }
        }

        internal static float GetBlockMaximum(float[] samples) {
            float max = Math.Abs(samples[0]), absSample;
            for (int sample = 1; sample < samples.Length; sample++) {
                absSample = Math.Abs(samples[sample]);
                if (max < absSample) {
                    max = absSample;
                }
            }
            return max;
        }

        /// <inheritdoc/>
        public override void Process(float[] samples) => ProcessWithState(samples);

        internal BlockState ProcessWithState(float[] samples) {
            float max = GetBlockMaximum(samples);
            bool attacked = max * lastGain > 1;
            if (attacked) {
                lastGain = .9f / max;
            }
            WaveformUtils.Gain(samples, lastGain); // Normalize last samples
            BlockState state = new BlockState(max, lastGain, attacked);
            // Decay
            lastGain += decayFactor;
            if (limiterOnly && lastGain > 1) {
                lastGain = 1;
            }
            return state;
        }

        /// <inheritdoc/>
        public override object Clone() => new Normalizer(limiterOnly);
    }
}
