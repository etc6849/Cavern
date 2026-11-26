using System;

using System.IO;

using Cavern;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Container;
using Cavern.Format.Environment;
using Cavern.Format.Exceptions;
using Cavern.Format.Renderers;
using Cavern.Utilities;
using Cavern.Virtualizer;
using Cavern.WPF;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;
using CavernizeGUI.CavernSettings;
using CavernizeGUI.Resources;

namespace CavernizeGUI;

partial class MainWindow {
    /// <summary>
    /// Total number of samples for all channels that will be written to the file at once.
    /// </summary>
    int blockSize;

    /// <summary>
    /// Prepare the renderer for export.
    /// </summary>
    void PreRender() {
        if (taskEngine.IsOperationRunning) {
            throw new ConcurrencyException(language["OpRun"]);
        }
        if (tracks.SelectedItem == null) {
            throw new TrackException(language["LdSrc"]);
        }
        if (!reportMode.IsChecked && !explicitAnalysisTrack && (grading.IsChecked || verifyPeakEvents.IsChecked) &&
            LoadedFile != null && (Path.GetExtension(LoadedFile.Path).ToLowerInvariant() is ".mkv" or ".mka")) {
            tracks.SelectedItem = IntegratedAnalysis.SelectTrack(LoadedFile, null);
        }

        if (!((CavernizeTrack)tracks.SelectedItem).Supported) {
            throw new TrackException(language["UnTrk"]);
        }

        ExportFormat format = (ExportFormat)audio.SelectedItem;
        bool needsFFmpeg = !string.IsNullOrEmpty(format.FFName) && format.Codec != Codec.PCM_Float && format.Codec != Codec.PCM_LE;
        if (needsFFmpeg && !ffmpeg.Found) {
            throw new TrackException(language["FFOnl"]);
        }

        try {
            AttachToListener();
        } catch (OverMaxChannelsException e) {
            throw new TrackException(string.Format(language["ChCnt"], e.Channels, e.MaxChannels));
        }
    }

    /// <summary>
    /// Attach the track to the environment and perform compatibility checks.
    /// </summary>
    void AttachToListener() {
        try {
            environment.AttachToListener((CavernizeTrack)tracks.SelectedItem);
        } catch (NonGroundChannelPresentException) {
            throw new NonGroundChannelPresentException(language["SpViE"]);
        } catch (SampleRateMismatchException) {
            throw new IncompatibleSettingsException(language["FiltC"]);
        }
    }

    /// <summary>
    /// Start rendering to a target <paramref name="path"/>.
    /// </summary>
    /// <returns>A task for rendering or null when an error happened.</returns>
    Action Render(string path) {
        CavernizeTrack target = (CavernizeTrack)tracks.SelectedItem;
        Codec codec = ((ExportFormat)audio.SelectedItem).Codec;
        BitDepth bits = codec == Codec.PCM_Float ? BitDepth.Float32 : Settings.Default.force24Bit ? BitDepth.Int24 : BitDepth.Int16;

        if (codec.IsEnvironmental()) {
            return () => {
                EnvironmentWriter transcoder;
                try {
                    transcoder = EnvironmentWriter.Create(path, codec, environment.Listener, target.Length, bits, target.Renderer);
                } catch (UnsupportedContainerForWriteException) {
                    Error(language["UnCod"]);
                    return;
                }
                TranscodeTask(target, transcoder);
            };
        }

        if (Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase) && target.Container == Container.Matroska) {
            if (!ffmpeg.Found) {
                throw new IOException("FFmpeg is required to preserve the source Matroska metadata.");
            }
            if (File.Exists(path)) {
                throw new IOException("Choose a new destination for the MKV export.");
            }
            var (layout, grade, verify, format, tool) = (RenderTarget, grading.IsChecked, verifyPeakEvents.IsChecked, ExportFormat, ffmpeg.Location);
            return () => RenderMatroska(target, path, layout, grade, verify, format, tool);
        }

