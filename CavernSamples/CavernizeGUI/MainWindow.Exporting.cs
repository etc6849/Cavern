using System;
using System.Threading;

using Cavern;
using Cavern.Format;
using Cavern.Format.Environment;
using Cavern.Utilities;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;
using VoidX.WPF;

namespace CavernizeGUI;

// Functions that are part of the render export process.
partial class MainWindow {
    /// <summary>
    /// Keeps track of export time and evaluates performance.
    /// </summary>
    class Progressor(long length, Listener listener, TaskEngine taskEngine) {
        /// <summary>
        /// Samples rendered so far.
        /// </summary>
        public long Rendered { get; private set; }

        /// <summary>
        /// Time of starting the export process.
        /// </summary>
        readonly DateTime start = DateTime.Now;

        /// <summary>
        /// Multiplier of the content length to get the progress ratio.
        /// </summary>
        readonly double samplesToProgress = 1.0 / length;

        /// <summary>
        /// Converts samples to seconds.
        /// </summary>
        readonly double samplesToSeconds = 1.0 / listener.SampleRate;

        /// <summary>
        /// Samples processed each frame.
        /// </summary>
        readonly long updateRate = listener.UpdateRate;

        /// <summary>
        /// UI updater instance.
        /// </summary>
        readonly TaskEngine taskEngine = taskEngine;

        /// <summary>
        /// Samples until next UI update.
        /// </summary>
        long untilUpdate = updateInterval;

        /// <summary>
        /// Report progress after each listener update.
        /// </summary>
        public void Update() {
            Rendered += updateRate;
            if ((untilUpdate -= updateRate) <= 0) {
                double progress = Rendered * samplesToProgress;
                TimeSpan elapsed = DateTime.Now - start, remaining = elapsed / progress - elapsed;
                double speed = Rendered * samplesToSeconds / elapsed.TotalSeconds;

                string remDisp;
                if (remaining.TotalDays < 1) {
                    if (remaining.TotalHours < 1) {
                        remDisp = remaining.ToString("mm':'ss");
                    } else {
                        remDisp = remaining.ToString("h':'mm':'ss");
                    }
                } else {
                    remDisp = remaining.ToString("d':'hh':'mm':'ss");
                }

                taskEngine.UpdateStatusLazy(string.Format(language["ProgP"],
                    progress.ToString("0.00%"), speed.ToString("0.00"), remDisp));
                taskEngine.UpdateProgressBar(progress);
                untilUpdate = updateInterval;
            }
        }

        /// <summary>
        /// Report custom progress as finalization.
        /// </summary>
        public void Finalize(double progress) {
            taskEngine.UpdateStatusLazy(string.Format(language["FinaP"], progress.ToString("0.00%")));
            taskEngine.UpdateProgressBar(progress);
        }
    }

    RenderStats WriteRender(CavernizeTrack target, CavernizeTrack renderedTrack, AudioWriter writer, RenderTarget renderTarget,
        bool verify, out PcmSamplePeakAnalysis peaks) {
        Listener listener = environment.Listener;
        Progressor progressor = new(target.Length, listener, taskEngine);
        var (grade, speakerVirtualizer) = Dispatcher.Invoke(() => (grading.IsChecked, RenderingSettings.SpeakerVirtualizer));
        return ChannelRender.Run(environment, target, renderedTrack, writer, renderTarget, RenderingSettings, grade, verify, speakerVirtualizer,
            blockSize, progressor.Update, (error, frame) => ThreadPool.QueueUserWorkItem(_ => {
                TimeSpan time = TimeSpan.FromSeconds(frame / (double)listener.SampleRate);
                Dispatcher.Invoke(() => Error(string.Format(language["RenEr"], time, error.Message)));
            }), out peaks);
    }

    /// <summary>
    /// Transcodes between object-based tracks, and returns some measurements of the render.
    /// </summary>
    RenderStats WriteTranscode(CavernizeTrack target, EnvironmentWriter writer) {
        RenderStats stats = new(environment.Listener);
        Progressor progressor = new Progressor(target.Length, environment.Listener, taskEngine);

        while (progressor.Rendered < target.Length) {
            writer.WriteNextFrame();
            progressor.Update();
        }

        writer.Dispose();
        return stats;
    }

    /// <summary>
    /// Transcodes from object-based tracks to ADM BWF, and returns some measurements of the render.
    /// </summary>
    RenderStats WriteTranscode(CavernizeTrack target, BroadcastWaveFormatWriter writer) {
        RenderStats stats = new(environment.Listener);
        Progressor progressor = new Progressor((long)(target.Length / progressSplit), environment.Listener, taskEngine);

        while (progressor.Rendered < target.Length) {
            writer.WriteNextFrame();
            progressor.Update();
        }

        writer.FinalFeedback = progressor.Finalize;
        writer.FinalFeedbackStart = progressSplit;
        writer.Dispose();
        return stats;
    }

    /// <summary>
    /// Update the UI after this many samples have been rendered.
    /// </summary>
    const long updateInterval = 50000;

    /// <summary>
    /// The export process exports PCM data until this percentage and extra metadata after that.
    /// </summary>
    const double progressSplit = .95;
}
