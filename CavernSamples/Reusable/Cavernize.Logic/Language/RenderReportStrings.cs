using VoidX.WPF.Language;

namespace Cavernize.Logic.Language;

/// <summary>
/// Strings used for generating a post-render report.
/// </summary>
public class RenderReportStrings() : LanguageBase<RenderReportStrings>(new() {
    ["Defau"] = "After rendering has finished, more track information will appear here, like true object usage statistics.",
    ["ABeds"] = "Encoded bed channels",
    ["FakeT"] = "Inactive object sources (silent decoded PCM)",
    ["EncodedObjects"] = "Encoded Atmos objects",
    ["DecodedInventory"] = "Source inventory from decoded DAMF presentation metadata",
    ["DamfInventory"] = "Source inventory from DAMF presentation metadata",
    ["UnknownInventory"] = "Encoded bed and object identities are unavailable for this source.",
    ["ObservedPositions"] = "Observed stationary or semi-static source positions, including unpositioned slots",
    ["Motion_dynamic"] = "Dynamic objects",
    ["Motion_stationary"] = "Stationary objects",
    ["Motion_semi_static"] = "Semi-static objects",
    ["Motion_unverified"] = "Objects with unverified motion",
    ["ObjectMotion_dynamic"] = "Dynamic",
    ["ObjectMotion_stationary"] = "Stationary",
    ["ObjectMotion_semi_static"] = "Semi-static",
    ["ObjectMotion_unverified"] = "Motion unverified",
    ["Activity_signal"] = "PCM signal observed",
    ["Activity_silent"] = "Silent decoded PCM",
    ["Activity_unverified"] = "PCM activity unverified",
    ["ObjectDetail"] = "  Object {0} (renderer ID {1}, PCM slot {2}) | {3} | {4} | last authored {5} | last observed {6}",
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
    ["PcmListenerGain"] = "Listener gain",
    ["PcmGainOverride"] = "Explicit override",
    ["PcmGainAuto"] = "Automatic",
    ["PcmGainRequested"] = "Requested",
    ["PcmGainSafe"] = "Automatic safety multiplier",
    ["PcmGainEffective"] = "Effective",
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
