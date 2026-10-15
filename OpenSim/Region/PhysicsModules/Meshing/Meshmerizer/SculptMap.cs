/*
 * Copyright (c) Contributors
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse.Imaging;

namespace PrimMesher
{
    public class SculptMap
    {
        public int width, height;
        public byte[] redBytes, greenBytes, blueBytes;

        public SculptMap() { }

        public SculptMap(ManagedImage image, int lod)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            int pixels = checked(image.Width * image.Height);
            if (image.Width <= 0 || image.Height <= 0 ||
                image.Red == null || image.Green == null || image.Blue == null ||
                image.Red.Length < pixels ||
                image.Green.Length < pixels ||
                image.Blue.Length < pixels)
                throw new ArgumentException("Invalid sculpt RGB image");

            if (lod <= 0)
                throw new ArgumentOutOfRangeException(nameof(lod));

            long lodPixels = (long)lod * lod;
            bool smallMap = (long)image.Width * image.Height <= lodPixels;

            width = image.Width;
            height = image.Height;

            bool scale = false;
            while ((long)width * height > lodPixels * 4)
            {
                width >>= 1;
                height >>= 1;
                scale = true;
                if (width == 0 || height == 0)
                    throw new ArgumentException("Invalid scaled dimensions");
            }

            if (scale)
                image = ScaleImage(image, width, height);

            if ((long)width * height > lodPixels)
            {
                width >>= 1;
                height >>= 1;
            }

            int count = checked((width + 1) * (height + 1));
            redBytes = new byte[count];
            greenBytes = new byte[count];
            blueBytes = new byte[count];

            int dest = 0;
            for (int y = 0; y <= height; y++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int sx = smallMap
                        ? (x < width ? x : x - 1)
                        : (x < width ? x * 2 : x * 2 - 1);
                    int sy = smallMap
                        ? (y < height ? y : y - 1)
                        : (y < height ? y * 2 : y * 2 - 1);

                    int src = checked(sy * image.Width + sx);
                    redBytes[dest] = image.Red[src];
                    greenBytes[dest] = image.Green[src];
                    blueBytes[dest] = image.Blue[src];
                    dest++;
                }
            }

            width++;
            height++;
        }

        public List<List<Coord>> ToRows(bool mirror)
        {
            List<List<Coord>> rows = new List<List<Coord>>(height);
            const float scale = 1.0f / 255;
            int index = 0;

            for (int y = 0; y < height; y++)
            {
                List<Coord> row = new List<Coord>(width);
                for (int x = 0; x < width; x++)
                {
                    float r = redBytes[index] * scale - 0.5f;
                    float g = greenBytes[index] * scale - 0.5f;
                    float b = blueBytes[index] * scale - 0.5f;
                    row.Add(new Coord(mirror ? -r : r, g, b));
                    index++;
                }
                rows.Add(row);
            }
            return rows;
        }

        private static ManagedImage ScaleImage(
            ManagedImage source, int destWidth, int destHeight)
        {
            ManagedImage result = new ManagedImage(
                destWidth, destHeight, ManagedImage.ImageChannels.Color);

            // Preserve original integer-division scaling.
            float xscale = source.Width / destWidth;
            float yscale = source.Height / destHeight;

            float sy = 0.5f;
            for (int y = 0; y < destHeight; y++)
            {
                float sx = 0.5f;
                for (int x = 0; x < destWidth; x++)
                {
                    int src = (int)sy * source.Width + (int)sx;
                    int dst = y * destWidth + x;
                    result.Red[dst] = source.Red[src];
                    result.Green[dst] = source.Green[src];
                    result.Blue[dst] = source.Blue[src];
                    sx += xscale;
                }
                sy += yscale;
            }
            return result;
        }
    }
}

