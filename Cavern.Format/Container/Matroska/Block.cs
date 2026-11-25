using System;
using System.IO;

using Cavern.Format.Common;
using Cavern.Format.Utilities;

namespace Cavern.Format.Container.Matroska {
    /// <summary>
    /// One or multiple frames of a Matroska file's single track.
    /// </summary>
    internal class Block {
        public enum Lacing {
            None = 0x00,
            Xiph = 0x02,
            FixedSize = 0x04,
            EBML = 0x06
        }

        /// <summary>
        /// The frame itself doesn't reference any other frames and no frame after this can reference any frame before this.
        /// </summary>
        public bool IsKeyframe => (flags & keyframeFlag) != 0;

        /// <summary>
        /// The duration of this block is 0.
        /// </summary>
        public bool IsInvisible => (flags & invisibleFlag) != 0;

        /// <summary>
        /// Used method of storing multiple frames in this block.
        /// </summary>
        public Lacing LacingType => (Lacing)(flags & lacingFlags);

        /// <summary>
        /// This frame can be dropped on poor decoder performance.
        /// </summary>
        public bool IsDiscardable => (flags & discardableFlag) != 0;

        /// <summary>
        /// Used <see cref="Track"/> from a <see cref="MatroskaReader"/>.
        /// </summary>
        public long Track { get; private set; }

        /// <summary>
        /// Timing relative to <see cref="Cluster.TimeStamp"/>.
        /// </summary>
        public short TimeStamp { get; private set; }

        /// <summary>
        /// Bit mask of property flags.
        /// </summary>
        readonly byte flags;

        /// <summary>
        /// Number of frames contained in this block.
        /// </summary>
        readonly int frameCount;

        /// <summary>
        /// Length of each frame's raw data.
        /// </summary>
        readonly int[] frameSizes;

        /// <summary>
        /// Position of the first byte of the first frame in the file.
        /// </summary>
        readonly long firstFrame;

        /// <summary>
        /// Source stream of the block, which might be a <see cref="MemoryStream"/>.
        /// </summary>
        readonly Stream reader;

        /// <summary>
        /// Parse the metadata of a data block.
        /// </summary>
        public Block(Stream reader, MatroskaTree source) {
            source.Position(reader);
            this.reader = reader;
            long end = checked(reader.Position + source.Length);
            if (end > reader.Length) {
                throw new EndOfStreamException("Truncated Matroska block.");
            }
            Track = ReadVint(end);
            if (Track == 0) {
                throw new InvalidDataException("Invalid Matroska block track number.");
            }
            TimeStamp = (short)((ReadHeaderByte(end) << 8) | ReadHeaderByte(end));
            flags = (byte)ReadHeaderByte(end);

            Lacing lacing = LacingType;
            frameCount = lacing == Lacing.None ? 1 : ReadHeaderByte(end) + 1;
            frameSizes = new int[frameCount];
            if (lacing != Lacing.None && frameCount == 1) {
                throw new InvalidDataException("A Matroska lace must contain multiple frames.");
            }

            long knownSize = 0;
            if (lacing == Lacing.Xiph) {
                for (int frame = 0; frame < frameCount - 1; frame++) {
                    long size = 0;
                    int part;
                    do {
                        part = ReadHeaderByte(end);
                        size += part;
                    } while (part == 255);
                    frameSizes[frame] = FrameSize(size);
                    knownSize += size;
                }
            } else if (lacing == Lacing.EBML) {
                long size = ReadVint(end);
                frameSizes[0] = FrameSize(size);
                knownSize = size;
                for (int frame = 1; frame < frameCount - 1; frame++) {
                    size += ReadVint(end, signed: true);
                    frameSizes[frame] = FrameSize(size);
                    knownSize += size;
                }
            }

            // All lace-size metadata precedes the first byte of the payload.
            firstFrame = reader.Position;
            payloadLength = FrameSize(end - firstFrame);
            if (lacing == Lacing.FixedSize) {
                if (payloadLength % frameCount != 0) {
                    throw new InvalidDataException("Fixed Matroska lace size is not divisible by the frame count.");
                }
                for (int frame = 0; frame < frameCount; frame++) {
                    frameSizes[frame] = payloadLength / frameCount;
                }
            } else {
                frameSizes[frameCount - 1] = FrameSize(payloadLength - knownSize);
            }
        }

