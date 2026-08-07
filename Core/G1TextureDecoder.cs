using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using System.Buffers.Binary;

namespace DWOriginsAssetTool.Core;

public sealed record G1TextureImage(int Width, int Height, byte[] BgraPixels, byte Format);

public static class G1TextureDecoder
{
    public static G1TextureImage ReadFirst(string path, int maxDimension = 1024)
    {
        var b = File.ReadAllBytes(path);
        Require(b.Length >= 40 && System.Text.Encoding.ASCII.GetString(b, 0, 4) == "GT1G", "不是有效的 G1T 文件");
        var tableOffset = checked((int)U32(b, 12)); var textureCount = checked((int)U32(b, 16));
        Require(textureCount > 0 && tableOffset >= 0 && tableOffset + textureCount * 4 <= b.Length, "G1T 贴图表越界");
        var textureOffset = checked((int)U32(b, tableOffset)); var cursor = checked(tableOffset + textureOffset);
        Require(cursor + 8 <= b.Length, "G1T 贴图头越界");
        var mipCount = Math.Max(1, b[cursor] >> 4); var format = b[cursor + 1]; var dimensions = b[cursor + 2]; var extraVersion = b[cursor + 7];
        var width = 1 << (dimensions & 0xf); var height = 1 << (dimensions >> 4); cursor += 8;
        if (extraVersion > 0)
        {
            Require(cursor + 12 <= b.Length, "G1T 扩展头越界");
            var extraSize = checked((int)U32(b, cursor)); cursor += 12;
            if (extraSize >= 0x10) { Require(cursor + 4 <= b.Length, "G1T 宽度越界"); width = checked((int)U32(b, cursor)); cursor += 4; }
            if (extraSize >= 0x14) { Require(cursor + 4 <= b.Length, "G1T 高度越界"); height = checked((int)U32(b, cursor)); cursor += 4; }
        }
        Require(width > 0 && height > 0 && width <= 32768 && height <= 32768, "G1T 贴图尺寸无效");

        var mip = 0; var mipWidth = width; var mipHeight = height;
        while (mip + 1 < mipCount && Math.Max(mipWidth, mipHeight) > maxDimension)
        {
            cursor += EncodedSize(mipWidth, mipHeight, format);
            mipWidth = Math.Max(1, mipWidth / 2); mipHeight = Math.Max(1, mipHeight / 2); mip++;
        }
        var encodedSize = EncodedSize(mipWidth, mipHeight, format);
        Require(cursor >= 0 && cursor + encodedSize <= b.Length, "G1T 像素数据越界");
        var encoded = b.AsSpan(cursor, encodedSize).ToArray();

        if (format is 0x00 or 0x01)
        {
            var bgra = new byte[mipWidth * mipHeight * 4];
            for (var i = 0; i < mipWidth * mipHeight; i++)
            {
                var p = i * 4;
                if (format == 0x01) { bgra[p] = encoded[p]; bgra[p + 1] = encoded[p + 1]; bgra[p + 2] = encoded[p + 2]; bgra[p + 3] = encoded[p + 3]; }
                else { bgra[p] = encoded[p + 2]; bgra[p + 1] = encoded[p + 1]; bgra[p + 2] = encoded[p]; bgra[p + 3] = encoded[p + 3]; }
            }
            return new(mipWidth, mipHeight, bgra, format);
        }

        var compression = format switch
        {
            0x06 or 0x3c or 0x3d or 0x59 or 0x60 => CompressionFormat.Bc1,
            0x07 => CompressionFormat.Bc2,
            0x08 or 0x5b or 0x62 => CompressionFormat.Bc3,
            0x5c or 0x63 => CompressionFormat.Bc4,
            0x5d or 0x64 => CompressionFormat.Bc5,
            0x5e or 0x65 => CompressionFormat.Bc6U,
            0x5f or 0x66 => CompressionFormat.Bc7,
            _ => throw new NotSupportedException($"暂不支持 G1T 格式 0x{format:X2}")
        };
        var pixels = new BcDecoder().DecodeRaw(encoded, mipWidth, mipHeight, compression);
        var output = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            output[i * 4] = pixels[i].b; output[i * 4 + 1] = pixels[i].g;
            output[i * 4 + 2] = pixels[i].r; output[i * 4 + 3] = pixels[i].a;
        }
        return new(mipWidth, mipHeight, output, format);
    }

    private static int EncodedSize(int width, int height, byte format) => format switch
    {
        0x00 or 0x01 => checked(width * height * 4),
        0x06 or 0x3c or 0x3d or 0x59 or 0x5c or 0x60 or 0x63 => checked(((width + 3) / 4) * ((height + 3) / 4) * 8),
        0x07 or 0x08 or 0x5b or 0x5d or 0x5e or 0x5f or 0x62 or 0x64 or 0x65 or 0x66 => checked(((width + 3) / 4) * ((height + 3) / 4) * 16),
        _ => throw new NotSupportedException($"暂不支持 G1T 格式 0x{format:X2}")
    };

    private static uint U32(byte[] b, int offset)
    {
        Require(offset >= 0 && offset + 4 <= b.Length, "G1T 读取越界");
        return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
