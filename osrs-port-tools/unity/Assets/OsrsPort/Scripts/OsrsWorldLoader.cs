using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsrsPort
{
    /// <summary>
    /// Loads an exported OSRS area (.osm) from StreamingAssets, builds its meshes and colliders,
    /// and spawns a first-person player. Starts automatically when you press Play.
    /// 1 Unity unit = 1 OSRS tile; x = east, z = north; origin = SW corner of the exported map square.
    /// </summary>
    public class OsrsWorldLoader : MonoBehaviour
    {
        public const string DefaultFile = "lumbridge.osm";

        public string fileName = DefaultFile;
        public Vector2 spawnTile = new Vector2(22.5f, 18.5f);

        public int BaseX { get; private set; }
        public int BaseY { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (FindFirstObjectByType<OsrsWorldLoader>() != null)
                return;
            if (!File.Exists(Path.Combine(Application.streamingAssetsPath, DefaultFile)))
                return;
            new GameObject("OsrsWorld").AddComponent<OsrsWorldLoader>();
        }

        private void Start()
        {
            var shader = Shader.Find("OsrsPort/VertexColorLit");
            if (shader == null)
            {
                Debug.LogError("OsrsPort: shader 'OsrsPort/VertexColorLit' not found.");
                return;
            }
            var material = new Material(shader);

            string path = Path.Combine(Application.streamingAssetsPath, fileName);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "OSM1")
                {
                    Debug.LogError("OsrsPort: not an OSM1 file: " + path);
                    return;
                }
                BaseX = reader.ReadInt32();
                BaseY = reader.ReadInt32();
                int chunks = reader.ReadInt32();
                int totalTris = 0;
                for (int c = 0; c < chunks; c++)
                {
                    string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
                    int count = reader.ReadInt32();
                    totalTris += count / 3;
                    BuildChunk(name, reader, count, material);
                }
                Debug.Log($"OsrsPort: loaded {fileName} ({chunks} chunks, {totalTris:N0} triangles) in {sw.ElapsedMilliseconds} ms");
            }

            SetupAtmosphere();
            SpawnPlayer();
        }

        private void BuildChunk(string name, BinaryReader reader, int count, Material material)
        {
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++)
                positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            var colorBytes = reader.ReadBytes(count * 4);
            var colors = new Color32[count];
            for (int i = 0; i < count; i++)
                colors[i] = new Color32(colorBytes[i * 4], colorBytes[i * 4 + 1], colorBytes[i * 4 + 2], 255);

            var indices = new int[count];
            for (int i = 0; i < count; i++)
                indices[i] = i;

            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(positions);
            mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            if (!name.StartsWith("decor"))
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void SetupAtmosphere()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 35f;
            RenderSettings.fogEndDistance = 90f;
            RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.82f);
        }

        private void SpawnPlayer()
        {
            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                cam.gameObject.SetActive(false);

            var start = new Vector3(spawnTile.x, 40f, spawnTile.y);
            if (Physics.Raycast(start, Vector3.down, out var hit, 80f))
                start.y = hit.point.y + 0.1f;
            else
                start.y = 2f;

            var player = new GameObject("Player");
            player.transform.position = start;
            player.AddComponent<FirstPersonController>();
            player.AddComponent<OsrsServerLink>();
        }
    }
}
