using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>
/// Minecraft Java 64x64 CLASSIC/SLIM UV-aware recolouring. Unlike resizing a
/// diffusion image into a skin, this operates on an owner-supplied valid atlas:
/// every face keeps its official UV position, unused pixels and other body
/// parts remain byte-for-byte equivalent in decoded RGBA, and transparency is
/// preserved. AI output is only a COLOUR/TEXTURE reference. No 3D/face
/// segmentation or claims of style accuracy are implied.
/// </summary>
internal static class MinecraftSkinUvRestyler
{
    internal const int MaximumSkinBytes = 2 * 1024 * 1024;
    private static readonly string[] ValidParts = ["head", "torso", "arms", "legs", "full"];

    internal readonly record struct UvBox(string Part, int U, int V, int Width, int Height, int Depth);
    internal readonly record struct Face(string Part, Rectangle Rect);

    /// <summary>
    /// Minecraft classic skin boxes: UV = [top,bottom] in row 0,
    /// [right,front,left,back] in row Depth. Slim arms have width 3.
    /// Overlay layers share the same box layout but are preserved when transparent.
    /// </summary>
    internal static IReadOnlyList<Face> Faces(string part, string armStyle)
    {
        ValidateSelection(part, armStyle);
        int arm = armStyle == "slim" ? 3 : 4;
        UvBox[] boxes =
        [
            new("head", 0, 0, 8, 8, 8),
            new("head", 32, 0, 8, 8, 8),
            new("torso", 16, 16, 8, 12, 4),
            new("torso", 16, 32, 8, 12, 4),
            new("arms", 40, 16, arm, 12, 4),
            new("arms", 40, 32, arm, 12, 4),
            new("arms", 32, 48, arm, 12, 4),
            new("arms", 48, 48, arm, 12, 4),
            new("legs", 0, 16, 4, 12, 4),
            new("legs", 0, 32, 4, 12, 4),
            new("legs", 16, 48, 4, 12, 4),
            new("legs", 0, 48, 4, 12, 4)
        ];
        var result = new List<Face>(72);
        foreach (UvBox box in boxes)
        {
            if (part != "full" && box.Part != part) continue;
            int u = box.U, v = box.V, w = box.Width, h = box.Height, d = box.Depth;
            Rectangle[] rectangles =
            [
                new(u + d, v, w, d),                      // top
                new(u + d + w, v, w, d),                  // bottom
                new(u, v + d, d, h),                      // right
                new(u + d, v + d, w, h),                  // front
                new(u + d + w, v + d, d, h),              // left
                new(u + d + w + d, v + d, w, h)           // back
            ];
            foreach (Rectangle rect in rectangles)
            {
                if (rect.Left < 0 || rect.Top < 0 || rect.Right > 64 || rect.Bottom > 64)
                    throw new InvalidOperationException("Invalid Minecraft UV box layout.");
                result.Add(new Face(box.Part, rect));
            }
        }
        return result;
    }

    internal static void ValidateSelection(string part, string armStyle)
    {
        if (!ValidParts.Contains(part, StringComparer.Ordinal))
            throw new ArgumentException("Skin part must be head, torso, arms, legs or full.");
        if (armStyle is not ("classic" or "slim"))
            throw new ArgumentException("Arm style must be classic or slim.");
    }

    /// <summary>
    /// Read the PNG IHDR before invoking the native image decoder. Reject
    /// compressed image bombs advertising enormous dimensions and non-PNG
    /// payloads while keeping this code independent of third-party decoders.
    /// </summary>
    internal static (int Width, int Height) ReadPngDimensions(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 ||
            !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Expected a standard PNG IHDR.");
        uint w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (w == 0 || h == 0 || w > 4096 || h > 4096)
            throw new InvalidDataException("PNG dimensions are out of bounds.");
        return ((int)w, (int)h);
    }

