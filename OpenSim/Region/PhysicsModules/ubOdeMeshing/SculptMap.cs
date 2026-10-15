
using System;
using System.Collections.Generic;

using OpenMetaverse;
using OpenMetaverse.Imaging;

namespace PrimMesher
{
    public class SculptMap
    {
        public int width;
        public int height;
        public byte[] redBytes;
        public byte[] greenBytes;
        public byte[] blueBytes;

        public SculptMap()
        {
        }

        public SculptMap(ManagedImage image, int lod)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            int imageWidth = image.Width;
            int imageHeight = image.Height;

            if (imageWidth <= 0 || imageHeight <= 0)
                throw new ArgumentException(
                    "SculptMap: image has no data");

            int pixelCount = checked(imageWidth * imageHeight);

            if (image.Red == null ||
                image.Green == null ||
                image.Blue == null ||
                image.Red.Length < pixelCount ||
                image.Green.Length < pixelCount ||
                image.Blue.Length < pixelCount)
            {
                throw new ArgumentException(
                    "SculptMap: missing or invalid RGB channels");
            }

            if (lod <= 0)
                throw new ArgumentOutOfRangeException(nameof(lod));

            long numLodPixels = (long)lod * lod;

            width = imageWidth;
            height = imageHeight;

            bool needsScaling = false;

            while ((long)width * height > numLodPixels * 4)
            {
                width >>= 1;
                height >>= 1;
                needsScaling = true;

                if (width == 0 || height == 0)
                    throw new ArgumentException(
                        "SculptMap: invalid scaled dimensions");
            }

            // Preserve the original nearest-neighbor sampling.
            // ScaleImage returns a new managed image buffer.
            if (needsScaling)
                image = ScaleImage(image, width, height);

            bool smallMap;

            if ((long)width * height > numLodPixels)
            {
                smallMap = false;
                width >>= 1;
                height >>= 1;
            }
            else
            {
                smallMap = true;
            }

            int numBytes = checked((width + 1) * (height + 1));

            redBytes = new byte[numBytes];
            greenBytes = new byte[numBytes];
            blueBytes = new byte[numBytes];

            int byteNdx = 0;

            for (int y = 0; y <= height; y++)
            {
                int sourceY = smallMap
                    ? (y < height ? y : y - 1)
                    : (y < height ? y * 2 : y * 2 - 1);

                for (int x = 0; x < width; x++)
                {
                    int sourceX = smallMap ? x : x * 2;

                    int sourceIndex =
                        sourceY * image.Width + sourceX;

                    redBytes[byteNdx] = image.Red[sourceIndex];
                    greenBytes[byteNdx] = image.Green[sourceIndex];
                    blueBytes[byteNdx] = image.Blue[sourceIndex];

                    byteNdx++;
                }

                // Preserve the original final-column sampling.
                int lastX = smallMap
                    ? width - 1
                    : width * 2 - 1;

                int lastIndex = sourceY * image.Width + lastX;

                redBytes[byteNdx] = image.Red[lastIndex];
                greenBytes[byteNdx] = image.Green[lastIndex];
                blueBytes[byteNdx] = image.Blue[lastIndex];

                byteNdx++;
            }

            width++;
            height++;
        }

        public List<List<Vector3>> ToRows(bool mirror)
        {
            int numRows = height;
            int numCols = width;

            List<List<Vector3>> rows =
                new List<List<Vector3>>(numRows);

            const float pixScale = 1.0f / 255.0f;

            int smNdx = 0;

            for (int rowNdx = 0; rowNdx < numRows; rowNdx++)
            {
                List<Vector3> row =
                    new List<Vector3>(numCols);

                for (int colNdx = 0; colNdx < numCols; colNdx++)
                {
                    float x = redBytes[smNdx] * pixScale - 0.5f;
                    float y = greenBytes[smNdx] * pixScale - 0.5f;
                    float z = blueBytes[smNdx] * pixScale - 0.5f;

                    if (mirror)
                        x = -x;

                    row.Add(new Vector3(x, y, z));

                    smNdx++;
                }

                rows.Add(row);
            }

            return rows;
        }

        private static ManagedImage ScaleImage(
            ManagedImage source,
            int destWidth,
            int destHeight)
        {
            int count = checked(destWidth * destHeight);

            // Construct an RGB image using the ManagedImage API.
            ManagedImage result = new ManagedImage(
                destWidth,
                destHeight,
                ManagedImage.ImageChannels.Color);

            float xscale = (float)source.Width / destWidth;
            float yscale = (float)source.Height / destHeight;

            int lastSourceX = source.Width - 1;
            int lastSourceY = source.Height - 1;

            int lastDestX = destWidth - 1;
            int lastDestY = destHeight - 1;

            for (int y = 0; y < destHeight; y++)
            {
                int sourceY = y == lastDestY
                    ? lastSourceY
                    : (int)(0.5f + y * yscale);

                for (int x = 0; x < destWidth; x++)
                {
                    int sourceX = x == lastDestX
                        ? lastSourceX
                        : (int)(0.5f + x * xscale);

                    int sourceIndex =
                        sourceY * source.Width + sourceX;

                    int destIndex = y * destWidth + x;

                    result.Red[destIndex] =
                        source.Red[sourceIndex];

                    result.Green[destIndex] =
                        source.Green[sourceIndex];

                    result.Blue[destIndex] =
                        source.Blue[sourceIndex];
                }
            }

            return result;
        }
    }
}


