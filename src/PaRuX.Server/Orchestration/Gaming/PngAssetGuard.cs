using System.Buffers.Binary;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Shared bounded PNG IHDR inspection for all game assets, not tied to any
/// individual game adapter. Invoked before platform/native raster decoders.
/// The raster decoder still validates the actual compressed image data.
/// </summary>
internal static class PngAssetGuard
{
    internal static (int Width, int Height) ReadDimensions(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes is null || bytes.Length < 33 ||
            !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Expected a standard PNG with a valid IHDR header.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width == 0 || height == 0 || width > 4096 || height > 4096)
            throw new InvalidDataException("PNG dimensions are out of bounds.");
        return ((int)width, (int)height);
    }
}
