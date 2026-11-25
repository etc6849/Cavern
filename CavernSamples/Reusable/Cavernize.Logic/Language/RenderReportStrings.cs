using VoidX.WPF.Language;

namespace Cavernize.Logic.Language;

/// <summary>
/// Strings used for generating a post-render report.
/// </summary>
public class RenderReportStrings() : LanguageBase<RenderReportStrings>(new() {
    ["Defau"] = "After rendering has finished, more track information will appear here, like true object usage statistics.",
    ["ABeds"] = "Actually present bed channels",
    ["AObjs"] = "Actually present dynamic objects",
    ["FakeT"] = "Unused (fake) rendering targets",
    ["PeaGa"] = "Peak audio frame level",
    ["RMSGa"] = "RMS content level",
    ["MacDy"] = "Macrodynamics",
    ["MicDy"] = "Microdynamics",
    ["NoLFE"] = "The LFE channel was either missing from the source, unused, or not rendered.",
    ["PeaLF"] = "Peak LFE level",
    ["RMSLF"] = "RMS LFE level",
    ["MacLF"] = "LFE macrodynamics",
    ["MicLF"] = "LFE microdynamics",
    ["CheSl"] = "Chest slam grade",
    ["SurUs"] = "Surround usage",
    ["HeiUs"] = "Height usage",
    ["PcmPre"] = "Rendered PCM sample peaks before the listener limiter",
    ["PcmOut"] = "Processed listener PCM sample peaks before export",
    ["PcmOk"] = "Complete program",
    ["PcmInc"] = "Observed peaks only — incomplete analysis",
    ["PcmUnv"] = "Observed peaks — source coverage unverified",
    ["PcmFrames"] = "PCM frames observed / expected",
    ["PcmMeasurementError"] = "The pre-limiter measurement encountered an error.",
    ["PcmAll"] = "Overall sample peak",
    ["PcmOverallRms"] = "Overall whole-program RMS",
    ["PcmWindow"] = "Activity uses {0} ms RMS windows, weighted by duration.",
    ["PcmSpeaker"] = "Speaker",
    ["PcmPeakColumn"] = "Peak",
    ["PcmRmsColumn"] = "Whole-program RMS",
    ["PcmActive"] = "Active >{0} dBFS",
    ["PcmVerification"] = "Peak verification",
    ["PcmMatched"] = "Matched",
    ["PcmMismatch"] = "Mismatch",
    ["PcmNA"] = "Unavailable",
    ["PcmMissingBlock"] = "One or more expected PCM frames were missing or invalid.",
    ["PcmInvalidBuffer"] = "A render block had an unexpected size or tap count.",
    ["PcmNonFinitePcm"] = "A render block contained NaN or infinite PCM.",
    ["PcmRenderError"] = "Rendering failed for a block. Substituted silence was excluded.",
    ["PcmLayoutChanged"] = "The listener layout or render block configuration changed.",
    ["PcmSourceLengthMismatch"] = "The source duration or sample rate did not match the render.",
    ["PcmInvalidSource"] = "The source PCM payload was truncated or not frame-aligned.",
    ["PcmInterrupted"] = "The render or analysis ended with an error.",
    ["Grad0"] = "A+",
    ["Grad1"] = "A",
    ["Grad2"] = "B",
    ["Grad3"] = "C",
    ["Grad4"] = "D",
    ["Grad5"] = "F",
}) {
    /// <inheritdoc/>
    protected override LanguageBase<RenderReportStrings>[] GetTranslations() => [new RenderReportStringsHU(), new RenderReportStringsZH()];

    /// <summary>
    /// 6 grades from best to worst.
    /// </summary>
    public string[] Grades => grades ??= [this["Grad0"], this["Grad1"], this["Grad2"], this["Grad3"], this["Grad4"], this["Grad5"]];
    string[] grades;
}
