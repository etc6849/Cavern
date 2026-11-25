using Cavern.Format.Common;
using Cavern.Utilities;
using Cavern;
using Cavernize.Logic.External;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

namespace Cavernize.Logic.Rendering;

/// <summary>The analysis-only operation. Media exports use their original conversion path.</summary>
internal static class IntegratedAnalysis {
    internal sealed record Options(IReadOnlyList<RenderTarget> Layouts, bool Quality, bool Verify, bool KeepEvidence, string WorkRoot);
    internal sealed record Result(string Path, string EvidenceDirectory, PostRenderReport LastReport);

    internal static bool Supports(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mka" or ".atmos";

    internal static CavernizeTrack SelectTrack(AudioFile file, int? trackIndex) {
        if (trackIndex is int index) {
            if (index < 0 || index >= file.Tracks.Count || !file.Tracks[index].Supported) {
                throw new ArgumentException($"Audio track index {index} is out of range or unsupported.");
            }
            return file.Tracks[index];
        }
        if (Path.GetExtension(file.Path).ToLowerInvariant() is ".mkv" or ".mka") {
            CavernizeTrack[] trueHd = file.Tracks.Where(x => x.Supported && x.Codec == Codec.TrueHD).ToArray();
            if (trueHd.Length > 1) {
                throw new ArgumentException("Multiple TrueHD audio tracks were found. Select an explicit audio track index (" +
                    string.Join(", ", trueHd.Select(x => x.Index)) + ").");
            }
            if (trueHd.Length == 1) {
                return trueHd[0];
            }
        }
        return file.Tracks.FirstOrDefault(x => x.Supported) ?? throw new IOException("No supported audio track was found.");
    }

    internal static Result Run(ICavernizeApp app, string original, CavernizeTrack selected, Options options,
        Func<string> decoderExecutable, Action<string> status, Action<double> progress, Action<PostRenderReport> reportReady = null) {
        bool decode = selected.Codec == Codec.TrueHD;
        AnalysisWorkspace workspace = new(options.WorkRoot, selected.Length, new FileInfo(original).Length, decode);
        string decoderVersion = null;
        CavernizeTrack decoded = null;
        Channel[] previousChannels = Listener.Channels;
        bool previousVirtualizer = Listener.HeadphoneVirtualizer;
        PostRenderReport report = null;
        try {
            IntegratedAnalysisReport aggregate = new(original, selected);
            if (decode) {
                (decoded, decoderVersion) = Truehdd.DecodeAnalysis(selected, workspace, decoderExecutable(), status);
            }
            CavernizeTrack target = decoded ?? selected;
            if (options.Verify && target.AtmosSourcePath == null) {
                throw new IOException("Peak-event verification requires an Atmos presentation. The selected track decoded to channel PCM.");
            }
            for (int i = 0; i < options.Layouts.Count; i++) {
                RenderTarget layout = options.Layouts[i];
                status($"Analyzing {layout.Name} ({i + 1}/{options.Layouts.Count})...");
                ConversionEnvironment environment = new(app);
                environment.AttachToListener(target, layout, int.MaxValue);
                PcmSamplePeakAnalysis peaks = null;
                report = new(environment.Listener);
                try {
                    long rendered = 0;
                    int lastPercent = -10;
                    void Update() {
                        rendered += environment.Listener.UpdateRate;
                        double ratio = Math.Min(1d, rendered / (double)target.Length);
                        int percent = (int)(ratio * 100);
                        if (percent >= lastPercent + 10 || percent == 100) {
                            progress((i + ratio) / options.Layouts.Count);
                            lastPercent = percent;
                        }
                    }
                    RenderStats stats = ChannelRender.Run(environment, target, target, null, layout, app.RenderingSettings,
                        options.Quality, options.Verify, app.RenderingSettings.SpeakerVirtualizer, 0, Update,
                        (error, frame) => status($"Render error at frame {frame}: {error.Message}"), out peaks, derived: decode);
                    report.Generate(stats, peaks);
                    reportReady?.Invoke(report);
                    if (peaks.Diagnostics != null) {
                        SaveEvidence(original, workspace, peaks);
                    }
                    string coverage = PcmPeakExport.Status(peaks);
                    if (coverage == "incomplete" || (!decode && selected.AtmosSourcePath != null && coverage != "complete")) {
                        throw new IOException($"Analysis of {layout.Name} ended with status {coverage}.");
                    }
                    aggregate.Add(app, peaks, report, decoderVersion, options.Quality);
                } catch {
                    if (peaks?.Diagnostics != null && !File.Exists(EvidencePath(workspace, peaks))) {
                        peaks.Flag(PcmPeakIssue.Interrupted);
                        SaveEvidence(original, workspace, peaks);
                    }
                    throw;
                } finally {
                    environment.Reset();
                }
            }
            status("Saving the completed analysis CSV...");
            string published = aggregate.Publish();
            decoded?.Dispose();
            decoded = null;
            workspace.Finish(options.KeepEvidence);
            status($"Analysis saved to {published}");
            return new(published, Directory.Exists(workspace.DirectoryPath) ? workspace.DirectoryPath : null, report);
        } catch (Exception error) {
            File.WriteAllText(workspace.FilePath("failure.log"), error.ToString());
            throw new IOException($"{error.Message}{Environment.NewLine}Analysis diagnostics: {workspace.DirectoryPath}", error);
        } finally {
            decoded?.Dispose();
            Listener.ReplaceChannels(previousChannels);
            Listener.HeadphoneVirtualizer = previousVirtualizer;
        }
    }

    static string EvidencePath(AnalysisWorkspace workspace, PcmSamplePeakAnalysis peaks) =>
        workspace.FilePath($"{peaks.LayoutId}-{peaks.AnalysisId}-Verification.json");

    static void SaveEvidence(string original, AnalysisWorkspace workspace, PcmSamplePeakAnalysis peaks) =>
        PcmPeakExport.SaveEvidence(original, peaks, EvidencePath(workspace, peaks));
}
