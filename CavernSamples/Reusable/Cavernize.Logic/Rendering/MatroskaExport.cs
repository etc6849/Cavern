using System.Globalization;

using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Format.Container;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

namespace Cavernize.Logic.Rendering;

/// <summary>Stages rendered audio beside the export, then copies the source's non-audio streams and container metadata.</summary>
internal static class MatroskaExport {
    internal sealed record Result(PostRenderReport Report, string AnalysisPath, string EvidencePath);

    internal static Result Run(ICavernizeApp app, ConversionEnvironment environment, CavernizeTrack original,
        CavernizeTrack target, string source, string destination, ExportFormat format, RenderTarget layout, bool grade,
        bool verify, string ffmpeg, Action<string> status, Action progress, bool keepEvidence = false) {
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) {
            throw new IOException($"Choose a new output path. The export cannot overwrite {destination}.");
        }
        if (verify && target.AtmosSourcePath == null) {
            throw new IOException("Peak verification requires a decoded Atmos presentation.");
        }
        string work = Path.Combine(Path.GetDirectoryName(destination), ".Cavernize-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try {
            Codec codec = format.Codec;
            bool native = codec is Codec.PCM_LE or Codec.PCM_Float;
            BitDepth bits = codec == Codec.PCM_Float ? BitDepth.Float32 :
                app.RenderingSettings.Force24Bit ? BitDepth.Int24 : BitDepth.Int16;
            int rate = environment.Listener.SampleRate, channels = layout.OutputChannels;
            int blockSize = CavernizeOutput.GetBlockSize(layout, environment);
            string render = Path.Combine(work, native ? "Render.mkv" : "Render.wav");
            AudioWriter writer = native ?
                new AudioWriterIntoContainer(render, [], codec, blockSize, channels, target.Length, rate, bits) :
                new RIFFWaveWriter(render, channels, target.Length, rate, bits);
            PcmSamplePeakAnalysis peaks;
            RenderStats stats;
            try {
                writer.WriteHeader();
                status(native ? "Rendering native Matroska PCM..." : "Rendering PCM for FFmpeg encoding...");
                stats = ChannelRender.Run(environment, target, target, writer, layout, app.RenderingSettings, grade,
                    verify, app.RenderingSettings.SpeakerVirtualizer, blockSize, progress,
                    (error, frame) => status($"Render error at frame {frame}: {error.Message}"), out peaks, derived: !ReferenceEquals(original, target));
                writer = null;
            } finally {
                writer?.Dispose();
            }
            string evidence = null;
            if (peaks.Diagnostics != null) {
                evidence = PcmPeakExport.SaveEvidence(source, peaks, Path.Combine(work, $"{peaks.AnalysisId}-Verification.json"));
            }
            if (PcmPeakExport.Status(peaks) == "incomplete") {
                throw new IOException("The render or PCM verification was incomplete.");
            }
            PostRenderReport report = new(environment.Listener);
            report.Generate(stats, peaks);

            string staged = Path.Combine(work, "Export.mkv");
            status("Copying video, subtitles, attachments and container metadata, and adding rendered audio...");
            List<string> args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-xerror", "-copyts", "-i", source];
            if (original.Track != null) {
                original.Track.Source.Seek(0);
                double offset = original.Track.GetNextBlockOffset();
                if (offset > 0) {
                    args.AddRange(["-itsoffset", offset.ToString("R", CultureInfo.InvariantCulture)]);
                }
            }
            string order = layout is VirtualizerRenderTarget ? "Front left,Front right" : string.Join(",", peaks.Labels.Take(channels));
            args.AddRange(["-i", render, "-map", "0:v?", "-map", "0:s?", "-map", "0:d?", "-map", "1:a:0",
                "-map", "0:t?", "-map_metadata", "0",
                "-map_chapters", "0", "-c", "copy", "-metadata:s:a:0", $"title=Cavern {layout.Name} render",
                "-metadata:s:a:0", $"language={original.Language ?? "und"}", "-metadata:s:a:0", $"CAVERNIZE_CHANNEL_ORDER={order}",
                "-disposition:a:0", "default"]);
            if (!native) {
                args.AddRange(["-c:a", format.FFName]);
            }
            foreach (var argument in app.RenderingSettings.MergeArguments) {
                args.Add(argument.key);
                if (!string.IsNullOrEmpty(argument.value)) {
                    args.Add(argument.value);
                }
            }
            args.Add(staged);
            MediaTool.Run(ffmpeg, work, args, Path.Combine(work, "ffmpeg.stdout.log"), Path.Combine(work, "ffmpeg.stderr.log"))
                .GetAwaiter().GetResult();
            if (!File.Exists(staged) || new FileInfo(staged).Length == 0) {
                throw new IOException("FFmpeg did not produce the staged Matroska export.");
            }
            File.Move(staged, destination);
            string analysisPath = null;
            if (grade || verify) {
                IntegratedAnalysisReport analysis = new(source, original);
                analysis.Add(app, peaks, report, null, grade, codec.ToString());
                analysisPath = analysis.Publish();
            }
            string publishedEvidence = null;
            if (keepEvidence && evidence != null) {
                publishedEvidence = destination + "-Verification.json";
                File.Move(evidence, publishedEvidence);
            }
            if (!RenderingSettings.KeepTempFiles) {
                Directory.Delete(work, recursive: true);
            }
            return new(report, analysisPath, publishedEvidence);
        } catch (Exception error) {
            throw new IOException($"{error.Message}{Environment.NewLine}Media export diagnostics: {work}", error);
        }
    }
}
