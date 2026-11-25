using Cavernize.Logic.External;
using System.Text.Json;

namespace Test.Cavernize.Logic.External;

[TestClass]
public class TruehddDecode_Tests {
    [TestMethod]
    public void OrdinaryWarningsDoNotRejectIntactDecoderOutput() =>
        WithLogs(Summary(), Log("WARN", "Unusual but recoverable stream timing"), (summary, log) => {
            TruehddDecode.Result result = TruehddDecode.CheckResult(summary, log);
            Assert.AreEqual(16000L, result.Samples);
            Assert.AreEqual(48000, result.SampleRate);
        });

    [DataTestMethod]
    [DataRow("ERROR", "Frame parity check failed for frame 1")]
    [DataRow("WARN", "lossless_check failed for substream 3")]
    public void CorruptOutputWithZeroSummaryCountersProducesOneBoundedFailure(string level, string message) =>
        WithLogs(Summary(), string.Join('\n', Enumerable.Repeat(Log(level, message + new string('x', 500)), 4000)), (summary, log) => {
            IOException error = Assert.ThrowsException<IOException>(() => TruehddDecode.CheckResult(summary, log));
            StringAssert.Contains(error.Message, "4000");
            StringAssert.Contains(error.Message, message);
            StringAssert.Contains(error.Message, log);
            StringAssert.Contains(error.Message, summary);
            Assert.IsTrue(error.Message.Length < 750);
            Assert.AreEqual(4000, File.ReadLines(log).Count());
        });

    [TestMethod]
    public void SkippedAndConcealedFramesRejectOutputEvenWithoutAnErrorLog() =>
        WithLogs(Summary(skipped: 5, concealed: 95), "", (summary, log) => {
            IOException error = Assert.ThrowsException<IOException>(() => TruehddDecode.CheckResult(summary, log));
            StringAssert.Contains(error.Message, "5 skipped frames");
            StringAssert.Contains(error.Message, "95 concealed frames");
        });

    [TestMethod]
    public void MissingSummaryFieldsProduceAnActionableFailure() =>
        WithLogs("{\"version\":\"old\"}", "Decoder warning", (summary, log) => {
            IOException error = Assert.ThrowsException<IOException>(() => TruehddDecode.CheckResult(summary, log));
            StringAssert.Contains(error.Message, "usable decode summary");
            StringAssert.Contains(error.Message, log);
        });

    static string Summary(int skipped = 0, int concealed = 0) =>
        JsonSerializer.Serialize(new {
            version = "0.6.3", frames = 400, skippedFrames = skipped, concealedFrames = concealed, samples = 16000,
            invalidBranches = 0, evoFailed = 0, sampleRate = 48000
        });

    static string Log(string level, string message) => JsonSerializer.Serialize(new { lvl = level, msg = message });

    static void WithLogs(string summaryText, string logText, Action<string, string> test) {
        string path = Directory.CreateTempSubdirectory("cavern-truehdd-").FullName;
        try {
            string summary = Path.Combine(path, "decoded.json"), log = Path.Combine(path, "decoder.log");
            File.WriteAllText(summary, summaryText);
            File.WriteAllText(log, logText);
            test(summary, log);
        } finally {
            Directory.Delete(path, true);
        }
    }
}
