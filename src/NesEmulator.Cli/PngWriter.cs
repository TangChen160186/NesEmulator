using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Cli;

/// <summary>
/// 不依赖 System.Drawing 的极简 PNG 写出器，用于无头模式保存截图。
/// 只支持 8 位真彩色、无滤波、无隔行，正好对应 NES 的 256×240 帧。
/// </summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Save(PpuFrameBuffer frameBuffer, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        int width = PpuFrameBuffer.Width;
        int height = PpuFrameBuffer.Height;
        var raw = new byte[height * (1 + width * 3)];
        ReadOnlySpan<uint> pixels = frameBuffer.Pixels.Span;

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * (1 + width * 3);
            raw[rowOffset] = 0; // filter: None
            for (int x = 0; x < width; x++)
            {
                uint argb = pixels[y * width + x];
                int pixelOffset = rowOffset + 1 + x * 3;
                raw[pixelOffset] = (byte)(argb >> 16);
                raw[pixelOffset + 1] = (byte)(argb >> 8);
                raw[pixelOffset + 2] = (byte)argb;
            }
        }

        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 2;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(file, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc32(typeBytes, data));
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (byte b in first)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in second)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFF_FFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
