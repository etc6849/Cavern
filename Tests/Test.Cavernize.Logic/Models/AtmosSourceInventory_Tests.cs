using System.Numerics;
using System.Text;

using Cavern;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Renderers;
using Cavern.Utilities;

using Cavernize.Logic.Models;
using Cavernize.Logic.Models.RenderTargets;
using Cavernize.Logic.Rendering;

namespace Test.Cavernize.Logic.Models;

[TestClass, DoNotParallelize]
public class AtmosSourceInventory_Tests {
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DamfIdentifiesEncodedBedAndObjectsIndependentlyOfSpeakerLayout(bool fullBed) {
        string folder = Directory.CreateTempSubdirectory().FullName;
        CavernizeCLI.Program app = new();
        try {
            ReferenceChannel[] beds = fullBed ? ChannelPrototype.ref710 : ChannelPrototype.ref510;
            string source = WriteDamf(folder, beds);
            app.OpenContent(source);
            Renderer renderer = app.SelectedTrack.Renderer;
            CollectionAssert.AreEqual(beds, renderer.GetChannels());
            for (int i = 0; i < beds.Length; i++) {
                Assert.AreEqual(beds[i] == ReferenceChannel.ScreenLFE, renderer.Objects[i].LFE);
            }

            string[] layouts = ["5.1.2 side", "5.1.4", "5.1.6 with top sides"];
            var options = new IntegratedAnalysis.Options(layouts.Select(x => RenderTarget.Targets.Single(y => y.Name == x)).ToArray(),
                Quality: false, Verify: true, KeepEvidence: false, Path.Combine(folder, "Work"));
            List<AtmosSourceInventory.Summary> summaries = [];
            var result = IntegratedAnalysis.Run(app, source, app.SelectedTrack, options, () => throw new AssertFailedException(),
                _ => { }, _ => { }, report => summaries.Add(report.Sources));
            Assert.AreEqual(3, summaries.Count);
            foreach (var summary in summaries) {
                CollectionAssert.AreEqual(beds, summary.Beds.Select(bed => bed.Channel).ToArray());
                CollectionAssert.AreEqual(beds.Select(channel => (int)channel).ToArray(), summary.Beds.Select(bed => bed.Id).ToArray());
                CollectionAssert.AreEqual(beds.Select(channel => new AtmosSourceInventory.Position(ChannelPrototype.AlternativePositions[(int)channel])).ToArray(),
                    summary.Beds.Select(bed => bed.Position).ToArray());
                Assert.AreEqual(beds.Length + 5, summary.AttachedSourceSlots);
                Assert.AreEqual(1, summary.DynamicObjects);
                Assert.AreEqual(1, summary.InactiveSources);
                var objects = summary.Objects;
                CollectionAssert.AreEqual(new int?[] { 10, 11, 12, 13, 14 }, objects.Select(obj => obj.DeclaredId).ToArray());
                CollectionAssert.AreEqual(new[] { "stationary", "semi_static", "dynamic", "unverified", "stationary" },
                    objects.Select(obj => obj.Motion).ToArray());
                CollectionAssert.AreEqual(new[] { "signal", "signal", "signal", "silent", "signal" },
                    objects.Select(obj => obj.Activity).ToArray());
                Assert.AreEqual(new AtmosSourceInventory.Position(0, 0, 0), objects[0].LastAuthoredPosition);
                Assert.AreEqual(new AtmosSourceInventory.Position(-0.8064516f, 0.4f, 0.12903225f), objects[1].LastAuthoredPosition);
                Assert.IsNull(objects[3].LastAuthoredPosition);
                Assert.AreEqual(summary.Beds[0].Position, objects[4].LastAuthoredPosition);
            }
            StringAssert.Contains(result.LastReport.Report, "Encoded Atmos objects: 5");
            StringAssert.Contains(result.LastReport.Report, "Inactive object sources (silent decoded PCM): 1");
        } finally {
            app.Reset();
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void CoordinatesAloneNeverIdentifyBedsAndUnmatchedObjectIdsStayUnverified() {
        Listener listener = new(false);
        listener.AttachSource(new Source { Position = Vector3.Zero });
        listener.AttachSource(new Source { Position = new Vector3(-0.8064516f, 0.4f, 0.12903225f) * Listener.EnvironmentSize });
        RenderStats stats = new(listener);
        stats.Update([]);
        var unavailable = AtmosSourceInventory.Unavailable(stats, listener.ActiveSources.Count);
        Assert.IsNull(unavailable.Beds);
        Assert.IsNull(unavailable.Objects);
        Assert.AreEqual(2, unavailable.ObservedStationaryPositions.Count);

        string folder = Directory.CreateTempSubdirectory().FullName;
        CavernizeCLI.Program app = new();
        try {
            string source = WriteDamf(folder, ChannelPrototype.ref510, unmatchedId: true);
            app.OpenContent(source);
            var summary = AtmosSourceInventory.Create(app.SelectedTrack.Renderer, false).Describe(4097, 0);
            Assert.AreEqual(99, summary.Objects[0].DeclaredId);
            Assert.AreEqual(10, summary.Objects[0].RendererId);
            Assert.AreEqual("unverified", summary.Objects[0].Motion);
            Assert.IsNull(summary.Objects[0].LastAuthoredPosition);
            Assert.IsNull(summary.InactiveSources);
        } finally {
            app.Reset();
            Directory.Delete(folder, recursive: true);
        }
    }

    static string WriteDamf(string folder, ReferenceChannel[] beds, bool unmatchedId = false) {
        string source = Path.Combine(folder, "Inventory.atmos");
        StringBuilder root = new("presentations:\n  - type: home\n    bedInstances:\n      - channels:\n");
        foreach (ReferenceChannel bed in beds) {
            root.Append($"          - channel: {bed.GetShortNameDCI()}\n            ID: {(int)bed}\n");
        }
        root.AppendLine("    objects:");
        for (int id = 10; id < 15; id++) {
            root.AppendLine($"      - ID: {(unmatchedId && id == 10 ? 99 : id)}");
        }
        File.WriteAllText(source, root.ToString());

        StringBuilder events = new("events:\n");
        void Event(int id, int frame, string position = null) {
            events.Append($"  - ID: {id}\n    samplePos: {frame}\n    gain: 0\n");
            if (position != null) {
                events.AppendLine($"    pos: {position}");
            }
        }
        Event(10, 0, "[0, 0, 0]");
        Event(11, 0, "[-0.8, 0.1, 0.4]");
        Event(11, 512, "[-0.8064516, 0.12903225, 0.4]");
        for (int pos = 0; pos < 16; pos++) {
            Event(12, pos * 96, $"[0.{pos + 10}, 0.25, 0.7]");
        }
        Event(13, 0);
        Event(14, 0, "[-1, 1, 0]");
        File.WriteAllText(source + ".metadata", events.ToString());

        const int frames = 4097;
        int channels = beds.Length + 5;
        float[] interleaved = new float[channels * frames];
        for (int i = 0; i < channels - 2; i++) {
            interleaved[i] = .08f;
        }
        interleaved[^1] = -.125f;
        using CoreAudioFormatWriter audio = new(source + ".audio", channels, frames, 48000, BitDepth.Int24);
        audio.WriteHeader();
        audio.WriteBlock(interleaved, 0, interleaved.Length);
        return source;
    }
}
