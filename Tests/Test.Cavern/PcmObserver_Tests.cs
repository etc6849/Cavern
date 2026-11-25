using Cavern;
using Cavern.Channels;
using Cavern.Listeners;

namespace Test.Cavern;

[TestClass, DoNotParallelize]
public class PcmObserver_Tests {
    [TestMethod]
    public void ObservePreLimiterPreservesPcmAndLimiterRecovery() {
        Channel[] original = Listener.Channels;
        try {
            Listener.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref510));
            Listener unobserved = CreateListener(false);
            Listener observed = CreateListener(false);
            float[] before = null;
            observed.ObservePreLimiterPcm = pcm => before = pcm.ToArray();
            float[] inputs = [1.25f, .5f, -.75f, .5f];
            float[] expected = [.9f, .5f * (.72f + 64f / 48000), -.75f * (.72f + 128f / 48000),
                .5f * (.72f + 192f / 48000)];
            for (int block = 0; block < inputs.Length; block++) {
                float[] control = unobserved.Render();
                float[] output = observed.Render();
                CollectionAssert.AreEqual(Array.ConvertAll(control, BitConverter.SingleToInt32Bits),
                    Array.ConvertAll(output, BitConverter.SingleToInt32Bits));
                Assert.AreEqual(inputs[block], before[3]);
                Assert.AreEqual(expected[block], output[3], .000001f);
                if (block == 0) {
                    Assert.AreEqual(.5f, before[9]);
                    Assert.AreEqual(.36f, output[9], .000001f);
                }
            }
        } finally {
            Listener.ReplaceChannels(original);
        }
    }

    [TestMethod]
    public void ConvolutionRunsAfterListenerTapAndLimiter() {
        Channel[] original = Listener.Channels;
        try {
            Listener.ReplaceChannels(ChannelPrototype.ToLayout(ChannelPrototype.ref510));
            Listener unobserved = CreateListener(true);
            Listener observed = CreateListener(true);
            float peak = 0;
            int callbacks = 0;
            observed.ObservePreLimiterPcm = pcm => {
                callbacks++;
                peak = pcm[3];
            };

            float[] control = unobserved.Render();
            float[] output = observed.Render();
            CollectionAssert.AreEqual(Array.ConvertAll(control, BitConverter.SingleToInt32Bits),
                Array.ConvertAll(output, BitConverter.SingleToInt32Bits));
            Assert.AreEqual(1, callbacks);
            Assert.AreEqual(1.25f, peak);
            Assert.AreEqual(.45f, output[3], .00001f);
        } finally {
            Listener.ReplaceChannels(original);
        }
    }

    static Listener CreateListener(bool convolution) {
        Listener listener = convolution ? new ConvolvedListener(false) : new Listener(false);
        listener.UpdateRate = 64;
        listener.SampleRate = 48000;
        listener.DirectLFE = true;
        listener.LFESeparation = true;
        if (listener is ConvolvedListener convolved) {
            float[] ir = [.5f, .5f, .5f, .5f, .5f, .5f];
            convolved.ConvolutionClip = new Clip(ir, 6, 48000);
        }

        float[] input = new float[4 * 64];
        input[0] = 1.25f;
        input[1] = .5f;
        input[64] = .5f;
        input[128] = -.75f;
        input[192] = .5f;
        listener.AttachSource(new Source {
            Clip = new Clip(input, 1, listener.SampleRate),
            VolumeRolloff = Rolloffs.Disabled,
            LFE = true
        });
        return listener;
    }
}
