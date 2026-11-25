using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cavernize.Logic.Rendering;

namespace Cavernize.Logic.Models;

internal static class PcmPeakExport {
    static readonly JsonSerializerOptions jsonOptions = new() {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    static readonly string[] columns = [
        "row_type", "analysis_id", "source_file", "speaker_configuration", "layout_id", "sample_rate_hz",
        "expected_frames", "pre_frames", "post_frames", "program_duration_seconds", "analyzed_duration_seconds", "status", "verification", "pre_issues", "post_issues",
        "slot", "speaker",
        "pre_slot", "pre_abs_linear", "pre_dbfs", "pre_frame", "pre_seconds", "pre_signed_sample", "pre_float_bits",
        "pre_same_sample_after_limiter", "pre_block_gain", "pre_block_maximum", "pre_block_attack",
        "post_slot", "post_abs_linear", "post_dbfs", "post_frame", "post_seconds", "post_signed_sample", "post_float_bits",
        "post_block_gain", "post_block_maximum", "post_block_attack",
        "quality_metric", "quality_db", "quality_grade",
        "pre_rms_linear", "pre_rms_dbfs", "post_rms_linear", "post_rms_dbfs",
        "pre_active_gt_minus60_percent", "pre_active_gt_minus40_percent",
        "post_active_gt_minus60_percent", "post_active_gt_minus40_percent",
        "activity_window_ms", "activity_threshold_60_dbfs", "activity_threshold_40_dbfs", "listener_gain_mode", "listener_gain_requested_db",
        "listener_automatic_safe_gain", "listener_gain_effective_linear", "listener_gain_effective_db"
    ];

    static readonly int gainColumn = Array.IndexOf(columns, "listener_gain_mode");
    static readonly int qualityColumn = Array.IndexOf(columns, "quality_metric");

    internal static string SafeComponent(string text) {
        StringBuilder name = new();
        foreach (char letter in text) {
            if (char.IsLetterOrDigit(letter) || letter == '.') {
                name.Append(letter);
            } else if (name.Length != 0 && name[^1] != '-') {
                name.Append('-');
            }
        }
        string result = name.ToString().Trim('.', '-');
        if (result.Length == 0) {
            return "layout-" + ShortHash(text);
        }
        return result.Length > 70 ? result[..70].TrimEnd('.', '-') + "-" + ShortHash(text) : result;
    }

    internal static string ShortHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    static string OutputBase(string source, PcmSamplePeakAnalysis analysis) {
        string stem = Path.GetFileNameWithoutExtension(source);
        string file = SafeComponent(stem);
        if (file != stem) {
            file += "-" + ShortHash(stem);
        }
        string status = Status(analysis);
        string suffix = status == "complete" ? "" : status == "incomplete" ? "-Incomplete" : "-SourceUnverified";
        string layout = analysis.LayoutId;
        if (analysis.ListenerGain?.RequestedDb is double requested) {
            string db = (requested == 0 ? 0d : requested).ToString("R", CultureInfo.InvariantCulture)
                .Replace("-", "m").Replace("+", "p");
            layout += "-ListenerGain-" + db + "dB";
        }
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source)), $"{file}-{layout}-PeakAnalysis{suffix}");
    }

    internal static string Status(PcmSamplePeakAnalysis analysis) =>
        !ChannelRender.HasCompleteFrames(analysis) || analysis.Diagnostics?.Matches(analysis) == false ? "incomplete" : analysis.SourceVerified ? "complete" : "source_coverage_unverified";

    internal static string SaveCsv(string source, PcmSamplePeakAnalysis analysis, IReadOnlyList<PostRenderReport.QualityMetric> quality) {
        string path = OutputBase(source, analysis) + ".csv";
        WriteAtomic(path, stream => {
            using StreamWriter writer = new(stream, new UTF8Encoding(true), leaveOpen: true);
            WriteRow(writer, columns);
            int rate = analysis.SampleRate;
            object[] metadata = [analysis.AnalysisId, Path.GetFileName(source), analysis.LayoutName, analysis.LayoutId, rate,
                analysis.ExpectedFrames, analysis.PreLimiter.ObservedFrames, analysis.Processed.ObservedFrames,
                rate > 0 ? analysis.ExpectedFrames / (double)rate : null,
                rate > 0 ? Math.Min(analysis.PreLimiter.ObservedFrames, analysis.Processed.ObservedFrames) / (double)rate : null,
                Status(analysis), analysis.Diagnostics == null ? "disabled" : analysis.Diagnostics.Matches(analysis) ? "matched" : "mismatch",
                analysis.PreLimiter.Issues, analysis.Processed.Issues];
            ListenerGainInfo? gain = analysis.ListenerGain;
            object[] gainFields = [gain?.Mode, gain?.RequestedDb, gain?.AutomaticSafeGain, gain?.Effective, gain?.EffectiveDb];
            void PeakRow(string kind, int? slot, string label, int preSlot, int postSlot) {
                PcmSignalLevel pre = slot.HasValue ? analysis.PreLimiter.ChannelLevel(slot.Value - 1) : analysis.PreLimiter.OverallLevel();
                PcmSignalLevel post = slot.HasValue ? analysis.Processed.ChannelLevel(slot.Value - 1) : analysis.Processed.OverallLevel();
                WriteRow(writer, [
                    kind, .. metadata, slot, label,
                    .. EventFields(analysis.PreLimiter, preSlot, rate, true),
                    .. EventFields(analysis.Processed, postSlot, rate, false),
                    null, null, null,
                    pre.Rms, pre.RmsDbfs, post.Rms, post.RmsDbfs,
                    pre.Active60, pre.Active40, post.Active60, post.Active40,
                    PcmSamplePeakAccumulator.ActivityWindowMs, PcmSamplePeakAccumulator.ActivityThreshold60,
                    PcmSamplePeakAccumulator.ActivityThreshold40, .. gainFields
                ]);
            }
            for (int slot = 0; slot < analysis.Labels.Length; slot++) {
                PeakRow("channel", slot + 1, analysis.Labels[slot], slot, slot);
            }
            PeakRow("overall", null, null, MaximumSlot(analysis.PreLimiter), MaximumSlot(analysis.Processed));
            foreach (PostRenderReport.QualityMetric metric in quality) {
                object[] row = new object[columns.Length];
                row[0] = "quality";
                metadata.CopyTo(row, 1);
                row[qualityColumn] = metric.Name;
                row[qualityColumn + 1] = metric.Db;
                row[qualityColumn + 2] = metric.Grade;
                gainFields.CopyTo(row, gainColumn);
                WriteRow(writer, row);
            }
        });
        return path;
    }

    internal static string SaveEvidence(string source, PcmSamplePeakAnalysis analysis) {
        string path = OutputBase(source, analysis) + "-Verification.json";
        object evidence = new {
            analysis.AnalysisId, SourceFile = Path.GetFileName(source), analysis.LayoutName, analysis.LayoutId, analysis.SampleRate,
            Status = Status(analysis), analysis.ExpectedFrames, AnalyzedFrames = analysis.PreLimiter.ObservedFrames,
            Evidence = analysis.Diagnostics.Evidence(analysis)
        };
        WriteAtomic(path, stream => JsonSerializer.Serialize(stream, evidence, jsonOptions));
        return path;
    }

    static int MaximumSlot(PcmSamplePeakAccumulator meter) {
        int best = -1;
        for (int i = 0; i < meter.Peaks.Length; i++) {
            float peak = meter.Peaks[i];
            if (peak >= 0 && (best < 0 || peak > meter.Peaks[best] ||
                (peak == meter.Peaks[best] && meter.GetEvent(i)!.Value.Frame < meter.GetEvent(best)!.Value.Frame))) {
                best = i;
            }
        }
        return best;
    }

    static object[] EventFields(PcmSamplePeakAccumulator meter, int slot, int rate, bool pre) {
        if (slot < 0 || meter.GetEvent(slot) is not PcmPeakEvent peak) {
            return new object[pre ? 11 : 10];
        }
        object[] common = [slot + 1, meter.Peaks[slot], peak.Dbfs, peak.Frame,
            rate > 0 ? peak.Frame / (double)rate : null, peak.Sample, PcmPeakDiagnostics.Bits(peak.Sample)];
        object[] limiter = [peak.Limiter?.Gain, peak.Limiter?.Maximum, peak.Limiter?.Attacked];
        return pre ? [.. common, peak.PostLimiterSample, .. limiter] : [.. common, .. limiter];
    }

    static void WriteRow(TextWriter writer, IEnumerable<object> fields) {
        writer.WriteLine(string.Join(",", fields.Select(field => {
            string value = Convert.ToString(field, CultureInfo.InvariantCulture) ?? "";
            return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        })));
    }

    internal static void WriteAtomic(string path, Action<Stream> write) {
        string temporary = Path.Combine(Path.GetDirectoryName(path), "." + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            // The temporary file is on the same filesystem as the destination.
            File.Move(temporary, path, overwrite: true);
        } finally {
            if (File.Exists(temporary)) {
                File.Delete(temporary);
            }
        }
    }
}
