using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

using Cavern.WPF.Utils;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using CavernizeGUI;
using CavernizeGUI.Language;
using Microsoft.VisualBasic.FileIO;

namespace Test.CavernizeGUI;

[TestClass, DoNotParallelize]
public class AnalysisParity_Tests {
    [TestMethod, Timeout(60000)]
    public async Task GuiAndWindowsCliAgreeOnDamfAnalysisInBothLayouts() {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try {
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"))) {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }
            string source = Path.Combine(directory, "ListenerGain.atmos");
            var layouts = new[] { ("5.1.6 with top sides", "5.1.6-with-top-sides"), ("5.1.2 side", "5.1.2-side") };
            TaskCompletionSource rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread gui = new(() => {
                try {
                    App application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources.MergedDictionaries.Add(MainWindowStrings.Active.ToResourceDictionary());
                    application.Resources.MergedDictionaries.Add(RenderTargetSelectorStrings.Active.ToResourceDictionary());
                    application.InitializeComponent();
                    foreach ((string name, _) in layouts) {
                        RenderWithGui(source, name);
                    }
                    application.Shutdown();
                    rendered.SetResult();
                } catch (Exception e) {
                    rendered.SetException(e);
                }
            }) { IsBackground = true };
            gui.SetApartmentState(ApartmentState.STA);
            gui.Start();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            foreach ((string name, string id) in layouts) {
                string report = Path.Combine(directory, $"ListenerGain-{id}-PeakAnalysis");
                string[] guiCsv = ReadCsvWithoutId(report + ".csv");
                string guiJson = ReadJsonWithoutId(report + "-Verification.json");
                Assert.IsTrue(guiCsv.Any(x => x.StartsWith("overall") && x.Contains("complete")));
                await RenderWithCli(source, name);
                CollectionAssert.AreEqual(guiCsv, ReadCsvWithoutId(report + ".csv"), name);
                Assert.AreEqual(guiJson, ReadJsonWithoutId(report + "-Verification.json"), name);
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

    static async Task RenderWithCli(string source, string name) {
        string cli = Environment.GetEnvironmentVariable("CAVERNIZE_PUBLISHED_CLI") ??
            Path.GetFullPath(Path.Combine("..", "..", "..", "..", "CavernSamples", "CavernizeCLI", "bin", "Release", "net8.0", "CavernizeCLI.exe"));
        ProcessStartInfo start = new(cli) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "--input", source, "--layout", name, "--report-only", "--quality", "--verify-peak-events" }) {
            start.ArgumentList.Add(argument);
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

    static string ReadJsonWithoutId(string path) {
        JsonObject json = JsonNode.Parse(File.ReadAllText(path)).AsObject();
        Assert.AreEqual("complete", json["Status"].GetValue<string>());
        Assert.IsTrue(json["Evidence"]["Matches"].GetValue<bool>());
        json.Remove("AnalysisId");
        return json.ToJsonString();
    }
}
