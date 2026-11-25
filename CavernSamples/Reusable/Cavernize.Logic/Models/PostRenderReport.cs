using System.Text;

using Cavern;
using Cavern.Channels;
using Cavern.Format.Renderers;
using Cavern.Utilities;

using Cavernize.Logic.Language;

namespace Cavernize.Logic.Models;

/// <summary>
/// Generates a report after a render has finished, scoring the audio in multiple stats.
/// </summary>
/// <param name="listener">The listener that played the content, where the objects are still attached</param>
public sealed class PostRenderReport(Listener listener) {
    /// <summary>
    /// Resulting post-render report from a <see cref="Generate"> call.
    /// </summary>
    public string Report {
        get => report ?? RenderReportStrings.Active["Defau"];
        private set => report = value;
    }
    string report;

    /// <summary>
    /// Give a grade to a metric. Starting from <paramref name="aPlus"/>, each grade has a range of <paramref name="stepsPerGrade"/>.
    /// </summary>
    /// <returns></returns>
    static int Grade(float value, float aPlus, float stepsPerGrade) {
        int grade = (int)((aPlus - value) / stepsPerGrade);
        if (grade < 0) {
            grade = 0;
        } else if (grade > 5) {
            grade = 5;
        }
        return grade;
    }

    internal sealed record QualityMetric(string Name, float? Db, string Grade);
    internal IReadOnlyList<QualityMetric> Quality { get; private set; } = Array.Empty<QualityMetric>();

    /// <summary>
    /// Extracts the <see cref="report"/> from a render to a text report.
    /// </summary>
    public void Generate(RenderStats stats) => Generate(stats, null);

    internal void Generate(RenderStats stats, PcmSamplePeakAnalysis peaks) {
        int total = listener.ActiveSources.Count;
        int dynamic = total - stats.GetSemiStaticCount() - stats.GetSuperStaticCount();

        List<ReferenceChannel> channels =
            [.. stats.GetStaticOrSemiStaticPositions().Select(x => Renderer.ChannelFromPosition(x / Listener.EnvironmentSize))];
        channels.Sort();

        int unused = total - dynamic - channels.Count;
        if (unused < 0) {
            dynamic += unused;
            unused = 0;
        }

        RenderReportStrings language = RenderReportStrings.Active;
        StringBuilder builder = new();
        List<QualityMetric> quality = new();
        Quality = quality;

        void ReportGrade(string metric, float decibelValue, int grade) {
            string gradeLabel = language.Grades[grade];
            quality.Add(new(metric, decibelValue, gradeLabel));
            builder.Append(metric).Append(valueIs).Append(decibelValue.ToString("0.00 dB (")).Append(gradeLabel).AppendLine(")");
        }

        void ReportValue(string metric, float signalValue) {
            float db = QMath.GainToDb(signalValue);
            quality.Add(new(metric, db, null));
            builder.Append(metric).Append(valueIs).AppendLine(db.ToString("0.00 dB"));
        }

        builder.Append(language["ABeds"]).Append(valueIs).Append(channels.Count);
        if (channels.Count != 0) {
            builder.Append(" (").Append(string.Join(", ", channels)).Append(')');
        }
        builder.AppendLine().Append(language["AObjs"]).Append(valueIs).AppendLine(dynamic.ToString())
            .Append(language["FakeT"]).Append(valueIs).AppendLine(unused.ToString());

        if (stats is RenderStatsEx statsEx) {
            float macrodynamics = QMath.GainToDb(statsEx.Macrodynamics),
                microdynamics = QMath.GainToDb(statsEx.Microdynamics),
                lfePeak = QMath.GainToDb(statsEx.LFELevelPeak),
                lfeMacrodynamics = QMath.GainToDb(statsEx.LFEMacrodynamics),
                lfeMicrodynamics = QMath.GainToDb(statsEx.LFEMicrodynamics),
                surroundUsage = QMath.GainToDb(statsEx.RelativeSurroundLevel),
                heightUsage = QMath.GainToDb(statsEx.RelativeHeightLevel);
            int macrodynamicsGrade = Grade(macrodynamics, 22, 3),
                microdynamicsGrade = Grade(microdynamics, 40, 3),
                lfePeakGrade = Grade(lfePeak, -3, 2),
                lfeMacrodynamicsGrade = Grade(lfeMacrodynamics, 30, 3),
                lfeMicrodynamicsGrade = Grade(lfeMicrodynamics, 60, 4),
                surroundGrade = Grade(surroundUsage, 10, 1),
                heightGrade = Grade(heightUsage, 10, 3);

            builder.AppendLine();
            ReportValue(language["PeaGa"], statsEx.FrameLevelPeak);
            ReportValue(language["RMSGa"], statsEx.FrameLevelRMS);
            ReportGrade(language["MacDy"], macrodynamics, macrodynamicsGrade);
            ReportGrade(language["MicDy"], microdynamics, microdynamicsGrade);
            builder.AppendLine();

            if (statsEx.LFELevelPeak != 0) {
                ReportGrade(language["PeaLF"], lfePeak, lfePeakGrade);
                ReportValue(language["RMSLF"], statsEx.LFELevelRMS);
                ReportGrade(language["MacLF"], lfeMacrodynamics, lfeMacrodynamicsGrade);
                ReportGrade(language["MicLF"], lfeMicrodynamics, lfeMicrodynamicsGrade);
                string chestSlam = language.Grades[Math.Max(lfePeakGrade, lfeMicrodynamicsGrade)];
                quality.Add(new(language["CheSl"], null, chestSlam));
                builder.Append(language["CheSl"]).Append(valueIs).Append(chestSlam).AppendLine();
            } else {
                builder.AppendLine(language["NoLFE"]);
            }
            builder.AppendLine();

            ReportGrade(language["SurUs"], surroundUsage, surroundGrade);
            if (statsEx.RelativeHeightLevel != 0) {
                ReportGrade(language["HeiUs"], heightUsage, heightGrade);
            }
        }

        if (peaks != null) {
            builder.AppendLine();
            AppendPeaks(builder, peaks);
        }
        Report = builder.ToString();
    }

