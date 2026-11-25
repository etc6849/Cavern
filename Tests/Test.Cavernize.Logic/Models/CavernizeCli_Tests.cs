using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cavern;
using Cavern.Format;
using Cavern.Utilities;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;
using Microsoft.VisualBasic.FileIO;
using Cli = CavernizeCLI.Program;

namespace Test.Cavernize.Logic.Models;

[TestClass, DoNotParallelize]
public class CavernizeCli_Tests {
    const string Wide = "5.1.6 with top sides", Side = "5.1.2 side";
    const string WideId = "5.1.6-with-top-sides", SideId = "5.1.2-side";
    const int Frames = 5381;
    const double FixtureSample = 16384d / 32767;
    static readonly string[] AnalysisOptions = ["--report-only", "--quality", "--verify-peak-events"];

    [TestMethod]
    public async Task HelpListsLayoutsAndFailuresReturnMeaningfulCodes() {
        StringAssert.Contains(await Invoke(0, "--help"), "--listener-gain-db");
        string layouts = await Invoke(0, "--list-layouts");
        StringAssert.Contains(layouts, Wide);
        StringAssert.Contains(layouts, Side);
        StringAssert.Contains(await Invoke(2, "--input"), "Missing value");
        StringAssert.Contains(await Invoke(2, "--layout", "5.1.6"), "Ambiguous layout");
        StringAssert.Contains(await Invoke(2, "--layout", "13.0.1"), "Unknown layout");
        StringAssert.Contains(await Invoke(2, "--listener-gain-db", "Infinity"), "Invalid listener gain");
        StringAssert.Contains(await Invoke(2, "--listener-gain-db", "0", "--render-gain", "0"), "cannot be used together");
        StringAssert.Contains(await Invoke(1, "--input", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".atmos"),
            "--report-only"), "Input file does not exist");
    }

    [TestMethod]
    public async Task FullAnalysisReportsGainPeaksActivitiesAndVerifiesBothLayoutsWithoutAudioOutput() {
        string dir = CreateFixture(out string input);
        try {
            await Invoke(0, ["--input", input, "--layout", Wide, .. AnalysisOptions]);
            await Invoke(0, ["--input", input, "--layout", Side, .. AnalysisOptions]);
            Dictionary<string, string>[] wide = ReadReport(dir, WideId), side = ReadReport(dir, SideId);
            Validate(wide, dir, WideId, 12, 1, 1, null);
            Validate(side, dir, SideId, 8, .707, .707, null);

            await Invoke(0, ["--listener-gain-db", "0", "-t", Side, .. AnalysisOptions, "-i", input]);
            var unity = ReadReport(dir, SideId + "-ListenerGain-0dB");
            Validate(unity, dir, SideId + "-ListenerGain-0dB", 8, .707, 1, 0);
            Assert.AreEqual(Overall(wide)["pre_float_bits"], Overall(unity)["pre_float_bits"]);
            CollectionAssert.AreEqual(side.Select(x => x["analysis_id"]).ToArray(),
                ReadReport(dir, SideId).Select(x => x["analysis_id"]).ToArray());

            foreach ((string db, string suffix) in new[] { ("-3", "m3"), ("8", "8"), ("-40", "m40"), ("-60", "m60") }) {
                await Invoke(0, ["-i", input, .. AnalysisOptions, "--listener-gain-db", db, "-t", Side]);
                double value = double.Parse(db, CultureInfo.InvariantCulture);
                string id = SideId + $"-ListenerGain-{suffix}dB";
                var rows = ReadReport(dir, id);
                Validate(rows, dir, id, 8, .707, Math.Pow(10, value / 20), value);
                var first = rows.First(x => x["row_type"] == "channel");
                if (value == 8) {
                    Assert.AreEqual("True", first["pre_block_attack"]);
                    Assert.AreEqual(-.9, Number(first, "pre_same_sample_after_limiter"), 1e-6);
                    Assert.IsTrue(Number(first, "post_abs_linear") <= 1);
                } else if (value == -40) {
                    Assert.AreEqual("100", first["pre_active_gt_minus60_percent"]);
                    Assert.AreEqual("0", first["pre_active_gt_minus40_percent"]);
                } else if (value == -60) {
                    Assert.AreEqual("0", first["pre_active_gt_minus60_percent"]);
                }
            }

            await Invoke(0, ["-i", input, "--render-gain", "-3,5", "-t", Side, .. AnalysisOptions]);
            var legacy = ReadReport(dir, SideId);
            Validate(legacy, dir, SideId, 8, .707, .707 * Math.Pow(10, -3.5 / 20), null);
            Assert.IsFalse(Directory.EnumerateFiles(dir, "*.wav").Any());
            Assert.IsFalse(Directory.EnumerateFiles(dir, "*.mka").Any());

            string complete = File.ReadAllText(ReportPath(dir, SideId));
            using (var audio = File.OpenWrite(input + ".audio")) {
                audio.SetLength(audio.Length - 2);
            }
            StringAssert.Contains(await Invoke(1, ["--input", input, "--layout", Side, .. AnalysisOptions]), "incomplete");
            Assert.AreEqual(complete, File.ReadAllText(ReportPath(dir, SideId)));
            Assert.AreEqual("incomplete", Overall(ReadReport(dir, SideId, "-Incomplete"))["status"]);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task HeadphoneReportDescribesTheAppliedVirtualSpeakerLayout() {
        string dir = CreateFixture(out string input);
        try {
            await Invoke(0, ["--input", input, "--layout", "Headphone Virtualizer", .. AnalysisOptions]);
            string csv = Directory.EnumerateFiles(dir, "ListenerGain-Headphone-Virtualizer-*-PeakAnalysis.csv").Single();
            string id = Path.GetFileName(csv)["ListenerGain-".Length..^"-PeakAnalysis.csv".Length];
            var rows = ReadReport(dir, id);
            var overall = Overall(rows);
            Assert.AreEqual("complete", overall["status"]);
            Assert.AreEqual("matched", overall["verification"]);
            Assert.AreEqual("5381", overall["pre_frames"]);
            Assert.AreEqual("5381", overall["post_frames"]);
            Assert.AreEqual(16, rows.Count(x => x["row_type"] == "channel"));
            Assert.IsFalse(Directory.EnumerateFiles(dir, "*.wav").Any());
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task ReportOnlyWorksWithoutOptionalAnalysisAndReportsAnUnwritableDestination() {
        string dir = CreateFixture(out string input);
        try {
            string csv = ReportPath(dir, SideId);
            string[] arguments = ["--input", input, "--layout", Side, "--report-only"];
            Directory.CreateDirectory(csv);
            StringAssert.Contains(await Invoke(1, arguments), "Could not save analysis beside");
            Assert.IsTrue(Directory.Exists(csv));
            Assert.IsFalse(Directory.EnumerateFiles(dir, "*.tmp").Any());
            Directory.Delete(csv);

            await Invoke(0, arguments);
            var rows = ReadReport(dir, SideId);
            Assert.AreEqual("complete", Overall(rows)["status"]);
            Assert.AreEqual("disabled", Overall(rows)["verification"]);
            Assert.IsFalse(rows.Any(x => x["row_type"] == "quality"));
            Assert.IsFalse(Directory.EnumerateFiles(dir, "*.json").Any());
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task HeadlessFloatWavIsBitIdenticalToTheUninstrumentedV3ListenerAndGradesMatch() {
        const string layout = "5.1.6 for WAVE", layoutId = "5.1.6-for-WAVE";
        string dir = CreateFixture(out string input);
        Channel[] saved = Listener.Channels;
        Cli app = new();
        try {
            string wav = Path.Combine(dir, "render.wav");
            await Invoke(0, "--output", wav, "-f", "PCM_Float", "--quality", "--input", input, "--layout", layout);
            app.RenderTarget = RenderTarget.Targets.Single(x => x.Name == layout);
            app.OpenContent(input);
            ConversionEnvironment environment = new(app);
            environment.AttachToListener(app.SelectedTrack);

            var listener = environment.Listener;
            int channels = Listener.Channels.Length, count = 0;
            int[] expected = new int[Frames * channels];
            RenderStatsEx legacy = new(listener);
            for (int frame = 0; frame < Frames; frame += listener.UpdateRate) {
                float[] block = listener.Render();
                int valid = Math.Min(listener.UpdateRate, Frames - frame) * channels;
                for (int i = 0; i < valid; i++) {
                    expected[count++] = BitConverter.SingleToInt32Bits(block[i]);
                }
                if (frame > 3072) {
                    legacy.Update(valid == block.Length ? block : block[..valid]);
                }
            }

            using AudioReader written = AudioReader.Open(wav);
            CollectionAssert.AreEqual(expected, written.Read().Select(BitConverter.SingleToInt32Bits).ToArray());
            PostRenderReport report = new(listener);
            report.Generate(legacy);
            Dictionary<string, string>[] grades = ReadReport(dir, layoutId).Where(x => x["row_type"] == "quality").ToArray();
            Assert.AreEqual(report.Quality.Count, grades.Length);
            for (int i = 0; i < grades.Length; i++) {
                Assert.AreEqual(report.Quality[i].Name, grades[i]["quality_metric"]);
                Assert.AreEqual(report.Quality[i].Grade ?? "", grades[i]["quality_grade"]);
                Assert.AreEqual(report.Quality[i].Db?.ToString(CultureInfo.InvariantCulture) ?? "", grades[i]["quality_db"]);
            }
        } finally {
            app.Reset();
            Listener.ReplaceChannels(saved);
            Directory.Delete(dir, true);
        }
    }

    static void Validate(Dictionary<string, string>[] rows, string dir, string id, int channels,
        double safeGain, double effective, double? requested) {
        var overall = Overall(rows);
        Assert.AreEqual("complete", overall["status"]);
        Assert.AreEqual("matched", overall["verification"]);
        Assert.AreEqual("5381", overall["pre_frames"]);
        Assert.AreEqual("5381", overall["post_frames"]);
        Assert.AreEqual("64", overall["pre_frame"]);
        Assert.AreEqual(64 / 48000d, Number(overall, "pre_seconds"), 1e-13);
        Assert.AreEqual(requested.HasValue ? "Explicit override" : "Automatic", overall["listener_gain_mode"]);
        Assert.AreEqual(requested?.ToString(CultureInfo.InvariantCulture) ?? "", overall["listener_gain_requested_db"]);
        Assert.AreEqual(safeGain, Number(overall, "listener_automatic_safe_gain"), 1e-7);
        Assert.AreEqual(effective, Number(overall, "listener_gain_effective_linear"), 1e-6);
        Assert.AreEqual(effective * FixtureSample, Number(overall, "pre_abs_linear"), 1e-6);
        Assert.AreEqual(20 * Math.Log10(effective), Number(overall, "listener_gain_effective_db"), 1e-6);
        Assert.AreEqual(effective * FixtureSample * Math.Sqrt(5317d / Frames / channels), Number(overall, "pre_rms_linear"), 1e-6);
        var speakers = rows.Where(x => x["row_type"] == "channel").ToArray();
        Assert.AreEqual(channels, speakers.Length);
        Assert.AreEqual("Front left", speakers[0]["speaker"]);
        Assert.AreEqual("-Infinity", speakers[1]["pre_dbfs"]);
        Assert.AreEqual("0", speakers[1]["post_active_gt_minus40_percent"]);
        Assert.IsTrue(rows.Any(x => x["row_type"] == "quality" && x["quality_grade"] != ""));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(ReportPath(dir, id), null) + "-Verification.json"));
        var evidence = json.RootElement.GetProperty("Evidence");
        Assert.IsTrue(evidence.GetProperty("Matches").GetBoolean());
        Assert.AreEqual(64, evidence.GetProperty("Highest").GetProperty("Frame").GetInt32());
        Assert.AreEqual(Number(overall, "pre_signed_sample"),
            evidence.GetProperty("Highest").GetProperty("Reconstructed").GetDouble(), 1e-6);
    }

    static Dictionary<string, string> Overall(Dictionary<string, string>[] rows) => rows.Single(x => x["row_type"] == "overall");
    static double Number(Dictionary<string, string> row, string column) => double.Parse(row[column], CultureInfo.InvariantCulture);
    static string ReportPath(string dir, string id, string status = "") => Path.Combine(dir, $"ListenerGain-{id}-PeakAnalysis{status}.csv");

    static Dictionary<string, string>[] ReadReport(string dir, string id, string status = "") {
        using TextFieldParser parser = new(ReportPath(dir, id, status));
        parser.SetDelimiters(",");
        string[] headers = parser.ReadFields();
        List<Dictionary<string, string>> result = [];
        while (!parser.EndOfData) {
            string[] fields = parser.ReadFields();
            result.Add(headers.Zip(fields).ToDictionary(x => x.First, x => x.Second));
        }
        return result.ToArray();
    }

    static string CreateFixture(out string input) {
        string dir = Directory.CreateTempSubdirectory().FullName;
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "ListenerGain.atmos*")) {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }
        input = Path.Combine(dir, "ListenerGain.atmos");
        return dir;
    }

    static async Task<string> Invoke(int expected, params string[] args) {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        ProcessStartInfo start = new(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Cli).Assembly.Location);
        foreach (string arg in args) {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try {
            await process.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            process.Kill(entireProcessTree: true);
            throw;
        }
        string combined = await stdout + await stderr;
        Assert.AreEqual(expected, process.ExitCode, string.Join(' ', args) + "\n" + combined);
        return combined;
    }
}
