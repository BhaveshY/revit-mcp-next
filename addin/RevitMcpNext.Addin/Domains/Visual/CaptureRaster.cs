using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitMcpNext.Addin
{
    /// <summary>
    /// The add-in's raster work for capture (SPEC §10.1): decode, crop, area-average downscale, PNG/JPEG encode and the
    /// compare diff, with WPF imaging (PresentationCore/WindowsBase on net48, UseWPF on net10). Pixels are kept as
    /// opaque BGR24 composited onto white, which is what Revit exports and what both encoders accept.
    /// </summary>
    internal sealed class Raster
    {
        private Raster(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        /// <summary>BGR24, stride = Width * 3.</summary>
        public byte[] Pixels { get; }
        public int Stride => Width * 3;
        public int LongEdge => Math.Max(Width, Height);
        public double Aspect => Height == 0 ? 1 : (double)Width / Height;

        public static Raster Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes))
            {
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames[0];
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = frame;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                int width = converted.PixelWidth, height = converted.PixelHeight;
                var bgra = new byte[width * height * 4];
                converted.CopyPixels(bgra, width * 4, 0);
                var bgr = new byte[width * height * 3];
                for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
                {
                    int alpha = bgra[i + 3];
                    if (alpha == 255)
                    {
                        bgr[j] = bgra[i];
                        bgr[j + 1] = bgra[i + 1];
                        bgr[j + 2] = bgra[i + 2];
                    }
                    else
                    {
                        // Composite onto white.
                        bgr[j] = (byte)((bgra[i] * alpha + 255 * (255 - alpha)) / 255);
                        bgr[j + 1] = (byte)((bgra[i + 1] * alpha + 255 * (255 - alpha)) / 255);
                        bgr[j + 2] = (byte)((bgra[i + 2] * alpha + 255 * (255 - alpha)) / 255);
                    }
                }
                return new Raster(width, height, bgr);
            }
        }

        /// <summary>A sub-rectangle (clamped to the image; at least 1x1).</summary>
        public Raster Crop(int x, int y, int width, int height)
        {
            x = Clamp(x, 0, Width - 1);
            y = Clamp(y, 0, Height - 1);
            width = Clamp(width, 1, Width - x);
            height = Clamp(height, 1, Height - y);
            var pixels = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(Pixels, (y + row) * Stride + x * 3, pixels, row * width * 3, width * 3);
            }
            return new Raster(width, height, pixels);
        }

        /// <summary>Area-average resample to an exact size (used for downscaling; upscaling uses nearest pixels).</summary>
        public Raster Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == Width && height == Height) return this;
            var pixels = new byte[width * height * 3];
            double sx = (double)Width / width, sy = (double)Height / height;
            for (int ty = 0; ty < height; ty++)
            {
                int y0 = (int)Math.Floor(ty * sy);
                int y1 = Math.Max(y0 + 1, Math.Min(Height, (int)Math.Ceiling((ty + 1) * sy)));
                for (int tx = 0; tx < width; tx++)
                {
                    int x0 = (int)Math.Floor(tx * sx);
                    int x1 = Math.Max(x0 + 1, Math.Min(Width, (int)Math.Ceiling((tx + 1) * sx)));
                    long b = 0, g = 0, r = 0;
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int offset = y * Stride + x0 * 3;
                        for (int x = x0; x < x1; x++, offset += 3)
                        {
                            b += Pixels[offset];
                            g += Pixels[offset + 1];
                            r += Pixels[offset + 2];
                            n++;
                        }
                    }
                    int target = (ty * width + tx) * 3;
                    pixels[target] = (byte)(b / n);
                    pixels[target + 1] = (byte)(g / n);
                    pixels[target + 2] = (byte)(r / n);
                }
            }
            return new Raster(width, height, pixels);
        }

        /// <summary>Scales so the long edge is at most <paramref name="longEdge"/> (aspect kept).</summary>
        public Raster FitLongEdge(int longEdge)
        {
            if (LongEdge <= longEdge) return this;
            double factor = (double)longEdge / LongEdge;
            return Resize((int)Math.Round(Width * factor), (int)Math.Round(Height * factor));
        }

        /// <summary>True when every pixel is (near) black: the DirectX surface was lost (RDP, minimized GPU).</summary>
        public bool IsBlank(out bool black)
        {
            black = false;
            if (Pixels.Length == 0) return true;
            byte b0 = Pixels[0], g0 = Pixels[1], r0 = Pixels[2];
            for (int i = 3; i < Pixels.Length; i += 3)
            {
                if (Math.Abs(Pixels[i] - b0) > 6 || Math.Abs(Pixels[i + 1] - g0) > 6 || Math.Abs(Pixels[i + 2] - r0) > 6) return false;
            }
            black = r0 < 16 && g0 < 16 && b0 < 16;
            return true;
        }

        public byte[] EncodePng()
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ToSource()));
            return Save(encoder);
        }

        public byte[] EncodeJpeg(int quality)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(ToSource()));
            return Save(encoder);
        }

        /// <summary>
        /// The compare image: the new capture faded toward white with changed pixels (any channel differing by more
        /// than the tolerance) in magenta. Both rasters must have the same size.
        /// </summary>
        public static Raster Diff(Raster before, Raster after, int tolerance, out int changed, out int[] bbox)
        {
            if (before.Width != after.Width || before.Height != after.Height) throw new ArgumentException("The images differ in size.");
            var pixels = new byte[after.Pixels.Length];
            changed = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < after.Height; y++)
            {
                for (int x = 0; x < after.Width; x++)
                {
                    int i = y * after.Stride + x * 3;
                    bool differs = Math.Abs(before.Pixels[i] - after.Pixels[i]) > tolerance ||
                                   Math.Abs(before.Pixels[i + 1] - after.Pixels[i + 1]) > tolerance ||
                                   Math.Abs(before.Pixels[i + 2] - after.Pixels[i + 2]) > tolerance;
                    if (differs)
                    {
                        pixels[i] = 255;
                        pixels[i + 1] = 0;
                        pixels[i + 2] = 255;
                        changed++;
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                    else
                    {
                        pixels[i] = Fade(after.Pixels[i]);
                        pixels[i + 1] = Fade(after.Pixels[i + 1]);
                        pixels[i + 2] = Fade(after.Pixels[i + 2]);
                    }
                }
            }
            bbox = changed == 0 ? null : new[] { minX, minY, maxX + 1, maxY + 1 };
            return new Raster(after.Width, after.Height, pixels);
        }

        private static byte Fade(byte value)
        {
            return (byte)(255 - (255 - value) * 30 / 100);
        }

        private BitmapSource ToSource()
        {
            BitmapSource source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr24, null, Pixels, Stride);
            source.Freeze();
            return source;
        }

        private static byte[] Save(BitmapEncoder encoder)
        {
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
