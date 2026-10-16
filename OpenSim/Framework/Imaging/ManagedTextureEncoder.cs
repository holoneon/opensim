using System;
using System.Runtime.InteropServices;
using OpenMetaverse.Imaging;
using SkiaSharp;

namespace OpenSim.Framework.Imaging
{
    public static class ManagedTextureEncoder
    {
        public static byte[] Convert(byte[] jpeg2000Data, string format)
        {
            if (jpeg2000Data == null || jpeg2000Data.Length == 0)
                return Array.Empty<byte>();

            SKEncodedImageFormat outputFormat;

            switch (format?.Trim().ToLowerInvariant())
            {
                case "png":
                case "image/png":
                    outputFormat = SKEncodedImageFormat.Png;
                    break;
            
                case "jpeg":
                case "jpg":
                case "image/jpeg":
                case "image/jpg":
                    outputFormat = SKEncodedImageFormat.Jpeg;
                    break;
            
                default:
                    return Array.Empty<byte>();
            }

            ManagedImage image = null;

            try
            {
                if (!OpenJPEG.DecodeToImage(jpeg2000Data, out image)
                    || image == null)
                    return Array.Empty<byte>();

                int width = image.Width;
                int height = image.Height;

                if (width <= 0 || height <= 0)
                    return Array.Empty<byte>();

                int pixelCount = checked(width * height);
                byte[] rgba = new byte[checked(pixelCount * 4)];

                byte[] red = image.Red;
                byte[] green = image.Green;
                byte[] blue = image.Blue;
                byte[] alpha = image.Alpha;

                if (red == null || green == null || blue == null ||
                    red.Length < pixelCount ||
                    green.Length < pixelCount ||
                    blue.Length < pixelCount)
                    return Array.Empty<byte>();

                bool hasAlpha = alpha != null && alpha.Length >= pixelCount;
                bool jpeg = outputFormat == SKEncodedImageFormat.Jpeg;

                for (int i = 0, p = 0; i < pixelCount; i++, p += 4)
                {
                    byte a = hasAlpha ? alpha[i] : (byte)255;

                    if (jpeg)
                    {
                        // Composite transparent pixels over white.
                        rgba[p]     = (byte)((red[i]   * a + 255 * (255 - a) + 127) / 255);
                        rgba[p + 1] = (byte)((green[i] * a + 255 * (255 - a) + 127) / 255);
                        rgba[p + 2] = (byte)((blue[i]  * a + 255 * (255 - a) + 127) / 255);
                        rgba[p + 3] = 255;
                    }
                    else
                    {
                        rgba[p]     = red[i];
                        rgba[p + 1] = green[i];
                        rgba[p + 2] = blue[i];
                        rgba[p + 3] = a;
                    }
                }

                var info = new SKImageInfo(
                    width,
                    height,
                    SKColorType.Rgba8888,
                    SKAlphaType.Unpremul);

                using var bitmap = new SKBitmap(info);

                Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);

                using var skImage = SKImage.FromBitmap(bitmap);
                using var encoded = skImage.Encode(
                    outputFormat,
                    jpeg ? 95 : 100);

                return encoded?.ToArray() ?? Array.Empty<byte>();
            }
            finally
            {
                image?.Clear();
            }
        }
    }
}

