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
        /// <summary>Hide planes above the player's level while something (floor or roof) is overhead, like OSRS roof removal.</summary>
        public bool hideRoofs = true;
        public const int Planes = 4;
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
        private readonly Dictionary<(int, int), Mesh> variantMeshes = new Dictionary<(int, int), Mesh>();
        private readonly Dictionary<(int x, int z, int level, int layer), LocChange> locChanges = new Dictionary<(int, int, int, int), LocChange>();
        private readonly Dictionary<(int x, int z, int level), PatchState> patchStates = new Dictionary<(int, int, int), PatchState>();
        private readonly List<HouseRoom> houseRooms = new List<HouseRoom>();
        private readonly Dictionary<(int, int), SquareBuild> templateSquares = new Dictionary<(int, int), SquareBuild>();
        private LoadedSquare house;
        private Task<SquareBuild> building;
        private (int, int) buildingKey;
        private Task loadTask;
        private bool texturesBound;
        private Texture2DArray texArray;
        private Texture2D scrollTex;
        private string status = "Loading world...";
        private int viewLevel;
        private bool roofed;
        private float roofClearTime; // hysteresis: roofs come back only after being clear this long
        private const float RoofClearDelay = 0.3f;

        /// <summary>The plane the world is currently shown/collided for (the server's level for the player).</summary>
        public int ViewLevel => viewLevel;

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
            /// <summary>Id the OsrsLocRef reports for ops when it differs from the model's loc (farming patch base multiloc); 0 = locId.</summary>
            public int refId;
        }

        private struct PatchState
        {
            public int variant, baseId, shape, rotation;
        }

        private struct HouseRoom
        {
            public int dstX, dstZ, srcX, srcZ, rot, level;
        }

        private struct LocChange
        {
            public bool add;
            public int id, shape, rotation;
        }

        private class SquareBuild
        {
            public int rx, ry;
            // One entry per plane (0-3).
            public float[][] terrainPos; public Color32[][] terrainCol; public float[][] terrainUv;
            public float[][] solidPos; public Color32[][] solidCol; public float[][] solidUv;
            public float[][] decorPos; public Color32[][] decorCol; public float[][] decorUv;
            public float[][] heights;  // 65x65 corner heights per plane, NaN where the plane has no ground
            public byte[][] tileFlags; // 64x64 per plane: 1 = ground tile, 2 = anything (ground or object)
            public List<Placement> placements;
            public List<DynamicSpec> dynamics;
        }

        private class LoadedSquare
        {
            public GameObject root;
            public GameObject[] planes = new GameObject[Planes];
            public Mesh[] solidMesh = new Mesh[Planes], decorMesh = new Mesh[Planes];
            public MeshCollider[] solidCollider = new MeshCollider[Planes];
            public float[][] heights;
            public byte[][] tileFlags;
            public List<Placement> placements;
            public Dictionary<(int, int, int, int), List<GameObject>> dynamics = new Dictionary<(int, int, int, int), List<GameObject>>();
        }

        public WorldData Data => world;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!OsrsTitleScreen.Released) return;
            if (FindAnyObjectByType<OsrsWorldLoader>() != null) return;
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
            if (!texturesBound) { BindTextures(); texturesBound = true; }

            UpdatePlaneState();
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

        /// <summary>Open doors and gates (first option "Close") swing into the doorway; they must not block walking.</summary>
        private static bool IsOpenPassage(WorldData.LocDef def)
        {
            if (def == null) return false;
            foreach (var op in def.ops)
                if (op == "Close") return true;
            return false;
        }

        private static float TerrainLight(float[] hgt, int cx, int cz, float y)
        {
            float H(int x, int z)
            {
                if (x < 0 || x > 64 || z < 0 || z > 64) return y;
                float v = hgt[x * 65 + z];
                return float.IsNaN(v) ? y : v;
            }
            float dx = H(cx + 1, cz) - H(cx - 1, cz), dz = H(cx, cz + 1) - H(cx, cz - 1);
            var n = new Vector3(-dx, 2f, -dz).normalized;
            float light = OsrsLight.Terrain(n.x, n.y, n.z);
            return Mathf.Pow(Mathf.Max(light, 8f) / 128f, 0.7f);
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
            bool sq3 = d[2] == '3';
            if (d[0] != 'S' || d[1] != 'Q' || (!sq3 && d[2] != '2'))
                throw new InvalidDataException("bad square blob (expected SQ2 or SQ3 per-plane terrain; re-run export_world.py)");
            int p = 11;
            var build = new SquareBuild
            {
                rx = rx, ry = ry, dynamics = new List<DynamicSpec>(),
                terrainPos = new float[Planes][], terrainCol = new Color32[Planes][], terrainUv = new float[Planes][],
                solidPos = new float[Planes][], solidCol = new Color32[Planes][], solidUv = new float[Planes][],
                decorPos = new float[Planes][], decorCol = new Color32[Planes][], decorUv = new float[Planes][],
                heights = new float[Planes][], tileFlags = new byte[Planes][],
            };
            float ox = rx * 64f, oz = ry * 64f;
            for (int lv = 0; lv < Planes; lv++)
            {
                int nv = BitConverter.ToInt32(d, p); p += 4;
                var tpos = new float[nv * 3];
                var tcol = new Color32[nv];
                var hgt = new float[65 * 65];
                for (int i = 0; i < hgt.Length; i++) hgt[i] = float.NaN;
                var flags = new byte[64 * 64];
                for (int i = 0; i < nv; i++)
                {
                    float lx = BitConverter.ToInt16(d, p) / 128f, y = BitConverter.ToInt16(d, p + 2) / 128f, lz = BitConverter.ToInt16(d, p + 4) / 128f;
                    tpos[i * 3] = ox + lx; tpos[i * 3 + 1] = y; tpos[i * 3 + 2] = oz + lz;
                    int cx = Mathf.RoundToInt(lx), cz = Mathf.RoundToInt(lz);
                    if (cx >= 0 && cx <= 64 && cz >= 0 && cz <= 64) hgt[cx * 65 + cz] = y;
                    p += 6;
                }
                var texOf = sq3 ? new byte[nv] : null;
                int stride = sq3 ? 4 : 3;
                for (int i = 0; i < nv; i++)
                {
                    tcol[i] = new Color32(d[p], d[p + 1], d[p + 2], 255);
                    if (sq3) texOf[i] = d[p + 3];
                    p += stride;
                }
                for (int i = 0; i < nv; i++)
                {
                    int cx = Mathf.RoundToInt(tpos[i * 3] - ox), cz = Mathf.RoundToInt(tpos[i * 3 + 2] - oz);
                    float f = TerrainLight(hgt, cx, cz, tpos[i * 3 + 1]);
                    if (texOf != null && texOf[i] != 0)
                    {
                        byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);
                        tcol[i] = new Color32(g, g, g, 255);
                    }
                    else
                    {
                        var tc = tcol[i];
                        tcol[i] = new Color32((byte)Mathf.Min(255, tc.r * f), (byte)Mathf.Min(255, tc.g * f), (byte)Mathf.Min(255, tc.b * f), 255);
                    }
                }
                var tuv = new float[nv * 3];
                for (int t = 0; t + 2 < nv; t += 3)
                {
                    float mx = (tpos[t * 3] + tpos[t * 3 + 3] + tpos[t * 3 + 6]) / 3f - ox;
                    float mz = (tpos[t * 3 + 2] + tpos[t * 3 + 5] + tpos[t * 3 + 8]) / 3f - oz;
                    int tileX = Mathf.FloorToInt(mx), tileZ = Mathf.FloorToInt(mz);
                    int tx = Mathf.Clamp(tileX, 0, 63), tz = Mathf.Clamp(tileZ, 0, 63);
                    flags[tx * 64 + tz] |= 3;
                    for (int k = 0; k < 3; k++)
                    {
                        int i = t + k;
                        tuv[i * 3] = (tpos[i * 3] - ox) - tileX;
                        tuv[i * 3 + 1] = (tpos[i * 3 + 2] - oz) - tileZ;
                        tuv[i * 3 + 2] = texOf != null ? texOf[i] : 0;
                    }
                }
                build.terrainPos[lv] = tpos; build.terrainCol[lv] = tcol; build.terrainUv[lv] = tuv;
                build.heights[lv] = hgt; build.tileFlags[lv] = flags;
            }

            int np = BitConverter.ToInt32(d, p); p += 4;
            build.placements = new List<Placement>(np);
            int start = p;
            var solidFaces = new int[Planes];
            var decorFaces = new int[Planes];
            for (int i = 0; i < np; i++)
            {
                int vid = BitConverter.ToInt32(d, p);
                int plv = Mathf.Clamp(d[p + 6], 0, Planes - 1);
                bool decor = d[p + 15] != 0;
                var variant = world.variants[vid];
                bool interactive = world.locs.TryGetValue(variant.locId, out var idef) && idef.HasOps;
                if (!interactive)
                {
                    int faces = world.GetVariantMesh(vid).faceCount;
                    if (decor) decorFaces[plv] += faces; else solidFaces[plv] += faces;
                }
                p += 16;
            }
            for (int lv = 0; lv < Planes; lv++)
            {
                build.solidPos[lv] = new float[solidFaces[lv] * 9]; build.solidCol[lv] = new Color32[solidFaces[lv] * 3];
                build.solidUv[lv] = new float[solidFaces[lv] * 9];
                build.decorPos[lv] = new float[decorFaces[lv] * 9]; build.decorCol[lv] = new Color32[decorFaces[lv] * 3];
                build.decorUv[lv] = new float[decorFaces[lv] * 9];
            }
            var so = new int[Planes];
            var de = new int[Planes];
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
                int lvi = Mathf.Clamp(level, 0, Planes - 1);
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
                    faceStart = decor ? de[lvi] / 3 : so[lvi] / 3, faceCount = vm.faceCount,
                };
                build.placements.Add(placement);
                var occupied = build.tileFlags[lvi];
                for (int tx = lx; tx < lx + fx && tx < 64; tx++)
                    for (int tz = lz; tz < lz + fz && tz < 64; tz++)
                        occupied[tx * 64 + tz] |= 2;
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
                float[] dst = decor ? build.decorPos[lvi] : build.solidPos[lvi];
                Color32[] dcol = decor ? build.decorCol[lvi] : build.solidCol[lvi];
                int o = decor ? de[lvi] : so[lvi];
                int n = vm.faceCount * 3;
                for (int k = 0; k < n; k++)
                {
                    float x = vm.positions[k * 3], y = vm.positions[k * 3 + 1], z = vm.positions[k * 3 + 2];
                    dst[(o + k) * 3] = px + (c * x + s * z) / 128f;
                    dst[(o + k) * 3 + 1] = py + y / 128f;
                    dst[(o + k) * 3 + 2] = pz + (-s * x + c * z) / 128f;
                }
                OsrsLight.ShadeLoc(vm, ang, dcol, o);
                if (vm.uv != null)
                {
                    float[] duv = decor ? build.decorUv[lvi] : build.solidUv[lvi];
                    Array.Copy(vm.uv, 0, duv, o * 3, n * 3);
                }
                if (decor) de[lvi] += n; else so[lvi] += n;
            }
            return build;
        }

        private void Upload(SquareBuild b)
        {
            var go = new GameObject($"square_{b.rx}_{b.ry}");
            go.transform.SetParent(transform, false);
            var sq = new LoadedSquare { root = go, placements = b.placements, heights = b.heights, tileFlags = b.tileFlags };
            for (int lv = 0; lv < Planes; lv++)
            {
                var plane = new GameObject($"plane_{lv}");
                plane.transform.SetParent(go.transform, false);
                sq.planes[lv] = plane;
                AddMesh(plane, "terrain", b.terrainPos[lv], b.terrainCol[lv], b.terrainUv[lv], true, out _, out _);
                AddMesh(plane, "objects", b.solidPos[lv], b.solidCol[lv], b.solidUv[lv], true, out sq.solidMesh[lv], out sq.solidCollider[lv]);
                AddMesh(plane, "decor", b.decorPos[lv], b.decorCol[lv], b.decorUv[lv], false, out sq.decorMesh[lv], out _);
            }
            foreach (var spec in b.dynamics)
                AddDynamic(sq, spec);
            loaded[(b.rx, b.ry)] = sq;
            ApplyPlaneState(sq);
            foreach (var kv in locChanges)
            {
                if ((kv.Key.x >> 6) != b.rx || (kv.Key.z >> 6) != b.ry) continue;
                ApplyChange(sq, kv.Key, kv.Value);
            }
            foreach (var kv in patchStates)
            {
                if ((kv.Key.x >> 6) != b.rx || (kv.Key.z >> 6) != b.ry) continue;
                ApplyPatch(sq, kv.Key, kv.Value);
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

        // ------------------------------------------------------------------ per-plane visibility / collision

        /// <summary>
        /// Follows the server level: planes at or below it collide; planes above never collide and are
        /// hidden while anything on them is directly over the player (indoors / under a roof).
        /// </summary>
        private void UpdatePlaneState()
        {
            var link = OsrsServerLink.Instance;
            int level = Mathf.Clamp(link != null ? link.CurrentLevel : 0, 0, Planes - 1);
            bool r = false;
            if (hideRoofs && player != null)
            {
                // Check the player's tile and its 8 neighbours so doorways/wall edges don't flicker.
                int tx = Mathf.FloorToInt(player.position.x), tz = Mathf.FloorToInt(player.position.z);
                for (int dx = -1; dx <= 1 && !r; dx++)
                    for (int dz = -1; dz <= 1 && !r; dz++)
                    {
                        int x = tx + dx, z = tz + dz;
                        if (!loaded.TryGetValue((x >> 6, z >> 6), out var sq)) continue;
                        for (int lv = level + 1; lv < Planes && !r; lv++)
                            r = (sq.tileFlags[lv][(x & 63) * 64 + (z & 63)] & 2) != 0;
                    }
                // Hysteresis: hide immediately, but only un-hide after staying uncovered briefly.
                if (r) roofClearTime = 0f;
                else if (roofed && level == viewLevel && (roofClearTime += Time.deltaTime) < RoofClearDelay) r = true;
            }
            if (level == viewLevel && r == roofed) return;
            viewLevel = level;
            roofed = r;
            foreach (var sq in loaded.Values) ApplyPlaneState(sq);
        }

        private void ApplyPlaneState(LoadedSquare sq)
        {
            for (int lv = 0; lv < Planes; lv++)
            {
                var plane = sq.planes[lv];
                if (plane == null) continue;
                plane.SetActive(lv <= viewLevel || !roofed);
                bool collide = lv <= viewLevel;
                foreach (var c in plane.GetComponentsInChildren<Collider>(true))
                    c.enabled = collide;
            }
        }

        private void BindTextures()
        {
            int n = world.textureCount;
            int size = world.textureSize;
            if (n <= 0 || size <= 0 || world.texturePixels == null || world.textureScroll == null) return;
            texArray = new Texture2DArray(size, size, n, TextureFormat.RGBA32, false, false);
            int stride = size * size * 4;
            for (int i = 0; i < n; i++)
                texArray.SetPixelData(world.texturePixels, 0, i, i * stride);
            texArray.wrapMode = TextureWrapMode.Repeat;
            texArray.filterMode = FilterMode.Point;
            texArray.Apply(false, true);
            var scroll = new float[256 * 2];
            Array.Copy(world.textureScroll, scroll, Math.Min(world.textureScroll.Length, scroll.Length));
            scrollTex = new Texture2D(256, 1, TextureFormat.RGFloat, false, true);
            scrollTex.wrapMode = TextureWrapMode.Clamp;
            scrollTex.filterMode = FilterMode.Point;
            scrollTex.SetPixelData(scroll, 0);
            scrollTex.Apply(false, true);
            material.SetTexture("_TexArr", texArray);
            material.SetTexture("_ScrollTex", scrollTex);
            material.SetFloat("_HasTex", 1f);
            Debug.Log($"OsrsPort: {n} textures, {size}px");
        }

        private void AddMesh(GameObject parent, string name, float[] pos, Color32[] col, float[] uv, bool collide, out Mesh meshOut, out MeshCollider colliderOut)
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
            if (uv != null && uv.Length == nv * 3)
            {
                var uvs = new Vector3[nv];
                for (int i = 0; i < nv; i++) uvs[i] = new Vector3(uv[i * 3], uv[i * 3 + 1], uv[i * 3 + 2]);
                mesh.SetUVs(0, uvs);
            }
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

        private Mesh VariantMesh(int vid, int yaw)
        {
            if (variantMeshes.TryGetValue((vid, yaw), out var cached)) return cached;
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
                var lit = new Color32[nv];
                OsrsLight.ShadeLoc(vm, yaw * (2f * Mathf.PI / 2048f), lit, 0);
                mesh.SetColors(lit);
                if (vm.uv != null)
                {
                    var uvs = new Vector3[nv];
                    for (int i = 0; i < nv; i++) uvs[i] = new Vector3(vm.uv[i * 3], vm.uv[i * 3 + 1], vm.uv[i * 3 + 2]);
                    mesh.SetUVs(0, uvs);
                }
                mesh.SetTriangles(indices, 0, false);
                mesh.RecalculateBounds();
            }
            variantMeshes[(vid, yaw)] = mesh;
            return mesh;
        }

        private void AddDynamic(LoadedSquare sq, DynamicSpec spec)
        {
            var mesh = VariantMesh(spec.vid, Mathf.RoundToInt(spec.yaw));
            if (mesh == null) return;
            world.locs.TryGetValue(spec.locId, out var def);
            var go = new GameObject(def != null ? $"loc_{spec.locId}_{def.name}" : $"loc_{spec.locId}");
            int plane = Mathf.Clamp(spec.level, 0, Planes - 1);
            go.transform.SetParent(sq.planes[plane] != null ? sq.planes[plane].transform : sq.root.transform, false);
            go.transform.position = spec.position;
            go.transform.rotation = Quaternion.Euler(0f, spec.yaw * (360f / 2048f), 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            if (spec.collide && !IsOpenPassage(def))
            {
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mesh;
                col.enabled = plane <= viewLevel;
            }
            var reference = go.AddComponent<OsrsLocRef>();
            reference.locId = spec.refId > 0 ? spec.refId : spec.locId;
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
            var solidChanged = new bool[Planes];
            for (int i = 0; i < sq.placements.Count; i++)
            {
                var pl = sq.placements[i];
                if (pl.interactive || pl.faceCount == 0 || pl.x != key.x || pl.z != key.z || pl.level != key.level || LayerOf(pl.modelType) != key.layer) continue;
                int lv = Mathf.Clamp(pl.level, 0, Planes - 1);
                var mesh = pl.decor ? sq.decorMesh[lv] : sq.solidMesh[lv];
                if (mesh == null) continue;
                var verts = mesh.vertices;
                int first = pl.faceStart * 3;
                var sink = verts[first];
                for (int k = 0; k < pl.faceCount * 3; k++) verts[first + k] = sink;
                mesh.SetVertices(verts);
                if (!pl.decor) solidChanged[lv] = true;
                pl.faceCount = 0;
                sq.placements[i] = pl;
            }
            for (int lv = 0; lv < Planes; lv++)
                if (solidChanged[lv] && sq.solidCollider[lv] != null)
                {
                    sq.solidCollider[lv].sharedMesh = null;
                    sq.solidCollider[lv].sharedMesh = sq.solidMesh[lv];
                }
        }

        /// <summary>Mirrors the exporter's placement rules for a loc the server added at runtime.</summary>
        private IEnumerable<DynamicSpec> SpecsFor(WorldData.LocDef def, int x, int z, int level, int shape, int orient)
        {
            int sx = def.sizeX, sz = def.sizeY;
            if ((orient & 1) == 1) { sx = def.sizeY; sz = def.sizeX; }
            if (shape != 10 && shape != 11) { sx = 1; sz = 1; }
            float cx = x + sx * 0.5f, cz = z + sz * 0.5f;
            float h = TerrainHeight(cx, cz, level) ?? 0f;
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

        /// <summary>Height of the ground at a point on the plane the world is shown for, or null if that square isn't loaded.</summary>
        public float? TerrainHeight(float x, float z) => TerrainHeight(x, z, viewLevel);

        /// <summary>
        /// Height of a plane's ground mesh at a point (ignores roofs and scenery), or null if that square isn't
        /// loaded. Where the plane has no ground (stairwells, open air) the nearest plane below is used.
        /// Read from the exported corner heights, so it works whether or not the plane is currently shown.
        /// </summary>
        public float? TerrainHeight(float x, float z, int level)
        {
            if (house != null && HouseHeight(x, z, out float hh)) return hh;
            int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
            if (!loaded.TryGetValue((ix >> 6, iz >> 6), out var sq) || sq.heights == null) return null;
            int lx = ix & 63, lz = iz & 63;
            float fx = x - ix, fz = z - iz;
            for (int lv = Mathf.Clamp(level, 0, Planes - 1); lv >= 0; lv--)
            {
                if ((sq.tileFlags[lv][lx * 64 + lz] & 1) == 0) continue;
                var h = sq.heights[lv];
                float h00 = h[lx * 65 + lz], h10 = h[(lx + 1) * 65 + lz], h01 = h[lx * 65 + lz + 1], h11 = h[(lx + 1) * 65 + lz + 1];
                if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11)) continue;
                return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
            }
            return 0f;
        }

        // ------------------------------------------------------------------ farming patches

        /// <summary>
        /// `PATCH x z level variantLocId baseLocId value shape rot`: the patch at that tile now shows
        /// the variant's model (crop + stage) with the variant's options, but ops still target the
        /// base multiloc id, which is what the server registers its handlers on.
        /// </summary>
        public void Patch(string rest)
        {
            var p = rest.Split(' ');
            if (p.Length < 8) return;
            var v = new int[8];
            for (int i = 0; i < 8; i++) if (!int.TryParse(p[i], out v[i])) return;
            var key = (v[0], v[1], v[2]);
            var state = new PatchState { variant = v[3], baseId = v[4], shape = v[6], rotation = v[7] };
            patchStates[key] = state;
            if (loaded.TryGetValue((v[0] >> 6, v[1] >> 6), out var sq)) ApplyPatch(sq, key, state);
        }

        private void ApplyPatch(LoadedSquare sq, (int x, int z, int level) key, PatchState state)
        {
            var lkey = (key.x, key.z, key.level, LayerOf(state.shape));
            RemoveAt(sq, lkey);
            if (state.variant < 0) return;
            if (!world.locs.TryGetValue(state.variant, out var raw)) return;
            if (!world.locs.TryGetValue(raw.resolvedId, out var def)) def = raw;
            foreach (var spec in SpecsFor(def, key.x, key.z, key.level, state.shape, state.rotation))
            {
                var s = spec;
                s.refId = state.baseId;
                AddDynamic(sq, s);
            }
        }

        // ------------------------------------------------------------------ player-owned house

        /// <summary>
        /// `HOUSE level;dstX|dstZ|srcX|srcZ|rot|name;...` builds the house floor by copying each room's
        /// 8x8 template chunk (from the exported template map square) to its instance chunk, rotated
        /// clockwise rot quarter-turns. Bare `HOUSE` clears it. Locs arrive separately as `HLOCS`.
        /// </summary>
        public void SetHouse(string rest)
        {
            ClearHouse();
            var entries = rest.Split(';');
            if (entries.Length < 2 || !int.TryParse(entries[0].Trim(), out int level)) return;
            level = Mathf.Clamp(level, 0, Planes - 1);
            for (int i = 1; i < entries.Length; i++)
            {
                var f = entries[i].Split('|');
                if (f.Length < 5) continue;
                if (!int.TryParse(f[0], out int dx) || !int.TryParse(f[1], out int dz) || !int.TryParse(f[2], out int sx)
                    || !int.TryParse(f[3], out int sz) || !int.TryParse(f[4], out int rot)) continue;
                houseRooms.Add(new HouseRoom { dstX = dx, dstZ = dz, srcX = sx, srcZ = sz, rot = rot & 3, level = level });
            }
            if (houseRooms.Count == 0 || world == null || loadTask == null || !loadTask.IsCompleted) return;

            var root = new GameObject("player_owned_house");
            root.transform.SetParent(transform, false);
            house = new LoadedSquare { root = root, placements = new List<Placement>() };
            for (int lv = 0; lv < Planes; lv++)
            {
                var plane = new GameObject($"plane_{lv}");
                plane.transform.SetParent(root.transform, false);
                house.planes[lv] = plane;
            }
            var pos = new List<float>();
            var col = new List<Color32>();
            var slice = new List<float>();
            foreach (var room in houseRooms)
            {
                var src = TemplateSquare(room.srcX >> 6, room.srcZ >> 6);
                if (src == null) { Debug.LogWarning($"OsrsPort: house template square {room.srcX >> 6}_{room.srcZ >> 6} is not exported"); continue; }
                // The engine copies template plane 0 into the house (RegionTemplate level 0).
                var tp = src.terrainPos[0];
                var tc = src.terrainCol[0];
                var tu = src.terrainUv[0];
                int nv = tc.Length;
                for (int t = 0; t + 2 < nv; t += 3)
                {
                    float mx = (tp[t * 3] + tp[t * 3 + 3] + tp[t * 3 + 6]) / 3f - room.srcX;
                    float mz = (tp[t * 3 + 2] + tp[t * 3 + 5] + tp[t * 3 + 8]) / 3f - room.srcZ;
                    if (mx < 0f || mx >= 8f || mz < 0f || mz >= 8f) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = t + k;
                        var (u, w) = RotateChunkPoint(tp[vi * 3] - room.srcX, tp[vi * 3 + 2] - room.srcZ, room.rot);
                        pos.Add(room.dstX + u); pos.Add(tp[vi * 3 + 1]); pos.Add(room.dstZ + w);
                        col.Add(tc[vi]);
                        slice.Add(tu != null ? tu[vi * 3 + 2] : 0f);
                    }
                }
            }
            var huv = new float[col.Count * 3];
            var hp = pos.ToArray();
            for (int t = 0; t + 2 < col.Count; t += 3)
            {
                float ax = (hp[t * 3] + hp[(t + 1) * 3] + hp[(t + 2) * 3]) / 3f;
                float az = (hp[t * 3 + 2] + hp[(t + 1) * 3 + 2] + hp[(t + 2) * 3 + 2]) / 3f;
                int tx = Mathf.FloorToInt(ax), tz = Mathf.FloorToInt(az);
                for (int k = 0; k < 3; k++)
                {
                    int i = t + k;
                    huv[i * 3] = hp[i * 3] - tx;
                    huv[i * 3 + 1] = hp[i * 3 + 2] - tz;
                    huv[i * 3 + 2] = slice[i];
                }
            }
            AddMesh(house.planes[0], "terrain", hp, col.ToArray(), huv, true, out _, out _);
        }

        /// <summary>`HLOCS id|x|z|level|shape|rot;...`: every loc in the house, already placed by the server.</summary>
        public void SetHouseLocs(string rest)
        {
            if (house == null) return;
            foreach (var list in house.dynamics.Values)
                foreach (var go in list) Destroy(go);
            house.dynamics.Clear();
            foreach (var e in rest.Split(';'))
            {
                if (e.Length == 0) continue;
                var f = e.Split('|');
                if (f.Length < 6) continue;
                var v = new int[6];
                bool ok = true;
                for (int i = 0; i < 6; i++) ok &= int.TryParse(f[i], out v[i]);
                if (!ok || !world.locs.TryGetValue(v[0], out var raw)) continue;
                if (!world.locs.TryGetValue(raw.resolvedId, out var def)) def = raw;
                foreach (var spec in SpecsFor(def, v[1], v[2], v[3], v[4], v[5]))
                {
                    var s = spec;
                    s.refId = v[0];
                    AddDynamic(house, s);
                }
            }
            // Only objects with options are targetable, like baked scenery in the streamed world.
            foreach (var list in house.dynamics.Values)
                foreach (var go in list)
                {
                    var r = go.GetComponent<OsrsLocRef>();
                    if (r != null && (r.def == null || !r.def.HasOps)) Destroy(r);
                }
        }

        public bool InHouse => house != null;

        private void ClearHouse()
        {
            houseRooms.Clear();
            if (house == null) return;
            foreach (var mf in house.root.GetComponentsInChildren<MeshFilter>())
                if (mf.sharedMesh != null && !mf.sharedMesh.name.StartsWith("variant_"))
                    Destroy(mf.sharedMesh);
            Destroy(house.root);
            house = null;
        }

        private SquareBuild TemplateSquare(int rx, int ry)
        {
            if (templateSquares.TryGetValue((rx, ry), out var b)) return b;
            b = world.squares.ContainsKey((rx, ry)) ? BuildSquare(rx, ry) : null;
            templateSquares[(rx, ry)] = b;
            return b;
        }

        /// <summary>Point inside an 8x8 chunk rotated clockwise <paramref name="rot"/> quarter-turns (tile x,z -> z,7-x for rot 1).</summary>
        private static (float, float) RotateChunkPoint(float x, float z, int rot)
        {
            switch (rot & 3)
            {
                case 1: return (z, 8f - x);
                case 2: return (8f - x, 8f - z);
                case 3: return (8f - z, x);
                default: return (x, z);
            }
        }

        /// <summary>Ground height inside the house, read from the template square's plane-0 corner heights.</summary>
        private bool HouseHeight(float x, float z, out float h)
        {
            h = 0f;
            foreach (var room in houseRooms)
            {
                float u = x - room.dstX, w = z - room.dstZ;
                if (u < 0f || u >= 8f || w < 0f || w >= 8f) continue;
                // Inverse of RotateChunkPoint.
                float a, b;
                switch (room.rot & 3)
                {
                    case 1: a = 8f - w; b = u; break;
                    case 2: a = 8f - u; b = 8f - w; break;
                    case 3: a = w; b = 8f - u; break;
                    default: a = u; b = w; break;
                }
                var src = TemplateSquare(room.srcX >> 6, room.srcZ >> 6);
                if (src == null) return false;
                float sx = (room.srcX & 63) + a, sz = (room.srcZ & 63) + b;
                int ix = Mathf.Clamp(Mathf.FloorToInt(sx), 0, 63), iz = Mathf.Clamp(Mathf.FloorToInt(sz), 0, 63);
                float fx = sx - ix, fz = sz - iz;
                var hg = src.heights[0];
                float h00 = hg[ix * 65 + iz], h10 = hg[(ix + 1) * 65 + iz], h01 = hg[ix * 65 + iz + 1], h11 = hg[(ix + 1) * 65 + iz + 1];
                if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11)) return false;
                h = Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
                return true;
            }
            return false;
        }

        private static readonly int[] ROT_X = { 1, 0, -1, 0 };
        private static readonly int[] ROT_Z = { 0, -1, 0, 1 };
        private static readonly int[] DIAG_X = { 1, -1, -1, 1 };
        private static readonly int[] DIAG_Z = { -1, -1, 1, 1 };

        private void SpawnPlayer()
        {
            foreach (var cam in FindObjectsByType<Camera>())
                cam.gameObject.SetActive(false);
            var start = new Vector3(spawnTile.x, 60f, spawnTile.y);
            start.y = Physics.Raycast(start, Vector3.down, out var hit, 120f) ? hit.point.y + 0.1f : 2f;
            var go = new GameObject("Player") { layer = OsrsNpcManager.PlayerLayer };
            go.transform.position = start;
            go.AddComponent<FirstPersonController>();
            var link = go.AddComponent<OsrsServerLink>();
            if (!string.IsNullOrEmpty(OsrsTitleScreen.PendingName)) link.playerName = OsrsTitleScreen.PendingName;
            go.AddComponent<OsrsInteraction>();
            go.AddComponent<OsrsPanels>();
            go.AddComponent<OsrsGroundItems>();
            go.AddComponent<OsrsBank>();
            go.AddComponent<OsrsShop>();
            go.AddComponent<OsrsSpellbook>();
            go.AddComponent<OsrsMinigameHud>();
            go.AddComponent<OsrsCharacterDesign>();
            var npcs = new GameObject("Npcs");
            npcs.transform.SetParent(transform, false);
            npcs.AddComponent<OsrsNpcManager>().material = material;
            player = go.transform;
        }

        private void OnGUI()
        {
            if (OsrsCharacterDesign.Active || !OsrsTitleScreen.Released) return;
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
