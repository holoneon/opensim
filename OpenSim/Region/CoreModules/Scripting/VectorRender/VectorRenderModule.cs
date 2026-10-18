/*
 * Copyright (c) Contributors, http://opensimulator.org/
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
using System.Drawing;
using SkiaSharp;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenSim.Region.CoreModules.Scripting.DynamicTexture;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using log4net;
using System.Reflection;
using Mono.Addins;

//using Cairo;

namespace OpenSim.Region.CoreModules.Scripting.VectorRender
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "VectorRenderModule")]
    public class VectorRenderModule : ISharedRegionModule, IDynamicTextureRender
    {
        // These fields exist for testing purposes, please do not remove.
//        private static bool s_flipper;
//        private static byte[] s_asset1Data;
//        private static byte[] s_asset2Data;

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static object thisLock = new object();
        private string m_fallbackFontFile = "";

        private Scene m_scene;
        private IDynamicTextureManager m_textureManager;

        private string m_fontName = "Arial";

        public VectorRenderModule()
        {
        }

        #region IDynamicTextureRender Members

        public string GetContentType()
        {
            return "vector";
        }

        public string GetName()
        {
            return Name;
        }

        public bool SupportsAsynchronous()
        {
            return true;
        }

//        public bool AlwaysIdenticalConversion(string bodyData, string extraParams)
//        {
//            string[] lines = GetLines(bodyData);
//            return lines.Any((str, r) => str.StartsWith("Image"));
//        }

        public IDynamicTexture ConvertUrl(string url, string extraParams)
        {
            return null;
        }

        public IDynamicTexture ConvertData(string bodyData, string extraParams)
        {
            return Draw(bodyData, extraParams);
        }

        public bool AsyncConvertUrl(UUID id, string url, string extraParams)
        {
            return false;
        }

        public bool AsyncConvertData(UUID id, string bodyData, string extraParams)
        {
            if (m_textureManager == null)
            {
                m_log.Warn("[VECTORRENDERMODULE]: No texture manager. Can't function");
                return false;
            }
            // XXX: This isn't actually being done asynchronously!
            m_textureManager.ReturnData(id, ConvertData(bodyData, extraParams));

            return true;
        }

        public void GetDrawStringSize(string text, string fontName, int fontSize,
                                      out double xSize, out double ySize)
        {
            using SKTypeface face = SKTypeface.FromFamilyName(fontName) ?? SKTypeface.Default;
            using SKFont font = new SKFont(face, fontSize);
            xSize = font.MeasureText(text ?? string.Empty);
            SKFontMetrics metrics = font.Metrics;
            ySize = metrics.Descent - metrics.Ascent;

        }

        #endregion

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource config)
        {
            IConfig cfg = config.Configs["VectorRender"];
            if (null != cfg)
            {
                m_fontName = cfg.GetString("font_name", m_fontName);
                m_fallbackFontFile = cfg.GetString("fallback_font_file", "").Trim();
            }
            m_log.DebugFormat("[VECTORRENDERMODULE]: using font \"{0}\" for text rendering.", m_fontName);


        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (m_scene == null)
            {
                m_scene = scene;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (m_textureManager == null && m_scene == scene)
            {
                m_textureManager = m_scene.RequestModuleInterface<IDynamicTextureManager>();
                if (m_textureManager != null)
                {
                    m_textureManager.RegisterRender(GetContentType(), this);
                }
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "VectorRenderModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        private IDynamicTexture Draw(string data, string extraParams)
        {
            // We need to cater for old scripts that didnt use extraParams neatly, they use either an integer size which represents both width and height, or setalpha
            // we will now support multiple comma seperated params in the form  width:256,height:512,alpha:255
            int width = 256;
            int height = 256;
            int alpha = 255; // 0 is transparent
            SKColor bgColor = SKColors.White;  // Default background color
            char altDataDelim = ';';

            char[] paramDelimiter = { ',' };
            char[] nvpDelimiter = { ':' };

            extraParams = extraParams.Trim();
            extraParams = extraParams.ToLower();

            string[] nvps = extraParams.Split(paramDelimiter);

            bool lossless = false;
            int temp = -1;
            foreach (string pair in nvps)
            {
                string[] nvp = pair.Split(nvpDelimiter);
                string name = "";
                string value = "";

                if (nvp[0] != null)
                {
                    name = nvp[0].Trim();
                }

                if (nvp.Length == 2)
                {
                    value = nvp[1].Trim();
                }

                switch (name)
                {
                    case "width":
                        temp = parseIntParam(value);
                        if (temp != -1)
                        {
                            if (temp < 1)
                            {
                                width = 1;
                            }
                            else if (temp > 2048)
                            {
                                width = 2048;
                            }
                            else
                            {
                                width = temp;
                            }
                        }
                        break;
                    case "height":
                        temp = parseIntParam(value);
                        if (temp != -1)
                        {
                            if (temp < 1)
                            {
                                height = 1;
                            }
                            else if (temp > 2048)
                            {
                                height = 2048;
                            }
                            else
                            {
                                height = temp;
                            }
                        }
                        break;
                     case "alpha":
                          temp = parseIntParam(value);
                          if (temp != -1)
                          {
                              if (temp < 0)
                              {
                                  alpha = 0;
                              }
                              else if (temp > 255)
                              {
                                  alpha = 255;
                              }
                              else
                              {
                                  alpha = temp;
                              }
                          }
                          // Allow a bitmap w/o the alpha component to be created
                          else if (value.ToLower() == "false") {
                               alpha = 256;
                          }
                          break;
                     case "bgcolor":
                     case "bgcolour":
                          int hex = 0;
                         if (Int32.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                         {
                             bgColor = ColorFromArgb(hex);
                         }
                         else
                         {
                             bgColor = ParseColor(value);
                         }
                         break;
                    case "altdatadelim":
                        altDataDelim = value.ToCharArray()[0];
                        break;
                    case "lossless":
                        if (value.ToLower() == "true")
                            lossless = true;
                        break;
                    case "":
                         // blank string has been passed do nothing just use defaults
                     break;
                     default: // this is all for backwards compat, all a bit ugly hopfully can be removed in future
                         // could be either set alpha or just an int
                         if (name == "setalpha")
                         {
                             alpha = 0; // set the texture to have transparent background (maintains backwards compat)
                         }
                         else
                         {
                             // this function used to accept an int on its own that represented both
                             // width and height, this is to maintain backwards compat, could be removed
                             // but would break existing scripts
                             temp = parseIntParam(name);
                             if (temp != -1)
                             {
                                 if (temp > 1024)
                                    temp = 1024;

                                 if (temp < 128)
                                     temp = 128;

                                 width = temp;
                                 height = temp;
                             }
                         }
                     break;
                }
            }

            bool reuseable = false;
            using SKBitmap bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (SKCanvas canvas = new SKCanvas(bitmap))
            {
                if (alpha == 256 && bgColor.Alpha != 255)
                    alpha = bgColor.Alpha;
                byte backgroundAlpha = alpha == 256 ? (byte)255 : (byte)alpha;
                canvas.Clear(new SKColor(bgColor.Red, bgColor.Green, bgColor.Blue, backgroundAlpha));
                SkiaDraw(data, canvas, altDataDelim, out reuseable);
                canvas.Flush();
            }

            byte[] imageJ2000 = Array.Empty<byte>();
            try
            {
                ManagedImage managed = new ManagedImage(width, height,
                    ManagedImage.ImageChannels.Color | ManagedImage.ImageChannels.Alpha);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        SKColor c = bitmap.GetPixel(x, y);
                        int i = y * width + x;
                        managed.Red[i] = c.Red;
                        managed.Green[i] = c.Green;
                        managed.Blue[i] = c.Blue;
                        managed.Alpha[i] = c.Alpha;
                    }
                imageJ2000 = OpenJPEG.Encode(managed, lossless);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[VECTORRENDERMODULE]: OpenJPEG Encode Failed: {0}", e);
            }
            return new OpenSim.Region.CoreModules.Scripting.DynamicTexture.DynamicTexture(
                data, extraParams, imageJ2000, new Size(width, height), reuseable);

        }

        private int parseIntParam(string strInt)
        {
            int parsed;
            try
            {
                parsed = Convert.ToInt32(strInt);
            }
            catch (Exception)
            {
                //Ckrinke: Add a WriteLine to remove the warning about 'e' defined but not used
                // m_log.Debug("Problem with Draw. Please verify parameters." + e.ToString());
                parsed = -1;
            }

            return parsed;
        }



        /// <summary>
        /// Split input data into discrete command lines.
        /// </summary>
        /// <returns></returns>
        /// <param name='data'></param>
        /// <param name='dataDelim'></param>
        private string[] GetLines(string data, char dataDelim)
        {
            char[] lineDelimiter = { dataDelim };
            return data.Split(lineDelimiter);
        }

        private static SKColor ColorFromArgb(int argb)
        {
            uint u = unchecked((uint)argb);
            return new SKColor((byte)(u >> 16), (byte)(u >> 8), (byte)u, (byte)(u >> 24));
        }

        private static SKColor ParseColor(string value)
        {
            value = value.Trim();
        
            string hex = value.StartsWith("#") ? value.Substring(1) : value;
        
            if (hex.Length == 8 &&
                uint.TryParse(hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out uint argb))
            {
                return new SKColor(
                    (byte)(argb >> 16),
                    (byte)(argb >> 8),
                    (byte)argb,
                    (byte)(argb >> 24));
            }
        
            if (hex.Length == 6 &&
                uint.TryParse(hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out uint rgb))
            {
                return new SKColor(
                    (byte)(rgb >> 16),
                    (byte)(rgb >> 8),
                    (byte)rgb,
                    255);
            }
        
            if (SKColor.TryParse(value, out SKColor parsed))
                return parsed;
        
            return SKColors.Black;
        }

        private SKTypeface MakeTypeface(string fontName, bool bold, bool italic)
        {
            SKFontStyleWeight weight = bold
                ? SKFontStyleWeight.Bold
                : SKFontStyleWeight.Normal;
        
            SKFontStyleSlant slant = italic
                ? SKFontStyleSlant.Italic
                : SKFontStyleSlant.Upright;
        
            SKFontStyle style = new SKFontStyle(
                weight,
                SKFontStyleWidth.Normal,
                slant);
        
            SKTypeface face = SKFontManager.Default.MatchFamily(fontName, style);
        
            if (face != null && !string.IsNullOrEmpty(face.FamilyName))
                return face;
        
            face?.Dispose();
        
            if (!string.IsNullOrEmpty(m_fallbackFontFile))
            {
                try
                {
                    face = SKTypeface.FromFile(m_fallbackFontFile);
        
                    if (face != null && !string.IsNullOrEmpty(face.FamilyName))
                        return face;
        
                    face?.Dispose();
                }
                catch (Exception e)
                {
                    m_log.WarnFormat(
                        "[VECTOR SKIA]: Cannot load fallback font '{0}': {1}",
                        m_fallbackFontFile, e.Message);
                }
            }
        
            face = SKTypeface.Default;
        
            if (face != null && !string.IsNullOrEmpty(face.FamilyName))
                return face;
        
            throw new InvalidOperationException(
                "SkiaSharp could not resolve a usable font. " +
                "Configure [VectorRender] fallback_font_file.");
        }

        private static void DrawCap(SKCanvas canvas, SKPoint point, SKPoint toward,
            string cap, SKPaint paint)
        {
            if (cap == "flat" || string.IsNullOrEmpty(cap)) return;
            float dx = point.X - toward.X, dy = point.Y - toward.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.001f) return;
            dx /= length; dy /= length;
            float nx = -dy, ny = dx;
            float r = Math.Max(3, paint.StrokeWidth * 1.5f);
            using SKPaint fill = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
            if (cap == "round")
                canvas.DrawCircle(point, r, fill);
            else if (cap == "arrow" || cap == "diamond")
            {
                using SKPath path = new SKPath();
                path.MoveTo(point.X + dx * r * 2, point.Y + dy * r * 2);
                path.LineTo(point.X + nx * r, point.Y + ny * r);
                if (cap == "diamond") path.LineTo(point.X - dx * r * 2, point.Y - dy * r * 2);
                path.LineTo(point.X - nx * r, point.Y - ny * r);
                path.Close();
                canvas.DrawPath(path, fill);
            }
        }

        private void SkiaDraw(string data, SKCanvas canvas, char dataDelim, out bool reuseable)
        {
            reuseable = true;
            Point startPoint = new Point(0, 0);
            Point endPoint = new Point(0, 0);
            SKColor color = SKColors.Black;
            string fontName = m_fontName;
            float fontSize = 14;
            bool bold = false, italic = false, underline = false, strike = false;
            string startCap = "flat", endCap = "flat";
            using SKPaint stroke = new SKPaint { Color = color, StrokeWidth = 7, Style = SKPaintStyle.Stroke, IsAntialias = true };
            using SKPaint fill = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
            char[] delim = { ',' };
            int baseSave = canvas.Save();
            try
            {
                foreach (string line in GetLines(data, dataDelim))
                {
                    string cmd = line.TrimStart();
                    if (cmd.StartsWith("Text") && cmd.Length > 5)
                    {
                        string value = cmd.Substring(cmd[4] == ' ' ? 5 : 4);
/*
                        using SKTypeface face = MakeTypeface(fontName, bold, italic);
                        using SKFont font = new SKFont(face, fontSize);
                        SKFontMetrics metrics = font.Metrics;
                        float baseline = startPoint.Y - metrics.Ascent;
                        canvas.DrawText(value, startPoint.X, baseline, font, fill);
*/

