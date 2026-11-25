namespace Cavernize.Logic.Language;

/// <summary>
/// Strings used for generating a post-render report in Chinese.
/// </summary>
public class RenderReportStringsZH : RenderReportStrings {
    /// <inheritdoc/>
    protected override string CultureCode => "zh-CN";

    /// <inheritdoc/>
    protected override void ApplyTranslation() {
        Set("Defau", "渲染完成后，您可以在此处查看额外的轨道信息，例如实际对象使用统计。");
        Set("ABeds", "实际包含的基础声道");
        Set("AObjs", "实际包含的动态对象");
        Set("FakeT", "未使用（虚假）渲染目标");
        Set("PeaGa", "峰值音频帧电平");
        Set("RMSGa", "内容RMS电平");
        Set("MacDy", "宏观动态");
        Set("MicDy", "微观动态");
        Set("NoLFE", "源中缺少LFE声道、未使用或未被渲染。");
        Set("PeaLF", "峰值LFE电平");
        Set("RMSLF", "RMS LFE电平");
        Set("MacLF", "LFE宏观动态");
        Set("MicLF", "LFE微观动态");
        Set("CheSl", "捶胸感等级");
        Set("SurUs", "环绕声使用率");
        Set("HeiUs", "高度使用率");
        Set("PcmPre", "监听器限幅前的渲染 PCM 样本峰值");
        Set("PcmOut", "导出前的监听器处理后 PCM 样本峰值");
        Set("PcmOk", "完整节目");
        Set("PcmInc", "仅报告已测峰值 — 分析不完整");
        Set("PcmUnv", "已测峰值 — 源数据覆盖范围未验证");
        Set("PcmFrames", "实测 / 预期 PCM 帧数");
        Set("PcmAll", "总样本峰值");
        Set("PcmOverallRms", "整段节目总体 RMS");
        Set("PcmWindow", "活动比例使用 {0} 毫秒 RMS 窗口并按时长加权。");
        Set("PcmSpeaker", "扬声器");
        Set("PcmPeakColumn", "峰值");
        Set("PcmRmsColumn", "整段节目 RMS");
        Set("PcmActive", "活动 >{0} dBFS");
        Set("PcmVerification", "峰值验证");
        Set("PcmMatched", "匹配");
        Set("PcmMismatch", "不匹配");
        Set("PcmNA", "不可用");
        Set("PcmMissingBlock", "一个或多个预期的 PCM 帧缺失或无效。");
        Set("PcmInvalidBuffer", "渲染块大小或测量点调用次数异常。");
        Set("PcmNonFinitePcm", "渲染块包含 NaN 或无穷大的 PCM 样本。");
        Set("PcmRenderError", "渲染块失败。替代静音已排除。");
        Set("PcmLayoutChanged", "监听器布局或渲染块配置发生变化。");
        Set("PcmSourceLengthMismatch", "源时长或采样率与渲染不匹配。");
        Set("PcmInvalidSource", "源 PCM 数据被截断或未按完整帧对齐。");
        Set("PcmInterrupted", "渲染或分析因错误结束。");
        Set("PcmMeasurementError", "限幅前测量发生错误。");
        Set("Grad0", "5*");
        Set("Grad1", "5");
        Set("Grad2", "4");
        Set("Grad3", "3");
        Set("Grad4", "2");
        Set("Grad5", "1");
    }
}