    internal void GenerateInterrupted(PcmSamplePeakAnalysis peaks) {
        Quality = Array.Empty<QualityMetric>();
        StringBuilder builder = new();
        AppendPeaks(builder, peaks);
        Report = builder.ToString();
    }

    static void AppendPeaks(StringBuilder builder, PcmSamplePeakAnalysis peaks) {
        RenderReportStrings language = RenderReportStrings.Active;
        if (peaks.ListenerGain is ListenerGainInfo gain) {
            builder.Append(language["PcmListenerGain"]).Append(valueIs)
                .Append(language[gain.RequestedDb.HasValue ? "PcmGainOverride" : "PcmGainAuto"]);
            if (gain.RequestedDb is double requested) {
                builder.Append(" | ").Append(language["PcmGainRequested"]).Append(valueIs)
                    .Append(requested.ToString("R")).Append(" dB");
            }
            builder.Append(" | ").Append(language["PcmGainSafe"]).Append(valueIs).Append(gain.AutomaticSafeGain.ToString("R"))
                .Append(" | ").Append(language["PcmGainEffective"]).Append(valueIs).Append(gain.Effective.ToString("R"))
                .Append(" (").Append(gain.EffectiveDb.ToString("0.00")).AppendLine(" dB)").AppendLine();
        }
        AppendPeakSection(builder, peaks, peaks.PreLimiter, language["PcmPre"]);
        builder.AppendLine();
        AppendPeakSection(builder, peaks, peaks.Processed, language["PcmOut"]);
        if (peaks.Diagnostics != null) {
            builder.AppendLine().Append(language["PcmVerification"]).Append(valueIs)
                .AppendLine(language[peaks.Diagnostics.Matches(peaks) ? "PcmMatched" : "PcmMismatch"]);
        }
    }

