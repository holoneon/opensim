/*
 * Copyright 2026 Fiona Sweet
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the conditions in the OpenSim
 * BSD license are met.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.OptionalModules.World.TerrainSkin
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "TerrainSkinModule")]
    public sealed class TerrainSkinModule : INonSharedRegionModule
    {
        private const string LogHeader = "[TERRAIN SKIN]";
        private const string ObjectPrefix = "TerrainSkin:";
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_enabled;
        private UUID m_owner = UUID.Zero;
        private UUID m_texture = UUID.Zero;
        private int m_tileSize = 64;
        private int m_sampleSpacing = 2;
        private float m_verticalOffset = 0.03f;
        private bool m_flipTextureV;

        public string Name => "TerrainSkinModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["TerrainSkin"];
            if (config == null)
                return;

            m_enabled = config.GetBoolean("Enabled", false);
            UUID.TryParse(config.GetString("OwnerUUID", string.Empty), out m_owner);
            UUID.TryParse(config.GetString("TextureUUID", string.Empty), out m_texture);
            // Keep tiles inside the non-physical prim size limit (default 256m).
            m_tileSize = Math.Clamp(config.GetInt("TileSize", 64), 4, 256);
            m_sampleSpacing = Math.Max(1, config.GetInt("SampleSpacing", 2));
            m_verticalOffset = config.GetFloat("VerticalOffset", 0.03f);
            m_flipTextureV = config.GetBoolean("FlipTextureV", false);
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.AddCommand(
                "Terrain", this, "terrain skin generate",
                "terrain skin generate [<texture-uuid>]",
                "Generate a phantom mesh skin over the selected region terrain.",
                HandleGenerate);

            scene.AddCommand(
                "Terrain", this, "terrain skin remove",
                "terrain skin remove",
                "Remove terrain-skin objects from the selected region.",
                HandleRemove);

            scene.AddCommand(
                "Terrain", this, "terrain skin status",
                "terrain skin status",
                "Show terrain-skin settings for the selected region.",
                HandleStatus);
        }

        public void RemoveRegion(Scene scene)
        {
            if (m_scene == scene)
                m_scene = null;
        }

        public void Close() { }

        private bool IsSelectedScene()
        {
            return m_scene != null &&
                (MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == m_scene);
        }

        private void HandleStatus(string module, string[] args)
        {
            if (!IsSelectedScene())
                return;

            MainConsole.Instance.Output(
                $"{LogHeader} region={m_scene.RegionInfo.RegionName}, size={m_scene.Heightmap.Width}x{m_scene.Heightmap.Height}, " +
                $"owner={m_owner}, texture={m_texture}, tile={m_tileSize}m, sample={m_sampleSpacing}m, " +
                $"offset={m_verticalOffset:0.###}m, flipV={m_flipTextureV}");
        }

        private void HandleGenerate(string module, string[] args)
        {
            if (!IsSelectedScene())
                return;

            UUID texture = m_texture;
            if (args.Length >= 4 && !UUID.TryParse(args[3], out texture))
            {
                MainConsole.Instance.Output($"{LogHeader} Invalid texture UUID: {args[3]}");
                return;
            }

            if (m_owner.IsZero())
            {
                MainConsole.Instance.Output($"{LogHeader} OwnerUUID must be configured before generating a skin.");
                return;
            }

            if (texture.IsZero())
            {
                MainConsole.Instance.Output($"{LogHeader} TextureUUID must be configured or supplied to the command.");
                return;
            }

            if (m_scene.AssetService.Get(texture.ToString()) == null)
            {
                MainConsole.Instance.Output($"{LogHeader} Texture asset {texture} was not found.");
                return;
            }

            int oldCount = RemoveObjects();
            int created = 0;

            try
            {
                int width = m_scene.Heightmap.Width;
                int height = m_scene.Heightmap.Height;

                for (int y = 0; y < height; y += m_tileSize)
                {
                    int tileHeight = Math.Min(m_tileSize, height - y);
                    for (int x = 0; x < width; x += m_tileSize)
                    {
                        int tileWidth = Math.Min(m_tileSize, width - x);
                        CreateTile(x, y, tileWidth, tileHeight, width, height, texture);
                        ++created;
                    }
                }

                MainConsole.Instance.Output($"{LogHeader} Generated {created} tiles; removed {oldCount} previous tiles.");
            }
            catch (Exception e)
            {
                m_log.Error($"{LogHeader} Generation failed after {created} tiles", e);
                MainConsole.Instance.Output($"{LogHeader} Generation failed after {created} tiles: {e.Message}");
            }
        }

        private void HandleRemove(string module, string[] args)
        {
            if (!IsSelectedScene())
                return;

            int count = RemoveObjects();
            MainConsole.Instance.Output($"{LogHeader} Removed {count} terrain-skin objects.");
        }

        private int RemoveObjects()
        {
            int count = 0;
            foreach (SceneObjectGroup group in m_scene.GetSceneObjectGroups())
            {
                if (group.RootPart.Name.StartsWith(ObjectPrefix, StringComparison.Ordinal))
                {
                    m_scene.DeleteSceneObject(group, false);
                    ++count;
                }
            }
            return count;
        }

        private void CreateTile(int originX, int originY, int width, int height,
            int regionWidth, int regionHeight, UUID texture)
        {
            TileMesh mesh = TileMesh.FromTerrain(
                m_scene.Heightmap, originX, originY, width, height,
                regionWidth, regionHeight, m_sampleSpacing, m_flipTextureV);

            byte[] meshData = MeshAssetEncoder.Encode(mesh);
            UUID meshID = UUID.Random();
            AssetBase asset = new(meshID,
                $"Terrain skin {m_scene.RegionInfo.RegionName} {originX},{originY}",
                (sbyte)AssetType.Mesh, m_owner.ToString())
            {
                Data = meshData,
                Description = "Generated terrain skin mesh",
                Local = false,
                Temporary = false
            };

            string storedID = m_scene.AssetService.Store(asset);
            if (string.IsNullOrEmpty(storedID) || storedID == UUID.Zero.ToString())
                throw new InvalidOperationException($"Could not store mesh asset for tile {originX},{originY}");

            PrimitiveBaseShape shape = PrimitiveBaseShape.CreateMesh(1, meshID);
            Primitive.TextureEntry te = new(texture);
            shape.TextureEntry = te.GetBytes(1);

            Vector3 position = new(
                originX + width * 0.5f,
                originY + height * 0.5f,
                (mesh.MinHeight + mesh.MaxHeight) * 0.5f + m_verticalOffset);

            SceneObjectGroup group = m_scene.AddNewPrim(m_owner, UUID.Zero, position,
                Quaternion.Identity, shape);
            SceneObjectPart part = group.RootPart;
            part.Name = $"{ObjectPrefix}{m_scene.RegionInfo.RegionID}:{originX}:{originY}";
            part.Description = "Generated visual terrain skin; native terrain supplies physics";
            // HeightScale is the same divisor the vertices were normalised by, so the
            // mesh ends up exactly on top of the real terrain.
            part.Scale = new Vector3(width, height, mesh.HeightScale);
            part.PhysicsShapeType = (byte)PhysShapeType.none;
            part.AddFlag(PrimFlags.Phantom);
            group.HasGroupChanged = true;
            group.ScheduleGroupForFullUpdate();
        }
    }

    /// <summary>
    /// A regular sample grid over a rectangle of the heightmap, expressed in mesh-local
    /// space: X, Y and Z all normalised into -0.5 .. 0.5.
    /// </summary>
    internal sealed class TileMesh
    {
        public const float MinHeightScale = 0.01f;

        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector2[] TexCoords;
        public int Cols;
        public int Rows;
        public float MinHeight;
        public float MaxHeight;
        public float HeightScale;

        public static TileMesh FromTerrain(ITerrainChannel terrain,
            int originX, int originY, int width, int height,
            int regionWidth, int regionHeight, int spacing, bool flipV)
        {
            List<int> xs = BuildSamples(width, spacing);
            List<int> ys = BuildSamples(height, spacing);

            long verts = (long)xs.Count * ys.Count;
            if (verts > 65535)
                throw new InvalidOperationException(
                    $"Tile would need {verts} vertices; reduce TileSize or increase SampleSpacing.");

            TileMesh m = new()
            {
                Cols = xs.Count,
                Rows = ys.Count,
                Positions = new Vector3[verts],
                Normals = new Vector3[verts],
                TexCoords = new Vector2[verts],
                MinHeight = float.MaxValue,
                MaxHeight = float.MinValue
            };

            for (int r = 0; r < ys.Count; ++r)
            {
                int localY = ys[r];
                int worldY = Math.Min(originY + localY, terrain.Height - 1);

                for (int c = 0; c < xs.Count; ++c)
                {
                    int localX = xs[c];
                    int worldX = Math.Min(originX + localX, terrain.Width - 1);

                    float z = (float)terrain[worldX, worldY];
                    if (z < m.MinHeight) m.MinHeight = z;
                    if (z > m.MaxHeight) m.MaxHeight = z;

                    int i = r * xs.Count + c;
                    m.Positions[i] = new Vector3(
                        localX / (float)width - 0.5f,
                        localY / (float)height - 0.5f,
                        z);

                    float u = (originX + localX) / (float)regionWidth;
                    float v = (originY + localY) / (float)regionHeight;
                    m.TexCoords[i] = new Vector2(u, flipV ? 1f - v : v);
                }
            }

            float center = (m.MinHeight + m.MaxHeight) * 0.5f;
            m.HeightScale = Math.Max(m.MaxHeight - m.MinHeight, MinHeightScale);

            for (int i = 0; i < m.Positions.Length; ++i)
            {
                Vector3 p = m.Positions[i];
                p.Z = (p.Z - center) / m.HeightScale;
                m.Positions[i] = p;
            }

            m.BuildNormals();
            return m;
        }

        /// <summary>
        /// Central-difference normals in mesh-local space. Cross(dX, dY) points at +Z,
        /// which matches the counter-clockwise-from-above winding used below.
        /// </summary>
        private void BuildNormals()
        {
            for (int r = 0; r < Rows; ++r)
            {
                int rDown = r > 0 ? r - 1 : r;
                int rUp = r < Rows - 1 ? r + 1 : r;

                for (int c = 0; c < Cols; ++c)
                {
                    int cLeft = c > 0 ? c - 1 : c;
                    int cRight = c < Cols - 1 ? c + 1 : c;

                    Vector3 dx = Positions[r * Cols + cRight] - Positions[r * Cols + cLeft];
                    Vector3 dy = Positions[rUp * Cols + c] - Positions[rDown * Cols + c];

                    Vector3 n = Vector3.Cross(dx, dy);
                    float len = n.Length();
                    Normals[r * Cols + c] = len > 1e-6f ? n / len : Vector3.UnitZ;
                }
            }
        }

        private static List<int> BuildSamples(int length, int spacing)
        {
            List<int> values = new();
            for (int value = 0; value < length; value += spacing)
                values.Add(value);
            if (values.Count == 0 || values[values.Count - 1] != length)
                values.Add(length);
            return values;
        }
    }

    internal static class MeshAssetEncoder
    {
        public static byte[] Encode(TileMesh mesh)
        {
            byte[] high = Compress(BuildLod(mesh, 1));
            byte[] medium = Compress(BuildLod(mesh, 2));
            byte[] low = Compress(BuildLod(mesh, 4));
            byte[] lowest = Compress(BuildLod(mesh, 8));
            byte[] convex = Compress(BuildConvex());

            int highOffset = 0;
            int mediumOffset = highOffset + high.Length;
            int lowOffset = mediumOffset + medium.Length;
            int lowestOffset = lowOffset + low.Length;
            int convexOffset = lowestOffset + lowest.Length;

            OSDMap header = new()
            {
                ["version"] = OSD.FromInteger(1),
                ["high_lod"] = Block(highOffset, high.Length),
                ["medium_lod"] = Block(mediumOffset, medium.Length),
                ["low_lod"] = Block(lowOffset, low.Length),
                ["lowest_lod"] = Block(lowestOffset, lowest.Length),
                ["physics_convex"] = Block(convexOffset, convex.Length)
            };

            // NOTE: no "<?llsd/binary?>" preamble - the viewer parses the mesh header
            // and every block with the raw binary parser.
            byte[] headerBytes = Serialize(header);

            using MemoryStream asset = new(headerBytes.Length + convexOffset + convex.Length);
            asset.Write(headerBytes, 0, headerBytes.Length);
            asset.Write(high, 0, high.Length);
            asset.Write(medium, 0, medium.Length);
            asset.Write(low, 0, low.Length);
            asset.Write(lowest, 0, lowest.Length);
            asset.Write(convex, 0, convex.Length);
            return asset.ToArray();
        }

        private static OSDMap Block(int offset, int size) => new()
        {
            ["offset"] = OSD.FromInteger(offset),
            ["size"] = OSD.FromInteger(size)
        };

        private static OSD BuildLod(TileMesh mesh, int stride)
        {
            int[] cols = Pick(mesh.Cols, stride);
            int[] rows = Pick(mesh.Rows, stride);
            int count = cols.Length * rows.Length;

            byte[] positions = new byte[count * 6];
            byte[] normals = new byte[count * 6];
            byte[] texCoords = new byte[count * 4];

            for (int r = 0; r < rows.Length; ++r)
            {
                for (int c = 0; c < cols.Length; ++c)
                {
                    int src = rows[r] * mesh.Cols + cols[c];
                    int dst = r * cols.Length + c;

                    Vector3 p = mesh.Positions[src];
                    PutU16(positions, dst * 6, Quantize(p.X, -0.5f, 0.5f));
                    PutU16(positions, dst * 6 + 2, Quantize(p.Y, -0.5f, 0.5f));
                    PutU16(positions, dst * 6 + 4, Quantize(p.Z, -0.5f, 0.5f));

                    Vector3 n = mesh.Normals[src];
                    PutU16(normals, dst * 6, Quantize(n.X, -1f, 1f));
                    PutU16(normals, dst * 6 + 2, Quantize(n.Y, -1f, 1f));
                    PutU16(normals, dst * 6 + 4, Quantize(n.Z, -1f, 1f));

                    Vector2 uv = mesh.TexCoords[src];
                    PutU16(texCoords, dst * 4, Quantize(uv.X, 0f, 1f));
                    PutU16(texCoords, dst * 4 + 2, Quantize(uv.Y, 0f, 1f));
                }
            }

            int quads = (cols.Length - 1) * (rows.Length - 1);
            byte[] triangles = new byte[quads * 6 * 2];
            int t = 0;
            for (int r = 0; r < rows.Length - 1; ++r)
            {
                for (int c = 0; c < cols.Length - 1; ++c)
                {
                    ushort sw = (ushort)(r * cols.Length + c);
                    ushort se = (ushort)(sw + 1);
                    ushort nw = (ushort)(sw + cols.Length);
                    ushort ne = (ushort)(nw + 1);

                    PutU16(triangles, t, sw); t += 2;
                    PutU16(triangles, t, se); t += 2;
                    PutU16(triangles, t, ne); t += 2;

                    PutU16(triangles, t, sw); t += 2;
                    PutU16(triangles, t, ne); t += 2;
                    PutU16(triangles, t, nw); t += 2;
                }
            }

            OSDMap submesh = new()
            {
                ["Position"] = OSD.FromBinary(positions),
                ["Normal"] = OSD.FromBinary(normals),
                ["TexCoord0"] = OSD.FromBinary(texCoords),
                ["TriangleList"] = OSD.FromBinary(triangles),
                ["PositionDomain"] = new OSDMap
                {
                    ["Min"] = OSD.FromVector3(new Vector3(-0.5f, -0.5f, -0.5f)),
                    ["Max"] = OSD.FromVector3(new Vector3(0.5f, 0.5f, 0.5f))
                },
                ["TexCoord0Domain"] = new OSDMap
                {
                    ["Min"] = OSD.FromVector2(Vector2.Zero),
                    ["Max"] = OSD.FromVector2(new Vector2(1f, 1f))
                }
            };
            return new OSDArray { submesh };
        }

        /// <summary>Row/column indices for a decimated LOD; always keeps both edges.</summary>
        private static int[] Pick(int count, int stride)
        {
            if (stride < 1)
                stride = 1;

            List<int> picked = new();
            for (int i = 0; i < count; i += stride)
                picked.Add(i);

            if (picked[picked.Count - 1] != count - 1)
                picked.Add(count - 1);

            if (picked.Count < 2)
                picked.Add(count - 1);

            return picked.ToArray();
        }

        private static OSD BuildConvex()
        {
            Vector3[] corners =
            {
                new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
                new(-0.5f, 0.5f, -0.5f),  new(0.5f, 0.5f, -0.5f),
                new(-0.5f, -0.5f, 0.5f),  new(0.5f, -0.5f, 0.5f),
                new(-0.5f, 0.5f, 0.5f),   new(0.5f, 0.5f, 0.5f)
            };
            byte[] points = new byte[corners.Length * 6];
            for (int i = 0; i < corners.Length; ++i)
            {
                PutU16(points, i * 6, Quantize(corners[i].X, -0.5f, 0.5f));
                PutU16(points, i * 6 + 2, Quantize(corners[i].Y, -0.5f, 0.5f));
                PutU16(points, i * 6 + 4, Quantize(corners[i].Z, -0.5f, 0.5f));
            }
            return new OSDMap
            {
                ["Min"] = OSD.FromVector3(new Vector3(-0.5f, -0.5f, -0.5f)),
                ["Max"] = OSD.FromVector3(new Vector3(0.5f, 0.5f, 0.5f)),
                ["BoundingVerts"] = OSD.FromBinary(points)
            };
        }

        private static byte[] Serialize(OSD value)
        {
            // prependHeader: false - the mesh format wants bare LLSD binary.
            return OSDParser.SerializeLLSDBinary(value, false);
        }

        private static byte[] Compress(OSD value)
        {
            byte[] raw = Serialize(value);
            using MemoryStream output = new();
            using (ZLibStream zlib = new(output, CompressionLevel.Optimal, true))
                zlib.Write(raw, 0, raw.Length);
            return output.ToArray();
        }

        private static ushort Quantize(float value, float min, float max)
        {
            float normalized = Math.Clamp((value - min) / (max - min), 0f, 1f);
            return (ushort)Math.Round(normalized * ushort.MaxValue);
        }

        /// <summary>Mesh payload integers are little-endian U16.</summary>
        private static void PutU16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        }
    }
}

