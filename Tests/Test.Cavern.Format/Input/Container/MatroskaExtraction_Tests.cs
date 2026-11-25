using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Cavern.Format.Common;
using Cavern.Format.Container;
using Cavern.Format.Container.Matroska;
using Cavern.Format.Operations;

namespace Test.Cavern.Format.Input.Container;

[TestClass]
public class MatroskaExtraction_Tests {
    [DataTestMethod, Timeout(5000)]
    [DataRow(0, 1)]
    [DataRow(2, 4)]
    [DataRow(4, 256)]
    [DataRow(6, 4)]
    public void FrameBoundariesAndConcatenatedPayloadMatch(int lacing, int count) {
        int[] sizes = [256, 296, 85, 311];
        byte[][] frames = Enumerable.Range(0, count)
            .Select(i => Samples((byte)i, lacing == 4 ? 14 : sizes[i])).ToArray();
        using MemoryStream stream = new(MakeBlock(129, -5, lacing, frames));
        Block parsed = new(stream, new MatroskaTree(stream));
        Assert.AreEqual(129L, parsed.Track);
        Assert.AreEqual((short)-5, parsed.TimeStamp);
        CollectionAssert.AreEqual(Join(frames), parsed.GetData());
        byte[][] parsedFrames = parsed.GetFrames();
        Assert.AreEqual(count, parsedFrames.Length);
        for (int i = 0; i < count; i++) {
            CollectionAssert.AreEqual(frames[i], parsedFrames[i]);
        }
    }

    [DataTestMethod, Timeout(5000)]
    [DataRow(2, "01FF")]
    [DataRow(2, "017F01")]
    [DataRow(4, "01010203")]
    [DataRow(6, "01")]
    [DataRow(6, "0282BAFF")]
    [DataRow(6, "0240")]
    public void RejectsInvalidOrTruncatedLaces(int flags, string laceHex) {
        using MemoryStream stream = new(Element(0xA3, [0x81, 0, 0, (byte)flags, .. Convert.FromHexString(laceHex)]));
        MatroskaTree element = new(stream);
        try {
            _ = new Block(stream, element);
            Assert.Fail("The corrupt block was accepted.");
        } catch (Exception error) when (error is InvalidDataException or EndOfStreamException) {
        }
    }

    [TestMethod]
    public void ExtractionStartsAtFirstPhysicalBlockAndIgnoresOtherTrackAndBlockMetadata() {
        byte[][] frames = [Samples(1, 25), Samples(2, 311), Samples(3, 14), Samples(4, 35), Samples(5, 42)];
        byte[] first = Join(
            MakeBlock(129, -1, 0, [frames[0]]), Number(0xE7, 1),
            MakeBlock(2, 0, 0, [Samples(99, 12)]),
            MakeBlock(129, -1, 2, frames[1..3], group: true));
        byte[] last = Join(Number(0xE7, 20), MakeBlock(129, 0, 6, frames[3..]));
        using MemoryStream input = new(FileBytes([first, last], secondTrack: true));
        using MatroskaReader reader = new(input);
        Track track = reader.Tracks[0];
        CollectionAssert.AreEqual(frames[0], track.ReadNextBlock());
        _ = track.ReadNextBlock();
        using MemoryStream output = new();
        Extract(track, output);
        CollectionAssert.AreEqual(Join(frames), output.ToArray());
    }

    [TestMethod]
    public void RejectsBlockContentEncodingBeforeWriting() {
        byte[] cluster = Join(Number(0xE7, 0), MakeBlock(129, 0, 0, [Samples(1, 16)]));
        using MemoryStream input = new(FileBytes([cluster], encoded: true));
        using MatroskaReader reader = new(input);
        using MemoryStream output = new();
        Assert.ThrowsException<NotSupportedException>(() => Extract(reader.Tracks[0], output));
        Assert.AreEqual(0, output.ToArray().Length);
    }

    [TestMethod]
    public void IndexedLookupAcceptsChildEndingAtExactParentBoundary() {
        using MemoryStream input = new(Element(0x1F43B675, Number(0xE7, 0)));
        var parent = new MatroskaTree(input);
        Assert.IsNotNull(parent.GetChild(input, 0xE7, 0));
    }