    static void AppendPeakSection(StringBuilder builder, PcmSamplePeakAnalysis analysis,
        PcmSamplePeakAccumulator accumulator, string title) {
        RenderReportStrings language = RenderReportStrings.Active;
        PcmPeakIssue issues = accumulator.Issues;
        if (accumulator.ObservedFrames != analysis.ExpectedFrames) {
            issues |= PcmPeakIssue.MissingBlock;
        }
        builder.AppendLine(title);
        string coverageKey = issues != PcmPeakIssue.None ? "PcmInc" : analysis.SourceVerified ? "PcmOk" : "PcmUnv";
        builder.Append(language[coverageKey]).Append(" (").Append(accumulator.ObservedFrames)
            .Append(" / ").Append(analysis.ExpectedFrames).Append(' ').Append(language["PcmFrames"]).AppendLine(")");
        foreach (PcmPeakIssue issue in peakIssues) {
            if ((issues & issue) != 0) {
                builder.Append("  - ").AppendLine(language["Pcm" + issue]);
            }
        }

        if (ReferenceEquals(accumulator, analysis.PreLimiter) && analysis.PreLimiterError is Exception error) {
            builder.Append("    ").Append(error.GetType().Name).Append(valueIs).AppendLine(error.Message);
        }

        ReadOnlySpan<float> values = accumulator.Peaks;
        float overall = -1;
        foreach (float value in values) {
            overall = Math.Max(overall, value);
        }
        builder.Append(language["PcmAll"]).Append(valueIs).AppendLine(FormatPeak(overall));
        builder.Append(language["PcmOverallRms"]).Append(valueIs).AppendLine(FormatDbfs(accumulator.OverallLevel().RmsDbfs));
        builder.AppendFormat(language["PcmWindow"], PcmSamplePeakAccumulator.ActivityWindowMs).AppendLine();

        string[] speakers = analysis.Labels.Select((speaker, slot) => $"{slot + 1}. {speaker}").ToArray();
        string speakerHeading = language["PcmSpeaker"];
        int speakerWidth = Math.Max(speakerHeading.Length, speakers.Max(speaker => speaker.Length));
        string peakHeading = language["PcmPeakColumn"], rmsHeading = language["PcmRmsColumn"];
        string active60 = string.Format(language["PcmActive"], PcmSamplePeakAccumulator.ActivityThreshold60),
            active40 = string.Format(language["PcmActive"], PcmSamplePeakAccumulator.ActivityThreshold40);
        int peakWidth = Math.Max(peakHeading.Length, 17), rmsWidth = Math.Max(rmsHeading.Length, 17);
        int activeWidth = Math.Max(Math.Max(active60.Length, active40.Length), 15);

        void Row(string speaker, string peak, string rms, string above60, string above40) =>
            builder.Append(speaker.PadRight(speakerWidth)).Append(" | ")
                .Append(peak.PadLeft(peakWidth)).Append(" | ").Append(rms.PadLeft(rmsWidth)).Append(" | ")
                .Append(above60.PadLeft(activeWidth)).Append(" | ").Append(above40.PadLeft(activeWidth)).AppendLine();

        Row(speakerHeading, peakHeading, rmsHeading, active60, active40);
        for (int slot = 0; slot < values.Length; slot++) {
            PcmSignalLevel level = accumulator.ChannelLevel(slot);
            Row(speakers[slot], FormatPeak(values[slot]), FormatDbfs(level.RmsDbfs),
                FormatActivity(level.Active60), FormatActivity(level.Active40));
        }
    }

    static string FormatActivity(double? value) => value.HasValue ? value.Value.ToString("0.#########") + "%" :
        RenderReportStrings.Active["PcmNA"];

    static readonly PcmPeakIssue[] peakIssues = Enum.GetValues<PcmPeakIssue>();

    static string FormatPeak(float value) => FormatDbfs(value < 0 ? null : 20 * Math.Log10(value));

    static string FormatDbfs(double? value) {
        if (value is not double db) {
            return RenderReportStrings.Active["PcmNA"];
        }
        if (double.IsNegativeInfinity(db)) {
            return "−∞ dBFS";
        }
        // Keep the sign of even the smallest float overs above unity.
        string format = db != 0 && Math.Abs(db) < .005 ? "+0.000000;-0.000000" : "+0.00;-0.00;0.00";
        return db.ToString(format) + " dBFS";
    }

    /// <summary>
    /// Globally cached metric/value separator, saves a few bytes of memory.
    /// </summary>
    const string valueIs = ": ";
}
