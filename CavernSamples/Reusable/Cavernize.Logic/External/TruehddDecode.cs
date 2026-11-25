using System.Globalization;
using System.Text.Json;

using Cavern.Format.Renderers;
using Cavernize.Logic.Rendering;

namespace Cavernize.Logic.External;

/// <summary>Runs TrueHDD with on-disk diagnostics and checks integrity before admitting decoded audio.</summary>
internal static class TruehddDecode {
    internal readonly record struct Result(string Version, long Samples, int SampleRate);
    readonly record struct Diagnostics(int IntegrityErrors, string FirstError);

    internal static Result Run(string executable, string workingDirectory, string input, string stem, string summary, string log) {
        List<string> arguments = ["decode", "--json", "--log-format", "json", input, "--output-path", stem];
        if (MeridianLosslessPackingRenderer.ForcedPresentation is int presentation) {
            arguments.AddRange(["--presentation", presentation.ToString(CultureInfo.InvariantCulture)]);
        }

        try {
            MediaTool.Run(executable, workingDirectory, arguments, summary, log).GetAwaiter().GetResult();
        } catch (Exception error) {
            Diagnostics diagnostics = ReadDiagnostics(log);
            throw Failure($"TrueHDD did not complete successfully. {error.Message}", diagnostics, summary, log, error);
        }
        return CheckResult(summary, log);
    }

    internal static Result CheckResult(string summary, string log) {
        Diagnostics diagnostics = ReadDiagnostics(log);
        string version;
        long skipped, concealed, invalidBranches, failedProtection, samples, frames;
        int rate;
        try {
            using FileStream input = File.OpenRead(summary);
            using JsonDocument json = JsonDocument.Parse(input);
            JsonElement root = json.RootElement;
            version = root.GetProperty("version").GetString();
            frames = root.GetProperty("frames").GetInt64();
            samples = root.GetProperty("samples").GetInt64();
            rate = root.GetProperty("sampleRate").GetInt32();
            skipped = root.GetProperty("skippedFrames").GetInt64();
            concealed = root.GetProperty("concealedFrames").GetInt64();
            invalidBranches = root.GetProperty("invalidBranches").GetInt64();
            failedProtection = root.GetProperty("evoFailed").GetInt64();
        } catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or KeyNotFoundException or FormatException) {
            throw Failure("TrueHDD did not write a usable decode summary. Use the bundled TrueHDD 0.6.3 or a compatible version.",
                diagnostics, summary, log, error);
        }

        if (diagnostics.IntegrityErrors != 0 || skipped != 0 || concealed != 0 || invalidBranches != 0 || failedProtection != 0) {
            throw Failure($"TrueHDD rejected the decode ({skipped} skipped frames, {concealed} concealed frames, " +
                $"{invalidBranches} invalid branches, {failedProtection} failed protection checks).", diagnostics, summary, log);
        }
        if (frames <= 0 || samples <= 0 || rate <= 0 || string.IsNullOrWhiteSpace(version)) {
            throw Failure("TrueHDD did not report a complete audio payload.", diagnostics, summary, log);
        }
        return new(version, samples, rate);
    }

    static Diagnostics ReadDiagnostics(string log) {
        int errors = 0;
        string first = null;
        if (!File.Exists(log)) {
            return default;
        }
        foreach (string line in File.ReadLines(log)) {
            if (line.Length == 0 || line[0] != '{') {
                continue;
            }
            try {
                using JsonDocument json = JsonDocument.Parse(line);
                if (!json.RootElement.TryGetProperty("lvl", out JsonElement level) || level.ValueKind != JsonValueKind.String ||
                    !json.RootElement.TryGetProperty("msg", out JsonElement message) || message.ValueKind != JsonValueKind.String) {
                    continue;
                }
                string severity = level.GetString(), text = message.GetString();
                if (severity == "ERROR" || severity == "WARN" && IsIntegrityWarning(text)) {
                    errors++;
                    first ??= text;
                }
            } catch (JsonException) {
                // A non-JSON diagnostic is still retained verbatim in the log.
            }
        }
        return new(errors, first);
    }

    static bool IsIntegrityWarning(string message) =>
        message.Contains("parity check failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("lossless_check failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("checksum mismatch", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("CRC check failed", StringComparison.OrdinalIgnoreCase);

    static IOException Failure(string reason, Diagnostics diagnostics, string summary, string log, Exception error = null) {
        if (diagnostics.IntegrityErrors != 0) {
            string first = diagnostics.FirstError.Replace('\r', ' ').Replace('\n', ' ');
            if (first.Length > 180) {
                first = first[..180] + "…";
            }
            reason += $" Decoder errors or integrity warnings: {diagnostics.IntegrityErrors}. First: {first}";
        }
        return new IOException($"{reason}{Environment.NewLine}Decoder log: {log}{Environment.NewLine}Decode summary: {summary}", error);
    }
}
