using System;
using System.Collections.Generic;
using System.IO;
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
    ///
    /// Static scenery is baked into one mesh per square. Objects with right-click options (doors,
    /// trees, ladders...) get their own GameObject with an <see cref="OsrsLocRef"/> so the
    /// crosshair can target them exactly and the server can swap them (open door, tree stump).
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
        private readonly Dictionary<(int, int), LoadedSquare> loaded = new Dictionary<(int, int), LoadedSquare>();
        private readonly HashSet<(int, int)> pending = new HashSet<(int, int)>();
        private readonly Queue<SquareBuild> uploads = new Queue<SquareBuild>();
        private readonly Dictionary<int, Mesh> variantMeshes = new Dictionary<int, Mesh>();
        private readonly Dictionary<(int x, int z, int level, int layer), LocChange> locChanges = new Dictionary<(int, int, int, int), LocChange>();
        private Task<SquareBuild> building;
        private (int, int) buildingKey;
        private Task loadTask;
        private string status = "Loading world...";

        public struct Placement
        {
            public int locId;
            public int x, z, level;     // south-west tile of the footprint (world tiles)
            public int sizeX, sizeZ;    // footprint after rotation
            public int modelType;
            public bool decor;
            public bool interactive;    // has its own GameObject instead of being baked
            public int faceStart, faceCount;
        }

        private struct DynamicSpec
        {
            public int vid, locId, level, layer, sizeX, sizeZ;
            public int x, z;
            public float yaw;
            public Vector3 position;
            public bool collide;
        }

        private struct LocChange
        {
            public bool add;
            public int id, shape, rotation;
        }

        private class SquareBuild
        {
            public int rx, ry;
            public float[] terrainPos; public Color32[] terrainCol;
            public float[] solidPos; public Color32[] solidCol;
            public float[] decorPos; public Color32[] decorCol;
            public List<Placement> placements;
            public List<DynamicSpec> dynamics;
        }

        private class LoadedSquare
        {
            public GameObject root;
            public Mesh solidMesh, decorMesh;
            public MeshCollider solidCollider, terrainCollider;
            public List<Placement> placements;
            public Dictionary<(int, int, int, int), List<GameObject>> dynamics = new Dictionary<(int, int, int, int), List<GameObject>>();
        }

        public WorldData Data => world;

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
                var sq = loaded[key];
                foreach (var mf in sq.root.GetComponentsInChildren<MeshFilter>())
                    if (mf.sharedMesh != null && !mf.sharedMesh.name.StartsWith("variant_"))
                        Destroy(mf.sharedMesh);
                Destroy(sq.root);
                loaded.Remove(key);
            }
        }

        private static int LayerOf(int shape)
        {
            if (shape <= 3) return 0;
            if (shape <= 8) return 1;
            if (shape == 22) return 3;
            return 2;
        }

        private SquareBuild BuildSquare(int rx, int ry)
        {
            var d = world.ReadSquare(rx, ry);
            if (d == null) return null;
            if (d[0] != 'S' || d[1] != 'Q' || d[2] != '1') throw new InvalidDataException("bad square blob");
            int p = 11;
            int nv = BitConverter.ToInt32(d, p); p += 4;
            var build = new SquareBuild { rx = rx, ry = ry, terrainPos = new float[nv * 3], terrainCol = new Color32[nv], dynamics = new List<DynamicSpec>() };
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
            build.placements = new List<Placement>(np);
            int start = p;
            int solidFaces = 0, decorFaces = 0;
            for (int i = 0; i < np; i++)
            {
                int vid = BitConverter.ToInt32(d, p);
                bool decor = d[p + 15] != 0;
                var variant = world.variants[vid];
                bool interactive = world.locs.TryGetValue(variant.locId, out var idef) && idef.HasOps;
                if (!interactive)
                {
                    int faces = world.GetVariantMesh(vid).faceCount;
                    if (decor) decorFaces += faces; else solidFaces += faces;
                }
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
                int lx = d[p + 4], lz = d[p + 5], level = d[p + 6];
                p += 16;
                var variant = world.variants[vid];
                int fx = 1, fz = 1;
                world.locs.TryGetValue(variant.locId, out var def);
                if (def != null && (variant.modelType == 10 || variant.modelType == 11))
                {
                    int rot = (yaw / 512) & 3;
                    fx = (rot & 1) == 1 ? def.sizeY : def.sizeX;
                    fz = (rot & 1) == 1 ? def.sizeX : def.sizeY;
                }
                bool interactive = def != null && def.HasOps;
                var vm = world.GetVariantMesh(vid);
                var placement = new Placement
                {
                    locId = variant.locId, x = rx * 64 + lx, z = ry * 64 + lz, level = level,
                    sizeX = fx, sizeZ = fz, modelType = variant.modelType, decor = decor, interactive = interactive,
                    faceStart = decor ? de / 3 : so / 3, faceCount = vm.faceCount,
                };
                build.placements.Add(placement);
                if (interactive)
                {
                    build.dynamics.Add(new DynamicSpec
                    {
                        vid = vid, locId = variant.locId, level = level, layer = LayerOf(variant.modelType),
                        sizeX = fx, sizeZ = fz, x = placement.x, z = placement.z,
                        yaw = yaw, position = new Vector3(px, py, pz), collide = !decor,
                    });
                    continue;
                }
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
            var sq = new LoadedSquare { root = go, placements = b.placements };
            AddMesh(go, "terrain", b.terrainPos, b.terrainCol, true, out _, out sq.terrainCollider);
            AddMesh(go, "objects", b.solidPos, b.solidCol, true, out sq.solidMesh, out sq.solidCollider);
            AddMesh(go, "decor", b.decorPos, b.decorCol, false, out sq.decorMesh, out _);
            foreach (var spec in b.dynamics)
                AddDynamic(sq, spec);
            loaded[(b.rx, b.ry)] = sq;
            foreach (var kv in locChanges)
            {
                if ((kv.Key.x >> 6) != b.rx || (kv.Key.z >> 6) != b.ry) continue;
                ApplyChange(sq, kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// The most specific interactable object covering a world tile, or null. Prefers objects
        /// with right-click options, then solid over decorative, then the smallest footprint.
        /// </summary>
        public Placement? FindPlacement(int tx, int tz, int level)
        {
            if (!loaded.TryGetValue((tx >> 6, tz >> 6), out var sq)) return null;
            Placement? best = null;
            int bestScore = int.MinValue;
            foreach (var pl in sq.placements)
            {
                if (pl.level != level) continue;
                if (tx < pl.x || tx >= pl.x + pl.sizeX || tz < pl.z || tz >= pl.z + pl.sizeZ) continue;
                if (!world.locs.TryGetValue(pl.locId, out var def) || def.name == "null") continue;
                int score = (def.HasOps ? 1000 : 0) + (pl.decor ? 0 : 100) - pl.sizeX * pl.sizeZ;
                if (score > bestScore) { bestScore = score; best = pl; }
            }
            return best;
        }

        private void AddMesh(GameObject parent, string name, float[] pos, Color32[] col, bool collide, out Mesh meshOut, out MeshCollider colliderOut)
        {
            meshOut = null; colliderOut = null;
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
            if (collide) colliderOut = go.AddComponent<MeshCollider>();
            if (collide) colliderOut.sharedMesh = mesh;
            meshOut = mesh;
        }

        private Mesh VariantMesh(int vid)
        {
            if (variantMeshes.TryGetValue(vid, out var cached)) return cached;
            var vm = world.GetVariantMesh(vid);
            Mesh mesh = null;
            if (vm.faceCount > 0)
            {
                int nv = vm.faceCount * 3;
                var verts = new Vector3[nv];
                var normals = new Vector3[nv];
                for (int i = 0; i < nv; i++)
                    verts[i] = new Vector3(vm.positions[i * 3], vm.positions[i * 3 + 1], vm.positions[i * 3 + 2]) / 128f;
                for (int i = 0; i < nv; i += 3)
                {
                    var n = Vector3.Cross(verts[i + 1] - verts[i], verts[i + 2] - verts[i]).normalized;
                    normals[i] = n; normals[i + 1] = n; normals[i + 2] = n;
                }
                var indices = new int[nv];
                for (int i = 0; i < nv; i++) indices[i] = i;
                mesh = new Mesh { name = $"variant_{vid}", indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(verts);
                mesh.SetNormals(normals);
                mesh.SetColors(vm.colors);
                mesh.SetTriangles(indices, 0, false);
                mesh.RecalculateBounds();
            }
            variantMeshes[vid] = mesh;
            return mesh;
        }

        private void AddDynamic(LoadedSquare sq, DynamicSpec spec)
        {
            var mesh = VariantMesh(spec.vid);
            if (mesh == null) return;
            world.locs.TryGetValue(spec.locId, out var def);
            var go = new GameObject(def != null ? $"loc_{spec.locId}_{def.name}" : $"loc_{spec.locId}");
            go.transform.SetParent(sq.root.transform, false);
            go.transform.position = spec.position;
            go.transform.rotation = Quaternion.Euler(0f, spec.yaw * (360f / 2048f), 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            if (spec.collide) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            var reference = go.AddComponent<OsrsLocRef>();
            reference.locId = spec.locId;
            reference.x = spec.x; reference.z = spec.z; reference.level = spec.level;
            reference.sizeX = spec.sizeX; reference.sizeZ = spec.sizeZ;
            reference.layer = spec.layer;
            reference.def = def;
            var key = (spec.x, spec.z, spec.level, spec.layer);
            if (!sq.dynamics.TryGetValue(key, out var list)) sq.dynamics[key] = list = new List<GameObject>();
            list.Add(go);
        }

        // ------------------------------------------------------------------ server loc changes

        /// <summary>A loc was added/changed on the server (e.g. a door opened). Shape and rotation follow the cache.</summary>
        public void LocAdd(int id, int x, int z, int level, int shape, int rotation)
        {
            var key = (x, z, level, LayerOf(shape));
            var change = new LocChange { add = true, id = id, shape = shape, rotation = rotation };
            locChanges[key] = change;
            if (loaded.TryGetValue((x >> 6, z >> 6), out var sq)) ApplyChange(sq, key, change);
        }

        public void LocDel(int x, int z, int level, int shape, int rotation)
        {
            var key = (x, z, level, LayerOf(shape));
            var change = new LocChange { add = false, shape = shape, rotation = rotation };
            locChanges[key] = change;
            if (loaded.TryGetValue((x >> 6, z >> 6), out var sq)) ApplyChange(sq, key, change);
        }

        private void ApplyChange(LoadedSquare sq, (int x, int z, int level, int layer) key, LocChange change)
        {
            RemoveAt(sq, key);
            if (!change.add) return;
            if (!world.locs.TryGetValue(change.id, out var raw)) return;
            if (!world.locs.TryGetValue(raw.resolvedId, out var def)) def = raw;
            foreach (var spec in SpecsFor(def, key.x, key.z, key.level, change.shape, change.rotation))
                AddDynamic(sq, spec);
        }

        private void RemoveAt(LoadedSquare sq, (int x, int z, int level, int layer) key)
        {
            if (sq.dynamics.TryGetValue(key, out var list))
            {
                foreach (var go in list) Destroy(go);
                sq.dynamics.Remove(key);
            }
            bool solidChanged = false, decorChanged = false;
            for (int i = 0; i < sq.placements.Count; i++)
            {
                var pl = sq.placements[i];
                if (pl.interactive || pl.faceCount == 0 || pl.x != key.x || pl.z != key.z || pl.level != key.level || LayerOf(pl.modelType) != key.layer) continue;
                var mesh = pl.decor ? sq.decorMesh : sq.solidMesh;
                if (mesh == null) continue;
                var verts = mesh.vertices;
                int first = pl.faceStart * 3;
                var sink = verts[first];
                for (int k = 0; k < pl.faceCount * 3; k++) verts[first + k] = sink;
                mesh.SetVertices(verts);
                if (pl.decor) decorChanged = true; else solidChanged = true;
                pl.faceCount = 0;
                sq.placements[i] = pl;
            }
            if (solidChanged && sq.solidCollider != null)
            {
                sq.solidCollider.sharedMesh = null;
                sq.solidCollider.sharedMesh = sq.solidMesh;
            }
            _ = decorChanged;
        }

        /// <summary>Mirrors the exporter's placement rules for a loc the server added at runtime.</summary>
        private IEnumerable<DynamicSpec> SpecsFor(WorldData.LocDef def, int x, int z, int level, int shape, int orient)
        {
            int sx = def.sizeX, sz = def.sizeY;
            if ((orient & 1) == 1) { sx = def.sizeY; sz = def.sizeX; }
            if (shape != 10 && shape != 11) { sx = 1; sz = 1; }
            float cx = x + sx * 0.5f, cz = z + sz * 0.5f;
            float h = GroundHeight(x >> 6, z >> 6, cx, cz);
            bool collide = !(shape == 22 || (shape >= 4 && shape <= 8) || def.solid == 0);
            var jobs = new List<(int mtype, int mrot, float dx, float dz, int extra)>();
            if (shape == 22 || (shape >= 12 && shape <= 21) || shape == 0 || shape == 1 || shape == 3 || shape == 9)
                jobs.Add((shape, orient, 0, 0, 0));
            else if (shape == 10 || shape == 11)
                jobs.Add((10, orient, 0, 0, shape == 11 ? 256 : 0));
            else if (shape == 2)
            {
                jobs.Add((2, orient + 4, 0, 0, 0));
                jobs.Add((2, (orient + 1) & 3, 0, 0, 0));
            }
            else if (shape == 4)
                jobs.Add((4, orient, 0, 0, 0));
            else if (shape == 5)
                jobs.Add((4, orient, 16 * ROT_X[orient], 16 * ROT_Z[orient], 0));
            else if (shape == 6)
                jobs.Add((4, orient + 4, 8 * DIAG_X[orient], 8 * DIAG_Z[orient], 0));
            else if (shape == 7)
                jobs.Add((4, ((orient + 2) & 3) + 4, 0, 0, 0));
            else if (shape == 8)
            {
                jobs.Add((4, orient + 4, 8 * DIAG_X[orient], 8 * DIAG_Z[orient], 0));
                jobs.Add((4, ((orient + 2) & 3) + 4, 0, 0, 0));
            }
            foreach (var (mtype, mrot, dx, dz, extra) in jobs)
            {
                bool mirror = def.rotated ^ (mrot > 3);
                if (!world.variantIndex.TryGetValue((def.id, mtype, mirror), out int vid) &&
                    !world.variantIndex.TryGetValue((def.id, mtype, !mirror), out vid))
                {
                    Debug.LogWarning($"OsrsPort: no model for loc {def.id} ({def.name}) shape {shape}");
                    continue;
                }
                int r = mrot & 3;
                int yaw = (r * 512 + extra) & 2047;
                float ox = 0, oz = 0;
                if (mtype == 4 && mrot > 3)
                {
                    yaw = (yaw + 256) & 2047;
                    ox = 45; oz = -45;
                    for (int i = 0; i < r; i++) { float t = ox; ox = oz; oz = -t; }
                }
                ox += dx + def.offX;
                oz += dz + def.offZ;
                yield return new DynamicSpec
                {
                    vid = vid, locId = def.id, level = level, layer = LayerOf(shape), sizeX = sx, sizeZ = sz,
                    x = x, z = z, yaw = yaw, collide = collide,
                    position = new Vector3(cx + ox / 128f, h - def.offY / 128f, cz + oz / 128f),
                };
            }
        }

        private float GroundHeight(int rx, int ry, float x, float z)
        {
            if (loaded.TryGetValue((rx, ry), out var sq) && sq.terrainCollider != null &&
                sq.terrainCollider.Raycast(new Ray(new Vector3(x, 100f, z), Vector3.down), out var hit, 200f))
                return hit.point.y;
            return 0f;
        }

        /// <summary>Height of the ground mesh at a point (ignores roofs and scenery), or null if that square isn't loaded.</summary>
        public float? TerrainHeight(float x, float z)
        {
            int rx = Mathf.FloorToInt(x) >> 6, ry = Mathf.FloorToInt(z) >> 6;
            if (!loaded.TryGetValue((rx, ry), out var sq) || sq.terrainCollider == null) return null;
            return sq.terrainCollider.Raycast(new Ray(new Vector3(x, 100f, z), Vector3.down), out var hit, 200f) ? hit.point.y : 0f;
        }

        private static readonly int[] ROT_X = { 1, 0, -1, 0 };
        private static readonly int[] ROT_Z = { 0, -1, 0, 1 };
        private static readonly int[] DIAG_X = { 1, -1, -1, 1 };
        private static readonly int[] DIAG_Z = { -1, -1, 1, 1 };

        private void SpawnPlayer()
        {
            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                cam.gameObject.SetActive(false);
            var start = new Vector3(spawnTile.x, 60f, spawnTile.y);
            start.y = Physics.Raycast(start, Vector3.down, out var hit, 120f) ? hit.point.y + 0.1f : 2f;
            var go = new GameObject("Player") { layer = OsrsNpcManager.PlayerLayer };
            go.transform.position = start;
            go.AddComponent<FirstPersonController>();
            go.AddComponent<OsrsServerLink>();
            go.AddComponent<OsrsInteraction>();
            var npcs = new GameObject("Npcs");
            npcs.transform.SetParent(transform, false);
            npcs.AddComponent<OsrsNpcManager>().material = material;
            player = go.transform;
        }

        private void OnGUI()
        {
            var link = OsrsServerLink.Instance;
            string text = status;
            if (link != null)
            {
                text = link.Status + "\n" + text;
                if (link.HasServerTile)
                    text += $"\nServer tile: {link.ServerTile.x}, {link.ServerTile.y}, level {link.ServerTile.z}";
            }
            GUI.Label(new Rect(10, 10, 600, 60), text);
        }
    }

    /// <summary>Identifies an interactable scenery object's GameObject so the crosshair can target it.</summary>
    public class OsrsLocRef : MonoBehaviour
    {
        public int locId, x, z, level, sizeX, sizeZ, layer;
        public WorldData.LocDef def;
    }
}