    [TestMethod, Timeout(60000)]
    public async Task GenuineAtmosMatchesMkvextractAndDecodesWithoutLoss() {
        string fixture = Environment.GetEnvironmentVariable("CAVERN_LACE_FIXTURE");
        string mkvextract = Environment.GetEnvironmentVariable("CAVERN_TEST_MKVEXTRACT");
        string truehdd = Environment.GetEnvironmentVariable("CAVERN_TEST_TRUEHDD");
        if (fixture == null || mkvextract == null || truehdd == null) {
            Assert.Inconclusive("Set CAVERN_LACE_FIXTURE, CAVERN_TEST_MKVEXTRACT and CAVERN_TEST_TRUEHDD for independent tool verification.");
        }

        byte[] original = File.ReadAllBytes(fixture);
        List<byte[]> frames = [];
        for (int offset = 0; offset < original.Length;) {
            int size = (BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(offset)) & 0xfff) * 2;
            Assert.IsTrue(size >= 8 && offset + size <= original.Length);
            frames.Add(original[offset..(offset + size)]);
            offset += size;
        }
        string work = Directory.CreateTempSubdirectory("cavern-lacing-").FullName;
        try {
            foreach (int lacing in new[] { 0, 2, 6 }) {
                string input = Path.Combine(work, $"source-{lacing}.mka");
                string cav = Path.Combine(work, $"cavern-{lacing}.thd"), refFile = Path.Combine(work, $"mkvextract-{lacing}.thd");
                using MemoryStream blocks = new();
                blocks.Write(Number(0xE7, 0));
                int width = lacing == 0 ? 1 : 5;
                for (int start = 0; start < frames.Count; start += width) {
                    byte[][] part = frames.Skip(start).Take(width).ToArray();
                    blocks.Write(MakeBlock(129, (short)(start * 5 / 6), part.Length == 1 ? 0 : lacing, part));
                }
                File.WriteAllBytes(input, FileBytes([blocks.ToArray()]));
                using (MatroskaReader reader = new(input)) {
                    Extract(reader.Tracks[0], File.Create(cav));
                }
                await Run(mkvextract, input, "tracks", $"0:{refFile}");
                CollectionAssert.AreEqual(original, File.ReadAllBytes(refFile));
                CollectionAssert.AreEqual(File.ReadAllBytes(refFile), File.ReadAllBytes(cav));

                string decoded = await Run(truehdd, "decode", "--json", cav, "--output-path", Path.Combine(work, $"decoded-{lacing}"));
                using JsonDocument result = JsonDocument.Parse(decoded);
                Assert.AreEqual(16000, result.RootElement.GetProperty("samples").GetInt32());
                foreach (string field in new[] { "skippedFrames", "concealedFrames", "invalidBranches" }) {
                    Assert.AreEqual(0, result.RootElement.GetProperty(field).GetInt32(), field);
                }
            }
        } finally {
            Directory.Delete(work, true);
        }
    }

    static async Task<string> Run(string executable, params string[] args) {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string arg in args) {
            start.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(start);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(0, process.ExitCode, await stderr);
        return await stdout;
    }

    static void Extract(Track track, Stream output) {
        using (output)
        using (ExtractTrackFromContainer extractor = new(track, output)) {
            while (extractor.Process()) {
            }
        }
    }

    static byte[] FileBytes(byte[][] clusters, bool secondTrack = false, bool encoded = false) {
        byte[] header = Element(0x1A45DFA3, Join(Number(0x4286, 1), Number(0x42F7, 1), Number(0x42F2, 4), Number(0x42F3, 8),
            Element(0x4282, Encoding.ASCII.GetBytes("matroska")), Number(0x4287, 4), Number(0x4285, 2)));
        byte[] info = Element(0x1549A966, Join(Number(0x2AD7B1, 1000000), Float(0x4489, 333),
            Element(0x4D80, Encoding.ASCII.GetBytes("Cavern tests")), Element(0x5741, Encoding.ASCII.GetBytes("Cavern tests"))));
        byte[] track = TrackEntry(129, "A_TRUEHD", encoded);
        byte[] tracks = Element(0x1654AE6B, secondTrack ? Join(track, TrackEntry(2, "A_PCM/INT/LIT", false)) : track);
        return Join(header, Element(0x18538067, [.. info, .. tracks, .. Join(clusters.Select(x => Element(0x1F43B675, x)).ToArray())]));
    }

    static byte[] TrackEntry(int id, string codec, bool encoded) => Element(0xAE, Join(Number(0xD7, id), Number(0x73C5, id),
        Number(0x83, 2), Element(0x86, Encoding.ASCII.GetBytes(codec)), Element(0xE1, Join(Float(0xB5, 48000), Number(0x9F, 12))),
        encoded ? Element(0x6D80, Element(0x6240, Element(0x5034, Number(0x4254, 3)))) : []));

    static byte[] MakeBlock(int track, short time, int lacing, byte[][] frames, bool group = false) {
        using MemoryStream payload = new();
        payload.Write(Vint(track));
        payload.Write([(byte)(time >> 8), (byte)time, (byte)(0x80 | lacing)]);
        if (lacing != 0) {
            payload.WriteByte((byte)(frames.Length - 1));
        }
        if (lacing == 2) {
            foreach (byte[] frame in frames[..^1]) {
                for (int remaining = frame.Length; remaining >= 255; remaining -= 255) {
                    payload.WriteByte(255);
                }
                payload.WriteByte((byte)(frame.Length % 255));
            }
        } else if (lacing == 6) {
            payload.Write(Vint(frames[0].Length));
            for (int i = 1; i < frames.Length - 1; i++) {
                payload.Write(Vint(frames[i].Length - frames[i - 1].Length + 8191, 2));
            }
        }
        payload.Write(Join(frames));
        return group ? Element(0xA0, Join(Element(0xA1, payload.ToArray()), Number(0x9B, 16), Element(0xEC, Samples(17, 12)))) :
            Element(0xA3, payload.ToArray());
    }

    static byte[] Samples(byte seed, int length) => Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();
    static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    static byte[] Number(int id, int value) => Element(id, Integer(value));
    static byte[] Float(int id, double value) {
        byte[] storage = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(storage, value);
        return Element(id, storage);
    }

    static byte[] Integer(long value) {
        byte[] storage = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(storage, value);
        int start = Array.FindIndex(storage, x => x != 0);
        return start < 0 ? [0] : storage[start..];
    }

    static byte[] Element(int id, byte[] data) => Join(Integer(id), Vint(data.Length), data);
    static byte[] Vint(int value, int width = 0) {
        if (width == 0) {
            width = 1;
            while (value >= (1L << (7 * width)) - 1) {
                width++;
            }
        }
        return Integer((1L << (7 * width)) + value);
    }
}
