using System;
using System.Collections.Generic;

using OpenMetaverse;
using OpenMetaverse.Rendering;

namespace OpenSim.Framework.SculptGeometry
{
    public static class FacetedMeshAdapter
    {
        public static FacetedMesh Convert(
            Primitive prim,
            SculptMesh sculpt)
        {
            if (prim == null)
                throw new ArgumentNullException(nameof(prim));

            if (sculpt == null)
                throw new ArgumentNullException(nameof(sculpt));

            FacetedMesh result = new FacetedMesh
            {
                Prim = prim,
                Faces = new List<OpenMetaverse.Rendering.Face>()
            };

            // OpenMetaverse uses ushort vertex indices.
            // Sculpt maps should normally be far smaller than this.
            if (sculpt.coords.Count > ushort.MaxValue + 1)
                throw new ArgumentException(
                    "Sculpt mesh exceeds maximum vertex count");

            OpenMetaverse.Rendering.Face face =
                new OpenMetaverse.Rendering.Face
                {
                    Vertices = new List<Vertex>(sculpt.coords.Count),
                    Indices = new List<ushort>(
                        checked(sculpt.faces.Count * 3))
                };

            foreach (Vector3 coord in sculpt.coords)
            {
                face.Vertices.Add(new Vertex
                {
                    Position = coord
                });
            }

            foreach (var triangle in sculpt.faces)
            {
                if (triangle.v1 < 0 ||
                    triangle.v2 < 0 ||
                    triangle.v3 < 0 ||
                    triangle.v1 >= sculpt.coords.Count ||
                    triangle.v2 >= sculpt.coords.Count ||
                    triangle.v3 >= sculpt.coords.Count)
                {
                    throw new ArgumentException(
                        "Sculpt mesh contains invalid triangle indices");
                }

                face.Indices.Add((ushort)triangle.v1);
                face.Indices.Add((ushort)triangle.v2);
                face.Indices.Add((ushort)triangle.v3);
            }

            result.Faces.Add(face);

            return result;
        }
    }
}

