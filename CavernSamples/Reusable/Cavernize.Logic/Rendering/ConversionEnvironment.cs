using Cavern;
using Cavern.Listeners;
using Cavern.Virtualizer;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

namespace Cavernize.Logic.Rendering;

/// <summary>
/// Handles main steps of the Cavernize process while being extensible.
/// </summary>
public sealed class ConversionEnvironment {
    /// <summary>
    /// Playback environment used for rendering.
    /// </summary>
    public Listener Listener { get; private set; }

    float safeGain = 1;
    double? requestedGain;
    internal ListenerGainInfo Gain => new(requestedGain, safeGain, Listener.Volume);

    /// <summary>
    /// Keeper of loaded files and settings.
    /// </summary>
    readonly ICavernizeApp app;

    /// <summary>
    /// Handles main steps of the Cavernize process while being extensible.
    /// </summary>
    /// <param name="app">Application implementation keeping</param>
    public ConversionEnvironment(ICavernizeApp app) {
        this.app = app;
        Listener = new() { // Create a listener, which triggers the loading of saved environment settings
            UpdateRate = 64,
            AudioQuality = QualityModes.Perfect
        };
    }

    /// <summary>
    /// Set up the listening environment for playing back the specified track.
    /// </summary>
    public void AttachToListener(CavernizeTrack target) => AttachToListener(target, app.RenderTarget, app.ExportFormat.MaxChannels);

    internal void AttachToListener(CavernizeTrack target, RenderTarget layout, int maxChannels) {
        layout.Apply(app.RenderingSettings.SurroundSwap);
        if (layout is not VirtualizerRenderTarget && maxChannels < Listener.Channels.Length) {
            throw new OverMaxChannelsException(Listener.Channels.Length, maxChannels);
        }

        if (app.RenderingSettings.SpeakerVirtualizer) {
            VirtualizerFilter.SetupForSpeakers();
            Listener.SampleRate = VirtualizerFilter.FilterSampleRate;
        }

        // Prevent height limiting, require at least 4 overhead channels for full gain
        safeGain = target.Renderer.HasObjects ? layout.GetSafeGain() : 1;
        requestedGain = app.RenderingSettings.ListenerGainDbOverride;
        Listener.Volume = requestedGain is double db ? ToListenerGain(db) : app.RenderingSettings.Gain * safeGain;

        if (layout is VirtualizerRenderTarget) {
            if (app.RenderingSettings.RoomCorrection != null && app.RenderingSettings.RoomCorrection.SampleRate != VirtualizerFilter.FilterSampleRate) {
                throw new SampleRateMismatchException();
            }
            Listener.SampleRate = VirtualizerFilter.FilterSampleRate;
        } else {
            Listener.SampleRate = app.RenderingSettings.RoomCorrection?.SampleRate ?? target.SampleRate;
        }

        if (app.RenderingSettings.RoomCorrectionUsable) {
            Listener = new ConvolvedListener(false) {
                SampleRate = Listener.SampleRate,
                UpdateRate = Listener.UpdateRate,
                AudioQuality = Listener.AudioQuality,
                Volume = Listener.Volume,
                ConvolutionClip = app.RenderingSettings.RoomCorrection
            };
        }

        Reset();
        target.Attach(Listener, app.UpmixingSettings);
    }

    internal static float ToListenerGain(double db) {
        float linear = (float)Math.Pow(10, db / 20);
        if (!float.IsFinite(linear) || linear <= 0) {
            throw new ArgumentOutOfRangeException(nameof(db), "Listener gain must produce a finite positive PCM multiplier.");
        }
        return linear;
    }

    /// <summary>
    /// Reset the environment, prepare for next attachment.
    /// </summary>
    public void Reset() => Listener.DetachAllSources();
}
