using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Cavern.Utilities;

using Cavernize.Logic.CommandLine;
using Cavernize.Logic.CommandLine.BaseClasses;
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
    string input, output;
    RenderTarget layout;
    double? gain;
    bool reportOnly, quality, verify, list, help;
    bool legacyGain, surroundSwap;

    internal static CliArguments Parse(string[] args) {
        CliArguments result = new() { args = args };
        for (int i = 0; i < args.Length;) {
            string argument = args[i++];
            if (i == 1 && !argument.StartsWith('-')) {
                result.SetInput(argument);
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
                    result.SetInput(ReadValue());
                    break;
                case "--layout": case "-target": case "-t":
                    if (result.layout != null) {
                        throw new CommandException("Specify the speaker layout only once.");
                    }
                    result.layout = FindLayout(ReadValue());
                    break;
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
        if (!result.help && !result.list && result.input == null) {
            throw new CommandException("Specify an input file with --input. Use --help for syntax.");
        }
        if ((result.quality || result.verify || result.gain.HasValue) && !result.reportOnly && result.output == null) {
            throw new CommandException("Specify --report-only or an output WAV to perform analysis.");
        }
        return result;
    }

    void SetInput(string path) {
        if (input != null) {
            throw new CommandException("Specify the input only once.");
        }
        input = path;
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
                CavernizeCLI --input <master.atmos> --layout "<layout>" --report-only [--quality] [--verify-peak-events]
                  --input, -input, -i      Atmos master (or first positional file).
                  --layout, -target, -t   Exact speaker layout. Default: 5.1.2 side.
                  --report-only           Render and save analysis without audio export.
                  --quality               Include legacy quality measurements and grades.
                  --verify-peak-events    Independently verify and save peak evidence JSON.
                  --listener-gain-db DB   Absolute final listener gain. Default: automatic.
                  --render-gain DB        Additional gain applied with automatic safety gain.
                  --surround-swap         Enable the existing surround remapping (-ss true).
                  --output, -output, -o   Render to a PCM WAV instead of Report Only.
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
        app.RenderTarget = layout ?? app.RenderTarget;
        app.Quality = quality;
        app.Verification = verify;
        app.RenderingSettings.ListenerGainDbOverride = gain;
        app.RenderingSettings.SurroundSwap = surroundSwap;
        foreach ((Command command, int offset) in settings) {
            command.Execute(args, offset, app);
        }
        string fullPath = Path.GetFullPath(input);
        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException($"Input file does not exist: {fullPath}");
        }
        app.OpenContent(fullPath);
        try {
            foreach ((Command command, int offset) in inspections) {
                command.Execute(args, offset, app);
            }
        } catch (CommandProcessingCanceledException) {
            return;
        }

        if (reportOnly || output != null) {
            app.RenderContent(output);
        }
    }
}
