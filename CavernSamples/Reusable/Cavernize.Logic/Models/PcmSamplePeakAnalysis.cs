using Cavern;
using Cavern.Channels;
using Cavern.Format.Renderers;

using Cavernize.Logic.Models.RenderTargets;

namespace Cavernize.Logic.Models;

/// <summary>
/// Coordinates the two PCM taps without owning any render buffers or changing the listener layout.
/// </summary>
internal sealed class PcmSamplePeakAnalysis {
    readonly record struct OutputSlot(Channel Channel, float Elevation, float Azimuth, bool Lfe) {
        internal OutputSlot(Channel channel) : this(channel, channel.X, channel.Y, channel.LFE) { }

        internal bool Matches(Channel current) => ReferenceEquals(Channel, current) &&
            Elevation == current.X && Azimuth == current.Y && Lfe == current.LFE;
    }

    readonly Listener listener;
    readonly OutputSlot[] slots;
    readonly int updateRate;
    readonly int sampleRate;
    long nextFrame;
    int validFrames;
    bool sawPreLimiter;
    bool layoutChanged;

    internal long ExpectedFrames { get; }
    internal Exception PreLimiterError { get; private set; }
    internal bool SourceVerified { get; private set; }
    internal string[] Labels { get; }
    internal PcmSamplePeakAccumulator PreLimiter { get; }
    internal PcmSamplePeakAccumulator Processed { get; }

    internal PcmSamplePeakAnalysis(Listener listener, long expectedFrames, RenderTarget target) {
        this.listener = listener;
        ExpectedFrames = expectedFrames;
        updateRate = listener.UpdateRate;
        sampleRate = listener.SampleRate;
        Channel[] layout = Listener.Channels;
        slots = new OutputSlot[layout.Length];
        Labels = new string[layout.Length];
        PreLimiter = new PcmSamplePeakAccumulator(layout.Length);
        Processed = new PcmSamplePeakAccumulator(layout.Length);
        for (int i = 0; i < layout.Length; i++) {
            slots[i] = new OutputSlot(layout[i]);
            Labels[i] = GetLabel(layout[i], target);
        }
        if (expectedFrames <= 0 || updateRate <= 0 || sampleRate <= 0) {
            Flag(PcmPeakIssue.SourceLengthMismatch);
        }
    }

    internal void CheckSource(CavernizeTrack track, bool originalTrack) {
        if (track.Length != ExpectedFrames || track.SampleRate != sampleRate) {
            Flag(PcmPeakIssue.SourceLengthMismatch);
            return;
        }

        // A matching duration alone cannot distinguish padded decoder samples from silence.
        bool? complete = (track.Renderer as CoreAudioFormatRenderer)?.HasCompletePcmPayload;
        SourceVerified = originalTrack && complete == true;
        if (complete == false) {
            Flag(PcmPeakIssue.InvalidSource);
        }
    }

    internal void BeginBlock(long startFrame) {
        if (startFrame != nextFrame || startFrame < 0 || startFrame >= ExpectedFrames) {
            Flag(PcmPeakIssue.MissingBlock);
        }
        validFrames = (int)Math.Clamp(ExpectedFrames - startFrame, 0, Math.Max(0, updateRate));
        nextFrame = startFrame + validFrames;
        sawPreLimiter = false;
        CheckLayout();
    }

    internal void ObservePreLimiter(ReadOnlySpan<float> pcm) {
        if (PreLimiterError != null) {
            return;
        }
        try {
            StagePreLimiter(pcm);
        } catch (Exception e) {
            // An instrumentation failure must be visible in the report and must not affect the render.
            PreLimiter.DiscardBlock();
            PreLimiterError = e;
            PreLimiter.Flag(PcmPeakIssue.MeasurementError);
        }
    }

    void StagePreLimiter(ReadOnlySpan<float> pcm) {
        if (sawPreLimiter) {
            PreLimiter.Flag(PcmPeakIssue.InvalidBuffer);
            return;
        }
        sawPreLimiter = true;
        CheckLayout();
        if (!layoutChanged) {
            PreLimiter.Stage(pcm, updateRate, validFrames);
        }
    }

    internal void AcceptRenderedBlock(float[] pcm) {
        CheckLayout();
        if (layoutChanged) {
            PreLimiter.DiscardBlock();
            return;
        }

        if (sawPreLimiter && PreLimiterError == null) {
            PreLimiter.CommitBlock();
        } else {
            PreLimiter.Flag(PcmPeakIssue.MissingBlock);
        }
        Processed.Stage(pcm, updateRate, validFrames);
        Processed.CommitBlock();
    }

    internal void RejectRenderedBlock() {
        PreLimiter.DiscardBlock();
        Flag(PcmPeakIssue.RenderError);
    }

    internal void Flag(PcmPeakIssue issue) {
        PreLimiter.Flag(issue);
        Processed.Flag(issue);
    }

    void CheckLayout() {
        if (layoutChanged) {
            return;
        }

        Channel[] layout = Listener.Channels;
        bool matches = layout.Length == slots.Length && listener.UpdateRate == updateRate && listener.SampleRate == sampleRate;
        for (int i = 0; matches && i < slots.Length; i++) {
            matches = slots[i].Matches(layout[i]);
        }
        if (!matches) {
            layoutChanged = true;
            Flag(PcmPeakIssue.LayoutChanged);
        }
    }

    static string GetLabel(Channel channel, RenderTarget target) {
        if (channel.LFE) {
            return "LFE";
        }
        if (target is not (null or DriverRenderTarget or VirtualizerRenderTarget)) {
            ReferenceChannel targetMatch = ReferenceChannel.Unknown;
            foreach (ReferenceChannel reference in target.Channels) {
                if (reference != ReferenceChannel.Unknown && RenderTarget.CreateChannel(reference).Equals(channel)) {
                    if (targetMatch != ReferenceChannel.Unknown && targetMatch != reference) {
                        return FormatPosition(channel);
                    }
                    targetMatch = reference;
                }
            }
            if (targetMatch != ReferenceChannel.Unknown) {
                return ChannelPrototype.GetName(targetMatch);
            }
        }

        ReferenceChannel match = ReferenceChannel.Unknown;
        for (int i = 0; i < ChannelPrototype.Mapping.Length; i++) {
            ReferenceChannel reference = (ReferenceChannel)i;
            if (reference == ReferenceChannel.Unknown || reference == ReferenceChannel.ScreenLFE) {
                continue;
            }
            ChannelPrototype prototype = ChannelPrototype.Mapping[i];
            if ((prototype.X == channel.X && prototype.Y == channel.Y) ||
                new Channel(ChannelPrototype.AlternativePositions[i], false).Equals(channel)) {
                if (match != ReferenceChannel.Unknown) {
                    match = ReferenceChannel.Unknown;
                    break;
                }
                match = reference;
            }
        }
        return match != ReferenceChannel.Unknown ? ChannelPrototype.GetName(match) :
            FormatPosition(channel);
    }

    static string FormatPosition(Channel channel) =>
        FormattableString.Invariant($"Azimuth {channel.Y:0.##}°, elevation {channel.X:0.##}°");
}
