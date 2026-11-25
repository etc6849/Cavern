using System;
using System.IO;

using Cavern.CavernSettings;
using Cavern.Format;
using Cavern.Format.Common;
using Cavern.Utilities;

using Cavernize.Logic.CavernSettings;
using Cavernize.Logic.CommandLine;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;

namespace CavernizeCLI;

/// <summary>
/// Headless Cavernize application.
/// </summary>
public class Program : ICavernizeApp {
    public bool Rendering { get; private set; }
    public AudioFile LoadedFile { get; private set; }
    public CavernizeTrack SelectedTrack { get; set; }
    public ExportFormat ExportFormat { get; set; } = new(Codec.PCM_LE, null, short.MaxValue, null);
    public RenderTarget RenderTarget { get; set; } = CliArguments.FindLayout("5.1.2 side");
    public UpmixingSettings UpmixingSettings { get; } = new(true);
    public RenderingSettings RenderingSettings { get; } = new();

    readonly ConversionEnvironment environment;
    internal bool Quality { get; set; }
    internal bool Verification { get; set; }
    internal int ExitCode { get; private set; }

    public Program() => environment = new(this);

    public static int Main(string[] args) {
        Program app = null;
        try {
            CliArguments options = CliArguments.Parse(args);
            if (options.PrintInformation()) {
                return 0;
            }
            app = new();
            options.Apply(app);
            return app.ExitCode;
        } catch (CommandException e) {
            Console.Error.WriteLine(e.Message);
            return 2;
        } catch (Exception e) {
            Console.Error.WriteLine(e.Message);
            return 1;
        } finally {
            app?.Reset();
        }
    }

    public void OpenContent(string path) => OpenContent(new AudioFile(path));

    public void OpenContent(AudioFile file) {
        if (file.Tracks.Count == 0) {
            file.Dispose();
            throw new CommandException("No supported audio tracks were found in the file.");
        }
        Reset();
        LoadedFile = file;
        SelectedTrack = file.Tracks[0];
        Console.WriteLine($"Opened {Path.GetFileNameWithoutExtension(file.Path)} containing {SelectedTrack}.");
    }

    public Action GetRenderTask(string path) {
        CavernizeTrack target = SelectedTrack ?? throw new CommandException("Select an input before rendering.");
        if (!target.Supported || target.Codec == Codec.TrueHD) {
            throw new CommandException("The selected track cannot be rendered headlessly. For TrueHD Atmos, supply the decoded .atmos master.");
        }
        if ((Verification || path == null) && target.AtmosSourcePath == null) {
            throw new CommandException("Headless Report Only and peak verification require a Dolby Atmos master (.atmos).");
        }
        if (path != null) {
            path = Path.GetFullPath(path);
            StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(path, Path.GetFullPath(LoadedFile.Path), pathComparison)) {
                throw new CommandException("Output cannot overwrite the input file.");
            }
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || ExportFormat.Codec is not (Codec.PCM_LE or Codec.PCM_Float)) {
                throw new CommandException("Headless audio export supports PCM WAV. Use --report-only for analysis without audio output.");
            }
        }
        return () => RenderTrack(target, path);
    }

    void RenderTrack(CavernizeTrack target, string output) {
        Rendering = true;
        try {
            environment.AttachToListener(target);
            PcmSamplePeakAnalysis peaks = null;
            PostRenderReport report = new(environment.Listener);
            AudioWriter writer = null;
            try {
                if (output != null) {
                    BitDepth bits = ExportFormat.Codec == Codec.PCM_Float ? BitDepth.Float32 :
                        RenderingSettings.Force24Bit ? BitDepth.Int24 : BitDepth.Int16;
                    writer = CavernizeOutput.CreateRenderOutput(this, output, environment, target, ExportFormat.Codec, bits) ??
                        throw new IOException("Could not create the WAV writer.");
                }

                long rendered = 0;
                int lastPercent = -10;
                Console.Error.WriteLine("Rendering...");
                void Progress() {
                    rendered += environment.Listener.UpdateRate;
                    int percent = (int)(100d * Math.Min(rendered, target.Length) / target.Length);
                    if (percent >= lastPercent + 10 || percent == 100) {
                        Console.Error.WriteLine($"{percent}%");
                        lastPercent = percent;
                    }
                }

                // One block per WAV write also flushes an update-aligned tail.
                int cacheSize = environment.Listener.UpdateRate * RenderTarget.OutputChannels;
                RenderStats stats = ChannelRender.Run(environment, target, target, writer, RenderTarget, RenderingSettings,
                    Quality, Verification, RenderingSettings.SpeakerVirtualizer, cacheSize, Progress,
                    (error, frame) => Console.Error.WriteLine($"Render error at frame {frame}: {error.Message}"), out peaks);
                writer = null; // ChannelRender completed and closed the writer.
                report.Generate(stats, peaks);
            } catch {
                peaks ??= ChannelRender.CreateAnalysis(environment, target, target, RenderTarget, Verification);
                peaks.Flag(PcmPeakIssue.Interrupted);
                report.GenerateInterrupted(peaks);
                if (target.AtmosSourcePath != null) {
                    SaveReports(target.AtmosSourcePath, peaks, report);
                }
                throw;
            } finally {
                writer?.Dispose();
            }

            Console.WriteLine(report.Report);
            if (target.AtmosSourcePath != null) {
                SaveReports(target.AtmosSourcePath, peaks, report);
                string status = PcmPeakExport.Status(peaks);
                if (status != "complete") {
                    Console.Error.WriteLine($"Analysis status: {status}.");
                    ExitCode = 1;
                } else {
                    Console.Error.WriteLine("Analysis status: complete.");
                }
            } else if (!ChannelRender.HasCompleteFrames(peaks)) {
                Console.Error.WriteLine("Render status: incomplete.");
                ExitCode = 1;
            } else {
                Console.Error.WriteLine("Render status: complete (source coverage unverified).");
            }
        } finally {
            Rendering = false;
        }
    }

    static void SaveReports(string source, PcmSamplePeakAnalysis peaks, PostRenderReport report) {
        try {
            Console.WriteLine($"CSV: {PcmPeakExport.SaveCsv(source, peaks, report.Quality)}");
            if (peaks.Diagnostics != null) {
                Console.WriteLine($"JSON: {PcmPeakExport.SaveEvidence(source, peaks)}");
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new IOException($"Could not save analysis beside {source}. {e.Message}", e);
        }
    }

    public void RenderContent(string path) => GetRenderTask(path).Invoke();

    public void Reset() {
        environment.Reset();
        LoadedFile?.Dispose();
        LoadedFile = null;
        SelectedTrack = null;
    }
}
