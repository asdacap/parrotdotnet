using System.Buffers.Binary;

namespace Parrot.Store;

internal sealed record ImageInspection(string MediaType, int Width, int Height, int FrameCount)
{
    public const int HeaderLength = 12;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Vp8StartCode => [0x9D, 0x01, 0x2A];

    public static string? DetectMediaType(ReadOnlySpan<byte> header) =>
        header.StartsWith(PngSignature) ? "image/png"
        : header.StartsWith(JpegSignature) ? "image/jpeg"
        : header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8) ? "image/gif"
        : header.Length >= HeaderLength && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    public static ImageInspection Inspect(ReadOnlySpan<byte> data) => DetectMediaType(data) switch
    {
        "image/png" => InspectPng(data),
        "image/jpeg" => InspectJpeg(data),
        "image/gif" => InspectGif(data),
        "image/webp" => InspectWebp(data),
        _ => throw new InvalidDataException("The image content is invalid or unsupported."),
    };

    private static ImageInspection Create(string mediaType, int width, int height, int frameCount) =>
        width > 0 && height > 0 && frameCount > 0
            ? new ImageInspection(mediaType, width, height, frameCount)
            : throw new InvalidDataException("The image content is invalid or unsupported.");

    private static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> data, long offset, long length) =>
        offset >= 0 && length >= 0 && offset + length <= data.Length
            ? data.Slice((int)offset, (int)length)
            : throw new InvalidDataException("The image content is truncated or invalid.");

    private static ImageInspection InspectPng(ReadOnlySpan<byte> data)
    {
        var width = 0;
        var height = 0;
        var frameCount = 1;
        var hasImageData = false;
        var offset = (long)PngSignature.Length;
        while (true)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(Read(data, offset, 4));
            var type = Read(data, offset + 4, 4);
            var body = Read(data, offset + 8, length);
            _ = Read(data, offset + 8 + length, 4);
            var isHeader = type.SequenceEqual("IHDR"u8);
            if ((offset == PngSignature.Length) != isHeader)
            {
                throw new InvalidDataException("A PNG image must start with exactly one IHDR chunk.");
            }

            if (isHeader)
            {
                width = BinaryPrimitives.ReadInt32BigEndian(Read(body, 0, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(Read(body, 4, 4));
            }
            else if (type.SequenceEqual("acTL"u8))
            {
                frameCount = BinaryPrimitives.ReadInt32BigEndian(Read(body, 0, 4));
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                hasImageData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return Create("image/png", width, height, hasImageData ? frameCount : 0);
            }

            offset += 12 + length;
        }
    }

    private static ImageInspection InspectJpeg(ReadOnlySpan<byte> data)
    {
        var width = 0;
        var height = 0;
        var offset = 2L;
        while (true)
        {
            var marker = Read(data, offset, 2);
            if (marker[0] != 0xFF)
            {
                throw new InvalidDataException("The JPEG image contains an invalid marker.");
            }

            if (marker[1] == 0xFF)
            {
                offset++;
                continue;
            }

            if (marker[1] == 0xD9)
            {
                return Create("image/jpeg", width, height, 1);
            }

            if (marker[1] is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(Read(data, offset + 2, 2));
            var segment = Read(data, offset + 4, length - 2);
            if (marker[1] is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(Read(segment, 1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(Read(segment, 3, 2));
            }

            offset += 2 + length;
            if (marker[1] == 0xDA)
            {
                offset = SkipJpegEntropyCodedData(data, offset);
            }
        }
    }

    private static long SkipJpegEntropyCodedData(ReadOnlySpan<byte> data, long offset)
    {
        while (true)
        {
            var markerIndex = Read(data, offset, data.Length - offset).IndexOf((byte)0xFF);
            if (markerIndex < 0)
            {
                throw new InvalidDataException("The JPEG image is truncated.");
            }

            offset += markerIndex;
            if (Read(data, offset + 1, 1)[0] is not (0x00 or (>= 0xD0 and <= 0xD7)))
            {
                return offset;
            }

            offset += 2;
        }
    }

    private static ImageInspection InspectGif(ReadOnlySpan<byte> data)
    {
        var screen = Read(data, 6, 7);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(screen);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(screen[2..]);
        var frameCount = 0;
        var offset = 13L + GifColorTableLength(screen[4]);
        while (true)
        {
            switch (Read(data, offset, 1)[0])
            {
                case 0x3B:
                    return Create("image/gif", width, height, frameCount);
                case 0x21:
                    offset = SkipGifSubBlocks(data, offset + 2);
                    break;
                case 0x2C:
                    var descriptor = Read(data, offset + 1, 9);
                    offset = SkipGifSubBlocks(data, offset + 11 + GifColorTableLength(descriptor[8]));
                    frameCount++;
                    break;
                default:
                    throw new InvalidDataException("The GIF image contains an unknown block.");
            }
        }
    }

    private static int GifColorTableLength(byte flags) => (flags & 0x80) == 0 ? 0 : 3 << ((flags & 0x07) + 1);

    private static long SkipGifSubBlocks(ReadOnlySpan<byte> data, long offset)
    {
        while (true)
        {
            var size = Read(data, offset, 1)[0];
            offset += 1 + size;
            if (size == 0)
            {
                return offset;
            }
        }
    }

    private static ImageInspection InspectWebp(ReadOnlySpan<byte> data)
    {
        var riff = Read(data, 0, 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(Read(data, 4, 4)));
        var width = 0;
        var height = 0;
        var animated = false;
        var hasImageData = false;
        var animationFrames = 0;
        var offset = (long)HeaderLength;
        while (offset < riff.Length)
        {
            var type = Read(riff, offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(Read(riff, offset + 4, 4));
            var body = Read(riff, offset + 8, size);
            if (type.SequenceEqual("VP8X"u8))
            {
                var canvas = Read(body, 0, 10);
                animated = (canvas[0] & 0x02) != 0;
                width = 1 + (canvas[4] | (canvas[5] << 8) | (canvas[6] << 16));
                height = 1 + (canvas[7] | (canvas[8] << 8) | (canvas[9] << 16));
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (!Read(body, 3, 3).SequenceEqual(Vp8StartCode))
                {
                    throw new InvalidDataException("The WebP image contains an invalid VP8 frame.");
                }

                hasImageData = true;
                width = width == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(Read(body, 6, 2)) & 0x3FFF : width;
                height = height == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(Read(body, 8, 2)) & 0x3FFF : height;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (Read(body, 0, 1)[0] != 0x2F)
                {
                    throw new InvalidDataException("The WebP image contains an invalid VP8L frame.");
                }

                var bits = BinaryPrimitives.ReadUInt32LittleEndian(Read(body, 1, 4));
                hasImageData = true;
                width = width == 0 ? (int)(bits & 0x3FFF) + 1 : width;
                height = height == 0 ? (int)((bits >> 14) & 0x3FFF) + 1 : height;
            }
            else if (type.SequenceEqual("ANMF"u8))
            {
                animationFrames++;
            }

            offset += 8 + size + (size & 1);
        }

        return Create("image/webp", width, height, animated ? animationFrames : hasImageData ? 1 : 0);
    }
}
