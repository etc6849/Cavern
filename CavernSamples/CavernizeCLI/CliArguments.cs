using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Cavern.Utilities;

using Cavernize.Logic.CommandLine;
using Cavernize.Logic.CommandLine.BaseClasses;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;

namespace CavernizeCLI;

/// <summary>
/// Resolves application options ahead of source loading and defers all rendering until configuration is complete.
/// </summary>
sealed class CliArguments {
    readonly List<(Command Command, int Offset)> settings = new();
    readonly List<(Command Command, int Offset)> inspections = new();
    string[] args;
    readonly List<string> inputs = new();
    readonly List<string> inputLists = new();
    readonly List<RenderTarget> layouts = new();
    string output, workRoot, trueHdd, ffmpeg;
    int? trackIndex;
    double? gain;
    bool reportOnly, quality, verify, list, help;
    bool legacyGain, surroundSwap;
    bool keepEvidence, legacyReports;

    internal static CliArguments Parse(string[] args) {
        CliArguments result = new() { args = args };
        for (int i = 0; i < args.Length;) {
            string argument = args[i++];
            if (i == 1 && !argument.StartsWith('-')) {
                result.inputs.Add(argument);
                continue;
            }

            string ReadValue() {
                if (i == args.Length || args[i].StartsWith("--")) {
                    throw new CommandException($"Missing value for {argument}.");
                }
                return args[i++];
            }

            switch (argument) {
                case "--input": case "-input": case "-i":
                    result.inputs.Add(ReadValue());
                    break;
                case "--input-list": result.inputLists.Add(ReadValue()); break;
                case "--layout": case "-target": case "-t":
                    result.layouts.Add(FindLayout(ReadValue()));
                    break;
                case "--track": case "-track": case "-trk":
                    if (result.trackIndex.HasValue || !int.TryParse(ReadValue(), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) {
                        throw new CommandException("Specify one non-negative audio track index.");
                    }
                    result.trackIndex = index;
                    break;
                case "--work-dir": result.workRoot = ReadValue(); break;
                case "--truehdd": result.trueHdd = ReadValue(); break;
                case "--ffmpeg": result.ffmpeg = ReadValue(); break;
                case "--keep-verification-json": result.keepEvidence = true; break;
                case "--legacy-report-files": result.legacyReports = true; break;
                case "--output": case "-output": case "-o":
                    if (result.output != null) {
                        throw new CommandException("Specify the output only once.");
                    }
                    result.output = ReadValue();
                    break;
                case "--report-only": result.reportOnly = true; break;
                case "--quality": result.quality = true; break;
                case "--verify-peak-events": result.verify = true; break;
                case "--surround-swap": result.surroundSwap = true; break;
                case "--list-layouts": result.list = true; break;
                case "--help": case "-help": case "-h": result.help = true; break;
                case "--listener-gain-db":
                    if (result.gain.HasValue) {
                        throw new CommandException("Specify the absolute listener gain only once.");
                    }
                    string text = ReadValue();
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double db) ||
                        !double.IsFinite(db)) {
                        throw new CommandException($"Invalid listener gain '{text}'. Use a finite number of decibels.");
                    }
                    try {
                        ConversionEnvironment.ToListenerGain(db);
                    } catch (ArgumentOutOfRangeException e) {
                        throw new CommandException(e.Message);
                    }
                    result.gain = db;
                    break;
                default:
                    Command command = Command.GetCommandByArgument(argument) ??
                        throw new CommandException($"Unknown argument '{argument}'. Use --help.");
                    int offset = i;
                    for (int parameter = 0; parameter < command.Parameters; parameter++) {
                        ReadValue();
                    }
                    if (argument == "--render-gain") {
                        if (!QMath.TryParseFloat(args[offset], out float extra) || !float.IsFinite(extra) ||
                            !float.IsFinite(QMath.DbToGain(extra))) {
                            throw new CommandException("Additional render gain must be finite.");
                        }
                        result.legacyGain = true;
                    }
                    if (command is TrackCommand or TracksCommand || command.Name == "--extract") {
                        result.inspections.Add((command, offset));
                    } else {
                        result.settings.Add((command, offset));
                    }
                    break;
            }
        }