using SKTypeface face = MakeTypeface(fontName, bold, italic);
using SKFont font = new SKFont(face, fontSize);

SKFontMetrics metrics = font.Metrics;
float baseline = startPoint.Y - metrics.Ascent;
float width = font.MeasureText(value);

m_log.WarnFormat(
    "[VECTOR SKIA TEXT] font={0}, family={1}, size={2}, " +
    "text='{3}', width={4}, ascent={5}, baseline={6}, color={7}",
    fontName,
    face.FamilyName,
    fontSize,
    value,
    width,
    metrics.Ascent,
    baseline,
    fill.Color
);

// Draw a marker at the text origin.
using (SKPaint marker = new SKPaint
{
    Color = SKColors.Magenta,
    Style = SKPaintStyle.Fill
})
{
    canvas.DrawCircle(startPoint.X, baseline, 5, marker);
}

canvas.DrawText(value, startPoint.X, baseline, font, fill);

                        float textWidth = font.MeasureText(value);
                        if (underline || strike)
                        {
                            float thickness = Math.Max(1, fontSize / 14);
                            using SKPaint decoration = new SKPaint { Color = color, StrokeWidth = thickness, IsAntialias = true };
                            if (underline) canvas.DrawLine(startPoint.X, baseline + Math.Max(1, metrics.Descent * 0.5f), startPoint.X + textWidth, baseline + Math.Max(1, metrics.Descent * 0.5f), decoration);
                            if (strike) canvas.DrawLine(startPoint.X, baseline + metrics.Ascent * 0.35f, startPoint.X + textWidth, baseline + metrics.Ascent * 0.35f, decoration);
                        }
                        continue;
                    }
                    cmd = cmd.TrimEnd();
                    if (cmd.StartsWith("ResetTransf"))
                    {
                        canvas.RestoreToCount(baseSave);
                        baseSave = canvas.Save();
                    }
                    else if (cmd.StartsWith("TransTransf"))
                    {
                        float x = 0, y = 0;
                        GetParams(delim, ref cmd, 11, ref x, ref y);
                        canvas.Translate(x, y);
                    }
                    else if (cmd.StartsWith("ScaleTransf"))
                    {
                        float x = 0, y = 0;
                        GetParams(delim, ref cmd, 11, ref x, ref y);
                        canvas.Scale(x, y);
                    }
                    else if (cmd.StartsWith("RotTransf"))
                    {
                        float angle = 0;
                        GetParams(delim, ref cmd, 9, ref angle);
                        canvas.RotateDegrees(angle);
                    }
                    else if (cmd.StartsWith("MoveTo"))
                    {
                        float x = 0, y = 0;
                        GetParams(delim, ref cmd, 6, ref x, ref y);
                        startPoint.X = (int)x; startPoint.Y = (int)y;
                    }
                    else if (cmd.StartsWith("LineTo"))
                    {
                        float x = 0, y = 0;
                        GetParams(delim, ref cmd, 6, ref x, ref y);
                        endPoint.X = (int)x; endPoint.Y = (int)y;
                        SKPoint a = new SKPoint(startPoint.X, startPoint.Y);
                        SKPoint b = new SKPoint(endPoint.X, endPoint.Y);
                        canvas.DrawLine(a, b, stroke);
                        DrawCap(canvas, a, b, startCap, stroke);
                        DrawCap(canvas, b, a, endCap, stroke);
                        startPoint = endPoint;
                    }
                    else if (cmd.StartsWith("Image"))
                    {
                        reuseable = false;
                        float x = 0, y = 0;
                        GetParams(delim, ref cmd, 5, ref x, ref y);
                        endPoint.X = (int)x; endPoint.Y = (int)y;
                        using SKBitmap image = ImageHttpRequest(cmd);
                        if (image != null)
                        {
                            canvas.DrawBitmap(image, new SKRect(startPoint.X, startPoint.Y, startPoint.X + x, startPoint.Y + y));
                        }
                        else
                        {
                            using SKTypeface face = MakeTypeface(m_fontName, false, false);
                            using SKFont font = new SKFont(face, 6);
                            canvas.DrawText("URL couldn't be resolved or is", startPoint.X, startPoint.Y + 7, font, fill);
                            canvas.DrawText("not an image. Please check URL.", startPoint.X, startPoint.Y + 19, font, fill);
                            canvas.DrawRect(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y, stroke);
                        }
                        startPoint.X += endPoint.X; startPoint.Y += endPoint.Y;
                    }
                    else if (cmd.StartsWith("Rectangle") || cmd.StartsWith("FillRectangle"))
                    {
                        bool filled = cmd.StartsWith("FillRectangle");
                        float x = 0, y = 0;
                        GetParams(delim, ref cmd, filled ? 13 : 9, ref x, ref y);
                        endPoint.X = (int)x; endPoint.Y = (int)y;
                        canvas.DrawRect(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y, filled ? fill : stroke);
                        startPoint.X += endPoint.X; startPoint.Y += endPoint.Y;
                    }
                    else if (cmd.StartsWith("FillPolygon") || cmd.StartsWith("Polygon"))
                    {
                        bool filled = cmd.StartsWith("FillPolygon");
                        PointF[] points = null;
                        GetParams(delim, ref cmd, filled ? 11 : 7, ref points);
                        if (points != null && points.Length > 1)
                        {
                            using SKPath path = new SKPath();
                            path.MoveTo(points[0].X, points[0].Y);
                            for (int i = 1; i < points.Length; i++) path.LineTo(points[i].X, points[i].Y);
                            path.Close();
                            canvas.DrawPath(path, filled ? fill : stroke);
                        }
                    }
                    else if (cmd.StartsWith("Ellipse") || cmd.StartsWith("FillEllipse"))
                    {
                        bool filled = cmd.StartsWith("FillEllipse");
                        float x = 0, y = 0;
                        GetParams(delim, ref cmd, filled ? 11 : 7, ref x, ref y);
                        endPoint.X = (int)x; endPoint.Y = (int)y;
                        canvas.DrawOval(new SKRect(startPoint.X, startPoint.Y, startPoint.X + endPoint.X, startPoint.Y + endPoint.Y), filled ? fill : stroke);
                        startPoint.X += endPoint.X; startPoint.Y += endPoint.Y;
                    }
                    else if (cmd.StartsWith("FontSize"))
                        fontSize = Convert.ToSingle(cmd.Substring(8).Trim(), CultureInfo.InvariantCulture);
                    else if (cmd.StartsWith("FontProp"))
                    {
                        foreach (string prop in cmd.Substring(8).Trim().Split(delim))
                        {
                            switch (prop.Trim())
                            {
                                case "B": bold = true; break;
                                case "I": italic = true; break;
                                case "U": underline = true; break;
                                case "S": strike = true; break;
                                case "R": bold = italic = underline = strike = false; break;
                            }
                        }
                    }
                    else if (cmd.StartsWith("FontName"))
                        fontName = cmd.Substring(8).Trim();
                    else if (cmd.StartsWith("PenSize"))
                        stroke.StrokeWidth = Convert.ToSingle(cmd.Substring(7).Trim(), CultureInfo.InvariantCulture);
                    else if (cmd.StartsWith("PenCap"))
                    {
                        string[] parts = cmd.Substring(6).Trim().Split(delim);
                        if (parts.Length >= 2)
                        {
                            string where = parts[0].Trim().ToLowerInvariant();
                            string cap = parts[1].Trim().ToLowerInvariant();
                            if (where == "start" || where == "both") startCap = cap;
                            if (where == "end" || where == "both") endCap = cap;
                        }
                    }
                    else if (cmd.StartsWith("PenColour") || cmd.StartsWith("PenColor"))
                    {
                        color = ParseColor(cmd.Substring(9).Trim());
                        stroke.Color = color; fill.Color = color;
                    }
                }
            }
            finally { canvas.RestoreToCount(baseSave); }
        }

        private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref float x)
        {
            line = line.Remove(0, startLength);
            string[] parts = line.Split(partsDelimiter);
            if (parts.Length > 0)
            {
                string xVal = parts[0].Trim();
                x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
            }
        }

        private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref float x, ref float y)
        {
            line = line.Remove(0, startLength);
            string[] parts = line.Split(partsDelimiter);
            if (parts.Length == 2)
            {
                string xVal = parts[0].Trim();
                string yVal = parts[1].Trim();
                x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
                y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);
            }
            else if (parts.Length > 2)
            {
                string xVal = parts[0].Trim();
                string yVal = parts[1].Trim();
                x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
                y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);

                line = "";
                for (int i = 2; i < parts.Length; i++)
                {
                    line = line + parts[i].Trim();
                    line = line + " ";
                }
            }
        }

        private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref PointF[] points)
        {
            line = line.Remove(0, startLength);
            string[] parts = line.Split(partsDelimiter);
            if (parts.Length > 1 && parts.Length % 2 == 0)
            {
                points = new PointF[parts.Length / 2];
                for (int i = 0; i < parts.Length; i = i + 2)
                {
                    string xVal = parts[i].Trim();
                    string yVal = parts[i+1].Trim();
                    float x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
                    float y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);
                    PointF point = new PointF(x, y);
                    points[i / 2] = point;

//                    m_log.DebugFormat("[VECTOR RENDER MODULE]: Got point {0}", points[i / 2]);
                }
            }
        }

        private SKBitmap ImageHttpRequest(string url)
        {
            try
            {
                WebRequest request = HttpWebRequest.Create(url);

                using (HttpWebResponse response = (HttpWebResponse)(request).GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        using (Stream s = response.GetResponseStream())
                        {
                            SKBitmap image = SKBitmap.Decode(s);
                            return image;
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}

