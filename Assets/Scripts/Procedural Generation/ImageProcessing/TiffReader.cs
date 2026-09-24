using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace ProceduralGeneration.ImageProcessing
{
    [Serializable]
    public class TifImage
    {
        [SerializeField] private int _width;
        [SerializeField] private int _height;
        [SerializeField] private int _channels;
        private int[] _data;

        public int width => _width;
        public int height => _height;
        public int channels => _channels;

        // Raw integer sample values, e.g. class ids for segmentation images.
        public int[] data => _data;


        public TifImage(
            int width,
            int height,
            int channels,
            int[] data)
        {
            this._width = width;
            this._height = height;
            this._channels = channels;
            this._data = data;
        }

        public int Get(int x, int y, int channel = 0)
        {
            return data[((long)y * _width + x) * _channels + channel];
        }
    }

    // Baseline TIFF reader.
    // Supports: strips and tiles, uncompressed and Deflate compression,
    // horizontal (2) predictor, 8/16/32 bit integer samples,
    // chunky planar configuration.
    // Decoded sample buffers are kept little-endian (all Unity targets are little-endian).
    public static class TiffReader
    {

        private sealed class Reader
        {
            public readonly BinaryReader br;
            public readonly bool littleEndian;

            public Reader(BinaryReader br, bool littleEndian)
            {
                this.br = br;
                this.littleEndian = littleEndian;
            }

            public ushort U16()
            {
                byte a = br.ReadByte();
                byte b = br.ReadByte();

                return littleEndian
                    ? (ushort)(a | b << 8)
                    : (ushort)(a << 8 | b);
            }

            public uint U32()
            {
                byte a = br.ReadByte();
                byte b = br.ReadByte();
                byte c = br.ReadByte();
                byte d = br.ReadByte();

                return littleEndian
                    ? (uint)(a | b << 8 | c << 16 | d << 24)
                    : (uint)(a << 24 | b << 16 | c << 8 | d);
            }

            public void Seek(long offset)
            {
                br.BaseStream.Seek(offset, SeekOrigin.Begin);
            }
        }

        private struct Entry
        {
            public ushort tag;
            public ushort type;
            public uint count;
            public uint value;
        }

        private const ushort TagImageWidth = 256;
        private const ushort TagImageLength = 257;
        private const ushort TagBitsPerSample = 258;
        private const ushort TagCompression = 259;
        private const ushort TagStripOffsets = 273;
        private const ushort TagSamplesPerPixel = 277;
        private const ushort TagRowsPerStrip = 278;
        private const ushort TagStripByteCounts = 279;
        private const ushort TagPlanarConfiguration = 284;
        private const ushort TagPredictor = 317;
        private const ushort TagTileWidth = 322;
        private const ushort TagTileLength = 323;
        private const ushort TagTileOffsets = 324;
        private const ushort TagTileByteCounts = 325;
        private const ushort TagSampleFormat = 339;

        public static TifImage Load(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (BinaryReader br = new BinaryReader(fs))
            {
                // ---------------------------------------------------------
                // Header
                // ---------------------------------------------------------

                byte b0 = br.ReadByte();
                byte b1 = br.ReadByte();

                bool littleEndian;

                if (b0 == 'I' && b1 == 'I')
                    littleEndian = true;
                else if (b0 == 'M' && b1 == 'M')
                    littleEndian = false;
                else
                    throw new Exception("Invalid TIFF byte order.");

                Reader r = new Reader(br, littleEndian);

                ushort magic = r.U16();

                if (magic == 43)
                    throw new NotSupportedException("BigTIFF files are not supported.");

                if (magic != 42)
                    throw new Exception("Invalid TIFF magic number.");

                uint ifdOffset = r.U32();

                r.Seek(ifdOffset);

                ushort entryCount = r.U16();

                Entry[] entries = new Entry[entryCount];

                for (int i = 0; i < entryCount; i++)
                {
                    entries[i].tag = r.U16();
                    entries[i].type = r.U16();
                    entries[i].count = r.U32();
                    entries[i].value = r.U32();
                }

                // ---------------------------------------------------------
                // TIFF tags
                // ---------------------------------------------------------

                int width = GetInt(r, Find(entries, TagImageWidth));
                int height = GetInt(r, Find(entries, TagImageLength));

                int samplesPerPixel = GetInt(r, entries, TagSamplesPerPixel, 1);

                long[] bits = Has(entries, TagBitsPerSample)
                    ? GetLongs(r, Find(entries, TagBitsPerSample))
                    : new long[] { 1 };
                int bitsPerSample = (int)bits[0];

                for (int i = 1; i < bits.Length; i++)
                {
                    if (bits[i] != bitsPerSample)
                        throw new NotSupportedException(
                            "Different channel bit depths are unsupported.");
                }

                if (bitsPerSample != 8 &&
                    bitsPerSample != 16 &&
                    bitsPerSample != 32)
                {
                    throw new NotSupportedException(
                        "Unsupported TIFF bit depth: " +
                        bitsPerSample);
                }

                int bytesPerSample = bitsPerSample / 8;

                // 1 = none, 8 / 32946 = Deflate (zlib)
                int compression = GetInt(r, entries, TagCompression, 1);

                if (compression != 1 &&
                    compression != 8 &&
                    compression != 32946)
                {
                    throw new NotSupportedException(
                        "Unsupported TIFF compression: " +
                        compression +
                        ". Only uncompressed and Deflate are supported.");
                }

                // 1 = none, 2 = horizontal differencing
                int predictor = GetInt(r, entries, TagPredictor, 1);

                if (predictor != 1 &&
                    predictor != 2)
                {
                    throw new NotSupportedException(
                        "Unsupported TIFF predictor: " +
                        predictor);
                }

                int planarConfiguration = GetInt(r, entries, TagPlanarConfiguration, 1);

                if (planarConfiguration != 1 && samplesPerPixel > 1)
                {
                    throw new NotSupportedException(
                        "Planar (separate) TIFF sample layout is not supported.");
                }

                // 1 = unsigned integer
                // 2 = signed integer
                // 3 = IEEE floating point
                int sampleFormat = GetInt(r, entries, TagSampleFormat, 1);

                if (sampleFormat != 1 && sampleFormat != 2)
                {
                    throw new NotSupportedException(
                        "Only integer TIFFs are supported. SampleFormat = " +
                        sampleFormat);
                }

                // ---------------------------------------------------------
                // Chunk layout (strips or tiles)
                // ---------------------------------------------------------

                bool tiled = Has(entries, TagTileWidth);

                int chunkWidth;
                int chunkHeight;
                long[] chunkOffsets;
                long[] chunkByteCounts;

                if (tiled)
                {
                    chunkWidth = GetInt(r, Find(entries, TagTileWidth));
                    chunkHeight = GetInt(r, Find(entries, TagTileLength));
                    chunkOffsets = GetLongs(r, Find(entries, TagTileOffsets));
                    chunkByteCounts = GetLongs(r, Find(entries, TagTileByteCounts));
                }
                else
                {
                    chunkWidth = width;
                    chunkHeight = Math.Min(
                        GetInt(r, entries, TagRowsPerStrip, height),
                        height);
                    chunkOffsets = GetLongs(r, Find(entries, TagStripOffsets));
                    chunkByteCounts = GetLongs(r, Find(entries, TagStripByteCounts));
                }

                int chunksAcross = (width + chunkWidth - 1) / chunkWidth;
                int chunkRowBytes = chunkWidth * samplesPerPixel * bytesPerSample;

                // ---------------------------------------------------------
                // Allocate
                // ---------------------------------------------------------

                int[] data =
                    new int[
                        (long)width *
                        height *
                        samplesPerPixel];

                // ---------------------------------------------------------
                // Read image chunks
                // ---------------------------------------------------------

                for (int chunk = 0;
                     chunk < chunkOffsets.Length;
                     chunk++)
                {
                    int x0 = chunk % chunksAcross * chunkWidth;
                    int y0 = chunk / chunksAcross * chunkHeight;

                    if (y0 >= height)
                        break;

                    // Tiles are always padded to full size, the last strip is not.
                    int rowsInChunk = tiled
                        ? chunkHeight
                        : Math.Min(chunkHeight, height - y0);

                    byte[] buffer = ReadChunk(
                        r,
                        chunkOffsets[chunk],
                        chunkByteCounts[chunk],
                        compression,
                        chunkRowBytes * rowsInChunk);

                    if (!littleEndian)
                        SwapBytes(buffer, bytesPerSample);

                    if (predictor == 2)
                    {
                        UndoHorizontalPredictor(
                            buffer,
                            chunkWidth * samplesPerPixel,
                            rowsInChunk,
                            samplesPerPixel,
                            bytesPerSample);
                    }

                    int copyWidth = Math.Min(chunkWidth, width - x0);
                    int copyHeight = Math.Min(rowsInChunk, height - y0);

                    for (int y = 0; y < copyHeight; y++)
                    {
                        int srcRow = y * chunkWidth * samplesPerPixel;
                        long dstRow = ((long)(y0 + y) * width + x0) * samplesPerPixel;
                        int rowSamples = copyWidth * samplesPerPixel;

                        for (int s = 0; s < rowSamples; s++)
                        {
                            data[dstRow + s] = DecodeSample(
                                buffer,
                                (srcRow + s) * bytesPerSample,
                                bitsPerSample,
                                sampleFormat);
                        }
                    }
                }

                return new TifImage(
                    width,
                    height,
                    samplesPerPixel,
                    data);
            }
        }

        // -------------------------------------------------------------
        // Pixel data
        // -------------------------------------------------------------

        private static byte[] ReadChunk(
            Reader r,
            long offset,
            long byteCount,
            int compression,
            int expectedSize)
        {
            r.Seek(offset);

            byte[] raw = r.br.ReadBytes((int)byteCount);
            byte[] result = new byte[expectedSize];

            if (compression == 1)
            {
                Buffer.BlockCopy(raw, 0, result, 0, Math.Min(raw.Length, expectedSize));
                return result;
            }

            // Deflate chunks are zlib streams: skip the 2 byte zlib header,
            // DeflateStream only understands the raw deflate payload.
            using (MemoryStream ms = new MemoryStream(raw, 2, raw.Length - 2))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int read = 0;

                while (read < expectedSize)
                {
                    int n = ds.Read(result, read, expectedSize - read);

                    if (n <= 0)
                        break;

                    read += n;
                }
            }

            return result;
        }

        private static void SwapBytes(byte[] buffer, int bytesPerSample)
        {
            if (bytesPerSample == 1)
                return;

            for (int i = 0; i + bytesPerSample <= buffer.Length; i += bytesPerSample)
                Array.Reverse(buffer, i, bytesPerSample);
        }

        // Expects little-endian samples.
        private static void UndoHorizontalPredictor(
            byte[] buffer,
            int rowSamples,
            int rows,
            int samplesPerPixel,
            int bytesPerSample)
        {
            for (int y = 0; y < rows; y++)
            {
                int rowStart = y * rowSamples;

                for (int s = samplesPerPixel; s < rowSamples; s++)
                {
                    int cur = (rowStart + s) * bytesPerSample;
                    int prev = cur - samplesPerPixel * bytesPerSample;

                    // Byte-wise addition with carry = integer addition modulo 2^bits.
                    int carry = 0;

                    for (int b = 0; b < bytesPerSample; b++)
                    {
                        int sum = buffer[cur + b] + buffer[prev + b] + carry;
                        buffer[cur + b] = (byte)sum;
                        carry = sum >> 8;
                    }
                }
            }
        }

        // Expects little-endian samples.
        // Unsigned 32 bit values above int.MaxValue wrap around.
        private static int DecodeSample(
            byte[] b,
            int o,
            int bitsPerSample,
            int sampleFormat)
        {
            switch (bitsPerSample)
            {
                case 8:
                    if (sampleFormat == 2)
                        return (sbyte)b[o];
                    return b[o];

                case 16:
                    ushort u16 = (ushort)(b[o] | b[o + 1] << 8);
                    if (sampleFormat == 2)
                        return (short)u16;
                    return u16;

                default: // 32
                    return b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24;
            }
        }

        // -------------------------------------------------------------
        // TIFF metadata
        // -------------------------------------------------------------

        private static Entry Find(
            Entry[] entries,
            ushort tag)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].tag == tag)
                    return entries[i];
            }

            throw new Exception(
                "Missing TIFF tag: " + tag);
        }

        private static bool Has(
            Entry[] entries,
            ushort tag)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].tag == tag)
                    return true;
            }

            return false;
        }

        private static int GetInt(
            Reader r,
            Entry[] entries,
            ushort tag,
            int defaultValue)
        {
            return Has(entries, tag)
                ? GetInt(r, Find(entries, tag))
                : defaultValue;
        }

        private static int GetInt(
            Reader r,
            Entry entry)
        {
            long[] values = GetLongs(r, entry);
            return (int)values[0];
        }

        private static long[] GetLongs(
            Reader r,
            Entry entry)
        {
            long[] result = new long[entry.count];

            int typeSize;

            switch (entry.type)
            {
                case 1: // BYTE
                    typeSize = 1;
                    break;

                case 3: // SHORT
                    typeSize = 2;
                    break;

                case 4: // LONG
                    typeSize = 4;
                    break;

                default:
                    throw new NotSupportedException(
                        "Unsupported TIFF tag type: " +
                        entry.type);
            }

            long byteSize =
                (long)typeSize * entry.count;

            if (byteSize <= 4)
            {
                // Value stored directly inside the entry.
                byte[] bytes = new byte[4];

                if (r.littleEndian)
                {
                    bytes[0] = (byte)entry.value;
                    bytes[1] = (byte)(entry.value >> 8);
                    bytes[2] = (byte)(entry.value >> 16);
                    bytes[3] = (byte)(entry.value >> 24);
                }
                else
                {
                    bytes[0] = (byte)(entry.value >> 24);
                    bytes[1] = (byte)(entry.value >> 16);
                    bytes[2] = (byte)(entry.value >> 8);
                    bytes[3] = (byte)entry.value;
                }

                using (MemoryStream ms = new MemoryStream(bytes))
                using (BinaryReader br = new BinaryReader(ms))
                {
                    for (int i = 0; i < result.Length; i++)
                    {
                        if (entry.type == 1)
                        {
                            result[i] = br.ReadByte();
                        }
                        else if (entry.type == 3)
                        {
                            byte a = br.ReadByte();
                            byte b = br.ReadByte();

                            result[i] = r.littleEndian
                                ? a | b << 8
                                : a << 8 | b;
                        }
                        else
                        {
                            result[i] = ReadU32(
                                br,
                                r.littleEndian);
                        }
                    }
                }
            }
            else
            {
                r.Seek(entry.value);

                for (int i = 0; i < result.Length; i++)
                {
                    if (entry.type == 1)
                        result[i] = r.br.ReadByte();
                    else if (entry.type == 3)
                        result[i] = r.U16();
                    else
                        result[i] = r.U32();
                }
            }

            return result;
        }

        private static uint ReadU32(
            BinaryReader br,
            bool littleEndian)
        {
            byte a = br.ReadByte();
            byte b = br.ReadByte();
            byte c = br.ReadByte();
            byte d = br.ReadByte();

            return littleEndian
                ? (uint)(a | b << 8 | c << 16 | d << 24)
                : (uint)(a << 24 | b << 16 | c << 8 | d);
        }
    }
}
