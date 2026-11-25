using System.Net;
using System.Text;

using Cavern.Format.Decoders;

namespace Test.Cavern.Format.Input;

[TestClass]
public class CafPayloadCoverage_Tests {
    [DataTestMethod]
    [DataRow(8, 8, true)]
    [DataRow(8, 4, false)]
    [DataRow(7, 7, false)]
    public void VerifyDeclaredPcmExtentAndAlignment(int declared, int actual, bool complete) {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, true);
        writer.Write("caff"u8);
        WriteBigEndian(writer, 65536);
        writer.Write("desc"u8);
        writer.Write(IPAddress.HostToNetworkOrder(32L));
        writer.Write(IPAddress.HostToNetworkOrder(BitConverter.DoubleToInt64Bits(48000)));
        writer.Write("lpcm"u8);
        WriteBigEndian(writer, 2);
        WriteBigEndian(writer, 4);
        WriteBigEndian(writer, 1);
        WriteBigEndian(writer, 2);
        WriteBigEndian(writer, 16);
        writer.Write("data"u8);
        writer.Write(IPAddress.HostToNetworkOrder(declared + 4L));
        WriteBigEndian(writer, 0);
        writer.Write(new byte[actual]);
        stream.Position = 0;

        CoreAudioFormatDecoder decoder = new(stream);
        Assert.AreEqual(complete, decoder.HasCompletePcmPayload);
        Assert.AreEqual(declared / 4L, decoder.Length);
    }

    static void WriteBigEndian(BinaryWriter writer, int value) => writer.Write(IPAddress.HostToNetworkOrder(value));
}
