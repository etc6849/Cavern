using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

using Cavern.WPF.Utils;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using CavernizeGUI;
using CavernizeGUI.Language;
using CavernizeGUI.Resources;
using Microsoft.VisualBasic.FileIO;

namespace Test.CavernizeGUI;

[TestClass, DoNotParallelize]
public class AnalysisParity_Tests {
    [TestMethod, Timeout(120000)]
    public async Task GuiAndWindowsCliAgreeOnIntegratedAnalysisInAllThreeLayouts() {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try {
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"))) {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }
            List<string> sources = [Path.Combine(directory, "ListenerGain.atmos")];
            string mediaFixtures = Environment.GetEnvironmentVariable("CAVERNIZE_V4_FIXTURES");
            if (mediaFixtures != null) {
                foreach (string fixture in Directory.GetFiles(mediaFixtures).Where(path =>
                    Path.GetExtension(path) is ".mkv" or ".mka" && !Path.GetFileName(path).StartsWith("Two"))) {
                    string file = Path.Combine(directory, Path.GetFileName(fixture));
                    File.Copy(fixture, file);
                    sources.Add(file);
                }
                Assert.AreEqual(3, sources.Count);
            }
            string guiWork = Path.Combine(directory, "GUI workspace"), cliWork = Path.Combine(directory, "CLI workspace");
            string decoder = Environment.GetEnvironmentVariable("CAVERNIZE_V4_DECODER");
            string[] layouts = ["5.1.6 with top sides", "5.1.4", "5.1.2 side"];
            TaskCompletionSource rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread gui = new(() => {
                try {
                    App application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources.MergedDictionaries.Add(MainWindowStrings.Active.ToResourceDictionary());
                    application.Resources.MergedDictionaries.Add(RenderTargetSelectorStrings.Active.ToResourceDictionary());
                    application.InitializeComponent();
                    Settings.Default.analysisWorkDirectory = guiWork;
                    Settings.Default.analysisTrueHdd = decoder ?? "";
                    Settings.Default.analysisKeepEvidence = true;
                    foreach (string source in sources) {
                        foreach (string layout in layouts) {
                            RenderWithGui(source, layout);
                        }
                    }
                    application.Shutdown();
                    rendered.SetResult();
                } catch (Exception e) {
                    rendered.SetException(e);
                }
            }) { IsBackground = true };
            gui.SetApartmentState(ApartmentState.STA);
            gui.Start();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(70));

            foreach (string source in sources) {
                string report = Path.ChangeExtension(source, ".csv");
                string[] guiCsv = ReadCsvWithoutId(report);
                string[] overall = guiCsv.Where(row => row.Split('\t').Contains("overall")).ToArray();
                Assert.AreEqual(layouts.Length, overall.Length);
                foreach (string row in overall) {
                    JsonObject inventory = JsonNode.Parse(row.Split('\t')[^1]).AsObject();
                    bool direct = Path.GetExtension(source) == ".atmos";
                    Assert.AreEqual(direct ? "damf" : "decoded_damf", inventory["Provenance"].GetValue<string>());
                    Assert.AreEqual(direct ? "FrontLeft" : "ScreenLFE", inventory["Beds"][0]["Channel"].GetValue<string>());
                    Assert.AreEqual(direct ? 0 : 11, inventory["Objects"].AsArray().Count);
                }
                Dictionary<string, string> guiJson = ReadEvidence(guiWork, source, layouts.Length);
                await RenderWithCli(source, layouts, cliWork, decoder);
                CollectionAssert.AreEqual(guiCsv, ReadCsvWithoutId(report), source);
                CollectionAssert.AreEquivalent(guiJson.ToArray(), ReadEvidence(cliWork, source, layouts.Length).ToArray(), source);
            }
            Assert.IsFalse(Directory.EnumerateFiles(directory, "*.wav").Any());
        } finally {
            Directory.Delete(directory, true);
        }
    }

    static void RenderWithGui(string source, string name) {
        MainWindow window = new();
        try {
            window.RenderTarget = RenderTarget.Targets.Single(x => x.Name == name);
            window.RenderingSettings.SpeakerVirtualizer = false;
            window.RenderingSettings.SurroundSwap = false;
            ((MenuItem)window.FindName("reportMode")).IsChecked = true;
            ((MenuItem)window.FindName("grading")).IsChecked = true;
            ((MenuItem)window.FindName("verifyPeakEvents")).IsChecked = true;
            window.OpenContent(new AudioFile(source));
            Action render = window.GetRenderTask(null);
            Assert.IsNotNull(render);
            render();
        } finally {
            window.Close();
        }
    }

    static async Task RenderWithCli(string source, IEnumerable<string> layouts, string work, string decoder) {
        string cli = Environment.GetEnvironmentVariable("CAVERNIZE_PUBLISHED_CLI") ??
            Path.GetFullPath(Path.Combine("..", "..", "..", "..", "CavernSamples", "CavernizeCLI", "bin", "Release", "net8.0", "CavernizeCLI.exe"));
        ProcessStartInfo start = new(cli) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        string[] args = ["--input", source, "--work-dir", work, "--report-only", "--quality", "--verify-peak-events",
            "--keep-verification-json"];
        foreach (string argument in args.Concat(layouts.SelectMany(x => new[] { "--layout", x }))) {
            start.ArgumentList.Add(argument);
        }
        if (decoder != null) {
            start.ArgumentList.Add("--truehdd");
            start.ArgumentList.Add(decoder);
        }
        using Process process = Process.Start(start);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        try {
            await process.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.AreEqual(0, process.ExitCode, await stdout + await stderr);
    }

    static string[] ReadCsvWithoutId(string path) {
        using TextFieldParser parser = new(path);
        parser.SetDelimiters(",");
        string[] header = parser.ReadFields();
        int id = Array.IndexOf(header, "analysis_id");
        Assert.IsTrue(id >= 0);
        List<string> rows = [];
        while (!parser.EndOfData) {
            string[] fields = parser.ReadFields();
            rows.Add(string.Join('\t', fields.Where((_, index) => index != id)));
        }
        return rows.ToArray();
    }

    static Dictionary<string, string> ReadEvidence(string root, string source, int layouts) {
        Dictionary<string, string> evidence = [];
        foreach (string path in Directory.GetFiles(root, "*-Verification.json", System.IO.SearchOption.AllDirectories)) {
            JsonObject json = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            if (json["SourceFile"].GetValue<string>() == Path.GetFileName(source)) {
                Assert.IsTrue(json["Evidence"]["Matches"].GetValue<bool>());
                json.Remove("AnalysisId");
                evidence.Add(json["LayoutId"].GetValue<string>(), json.ToJsonString());
            }
        }
        Assert.AreEqual(layouts, evidence.Count);
        return evidence;
    }
}