        if (result.gain.HasValue && result.legacyGain) {
            throw new CommandException("--listener-gain-db and --render-gain cannot be used together.");
        }
        if (result.reportOnly && result.output != null) {
            throw new CommandException("--report-only cannot be combined with an output WAV.");
        }
        if (!result.help && !result.list && result.inputs.Count == 0 && result.inputLists.Count == 0) {
            throw new CommandException("Specify an input file with --input. Use --help for syntax.");
        }
        if (!result.reportOnly && (result.layouts.Count > 1 || result.inputs.Count > 1 || result.inputLists.Count != 0)) {
            throw new CommandException("Multiple sources and layouts require --report-only.");
        }
        if (result.keepEvidence && !result.verify) {
            throw new CommandException("--keep-verification-json requires --verify-peak-events.");
        }
        if (result.legacyReports && (!result.reportOnly || result.layouts.Count > 1)) {
            throw new CommandException("--legacy-report-files requires Report Only with one layout.");
        }
        if ((result.quality || result.verify || result.gain.HasValue) && !result.reportOnly && result.output == null) {
            throw new CommandException("Specify --report-only or an output WAV to perform analysis.");
        }
        return result;
    }

    internal static RenderTarget FindLayout(string requested) {
        RenderTarget exact = Array.Find(RenderTarget.Targets, x => x.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (exact != null) {
            return exact;
        }

        RenderTarget[] aliases = RenderTarget.Targets.Where(x => {
            string prefix = x.Name.Split(' ')[0];
            return requested.Equals(prefix, StringComparison.OrdinalIgnoreCase) || requested == prefix.Replace(".", "");
        }).ToArray();
        return aliases.Length switch {
            1 => aliases[0],
            0 => throw new CommandException($"Unknown layout '{requested}'. Use --list-layouts."),
            _ => throw new CommandException($"Ambiguous layout '{requested}'. Choose one of: {string.Join(", ", aliases.Select(x => x.Name))}.")
        };
    }

    internal bool PrintInformation() {
        if (help) {
            Console.WriteLine("""
                CavernizeCLI --input <source.mkv|mka|atmos> --layout "<layout>" --report-only [--quality] [--verify-peak-events]
                  --input, -input, -i      Input file (repeat for batch analysis).
                  --input-list FILE       UTF-8 file with one source per line.
                  --track, -track, -trk   Zero-based audio track index; required for ambiguous TrueHD.
                  --layout, -target, -t   Exact layout (repeat for combined CSV). Default: 5.1.2 side.
                  --report-only           Save analysis without exporting media.
                  --work-dir PATH         Local SSD workspace for analysis only.
                  --truehdd FILE          Path to a TrueHDD executable; otherwise find an existing install.
                  --ffmpeg FILE           Path to FFmpeg for media export.
                  --quality               Include legacy quality measurements and grades.
                  --verify-peak-events    Independently verify Atmos peak events.
                  --keep-verification-json Retain full verification evidence.
                  --listener-gain-db DB   Absolute final listener gain. Default: automatic.
                  --render-gain DB        Additional gain applied with automatic safety gain.
                  --surround-swap         Enable the existing surround remapping (-ss true).
                  --output, -output, -o   Export rendered media instead of Report Only.
                  --legacy-report-files   Use V3 report filenames for direct Atmos regression.
                  --list-layouts          List the exact names accepted by --layout.
                  --help, -help, -h       Show this help.
                Exit codes: 0 success, 1 input/render/verification/export failure, 2 invalid arguments.
                """);
        }
        if (list) {
            foreach (RenderTarget target in RenderTarget.Targets) {
                Console.WriteLine(target.Name);
            }
        }
        return help || list;
    }

    internal void Apply(Program app) {
        app.RenderTarget = layouts.FirstOrDefault() ?? app.RenderTarget;
        app.Quality = quality;
        app.Verification = verify;
        app.KeepEvidence = keepEvidence;
        app.TrueHddPath = trueHdd;
        app.FFmpegPath = ffmpeg;
        app.RenderingSettings.ListenerGainDbOverride = gain;
        app.RenderingSettings.SurroundSwap = surroundSwap;
        foreach ((Command command, int offset) in settings) {
            command.Execute(args, offset, app);
        }
        foreach (string listPath in inputLists) {
            string folder = Path.GetDirectoryName(Path.GetFullPath(listPath));
            foreach (string entry in File.ReadLines(listPath)) {
                if (!string.IsNullOrWhiteSpace(entry)) {
                    inputs.Add(Path.GetFullPath(entry, folder));
                }
            }
        }
        if (inputs.Count == 0) {
            throw new CommandException("The input list has no sources.");
        }
        foreach (string path in inputs) {
            try {
                ProcessSource(app, path);
            } catch (Exception error) when (inputs.Count > 1) {
                Console.Error.WriteLine($"{path}: {error.Message}");
                app.SetFailure();
            }
        }
    }

    void ProcessSource(Program app, string input) {
        string fullPath = Path.GetFullPath(input);
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException($"Input file does not exist: {fullPath}");
        }
        app.OpenContent(fullPath);
        if (trackIndex.HasValue || reportOnly || output != null) {
            try {
                app.SelectedTrack = IntegratedAnalysis.SelectTrack(app.LoadedFile, trackIndex);
            } catch (ArgumentException e) {
                throw new CommandException(e.Message);
            }
        }
        try {
            foreach ((Command command, int offset) in inspections) {
                command.Execute(args, offset, app);
            }
        } catch (CommandProcessingCanceledException) {
            return;
        }
        if (reportOnly && !legacyReports) {
            app.Analyze(new(layouts.Count == 0 ? [app.RenderTarget] : layouts.Distinct().ToArray(),
                quality, verify, keepEvidence, workRoot));
        } else if (reportOnly || output != null) {
            app.RenderContent(output);
        }
    }
}
