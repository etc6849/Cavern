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

    /// <summary>
    /// Print a graded metric.
    /// </summary>
    static void ReportGrade(StringBuilder builder, string metric, float decibelValue, int grade) =>
        builder.Append(metric).Append(valueIs).Append(decibelValue.ToString("0.00 dB (")).Append(RenderReportStrings.Active.Grades[grade]).AppendLine(")");

    /// <summary>
    /// Print a raw signal level value in decibels, without grading.
    /// </summary>
    static void ReportValue(StringBuilder builder, string metric, float signalValue) =>
        builder.Append(metric).Append(valueIs).AppendLine(QMath.GainToDb(signalValue).ToString("0.00 dB"));


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
            ReportValue(builder, language["PeaGa"], statsEx.FrameLevelPeak);
            ReportValue(builder, language["RMSGa"], statsEx.FrameLevelRMS);
            ReportGrade(builder, language["MacDy"], macrodynamics, macrodynamicsGrade);
            ReportGrade(builder, language["MicDy"], microdynamics, microdynamicsGrade);
            builder.AppendLine();

            if (statsEx.LFELevelPeak != 0) {
                ReportGrade(builder, language["PeaLF"], lfePeak, lfePeakGrade);
                ReportValue(builder, language["RMSLF"], statsEx.LFELevelRMS);
                ReportGrade(builder, language["MacLF"], lfeMacrodynamics, lfeMacrodynamicsGrade);
                ReportGrade(builder, language["MicLF"], lfeMicrodynamics, lfeMicrodynamicsGrade);
                builder.Append(language["CheSl"]).Append(valueIs).Append(language.Grades[Math.Max(lfePeakGrade, lfeMicrodynamicsGrade)])
                    .AppendLine();
            } else {
                builder.AppendLine(language["NoLFE"]);
            }
            builder.AppendLine();

            ReportGrade(builder, language["SurUs"], surroundUsage, surroundGrade);
            if (statsEx.RelativeHeightLevel != 0) {
                ReportGrade(builder, language["HeiUs"], heightUsage, heightGrade);
            }
        }

        if (peaks != null) {
            builder.AppendLine();
            AppendPeaks(builder, peaks);
        }
        Report = builder.ToString();
    }

    internal void GenerateInterrupted(PcmSamplePeakAnalysis peaks) {
        StringBuilder builder = new();
        AppendPeaks(builder, peaks);
        Report = builder.ToString();
    }

    static void AppendPeaks(StringBuilder builder, PcmSamplePeakAnalysis peaks) {
        RenderReportStrings language = RenderReportStrings.Active;
        AppendPeakSection(builder, peaks, peaks.PreLimiter, language["PcmPre"]);
        builder.AppendLine();
        AppendPeakSection(builder, peaks, peaks.Processed, language["PcmOut"]);
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
        for (int slot = 0; slot < values.Length; slot++) {
            builder.Append("  ").Append(slot + 1).Append(". ").Append(analysis.Labels[slot])
                .Append(valueIs).AppendLine(FormatPeak(values[slot]));
        }
    }

    static readonly PcmPeakIssue[] peakIssues = Enum.GetValues<PcmPeakIssue>();

    static string FormatPeak(float value) {
        if (value < 0) {
            return RenderReportStrings.Active["PcmNA"];
        }
        if (value == 0) {
            return "−∞ dBFS";
        }
        double db = 20 * Math.Log10(value);
        // Keep the sign of even the smallest float overs above unity.
        string format = value != 1 && Math.Abs(db) < .005 ? "+0.000000;-0.000000" : "+0.00;-0.00;0.00";
        return db.ToString(format) + " dBFS";
    }

    /// <summary>
    /// Globally cached metric/value separator, saves a few bytes of memory.
    /// </summary>
    const string valueIs = ": ";
}
