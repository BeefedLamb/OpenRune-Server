using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsrsPort
{
    /// <summary>
    /// Builds (and caches) Unity meshes for OSRS player bodies from a resolved look sent by the
    /// server as `PLOOK`: [bodyType, 5 colours, 12 slots]. Shared by the local avatar
    /// (third person) and other players drawn by <see cref="OsrsNpcManager"/>.
    /// </summary>
    public static class OsrsPlayerModels
    {
        public const int LookLength = 18;
        private const int MaxCached = 64;
        private static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();

        public static string Key(int[] look) => look == null ? "" : string.Join(",", look);

        /// <summary>The body mesh for <paramref name="look"/>, or null if parts aren't exported / loaded yet.</summary>
        public static Mesh GetMesh(int[] look)
        {
            var world = OsrsWorldLoader.Instance;
            if (look == null || look.Length < LookLength || world == null || world.Data == null || !world.Data.HasPlayerParts)
                return null;
            string key = Key(look);
            if (cache.TryGetValue(key, out var cached) && cached != null) return cached;
            var vm = world.Data.BuildPlayerMesh(look);
            if (vm.faceCount == 0) return null;
            if (cache.Count >= MaxCached)
            {
                foreach (var m in cache.Values) if (m != null) Object.Destroy(m);
                cache.Clear();
            }
            var mesh = ToMesh(vm, "player_" + key.GetHashCode().ToString("x8"));
            cache[key] = mesh;
            return mesh;
        }

        /// <summary>Flat-shaded mesh from exported triangles (positions in 1/128 tile units).</summary>
        public static Mesh ToMesh(WorldData.VariantMesh vm, string name)
        {
            int nv = vm.faceCount * 3;
            var verts = new Vector3[nv];
            var normals = new Vector3[nv];
            var indices = new int[nv];
            for (int i = 0; i < nv; i++)
            {
                verts[i] = new Vector3(vm.positions[i * 3], vm.positions[i * 3 + 1], vm.positions[i * 3 + 2]) / 128f;
                indices[i] = i;
            }
            for (int i = 0; i < nv; i += 3)
            {
                var n = Vector3.Cross(verts[i + 1] - verts[i], verts[i + 2] - verts[i]).normalized;
                normals[i] = n; normals[i + 1] = n; normals[i + 2] = n;
            }
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(vm.colors);
            mesh.SetTriangles(indices, 0, false);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
