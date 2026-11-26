using System.Numerics;

using Cavern;
using Cavern.Channels;
using Cavern.Format.Renderers;
using Cavern.Format.Renderers.CoreAudioFormat;
using Cavern.Utilities;

namespace Cavernize.Logic.Models;

/// <summary>
/// Reports the DAMF source inventory separately from speaker output and legacy render statistics.
/// </summary>
internal sealed class AtmosSourceInventory {
    internal sealed record Position(float X, float Y, float Z) {
        internal Position(Vector3 position) : this(position.X, position.Y, position.Z) { }

        internal static Position Read(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) ? new(value) : null;
    }

    internal sealed record Bed(int Id, ReferenceChannel Channel, Position Position, Position LastObservedPosition);
    internal sealed record AtmosObject(int PcmSlot, int? DeclaredId, int RendererId, string Motion, string Activity,
        Position LastAuthoredPosition, Position LastObservedPosition);
    internal sealed record Summary(string Provenance, int AttachedSourceSlots, IReadOnlyList<Bed> Beds, IReadOnlyList<AtmosObject> Objects,
        IReadOnlyList<Position> ObservedStationaryPositions) {
        internal int? DynamicObjects => Objects?.Count(item => item.Motion == "dynamic");
        internal int? InactiveSources => Objects != null && Objects.All(item => item.Activity != "unverified") ?
            Objects.Count(item => item.Activity == "silent") : null;
    }

    readonly CoreAudioFormatRenderer renderer;
    readonly bool derived;
    readonly ReferenceChannel[] bedChannels;
    readonly List<int> pending;
    readonly bool[] hasAudio;
    readonly bool[] missingAudio;
    long observedFrames;

    internal AtmosSourceInventory(CoreAudioFormatRenderer renderer, bool derived) {
        this.renderer = renderer;
        this.derived = derived;
        bedChannels = renderer.GetChannels();
        hasAudio = new bool[renderer.DeclaredObjectIds.Count];
        missingAudio = new bool[hasAudio.Length];
        pending = Enumerable.Range(0, hasAudio.Length).ToList();
    }

    internal static AtmosSourceInventory Create(Renderer renderer, bool derived) =>
        renderer is CoreAudioFormatRenderer { AtmosSourceIds: not null } caf ? new(caf, derived) : null;

    internal void Observe(int frames) {
        observedFrames += frames;
        for (int i = pending.Count - 1; i >= 0; i--) {
            int obj = pending[i];
            var block = renderer.Objects[bedChannels.Length + obj].Rendered;
            if (block == null || block.Channels != 1 || block[0].Length < frames) {
                missingAudio[obj] = true;
                continue;
            }
            ReadOnlySpan<float> samples = block[0].AsSpan(0, frames);
            int nonzero = samples.IndexOfAnyExcept(0f);
            if (nonzero < 0) {
                continue;
            }
            for (int sample = nonzero; sample < samples.Length; sample++) {
                if (!float.IsFinite(samples[sample])) {
                    missingAudio[obj] = true;
                } else if (samples[sample] != 0) {
                    hasAudio[obj] = true;
                    pending.RemoveAt(i);
                    break;
                }
            }
        }
    }

    internal Summary Describe(long expectedFrames, int attachedSourceSlots) {
        Bed[] beds = bedChannels.Select((channel, i) => new Bed(renderer.AtmosSourceIds[i], channel,
            new(ChannelPrototype.AlternativePositions[(int)channel]), LastObservedPosition(i))).ToArray();
        AtmosObject[] objects = new AtmosObject[hasAudio.Length];
        bool complete = expectedFrames > 0 && observedFrames == expectedFrames && renderer.HasCompletePcmPayload == true;
        for (int obj = 0; obj < objects.Length; obj++) {
            int slot = bedChannels.Length + obj;
            int rendererId = renderer.AtmosSourceIds[slot];
            int? declaredId = renderer.DeclaredObjectIds[obj];
            // The existing renderer uses sequential IDs for the PCM and event mapping. Flag any mismatch explicitly.
            bool identified = declaredId == rendererId && renderer.DeclaredObjectIds.Count(id => id == declaredId) == 1;
            var positions = renderer.ExplicitPositions[slot];
            string motion = !identified || positions == null ? "unverified" : positions.DistinctCount switch {
                1 => "stationary",
                < DolbyAtmosMasterMetadataFile.DynamicPositionThreshold => "semi_static",
                _ => "dynamic"
            };
            string activity = hasAudio[obj] ? "signal" : complete && !missingAudio[obj] ? "silent" : "unverified";
            objects[obj] = new(slot + 1, declaredId, rendererId, motion, activity,
                identified && positions != null ? new(positions.Last) : null,
                LastObservedPosition(slot));
        }
        return new(derived ? "decoded_damf" : "damf", attachedSourceSlots, beds, objects, null);
    }

    Position LastObservedPosition(int slot) =>
        observedFrames != 0 ? Position.Read(renderer.Objects[slot].Position / Listener.EnvironmentSize) : null;

    internal static Summary Unavailable(RenderStats stats, int attachedSourceSlots) => new("unavailable", attachedSourceSlots, null, null,
        stats.GetStaticOrSemiStaticPositions().Select(position => Position.Read(position / Listener.EnvironmentSize))
            .Where(position => position != null).ToArray());
}
