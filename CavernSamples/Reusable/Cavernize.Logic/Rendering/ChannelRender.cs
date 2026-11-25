using System.Numerics;
using Cavern;
using Cavern.Filters;
using Cavern.Format;
using Cavern.Utilities;
using Cavern.Virtualizer;
using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// The shared speaker render loop, including PCM taps, legacy grading, and optional audio export.
/// </summary>
internal static class ChannelRender {
    internal static PcmSamplePeakAnalysis CreateAnalysis(ConversionEnvironment environment, CavernizeTrack target,
        CavernizeTrack renderedTrack, RenderTarget layout, bool verify) =>
        new(environment.Listener, target.Length, layout,
            verify && renderedTrack.AtmosSourcePath != null ? new PcmPeakDiagnostics(Listener.Channels.Length, renderedTrack) : null,
            environment.Gain);

    /// <summary>
    /// Renders a listener to a file, and returns some measurements of the render.
    /// </summary>
    internal static RenderStats Run(ConversionEnvironment environment, CavernizeTrack target, CavernizeTrack renderedTrack,
        AudioWriter writer, RenderTarget renderTarget, RenderingSettings settings, bool grading, bool verify, bool speakerVirtualizer,
        int blockSize, Action progress, Action<Exception, long> renderError, out PcmSamplePeakAnalysis peaks, bool derived = false) {
        Listener listener = environment.Listener;
        if (listener.ObservePreLimiterPcm != null || listener.ObservePostLimiterPcm != null) {
            throw new InvalidOperationException("The listener already has a PCM observer.");
        }

        bool virtualizerState = Listener.HeadphoneVirtualizer;
        try {
            // Apply the same virtual speaker layout used by the writer before taking the measurement snapshot.
            VirtualizerFilter virtualizer = null;
            Normalizer normalizer = null;
            if (virtualizerState || speakerVirtualizer) {
                Listener.HeadphoneVirtualizer = false;
                virtualizer = new VirtualizerFilter();
                virtualizer.SetLayout();
                normalizer = new Normalizer(true) {
                    decayFactor = 10 * (float)listener.UpdateRate / listener.SampleRate
                };
            }

            bool direct = !derived && ReferenceEquals(target, renderedTrack);
            peaks = CreateAnalysis(environment, target, renderedTrack, renderTarget, verify);
            peaks.CheckSource(renderedTrack, direct);
            listener.ObservePreLimiterPcm = peaks.ObservePreLimiter;
            listener.ObservePostLimiterPcm = peaks.ObservePostLimiter;
            listener.TracePcmSources = peaks.Diagnostics != null;
            RenderStats stats = RenderBlocks(listener, target, writer, renderTarget, peaks, settings, grading, virtualizer, normalizer,
                blockSize, progress, renderError);
            peaks.CheckSource(renderedTrack, direct);
            return stats;
        } finally {
            listener.ObservePreLimiterPcm = null;
            listener.ObservePostLimiterPcm = null;
            listener.ClearPcmSourceTrace();
            if (virtualizerState) {
                Listener.HeadphoneVirtualizer = true;
            }
        }
    }

    static RenderStats RenderBlocks(Listener listener, CavernizeTrack target, AudioWriter writer, RenderTarget renderTarget,
        PcmSamplePeakAnalysis peaks, RenderingSettings settings, bool grading, VirtualizerFilter virtualizer, Normalizer normalizer,
        int blockSize, Action progress, Action<Exception, long> renderError) {
        RenderStats stats = grading ? new RenderStatsEx(listener) : new RenderStats(listener);
        bool customMuting = settings.MuteBed || settings.MuteGround;

        int cachePosition = 0;
        // Preserve the writer's original cache size and channel stride.
        float[] writeCache = writer == null ? null : new float[blockSize / renderTarget.OutputChannels * Listener.Channels.Length];
        bool flush = false;

        bool wasError = false;
        for (long renderedFrames = 0; renderedFrames < target.Length; renderedFrames += listener.UpdateRate) {
            peaks.BeginBlock(renderedFrames);
            float[] result;
            bool rendered = false;
            try {
                result = listener.Render();
                rendered = true;
            } catch (Exception e) {
                if (!wasError) {
                    wasError = true;
                    renderError(e, renderedFrames);
                }
                result = new float[Listener.Channels.Length * listener.UpdateRate];
            }
            if (rendered) {
                peaks.AcceptRenderedBlock(result);
            } else {
                peaks.RejectRenderedBlock();
            }

            // Alignment of split parts
            if (target.Length - renderedFrames <= listener.UpdateRate) {
                Array.Resize(ref result, (int)((target.Length - renderedFrames) * Listener.Channels.Length));
                flush = true;
            }

            if (writeCache != null) {
                Array.Copy(result, 0, writeCache, cachePosition, result.Length);
                cachePosition += result.Length;
                if (cachePosition == writeCache.Length || flush) {
                    if (virtualizer == null) {
                        if (renderTarget is not DownmixedRenderTarget downmix) {
                            writer?.WriteBlock(writeCache, 0, cachePosition);
                        } else {
                            downmix.PerformMerge(writeCache);
                            writer?.WriteChannelLimitedBlock(writeCache, downmix.OutputChannels,
                                Listener.Channels.Length, 0, cachePosition);
                        }
                    } else {
                        virtualizer.Process(writeCache, listener.SampleRate);
                        normalizer.Process(writeCache);
                        writer?.WriteChannelLimitedBlock(writeCache, renderTarget.OutputChannels,
                            Listener.Channels.Length, 0, cachePosition);
                    }
                    cachePosition = 0;
                }
            }

            if (renderedFrames > secondFrame) {
                stats.Update(result);
            }

            if (customMuting) {
                IReadOnlyList<Source> objects = target.Renderer.Objects;
                for (int i = 0, c = objects.Count; i < c; i++) {
                    Vector3 rawPos = objects[i].Position / Listener.EnvironmentSize;
                    objects[i].Mute =
                        (settings.MuteBed && MathF.Abs(rawPos.X) % 1 < .01f &&
                            MathF.Abs(rawPos.Y) % 1 < .01f && MathF.Abs(rawPos.Z % 1) < .01f) ||
                        (settings.MuteGround && rawPos.Y == 0);
                }
            }

            progress?.Invoke();
        }

        writer?.Dispose();
        return stats;
    }

    internal static bool HasCompleteFrames(PcmSamplePeakAnalysis analysis) =>
        analysis.PreLimiter.Issues == PcmPeakIssue.None && analysis.Processed.Issues == PcmPeakIssue.None &&
        analysis.PreLimiter.ObservedFrames == analysis.ExpectedFrames && analysis.Processed.ObservedFrames == analysis.ExpectedFrames;

    // The OAMD objects can take two codec frames to move to their initial position.
    const int secondFrame = 2 * 1536;
}
