using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsrsPort
{
    /// <summary>Shows the NPCs the server reports near the player, moving them smoothly between ticks.</summary>
    public class OsrsNpcManager : MonoBehaviour
    {
        public const int NpcLayer = 8;
        public static int WorldMask => ~(1 << NpcLayer);
        public static int NpcMask => 1 << NpcLayer;

        public Material material;

        private class Visual
        {
            public GameObject go;
            public OsrsNpcRef reference;
            public Vector3 from, to;
            public float moveStart;
            public int lastHp;
        }

        private readonly Dictionary<int, Visual> visuals = new Dictionary<int, Visual>();
        private readonly Dictionary<int, Mesh> meshCache = new Dictionary<int, Mesh>();
        private int seenRevision = -1;
        private OsrsWorldLoader world;

        private void Start()
        {
            world = OsrsWorldLoader.Instance;
        }

        private void Update()
        {
            var link = OsrsServerLink.Instance;
            if (link == null || world == null || world.Data == null) return;
            if (link.NpcRevision != seenRevision)
            {
                seenRevision = link.NpcRevision;
                Apply(link.npcs);
            }
            float t = Time.time;
            foreach (var v in visuals.Values)
            {
                float k = Mathf.Clamp01((t - v.moveStart) / 0.6f);
                var pos = Vector3.Lerp(v.from, v.to, k);
                if (v.go.transform.position != pos)
                {
                    var dir = v.to - v.from; dir.y = 0f;
                    if (dir.sqrMagnitude > 0.001f)
                        v.go.transform.rotation = Quaternion.LookRotation(dir);
                }
                v.go.transform.position = pos;
            }
        }

        private void Apply(List<OsrsServerLink.NpcState> states)
        {
            var seen = new HashSet<int>();
            foreach (var s in states)
            {
                seen.Add(s.index);
                world.Data.npcs.TryGetValue(s.id, out var def);
                float size = def != null ? def.size : 1;
                var target = new Vector3(s.x + size * 0.5f, 0f, s.z + size * 0.5f);
                target.y = GroundHeight(target);
                if (!visuals.TryGetValue(s.index, out var v))
                {
                    v = Create(s, def);
                    v.from = v.to = target;
                    v.go.transform.position = target;
                    visuals[s.index] = v;
                }
                else if (v.reference.id != s.id)
                {
                    Destroy(v.go);
                    v = Create(s, def);
                    v.from = v.to = target;
                    v.go.transform.position = target;
                    visuals[s.index] = v;
                }
                if ((v.to - target).sqrMagnitude > 0.0001f)
                {
                    v.from = v.go.transform.position;
                    v.to = target;
                    v.moveStart = Time.time;
                }
                if (s.hp < v.lastHp && v.lastHp >= 0)
                    v.reference.RecordHit(v.lastHp - s.hp);
                v.lastHp = s.hp;
                v.reference.hp = s.hp;
                v.reference.maxHp = s.maxHp;
            }
            List<int> gone = null;
            foreach (var k in visuals.Keys)
                if (!seen.Contains(k)) (gone ??= new List<int>()).Add(k);
            if (gone != null)
                foreach (var k in gone) { Destroy(visuals[k].go); visuals.Remove(k); }
        }

        private float GroundHeight(Vector3 p)
        {
            p.y = 60f;
            return Physics.Raycast(p, Vector3.down, out var hit, 120f, WorldMask) ? hit.point.y : 0f;
        }

        private Visual Create(OsrsServerLink.NpcState s, WorldData.NpcDef def)
        {
            var go = new GameObject(def != null ? $"npc_{s.index}_{def.name}" : $"npc_{s.index}");
            go.layer = NpcLayer;
            go.transform.SetParent(transform, false);
            var reference = go.AddComponent<OsrsNpcRef>();
            reference.index = s.index;
            reference.id = s.id;
            reference.displayName = def != null ? def.name : "?";
            reference.combatLevel = def != null ? def.combatLevel : -1;
            reference.size = def != null ? def.size : 1;
            reference.ops = def != null ? def.ops : new string[5];

            var mesh = GetMesh(s.id);
            float height = 1.8f;
            if (mesh != null)
            {
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                height = Mathf.Max(0.5f, mesh.bounds.max.y);
            }
            bool targetable = mesh != null && def != null && def.name != "null";
            if (targetable)
            {
                var col = go.AddComponent<CapsuleCollider>();
                col.radius = Mathf.Max(0.3f, reference.size * 0.35f);
                col.height = height;
                col.center = new Vector3(0f, height * 0.5f, 0f);
            }
            reference.height = height;
            return new Visual { go = go, reference = reference, lastHp = -1 };
        }

        private Mesh GetMesh(int npcId)
        {
            if (meshCache.TryGetValue(npcId, out var cached)) return cached;
            var vm = world.Data.GetNpcMesh(npcId);
            if (vm.faceCount == 0) { meshCache[npcId] = null; return null; }
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
            var mesh = new Mesh { name = $"npc_{npcId}", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(vm.colors);
            mesh.SetTriangles(indices, 0, false);
            mesh.RecalculateBounds();
            meshCache[npcId] = mesh;
            return mesh;
        }
    }

    /// <summary>Identifies an NPC's GameObject so the crosshair can target it.</summary>
    public class OsrsNpcRef : MonoBehaviour
    {
        public static readonly List<OsrsNpcRef> All = new List<OsrsNpcRef>();

        public int index, id, combatLevel, size, hp = -1, maxHp = -1;
        public float height = 1.8f;
        public string displayName;
        public string[] ops;
        public readonly List<(float time, int damage)> hits = new List<(float, int)>();

        private void OnEnable() { All.Add(this); }

        private void OnDisable() { All.Remove(this); }

        public void RecordHit(int damage)
        {
            hits.Add((Time.time, damage));
            if (hits.Count > 8) hits.RemoveAt(0);
        }
    }
}
