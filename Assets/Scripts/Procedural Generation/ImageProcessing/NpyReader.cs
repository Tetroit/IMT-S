using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProceduralGeneration.ImageProcessing
{
    [Serializable]
    public class NpyArray
    {
        public int[] shape { get; private set; }
        public float[] data { get; private set; }

        public NpyArray(int[] shape, float[] data)
        {
            this.shape = shape;
            this.data = data;
        }

        // Convert multidimensional index to flat index
        public float Get(params int[] indices)
        {
            if (indices.Length != shape.Length)
                throw new ArgumentException("Wrong number of indices.");

            int index = 0;
            int stride = 1;

            for (int i = shape.Length - 1; i >= 0; i--)
            {
                if (indices[i] < 0 || indices[i] >= shape[i])
                    throw new IndexOutOfRangeException();

                index += indices[i] * stride;
                stride *= shape[i];
            }

            return data[index];
        }
    }

    public static class NpyReader
    {

        public static NpyArray Load(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                // ---------------------------------------------------------
                // Magic number
                // ---------------------------------------------------------

                byte[] magic = reader.ReadBytes(6);

                if (magic.Length != 6 ||
                    magic[0] != 0x93 ||
                    magic[1] != (byte)'N' ||
                    magic[2] != (byte)'U' ||
                    magic[3] != (byte)'M' ||
                    magic[4] != (byte)'P' ||
                    magic[5] != (byte)'Y')
                {
                    throw new Exception("Not a valid .npy file.");
                }

                // ---------------------------------------------------------
                // Version
                // ---------------------------------------------------------

                byte major = reader.ReadByte();
                byte minor = reader.ReadByte();

                if (major != 1 && major != 2)
                    throw new NotSupportedException(
                        $"Unsupported .npy version: {major}.{minor}");

                // ---------------------------------------------------------
                // Header length
                // ---------------------------------------------------------

                int headerLength;

                if (major == 1)
                {
                    headerLength = reader.ReadUInt16();
                }
                else
                {
                    headerLength = reader.ReadInt32();
                }

                // ---------------------------------------------------------
                // Header
                // ---------------------------------------------------------

                byte[] headerBytes = reader.ReadBytes(headerLength);
                string header = Encoding.ASCII.GetString(headerBytes);

                // ---------------------------------------------------------
                // dtype
                // ---------------------------------------------------------

                Match dtypeMatch = Regex.Match(
                    header,
                    @"['""]descr['""]\s*:\s*['""]([^'""]+)['""]"
                );

                if (!dtypeMatch.Success)
                    throw new Exception("Could not read dtype from .npy header.");

                string dtype = dtypeMatch.Groups[1].Value;

                // e.g. "<f4": byte order, type kind, item size
                Match dtypeParts = Regex.Match(dtype, @"^([<>|=]?)([fiub])(\d+)$");

                if (!dtypeParts.Success)
                    throw new NotSupportedException($"Unsupported .npy dtype: {dtype}");

                bool fileLittleEndian = dtypeParts.Groups[1].Value != ">";
                char kind = dtypeParts.Groups[2].Value[0];
                int itemSize = int.Parse(dtypeParts.Groups[3].Value);

                bool supported =
                    kind == 'f' ? itemSize == 4 || itemSize == 8 :
                    kind == 'b' ? itemSize == 1 :
                    itemSize == 1 || itemSize == 2 || itemSize == 4 || itemSize == 8;

                if (!supported)
                    throw new NotSupportedException($"Unsupported .npy dtype: {dtype}");

                // ---------------------------------------------------------
                // Fortran order
                // ---------------------------------------------------------

                Match fortranMatch = Regex.Match(
                    header,
                    @"['""]fortran_order['""]\s*:\s*(True|False)"
                );

                if (!fortranMatch.Success)
                    throw new Exception("Could not read fortran_order.");

                bool fortranOrder =
                    fortranMatch.Groups[1].Value == "True";

                if (fortranOrder)
                {
                    throw new NotSupportedException(
                        "Fortran-ordered arrays are not supported.");
                }

                // ---------------------------------------------------------
                // Shape
                // ---------------------------------------------------------

                Match shapeMatch = Regex.Match(
                    header,
                    @"['""]shape['""]\s*:\s*\(([^)]*)\)"
                );

                if (!shapeMatch.Success)
                    throw new Exception("Could not read shape from .npy header.");

                string shapeString = shapeMatch.Groups[1].Value;

                string[] dimensions = shapeString
                    .Split(new[]
                    {
                        ','
                    }, StringSplitOptions.RemoveEmptyEntries);

                int[] shape = new int[dimensions.Length];

                for (int i = 0; i < dimensions.Length; i++)
                {
                    shape[i] = int.Parse(dimensions[i].Trim());
                }

                // ---------------------------------------------------------
                // Calculate number of elements
                // ---------------------------------------------------------

                long elementCount = 1;

                for (int i = 0; i < shape.Length; i++)
                {
                    elementCount *= shape[i];
                }

                if (elementCount > int.MaxValue)
                    throw new NotSupportedException(
                        "Array is too large for this reader.");

                // ---------------------------------------------------------
                // Read data
                // ---------------------------------------------------------

                float[] data = new float[elementCount];

                ReadData(
                    stream,
                    data,
                    kind,
                    itemSize,
                    fileLittleEndian != BitConverter.IsLittleEndian);

                return new NpyArray(shape, data);
            }
        }

        private const int ChunkElements = 1 << 20;

        // Reads in chunks instead of per element, large arrays would otherwise take very long.
        private static void ReadData(
            Stream stream,
            float[] data,
            char kind,
            int itemSize,
            bool swapBytes)
        {
            byte[] buffer = new byte[ChunkElements * itemSize];
            int done = 0;

            while (done < data.Length)
            {
                int count = Math.Min(ChunkElements, data.Length - done);
                int byteCount = count * itemSize;

                ReadExactly(stream, buffer, byteCount);

                if (swapBytes && itemSize > 1)
                {
                    for (int i = 0; i < byteCount; i += itemSize)
                        Array.Reverse(buffer, i, itemSize);
                }

                if (kind == 'f' && itemSize == 4)
                {
                    Buffer.BlockCopy(buffer, 0, data, done * 4, byteCount);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        data[done + i] = Convert(buffer, i * itemSize, kind, itemSize);
                }

                done += count;
            }
        }

        private static float Convert(byte[] b, int o, char kind, int itemSize)
        {
            switch (kind)
            {
                case 'f':
                    return (float)BitConverter.ToDouble(b, o);

                case 'i':
                    switch (itemSize)
                    {
                        case 1: return (sbyte)b[o];
                        case 2: return BitConverter.ToInt16(b, o);
                        case 4: return BitConverter.ToInt32(b, o);
                        default: return BitConverter.ToInt64(b, o);
                    }

                case 'u':
                    switch (itemSize)
                    {
                        case 1: return b[o];
                        case 2: return BitConverter.ToUInt16(b, o);
                        case 4: return BitConverter.ToUInt32(b, o);
                        default: return BitConverter.ToUInt64(b, o);
                    }

                default: // 'b' bool
                    return b[o] != 0 ? 1f : 0f;
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;

            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);

                if (n <= 0)
                    throw new EndOfStreamException("Unexpected end of .npy data.");

                read += n;
            }
        }
    }
}