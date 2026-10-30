using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace MphRecomp.Import
{
    // RGBA8 pixels to a PNG, every channel kept as is (8-bit RGBA, no palette, transparent texels keep their colour) --
    // the same pixels PIL's Image.fromarray(..., "RGBA").save writes for the reference converters.
    public static class PngOut
    {
        static readonly PngEncoder Encoder = new()
        {
            ColorType = PngColorType.RgbWithAlpha,
            BitDepth = PngBitDepth.Bit8,
            TransparentColorMode = PngTransparentColorMode.Preserve,
        };

        public static void Write(string path, byte[] rgba, int width, int height)
        {
            using Image<Rgba32> img = Image.LoadPixelData<Rgba32>(rgba, width, height);
            using FileStream f = File.Create(path);
            img.SaveAsPng(f, Encoder);
        }

        // a PNG back to RGBA8 (for checks)
        public static byte[] Read(string path, out int width, out int height)
        {
            using Image<Rgba32> img = Image.Load<Rgba32>(path);
            width = img.Width; height = img.Height;
            var px = new byte[width * height * 4];
            img.CopyPixelDataTo(px);
            return px;
        }
    }
}
