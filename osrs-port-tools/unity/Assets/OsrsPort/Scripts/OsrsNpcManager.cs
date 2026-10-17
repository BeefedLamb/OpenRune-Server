using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsrsPort
{
    /// <summary>Shows the NPCs the server reports near the player, moving them smoothly between ticks.</summary>
    public class OsrsNpcManager : MonoBehaviour
    {
        public const int NpcLayer = 8;
        public const int PlayerLayer = 2;
        public static int WorldMask => ~((1 << NpcLayer) | (1 << PlayerLayer));
        public static int NpcMask => 1 << NpcLayer;

        public Material material;

        private class Visual
        {
            public GameObject go;
            public OsrsNpcRef reference;
            public Vector3 from, to;
            public float moveStart;
            public int lastHp;
            /// <summary>For players: the PLOOK the body was built from ("" = stand-in model).</summary>
            public string lookKey = "";
            /// <summary>Plays OSRS sequences on this body (null when animations aren't exported).</summary>
            public OsrsAnimator animator;
            public int lastAnim = -1;
        }

        private readonly Dictionary<int, Visual> visuals = new Dictionary<int, Visual>();
        private readonly Dictionary<int, Mesh> meshCache = new Dictionary<int, Mesh>();
        private int seenRevision = -1;
        private int seenLookRevision = -1;
        private OsrsWorldLoader world;

        private void Start()
        {
            world = OsrsWorldLoader.Instance;
        }

        private void Update()
        {
            var link = OsrsServerLink.Instance;
            if (link == null || world == null || world.Data == null) return;
            if (link.NpcRevision != seenRevision || link.LookRevision != seenLookRevision)
            {
                seenRevision = link.NpcRevision;
                seenLookRevision = link.LookRevision;
                Apply(link.npcs);
            }
            float t = Time.time;
            for (int i = dyingVisuals.Count - 1; i >= 0; i--)
            {
                var dv = dyingVisuals[i];
                if (dv.go == null) { dyingVisuals.RemoveAt(i); continue; }
                if (t - dv.reference.deathTime >= DeathDuration) { Destroy(dv.go); dyingVisuals.RemoveAt(i); continue; }
                AnimateDeath(dv);
            }
            foreach (var v in visuals.Values)
            {
                if (v.reference.dying) { AnimateDeath(v); continue; }
                float k = Mathf.Clamp01((t - v.moveStart) / 0.6f);
                var pos = Vector3.Lerp(v.from, v.to, k);
                if (v.go.transform.position != pos)
                {
                    var dir = v.to - v.from; dir.y = 0f;
                    if (dir.sqrMagnitude > 0.001f)
                        v.go.transform.rotation = Quaternion.LookRotation(-dir);
                }
                v.go.transform.position = pos;
                if (v.animator != null)
                {
                    float step = (v.to - v.from).magnitude;
                    v.animator.moving = k < 1f && step > 0.01f;
                    v.animator.running = step > 1.5f;
                    if (v.reference.isPlayer && link.playerBas.TryGetValue(v.reference.index, out var bas))
                        SetBas(v.animator, bas);
                    continue;
                }
                float bob = Mathf.Clamp01((t - v.reference.lastAnimTime) / 0.4f);
                v.go.transform.localScale = new Vector3(1f, 1f + 0.08f * Mathf.Sin(bob * Mathf.PI), 1f);
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
                target.y = GroundHeight(target, s.level);
                if (!visuals.TryGetValue(s.index, out var v))
                {
                    v = Create(s, def);
                    v.from = v.to = target;
                    v.go.transform.position = target;
                    visuals[s.index] = v;
                }
                else if (v.reference.id != s.id || v.reference.displayName != (s.playerName ?? v.reference.displayName)
                    || (s.playerName != null && v.lookKey != LookKeyFor(s)))
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
                // Fallback hitsplats from hp drops, only until the server sends real HIT lines.
                if (s.hp < v.lastHp && v.lastHp >= 0 && !OsrsServerLink.Instance.ServerNpcHits)
                    v.reference.RecordHit(v.lastHp - s.hp);
                if (v.reference.dying && s.hp > 0 && Time.time - v.reference.deathTime > 3f)
                {
                    // Same slot reused by a respawn: rebuild it fresh.
                    Destroy(v.go);
                    v = Create(s, def);
                    v.from = v.to = target;
                    v.go.transform.position = target;
                    visuals[s.index] = v;
                }
                if (v.animator != null && s.playerName != null && s.anim != v.lastAnim)
                {
                    if (s.anim >= 0) v.animator.Play(s.anim);
                    v.lastAnim = s.anim;
                }
                if (v.reference.dying) { v.lastHp = 0; continue; }
                v.lastHp = s.hp;
                v.reference.hp = s.hp;
                v.reference.maxHp = s.maxHp;
            }
            List<int> gone = null;
            foreach (var k in visuals.Keys)
                if (!seen.Contains(k)) (gone ??= new List<int>()).Add(k);
            if (gone != null)
                foreach (var k in gone)
                {
                    var v = visuals[k];
                    visuals.Remove(k);
                    if (!v.reference.isPlayer && (v.reference.dying || v.lastHp == 0))
                    {
                        // Killed: let it finish the death animation instead of popping out.
                        v.reference.MarkDying();
                        v.go.name += "_dead";
                        dyingVisuals.Add(v);
                    }
                    else Destroy(v.go);
                }
        }

        private readonly List<Visual> dyingVisuals = new List<Visual>();
        public const float DeathDuration = 1.8f;

        /// <summary>OSRS-style death: a short pause, then the body sinks and shrinks away.</summary>
        private void AnimateDeath(Visual v)
        {
            float age = Time.time - v.reference.deathTime;
            float k = Mathf.Clamp01((age - 0.6f) / (DeathDuration - 0.6f));
            var basePos = v.to;
            v.go.transform.position = basePos + Vector3.down * (v.reference.height * 0.6f * k * k);
            float s = Mathf.Lerp(1f, 0.15f, k);
            v.go.transform.localScale = new Vector3(s, Mathf.Lerp(1f, 0.05f, k), s);
        }

        private float GroundHeight(Vector3 p, int level)
        {
            var terrain = world.TerrainHeight(p.x, p.z, level);
            if (terrain.HasValue) return terrain.Value;
            p.y = 60f;
            return Physics.Raycast(p, Vector3.down, out var hit, 120f, WorldMask) ? hit.point.y : 0f;
        }

        /// <summary>The PLOOK key a player would be built from now ("" if none or not drawable yet).</summary>
        private static string LookKeyFor(OsrsServerLink.NpcState s)
        {
            var link = OsrsServerLink.Instance;
            if (link == null || !link.playerLooks.TryGetValue(s.index, out var look)) return "";
            return OsrsPlayerModels.GetMesh(look) != null ? OsrsPlayerModels.Key(look) : "";
        }

        private Visual Create(OsrsServerLink.NpcState s, WorldData.NpcDef def)
        {
            string lookKey = "";
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
            if (s.playerName != null)
            {
                reference.displayName = s.playerName;
                reference.combatLevel = -1;
                reference.ops = new string[5];
                reference.isPlayer = true;
                go.name = $"player_{s.playerName}";
            }

            Mesh mesh = null;
            if (s.playerName != null && OsrsServerLink.Instance != null
                && OsrsServerLink.Instance.playerLooks.TryGetValue(s.index, out var look))
            {
                // Real OSRS body from the player's identkits, colours and worn items.
                mesh = OsrsPlayerModels.GetMesh(look);
                if (mesh != null) lookKey = OsrsPlayerModels.Key(look);
            }
            if (mesh == null) mesh = GetMesh(s.id);
            OsrsAnimator animator = null;
            var animData = world.Data.anim;
            if (mesh != null && animData != null && animData.Loaded)
            {
                // Animatable copy of the same body: its own mesh, posed per OSRS frame.
                AnimMeshData ad = lookKey.Length > 0
                    ? world.Data.BuildPlayerAnimData(OsrsServerLink.Instance.playerLooks[s.index])
                    : world.Data.BuildNpcAnimData(s.id);
                if (ad != null)
                {
                    animator = go.AddComponent<OsrsAnimator>();
                    mesh = animator.Init(animData, ad, go.name);
                    if (def != null) { animator.standSeq = def.standSeq; animator.walkSeq = def.walkSeq; animator.runSeq = def.runSeq; }
                    if (s.playerName != null && OsrsServerLink.Instance.playerBas.TryGetValue(s.index, out var pb)) SetBas(animator, pb);
                }
            }
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
            return new Visual { go = go, reference = reference, lastHp = -1, lookKey = lookKey, animator = animator };
        }

        /// <summary>Applies a BAS set (ready, turn, walkF, walkB, walkL, walkR, run).</summary>
        public static void SetBas(OsrsAnimator a, int[] bas)
        {
            if (a == null || bas == null || bas.Length < 7) return;
            a.standSeq = bas[0]; a.walkSeq = bas[2]; a.runSeq = bas[6];
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
        public float lastAnimTime = -10f;
        public bool isPlayer;
        public int lastAnimId = -1;
        public string displayName;
        public string[] ops;
        public readonly List<(float time, int damage)> hits = new List<(float, int)>();
        /// <summary>Last hit time; the OSRS health bar shows for a few seconds after it.</summary>
        public float lastHitTime = -100f;
        /// <summary>Set by NDIE (or hp 0 on removal): plays the death sink/fade, then the object goes.</summary>
        public bool dying;
        public float deathTime = -1f;

        private void OnEnable() { All.Add(this); }

        private void OnDisable() { All.Remove(this); }

        public void RecordHit(int damage)
        {
            hits.Add((Time.time, damage));
            if (hits.Count > 8) hits.RemoveAt(0);
            lastHitTime = Time.time;
        }

        public void MarkDying()
        {
            if (dying) return;
            dying = true;
            deathTime = Time.time;
            hp = 0;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
        }
    }
}
