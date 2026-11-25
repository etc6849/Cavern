using Microsoft.VisualBasic.FileIO;

using Cavern;
using Cavern.Utilities;
using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;

namespace Test.Cavernize.Logic.Models;

[TestClass, DoNotParallelize]
public class IntegratedAnalysisReport_Tests {
    [TestMethod]
    public void StructuredReportRetainsTheFullQualityInventoryAndHeader() {
        CavernizeCLI.Program app = new();
        string folder = Directory.CreateTempSubdirectory().FullName;
        Channel[] channels = Listener.Channels;
        try {
            string source = Path.Combine(folder, "Fixture (Atmos).atmos");
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"))) {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file).Replace("ListenerGain", "Fixture (Atmos)")));
            }
            app.OpenContent(source);
            RenderTarget layout = app.RenderTarget = RenderTarget.Targets.Single(x => x.Name == "5.1.6 with top sides");
            layout.Apply(false);
            Listener listener = new(false) { UpdateRate = 64, SampleRate = 48000 };
            Source bed = new() { Position = new(-1, 0, 1) };
            listener.AttachSource(bed);
            RenderStatsEx stats = new(listener);
            float[] block = new float[64 * Listener.Channels.Length];
            for (int sample = 0; sample < block.Length; sample++) {
                block[sample] = .125f;
            }
            stats.Update(block);
            stats.Update(block);
            PcmSamplePeakAnalysis peaks = new(listener, 64, layout);
            PostRenderReport report = new(listener);
            report.Generate(stats, peaks);

            IntegratedAnalysisReport integrated = new(source, app.SelectedTrack);
            integrated.Add(app, peaks, report, null, true);
            string output = integrated.Publish();
            using TextFieldParser csv = new(output);
            csv.SetDelimiters(",");
            string[] header = csv.ReadFields();
            List<Dictionary<string, string>> rows = [];
            while (!csv.EndOfData) {
                rows.Add(header.Zip(csv.ReadFields()).ToDictionary(pair => pair.First, pair => pair.Second));
            }

            string[] expected = ["PeaGa", "RMSGa", "MacDy", "MicDy", "PeaLF", "RMSLF", "MacLF", "MicLF", "CheSl", "SurUs", "HeiUs"];
            var quality = rows.Where(row => row["row_type"] == "quality").ToArray();
            CollectionAssert.AreEqual(expected, quality.Select(row => row["quality_metric_id"]).ToArray());
            var overall = rows.Single(row => row["row_type"] == "overall");
            Assert.AreEqual(report.Beds.Count.ToString(), overall["bed_count"]);
            Assert.AreEqual(report.DynamicObjects.ToString(), overall["dynamic_object_count"]);
            Assert.AreEqual(report.UnusedTargets.ToString(), overall["unused_target_count"]);
            Assert.IsTrue(overall["bed_positions"].Contains("Label"));
            Assert.AreEqual("Fixture (Atmos).csv", Path.GetFileName(output));
        } finally {
            app.Reset();
            Listener.ReplaceChannels(channels);
            Directory.Delete(folder, recursive: true);
        }
    }

}