        readonly int payloadLength;

        static int FrameSize(long size) {
            if (size < 0 || size > int.MaxValue) {
                throw new InvalidDataException("Invalid Matroska frame size.");
            }
            return (int)size;
        }

        int ReadHeaderByte(long end) {
            int value = reader.Position < end ? reader.ReadByte() : -1;
            if (value < 0) {
                throw new EndOfStreamException("Truncated Matroska block header or lace table.");
            }
            return value;
        }

        long ReadVint(long end, bool signed = false) {
            int first = ReadHeaderByte(end);
            if (first == 0) {
                throw new InvalidDataException("Invalid Matroska block variable-length integer.");
            }
            int width = 1;
            int marker = 0x80;
            while ((first & marker) == 0) {
                marker >>= 1;
                width++;
            }

            long value = first & (marker - 1);
            for (int i = 1; i < width; i++) {
                value = (value << 8) | (uint)ReadHeaderByte(end);
            }
            return signed ? value - ((1L << (7 * width - 1)) - 1) : value;
        }

        /// <summary>
        /// Write a block of data to a Matroska file that's under creation.
        /// </summary>
        /// <param name="tree">EBML writer object</param>
        /// <param name="writer">The stream used by the <paramref name="tree"/> writer for writing raw bytes</param>
        /// <param name="keyframe">This block can be decoded on its own</param>
        /// <param name="track">Unique identifier of the track</param>
        /// <param name="timeStamp">Timing relative to <see cref="Cluster.TimeStamp"/></param>
        /// <param name="data">Raw data of the block</param>
        /// <remarks>Only a single frame can be written using this function.</remarks>
        public static void Write(MatroskaTreeWriter tree, Stream writer, bool keyframe, int track, short timeStamp, byte[] data) {
            tree.OpenSequence(MatroskaTree.Segment_Cluster_SimpleBlock, 3);
            VarInt.Write(writer, track);
            writer.WriteByte((byte)(timeStamp >> 8));
            writer.WriteByte((byte)timeStamp);
            writer.WriteByte(keyframe ? keyframeFlag : (byte)0);
            writer.Write(data);
            tree.CloseSequence();
        }

        /// <summary>
        /// Read all stream data from this block, without separating frames.
        /// </summary>
        public byte[] GetData() {
            reader.Position = firstFrame;
            return reader.ReadAllBytes(payloadLength);
        }

        /// <summary>
        /// Get the raw stream bytes for each frame contained in this block.
        /// </summary>
        public byte[][] GetFrames() {
            reader.Position = firstFrame;
            byte[][] result = new byte[frameCount][];
            for (int frame = 0; frame < frameCount; ++frame) {
                result[frame] = reader.ReadAllBytes(frameSizes[frame]);
            }
            return result;
        }

        /// <summary>
        /// Provides basic information about the block.
        /// </summary>
        public override string ToString() => $"Matroska block, track {Track}, relative time: {TimeStamp}";

        /// <summary>
        /// Flag mask for <see cref="IsKeyframe"/>.
        /// </summary>
        const byte keyframeFlag = 0x80;

        /// <summary>
        /// Flag mask for <see cref="IsInvisible"/>.
        /// </summary>
        const byte invisibleFlag = 0x08;

        /// <summary>
        /// Flag mask for <see cref="LacingType"/>.
        /// </summary>
        const byte lacingFlags = 0x06;

        /// <summary>
        /// Flag mask for <see cref="IsDiscardable"/>.
        /// </summary>
        const byte discardableFlag = 0x01;
    }
}