    /// <summary>Do not resize or reinterpret a non-PNG baseline as an authentic skin.</summary>
    internal static void ValidateSkinPng(byte[] bytes)
    {
        if (bytes is not { Length: >= 100 and <= MaximumSkinBytes })
            throw new InvalidDataException("The original Minecraft PNG is missing or too large.");
        (int width, int height) = ReadPngDimensions(bytes);
        if (width != 64 || height != 64)
            throw new InvalidDataException("The original skin PNG must be exactly 64x64 pixels.");
    }

    /// <summary>
    /// Preserve existing UV positions and alpha. Local media provides the palette/
    /// material reference: the original body's silhouettes, individual faces, and
    /// unselected regions are not replaced. This is a recolour, NOT a skin
    /// generated from scratch or a pixel-exact copy of generated artwork.
    /// </summary>
    internal static byte[] Recolour(byte[] originalPng, byte[] referenceImage,
        string part, string armStyle)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Minecraft skin decoding requires Windows.");
        ValidateSelection(part, armStyle);
        ValidateSkinPng(originalPng);
        if (referenceImage is not { Length: > 0 and <= 16 * 1024 * 1024 })
            throw new InvalidDataException("Local image reference is missing or too large.");
        (int rw, int rh) = ReadPngDimensions(referenceImage);
        if (rw is < 64 or > 4096 || rh is < 64 or > 4096)
            throw new InvalidDataException("The local image reference dimensions are not supported.");
        try
        {
            using var source = new MemoryStream(originalPng, writable: false);
            using var imported = new Bitmap(source);
            if (imported.Width != 64 || imported.Height != 64)
                throw new InvalidDataException("Only native 64x64 Minecraft Java PNG skins can be restyled.");
            using var imageStream = new MemoryStream(referenceImage, writable: false);
            using var reference = new Bitmap(imageStream);
            if (reference.Width is < 64 or > 4096 || reference.Height is < 64 or > 4096)
                throw new InvalidDataException("The local image reference dimensions are not supported.");
            using var output = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            // Copy decoded pixels exactly. Graphics.DrawImage with SourceOver
            // may alter partially transparent unselected pixels, breaking the
            // preservation guarantee of a body-part-only restyle.
            for (int yy = 0; yy < 64; yy++)
            for (int xx = 0; xx < 64; xx++)
                output.SetPixel(xx, yy, imported.GetPixel(xx, yy));
            foreach (Face face in Faces(part, armStyle))
            {
                var r = face.Rect;
                for (int yy = 0; yy < r.Height; yy++)
                for (int xx = 0; xx < r.Width; xx++)
                {
                    int x = r.X + xx, y = r.Y + yy;
                    Color before = output.GetPixel(x, y);
                    if (before.A == 0) continue; // transparent overlay never becomes opaque
                    // Sample the material reference at the same relative UV
                    // position on each face, not at arbitrary atlas coordinates.
                    int sampleX = Math.Min(reference.Width - 1,
                        (int)((xx + 0.5) * reference.Width / r.Width));
                    int sampleY = Math.Min(reference.Height - 1,
                        (int)((yy + 0.5) * reference.Height / r.Height));
                    Color texture = reference.GetPixel(sampleX, sampleY);
                    // Preserve baseline light/dark texture contours while
                    // introducing a dominant material colour (per-channel).
                    double shading = 0.65 + (0.35 * (before.R * 0.2126 +
                        before.G * 0.7152 + before.B * 0.0722) / 255.0);
                    static int Limit(double c) => Math.Clamp((int)Math.Round(c), 0, 255);
                    output.SetPixel(x, y, Color.FromArgb(before.A,
                        Limit(before.R * 0.3 + texture.R * 0.7 * shading),
                        Limit(before.G * 0.3 + texture.G * 0.7 * shading),
                        Limit(before.B * 0.3 + texture.B * 0.7 * shading)));
                }
            }
            using var result = new MemoryStream();
            output.Save(result, ImageFormat.Png);
            return result.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException
            or System.Runtime.InteropServices.ExternalException)
        {
            throw new InvalidDataException("Skin or local material image could not be decoded.", ex);
        }
    }
}