        blockSize = CavernizeOutput.GetBlockSize(RenderTarget, environment);
        AudioWriter writer = CavernizeOutput.CreateRenderOutput(this, path, environment, target, codec, bits);
        return () => RenderTask(target, writer, path);
    }

    /// <summary>
    /// Create an external converter if it's needed for rendering a specific track.
    /// </summary>
    ExternalConverterHandler CreateExternalHandler(CavernizeTrack target, int keepFirstSources) {
        LicenceWindow licenceWindow = Dispatcher.Invoke(() => new LicenceWindow());
        ExternalConverterHandler external = new(target, licenceWindow, taskEngine.UpdateProgressBar, taskEngine.UpdateStatus, Dispatcher.Invoke);
        Dispatcher.Invoke(licenceWindow.Close);
        if (external.Failed) {
            Dispatcher.Invoke(() => Error(external.Error.Message));
        } else {
            external.Attach(environment.Listener, new DynamicUpmixingSettings(), keepFirstSources);
        }
        return external;
    }

    void RenderMatroska(CavernizeTrack target, string output, RenderTarget layout, bool grade, bool verify, ExportFormat format, string tool) {
        using ExternalConverterHandler external = CreateExternalHandler(target, 0);
        if (external.Failed) {
            return; // CreateExternalHandler already showed the decoder's summary.
        }
        CavernizeTrack rendered = external.GetRenderedTrack(target);
        Progressor progressor = new(rendered.Length, environment.Listener, taskEngine);
        var result = MatroskaExport.Run(this, environment, target, rendered, target.Path, output, format, layout, grade, verify,
            tool, taskEngine.UpdateStatus, progressor.Update);
        report = result.Report;
        FinishTask(target);
        if (result.AnalysisPath != null) {
            taskEngine.UpdateStatus($"Export finished. Analysis saved to {result.AnalysisPath}");
        }
    }

    /// <summary>
    /// Render the content and export it to a channel-based format.
    /// </summary>
    void RenderTask(CavernizeTrack target, AudioWriter writer, string finalName) {
        using ExternalConverterHandler external = CreateExternalHandler(target, 0);
        if (external.Failed) {
            writer?.Dispose();
            return;
        }

        taskEngine.Progress = 0;
        taskEngine.UpdateStatus(language["Start"]);
        var (renderTargetRef, verify, grade) = Dispatcher.Invoke(() => (RenderTarget, verifyPeakEvents.IsChecked, grading.IsChecked));
        CavernizeTrack renderedTrack = external.GetRenderedTrack(target);
        string source = target.AtmosSourcePath;
        bool exportAnalysis = source != null;
        bool integratedReport = target.Container == Container.Matroska && (grade || verify);
        PcmSamplePeakAnalysis peaks = null;
        try {
            RenderStats stats = WriteRender(target, renderedTrack, writer, renderTargetRef, verify, out peaks);
            report.Generate(stats, peaks);
        } catch {
            peaks ??= ChannelRender.CreateAnalysis(environment, target, renderedTrack, renderTargetRef, verify);
            peaks.Flag(PcmPeakIssue.Interrupted);
            report.GenerateInterrupted(peaks);
            if (exportAnalysis) {
                SavePeakAnalysis(source, peaks);
            }
            throw;
        }

        if (exportAnalysis) {
            SavePeakAnalysis(source, peaks);
        }

        string targetCodec = null;
        audio.Dispatcher.Invoke(() => targetCodec = ((ExportFormat)audio.SelectedItem).FFName);

        if (writer is RIFFWaveWriter && finalName[^4..] != waveExtension) {
            taskEngine.UpdateStatus("Merging to final container...");
            string exportedAudio = finalName[..^4] + waveExtension;
            MergeToContainer merger = new(LoadedFile.Path, exportedAudio, targetCodec);
            merger.AddArguments(RenderingSettings.MergeArguments);
            merger.SetTrackName($"Cavern {renderTargetRef.Name} render");
            if (writer.ChannelCount > 8) {
                merger.Allow8PlusChannels();
            }
            merger.MakeSafe(finalName);
            if (!merger.Merge(ffmpeg, finalName)) {
                taskEngine.UpdateStatus("Failed to create the final file. Are your permissions sufficient in the export folder?");
                return;
            }
        }

        string reportPath = null;
        if (integratedReport) {
            if (PcmPeakExport.Status(peaks) == "incomplete" || (verify && peaks.Diagnostics == null)) {
                throw new IOException("The PCM report or requested Atmos verification was incomplete.");
            }
            IntegratedAnalysisReport analysis = new(target.Path, target);
            analysis.Add(this, peaks, report, null, grade, targetCodec);
            reportPath = analysis.Publish();
        }
        FinishTask(target);
        if (reportPath != null) {
            taskEngine.UpdateStatus($"Export finished. Analysis saved to {reportPath}");
        }
    }

    void SavePeakAnalysis(string source, PcmSamplePeakAnalysis peaks) {
        void Save(Func<string> write) {
            try {
                write();
            } catch (Exception e) {
                Dispatcher.Invoke(() => Error($"Could not save peak analysis beside {source}.\n{e.Message}"));
            }
        }

        Save(() => PcmPeakExport.SaveCsv(source, peaks, report.Quality));
        if (peaks.Diagnostics != null) {
            Save(() => PcmPeakExport.SaveEvidence(source, peaks));
        }
    }

    /// <summary>
    /// Decode the source and export it to an object-based format.
    /// </summary>
    void TranscodeTask(CavernizeTrack target, EnvironmentWriter writer) {
        if (writer is DolbyAtmosBWFWriter bwfWriter) {
            bwfWriter.ExtendWithMuteTarget();
        }
        ExternalConverterHandler external = CreateExternalHandler(target, writer is DolbyAtmosBWFWriter ? 10 : 0);
        if (external.Failed) {
            return;
        }

        taskEngine.Progress = 0;
        taskEngine.UpdateStatus(language["Start"]);

        RenderStats stats;
        if (writer is BroadcastWaveFormatWriter bwf) {
            stats = WriteTranscode(target, bwf);
        } else {
            stats = WriteTranscode(target, writer);
        }
        report.Generate(stats, null, AtmosSourceInventory.Create(external.GetRenderedTrack(target).Renderer, external.ExternalConverterNeeded));
        external.Dispose();
        FinishTask(target);
    }

    /// <summary>
    /// Operations to perform after a conversion was successful.
    /// </summary>
    void FinishTask(CavernizeTrack target) {
        taskEngine.UpdateStatus(language["ExpOk"]);
        taskEngine.Progress = 1;

        if (Program.ConsoleMode) {
            Dispatcher.Invoke(Close);
        }
    }

    /// <summary>
    /// RIFF Wave file extension.
    /// </summary>
    const string waveExtension = ".wav";
}
