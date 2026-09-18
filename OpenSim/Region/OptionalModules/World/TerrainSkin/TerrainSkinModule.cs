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
            m_tileSize = Math.Max(4, config.GetInt("TileSize", 64));
            m_sampleSpacing = Math.Max(1, config.GetInt("SampleSpacing", 2));
            m_verticalOffset = config.GetFloat("VerticalOffset", 0.03f);
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
                $"owner={m_owner}, texture={m_texture}, tile={m_tileSize}m, sample={m_sampleSpacing}m, offset={m_verticalOffset:0.###}m");
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
                regionWidth, regionHeight, m_sampleSpacing);

            byte[] meshData = MeshAssetEncoder.Encode(mesh);
            UUID meshID = UUID.Random();
            AssetBase asset = new(meshID,
                $"Terrain skin {m_scene.RegionInfo.RegionName} {originX},{originY}",
                (sbyte)AssetType.Mesh, m_owner.ToString())
            {
                Data = meshData,
                Description = "Generated terrain skin mesh"
            };

            string storedID = m_scene.AssetService.Store(asset);
            if (string.IsNullOrEmpty(storedID) || storedID == UUID.Zero.ToString())
                throw new InvalidOperationException($"Could not store mesh asset for tile {originX},{originY}");

            PrimitiveBaseShape shape = PrimitiveBaseShape.CreateMesh(1, meshID);
            Primitive.TextureEntry te = new(texture);
            shape.TextureEntry = te.GetBytes(1);

            float scaleZ = Math.Max(mesh.MaxHeight - mesh.MinHeight, 0.01f);
            Vector3 position = new(
                originX + width * 0.5f,
                originY + height * 0.5f,
                (mesh.MinHeight + mesh.MaxHeight) * 0.5f + m_verticalOffset);

            SceneObjectGroup group = m_scene.AddNewPrim(m_owner, UUID.Zero, position,
                Quaternion.Identity, shape);
            SceneObjectPart part = group.RootPart;
            part.Name = $"{ObjectPrefix}{m_scene.RegionInfo.RegionID}:{originX}:{originY}";
            part.Description = "Generated visual terrain skin; native terrain supplies physics";
            part.Scale = new Vector3(width, height, scaleZ);
            part.PhysicsShapeType = (byte)PhysShapeType.none;
            part.AddFlag(PrimFlags.Phantom);
            group.HasGroupChanged = true;
            group.ScheduleGroupForFullUpdate();
        }
    }

    internal sealed class TileMesh
    {
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector2> TexCoords = new();
        public readonly List<ushort> Indices = new();
        public float MinHeight = float.MaxValue;
        public float MaxHeight = float.MinValue;

        public static TileMesh FromTerrain(ITerrainChannel terrain,
            int originX, int originY, int width, int height,
            int regionWidth, int regionHeight, int spacing)
        {
            TileMesh result = new();
            List<int> xs = BuildSamples(width, spacing);
            List<int> ys = BuildSamples(height, spacing);

            foreach (int localY in ys)
            {
                foreach (int localX in xs)
                {
                    int worldX = Math.Min(originX + localX, terrain.Width - 1);
                    int worldY = Math.Min(originY + localY, terrain.Height - 1);
                    float z = terrain[worldX, worldY];
                    result.MinHeight = Math.Min(result.MinHeight, z);
                    result.MaxHeight = Math.Max(result.MaxHeight, z);
                    result.Positions.Add(new Vector3(
                        localX / (float)width - 0.5f,
                        localY / (float)height - 0.5f,
                        z));
                    result.TexCoords.Add(new Vector2(
                        (originX + localX) / (float)regionWidth,
                        (originY + localY) / (float)regionHeight));
                }
            }

            int row = xs.Count;
            for (int y = 0; y < ys.Count - 1; ++y)
            {
                for (int x = 0; x < xs.Count - 1; ++x)
                {
                    ushort sw = checked((ushort)(y * row + x));
                    ushort se = checked((ushort)(sw + 1));
                    ushort nw = checked((ushort)(sw + row));
                    ushort ne = checked((ushort)(nw + 1));

                    result.Indices.Add(sw); result.Indices.Add(se); result.Indices.Add(ne);
                    result.Indices.Add(sw); result.Indices.Add(ne); result.Indices.Add(nw);
                }
            }

            float center = (result.MinHeight + result.MaxHeight) * 0.5f;
            float scale = Math.Max(result.MaxHeight - result.MinHeight, 0.01f);
            for (int i = 0; i < result.Positions.Count; ++i)
            {
                Vector3 p = result.Positions[i];
                p.Z = (p.Z - center) / scale;
                result.Positions[i] = p;
            }
            return result;
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
            byte[] high = Compress(BuildLod(mesh));
            byte[] convex = Compress(BuildConvex());

            OSDMap header = new()
            {
                ["version"] = OSD.FromInteger(1),
                ["high_lod"] = Block(0, high.Length),
                ["physics_convex"] = Block(high.Length, convex.Length)
            };

            byte[] headerBytes = OSDParser.SerializeLLSDBinary(header);
            byte[] asset = new byte[headerBytes.Length + high.Length + convex.Length];
            Buffer.BlockCopy(headerBytes, 0, asset, 0, headerBytes.Length);
            Buffer.BlockCopy(high, 0, asset, headerBytes.Length, high.Length);
            Buffer.BlockCopy(convex, 0, asset, headerBytes.Length + high.Length, convex.Length);
            return asset;
        }

        private static OSDMap Block(int offset, int size) => new()
        {
            ["offset"] = OSD.FromInteger(offset),
            ["size"] = OSD.FromInteger(size)
        };

        private static OSD BuildLod(TileMesh mesh)
        {
            byte[] positions = new byte[mesh.Positions.Count * 6];
            byte[] texCoords = new byte[mesh.TexCoords.Count * 4];
            byte[] triangles = new byte[mesh.Indices.Count * 2];

            for (int i = 0; i < mesh.Positions.Count; ++i)
            {
                Vector3 p = mesh.Positions[i];
                PutU16(positions, i * 6, Quantize(p.X, -0.5f, 0.5f));
                PutU16(positions, i * 6 + 2, Quantize(p.Y, -0.5f, 0.5f));
                PutU16(positions, i * 6 + 4, Quantize(p.Z, -0.5f, 0.5f));

                Vector2 uv = mesh.TexCoords[i];
                PutU16(texCoords, i * 4, Quantize(uv.X, 0f, 1f));
                PutU16(texCoords, i * 4 + 2, Quantize(uv.Y, 0f, 1f));
            }

            for (int i = 0; i < mesh.Indices.Count; ++i)
                PutU16(triangles, i * 2, mesh.Indices[i]);

            OSDMap submesh = new()
            {
                ["Position"] = OSD.FromBinary(positions),
                ["TriangleList"] = OSD.FromBinary(triangles),
                ["TexCoord0"] = OSD.FromBinary(texCoords),
                ["TexCoord0Domain"] = new OSDMap
                {
                    ["Min"] = OSD.FromVector2(Vector2.Zero),
                    ["Max"] = OSD.FromVector2(new Vector2(1f, 1f))
                }
            };
            return new OSDArray { submesh };
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
            return new OSDMap { ["BoundingVerts"] = OSD.FromBinary(points) };
        }

        private static byte[] Compress(OSD value)
        {
            byte[] raw = OSDParser.SerializeLLSDBinary(value);
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

        private static void PutU16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value >> 8);
            data[offset + 1] = (byte)value;
        }
    }
}

