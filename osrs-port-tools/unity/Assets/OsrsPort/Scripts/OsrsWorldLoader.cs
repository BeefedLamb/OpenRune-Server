using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsrsPort
{
    /// <summary>
    /// Streams the exported OSRS world (StreamingAssets/world) around the player: map squares
    /// within <see cref="radius"/> are built on a background thread and uploaded one per frame,
    /// squares further away are freed. Starts automatically when you press Play.
    /// 1 Unity unit = 1 OSRS tile; x = east, z = north; world position = OSRS tile coordinate.
    /// </summary>
    public class OsrsWorldLoader : MonoBehaviour
    {
        public static OsrsWorldLoader Instance { get; private set; }

        public int radius = 1;
        public Vector2 spawnTile = new Vector2(3221.5f, 3218.5f);

        /// <summary>Kept for the server link; the world no longer has an origin shift.</summary>
        public int BaseX => 0;
        public int BaseY => 0;

        private WorldData world;
        private Material material;
        private Transform player;
        private readonly Dictionary<(int, int), GameObject> loaded = new Dictionary<(int, int), GameObject>();
        private readonly HashSet<(int, int)> pending = new HashSet<(int, int)>();
        private readonly Queue<SquareBuild> uploads = new Queue<SquareBuild>();
        private Task<SquareBuild> building;
        private (int, int) buildingKey;
        private Task loadTask;
        private string status = "Loading world...";

        private class SquareBuild
        {
            public int rx, ry;
            public float[] terrainPos; public Color32[] terrainCol;
            public float[] solidPos; public Color32[] solidCol;
            public float[] decorPos; public Color32[] decorCol;
            public int placements;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (FindFirstObjectByType<OsrsWorldLoader>() != null) return;
            if (!File.Exists(Path.Combine(Application.streamingAssetsPath, "world", "index.bin"))) return;
            new GameObject("OsrsWorld").AddComponent<OsrsWorldLoader>();
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            var shader = Shader.Find("OsrsPort/VertexColorLit");
            if (shader == null) { Debug.LogError("OsrsPort: shader 'OsrsPort/VertexColorLit' not found."); return; }
            material = new Material(shader);
            world = new WorldData(Path.Combine(Application.streamingAssetsPath, "world"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            loadTask = Task.Run(() =>
            {
                world.Load();
                Debug.Log($"OsrsPort: world index loaded: {world.squares.Count} squares, {world.models.Count:N0} models, {world.variants.Length:N0} variants in {sw.ElapsedMilliseconds} ms");
            });
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 28f;
            RenderSettings.fogEndDistance = 62f;
            RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.82f);
        }

        public bool GroundReadyAt(Vector3 position)
        {
            int rx = Mathf.FloorToInt(position.x) >> 6, ry = Mathf.FloorToInt(position.z) >> 6;
            return loaded.ContainsKey((rx, ry)) || (world != null && loadTask != null && loadTask.IsCompleted && !world.squares.ContainsKey((rx, ry)));
        }

        private void Update()
        {
            if (world == null || loadTask == null) return;
            if (loadTask.IsFaulted)
            {
                status = "World failed to load: " + loadTask.Exception?.GetBaseException().Message;
                Debug.LogError(status);
                loadTask = null;
                return;
            }
            if (!loadTask.IsCompleted) return;

            Vector3 focus = player != null ? player.position : new Vector3(spawnTile.x, 0f, spawnTile.y);
            int cx = Mathf.FloorToInt(focus.x) >> 6, cz = Mathf.FloorToInt(focus.z) >> 6;

            CollectFinishedBuild();
            if (uploads.Count > 0)
            {
                Upload(uploads.Dequeue());
            }
            if (building == null)
                StartNextBuild(cx, cz);
            UnloadFar(cx, cz);

            if (player == null && loaded.ContainsKey((Mathf.FloorToInt(spawnTile.x) >> 6, Mathf.FloorToInt(spawnTile.y) >> 6)))
                SpawnPlayer();
            status = $"Squares loaded: {loaded.Count}" + (building != null ? $" (building {buildingKey.Item1}_{buildingKey.Item2})" : "");
        }

        private void StartNextBuild(int cx, int cz)
        {
            (int, int)? best = null;
            int bestDist = int.MaxValue;
            for (int dx = -radius; dx <= radius; dx++)
                for (int dz = -radius; dz <= radius; dz++)
                {
                    var key = (cx + dx, cz + dz);
                    if (loaded.ContainsKey(key) || pending.Contains(key) || !world.squares.ContainsKey(key)) continue;
                    int dist = dx * dx + dz * dz;
                    if (dist < bestDist) { bestDist = dist; best = key; }
                }
            if (best == null) return;
            buildingKey = best.Value;
            pending.Add(buildingKey);
            int rx = buildingKey.Item1, ry = buildingKey.Item2;
            building = Task.Run(() => BuildSquare(rx, ry));
        }

        private void CollectFinishedBuild()
        {
            if (building == null || !building.IsCompleted) return;
            if (building.IsFaulted)
                Debug.LogError($"OsrsPort: square {buildingKey} failed: {building.Exception?.GetBaseException()}");
            else if (building.Result != null)
                uploads.Enqueue(building.Result);
            pending.Remove(buildingKey);
            building = null;
        }

        private void UnloadFar(int cx, int cz)
        {
            List<(int, int)> far = null;
            foreach (var key in loaded.Keys)
                if (Mathf.Abs(key.Item1 - cx) > radius + 1 || Mathf.Abs(key.Item2 - cz) > radius + 1)
                    (far ??= new List<(int, int)>()).Add(key);
            if (far == null) return;
            foreach (var key in far)
            {
                var go = loaded[key];
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                    Destroy(mf.sharedMesh);
                Destroy(go);
                loaded.Remove(key);
            }
        }

        private SquareBuild BuildSquare(int rx, int ry)
        {
            var d = world.ReadSquare(rx, ry);
            if (d == null) return null;
            if (d[0] != 'S' || d[1] != 'Q' || d[2] != '1') throw new InvalidDataException("bad square blob");
            int p = 11;
            int nv = BitConverter.ToInt32(d, p); p += 4;
            var build = new SquareBuild { rx = rx, ry = ry, terrainPos = new float[nv * 3], terrainCol = new Color32[nv] };
            float ox = rx * 64f, oz = ry * 64f;
            for (int i = 0; i < nv; i++)
            {
                build.terrainPos[i * 3] = ox + BitConverter.ToInt16(d, p) / 128f;
                build.terrainPos[i * 3 + 1] = BitConverter.ToInt16(d, p + 2) / 128f;
                build.terrainPos[i * 3 + 2] = oz + BitConverter.ToInt16(d, p + 4) / 128f;
                p += 6;
            }
            for (int i = 0; i < nv; i++) { build.terrainCol[i] = new Color32(d[p], d[p + 1], d[p + 2], 255); p += 3; }

            int np = BitConverter.ToInt32(d, p); p += 4;
            build.placements = np;
            int start = p;
            int solidFaces = 0, decorFaces = 0;
            for (int i = 0; i < np; i++)
            {
                int vid = BitConverter.ToInt32(d, p);
                bool decor = d[p + 15] != 0;
                int faces = world.GetVariantMesh(vid).faceCount;
                if (decor) decorFaces += faces; else solidFaces += faces;
                p += 16;
            }
            build.solidPos = new float[solidFaces * 9]; build.solidCol = new Color32[solidFaces * 3];
            build.decorPos = new float[decorFaces * 9]; build.decorCol = new Color32[decorFaces * 3];
            int so = 0, de = 0;
            p = start;
            for (int i = 0; i < np; i++)
            {
                int vid = BitConverter.ToInt32(d, p);
                int yaw = BitConverter.ToUInt16(d, p + 7);
                float px = ox + BitConverter.ToInt16(d, p + 9) / 128f;
                float pz = oz + BitConverter.ToInt16(d, p + 11) / 128f;
                float py = BitConverter.ToInt16(d, p + 13) / 128f;
                bool decor = d[p + 15] != 0;
                p += 16;
                var vm = world.GetVariantMesh(vid);
                float ang = yaw * (2f * Mathf.PI / 2048f);
                float s = Mathf.Sin(ang), c = Mathf.Cos(ang);
                float[] dst = decor ? build.decorPos : build.solidPos;
                Color32[] dcol = decor ? build.decorCol : build.solidCol;
                int o = decor ? de : so;
                int n = vm.faceCount * 3;
                for (int k = 0; k < n; k++)
                {
                    float x = vm.positions[k * 3], y = vm.positions[k * 3 + 1], z = vm.positions[k * 3 + 2];
                    dst[(o + k) * 3] = px + (c * x + s * z) / 128f;
                    dst[(o + k) * 3 + 1] = py + y / 128f;
                    dst[(o + k) * 3 + 2] = pz + (-s * x + c * z) / 128f;
                    dcol[o + k] = vm.colors[k];
                }
                if (decor) de += n; else so += n;
            }
            return build;
        }

        private void Upload(SquareBuild b)
        {
            var go = new GameObject($"square_{b.rx}_{b.ry}");
            go.transform.SetParent(transform, false);
            AddMesh(go, "terrain", b.terrainPos, b.terrainCol, true);
            AddMesh(go, "objects", b.solidPos, b.solidCol, true);
            AddMesh(go, "decor", b.decorPos, b.decorCol, false);
            loaded[(b.rx, b.ry)] = go;
        }

        private void AddMesh(GameObject parent, string name, float[] pos, Color32[] col, bool collide)
        {
            int nv = col.Length;
            if (nv == 0) return;
            var verts = new Vector3[nv];
            var normals = new Vector3[nv];
            for (int i = 0; i < nv; i++)
                verts[i] = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            for (int i = 0; i < nv; i += 3)
            {
                var n = Vector3.Cross(verts[i + 1] - verts[i], verts[i + 2] - verts[i]).normalized;
                normals[i] = n; normals[i + 1] = n; normals[i + 2] = n;
            }
            var indices = new int[nv];
            for (int i = 0; i < nv; i++) indices[i] = i;
            var mesh = new Mesh { name = $"{parent.name}_{name}", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(col);
            mesh.SetTriangles(indices, 0, false);
            mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            if (collide) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void SpawnPlayer()
        {
            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                cam.gameObject.SetActive(false);
            var start = new Vector3(spawnTile.x, 60f, spawnTile.y);
            start.y = Physics.Raycast(start, Vector3.down, out var hit, 120f) ? hit.point.y + 0.1f : 2f;
            var go = new GameObject("Player");
            go.transform.position = start;
            go.AddComponent<FirstPersonController>();
            go.AddComponent<OsrsServerLink>();
            player = go.transform;
        }

        private void OnGUI()
        {
            GUI.Label(new Rect(10, 70, 600, 40), status);
        }
    }
}
