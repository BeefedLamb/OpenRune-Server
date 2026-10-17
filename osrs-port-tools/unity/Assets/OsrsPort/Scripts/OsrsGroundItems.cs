using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// Ground items (loot, drops, item spawns) from the server's `GITEMS` list: a small gold marker on
    /// each tile with an item, OSRS-orange name labels above them, and [F] to take the item nearest
    /// the crosshair within one tile (sends `TAKE id x z level`; the server runs the normal Take op).
    /// Labels show the item's inventory icon (when exported) beside its name; no 3D item models yet.
    /// </summary>
    public class OsrsGroundItems : MonoBehaviour
    {
        public float labelDistance = 14f;
        public int takeRange = 1;

        private OsrsServerLink link;
        private OsrsWorldLoader world;
        private Camera cam;
        private int seenRevision = -1;
        private Mesh markerMesh;
        private Material markerMaterial;
        private GUIStyle labelStyle, hintStyle;
        private OsrsServerLink.GroundItem target;

        private class Pile
        {
            public GameObject go;
            public Vector3 pos;
            public List<OsrsServerLink.GroundItem> items = new List<OsrsServerLink.GroundItem>();
        }

        private readonly Dictionary<Vector3Int, Pile> piles = new Dictionary<Vector3Int, Pile>();
        private GameObject root;

        private void Start()
        {
            link = GetComponent<OsrsServerLink>();
            world = OsrsWorldLoader.Instance;
            root = new GameObject("GroundItems");
        }

        private void OnDestroy()
        {
            if (root != null) Destroy(root);
        }

        private void Update()
        {
            if (link == null) return;
            if (cam == null) cam = GetComponentInChildren<Camera>();
            if (link.GroundRevision != seenRevision)
            {
                seenRevision = link.GroundRevision;
                Rebuild();
            }
            target = FindTarget();
            if (target != null && TakePressed()) link.Send($"TAKE {target.id} {target.x} {target.z} {target.level}");
        }

        private bool TakePressed()
        {
            if (OsrsPanels.UiOpen || OsrsInteraction.TextInputActive || link.CurrentDialogue != null) return false;
            if (Cursor.lockState != CursorLockMode.Locked && !OsrsInteraction.CursorMode) return false;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.fKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.F);
#endif
        }

        /// <summary>The takeable item (within range) whose pile is closest to the crosshair.</summary>
        private OsrsServerLink.GroundItem FindTarget()
        {
            if (cam == null || !link.LoggedIn || piles.Count == 0) return null;
            var me = link.CurrentTile();
            OsrsServerLink.GroundItem best = null;
            float bestScore = float.MaxValue;
            var centre = new Vector2(Screen.width / 2f, Screen.height / 2f);
            foreach (var kv in piles)
            {
                var k = kv.Key;
                if (k.z != me.z || Mathf.Max(Mathf.Abs(k.x - me.x), Mathf.Abs(k.y - me.y)) > takeRange) continue;
                var sp = cam.WorldToScreenPoint(kv.Value.pos);
                float score = sp.z > 0 ? Vector2.Distance(centre, new Vector2(sp.x, sp.y)) : 100000f;
                if (score < bestScore) { bestScore = score; best = kv.Value.items[0]; }
            }
            return best;
        }

        private void Rebuild()
        {
            var fresh = new Dictionary<Vector3Int, List<OsrsServerLink.GroundItem>>();
            foreach (var it in link.groundItems)
            {
                var key = new Vector3Int(it.x, it.z, it.level);
                if (!fresh.TryGetValue(key, out var list)) fresh[key] = list = new List<OsrsServerLink.GroundItem>();
                list.Add(it);
            }
            var gone = new List<Vector3Int>();
            foreach (var key in piles.Keys) if (!fresh.ContainsKey(key)) gone.Add(key);
            foreach (var key in gone) { Destroy(piles[key].go); piles.Remove(key); }
            foreach (var kv in fresh)
            {
                if (!piles.TryGetValue(kv.Key, out var pile))
                {
                    pile = new Pile();
                    pile.pos = new Vector3(kv.Key.x + 0.5f, GroundHeight(kv.Key.x + 0.5f, kv.Key.y + 0.5f, kv.Key.z) + 0.12f, kv.Key.y + 0.5f);
                    pile.go = CreateMarker(kv.Key, pile.pos);
                    piles[kv.Key] = pile;
                }
                pile.items = kv.Value;
            }
        }

        private float GroundHeight(float x, float z, int level)
        {
            var h = world != null ? world.TerrainHeight(x, z, level) : null;
            if (h.HasValue) return h.Value;
            return Physics.Raycast(new Vector3(x, 60f, z), Vector3.down, out var hit, 120f, OsrsNpcManager.WorldMask) ? hit.point.y : 0f;
        }

        private GameObject CreateMarker(Vector3Int key, Vector3 pos)
        {
            EnsureMarkerAssets();
            var go = new GameObject($"gitem_{key.x}_{key.y}_{key.z}");
            go.transform.SetParent(root.transform, false);
            go.transform.position = pos;
            go.transform.localScale = new Vector3(0.28f, 0.2f, 0.28f);
            go.AddComponent<MeshFilter>().sharedMesh = markerMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = markerMaterial;
            return go;
        }

        private void EnsureMarkerAssets()
        {
            if (markerMesh != null) return;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            markerMesh = Instantiate(tmp.GetComponent<MeshFilter>().sharedMesh);
            Destroy(tmp);
            var cols = new Color32[markerMesh.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(230, 180, 40, 255);
            markerMesh.colors32 = cols;
            var npcs = FindAnyObjectByType<OsrsNpcManager>();
            markerMaterial = npcs != null && npcs.material != null ? npcs.material : new Material(Shader.Find("Sprites/Default"));
        }

        private void OnGUI()
        {
            if (cam == null || piles.Count == 0) return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.UpperCenter };
            }
            var level = link.CurrentLevel;
            foreach (var pile in piles.Values)
            {
                if (pile.items.Count == 0 || pile.items[0].level != level) continue;
                float dist = Vector3.Distance(cam.transform.position, pile.pos);
                if (dist > labelDistance) continue;
                var sp = cam.WorldToScreenPoint(pile.pos + Vector3.up * 0.25f);
                if (sp.z <= 0) continue;
                float y = Screen.height - sp.y;
                int shown = Mathf.Min(pile.items.Count, 4);
                for (int i = 0; i < shown; i++)
                {
                    var it = pile.items[i];
                    string text = it.count > 1 ? $"{it.name} ({it.count:N0})" : it.name;
                    if (i == 3 && pile.items.Count > 4) text = $"+{pile.items.Count - 3} more";
                    var lr = new Rect(sp.x - 120, y - 18 - i * 20, 240, 18);
                    bool more = i == 3 && pile.items.Count > 4;
                    if (!more)
                    {
                        float tw = labelStyle.CalcSize(new GUIContent(text)).x;
                        OsrsItemIcons.Draw(new Rect(sp.x - tw / 2f - 24, lr.y - 1, 22, 20), it.id);
                    }
                    Shadowed(lr, text, labelStyle, new Color(1f, 0.6f, 0.2f));
                }
            }
            if (target != null && !OsrsPanels.UiOpen && link.CurrentDialogue == null)
            {
                string t = target.count > 1 ? $"{target.name} ({target.count:N0})" : target.name;
                OsrsItemIcons.Draw(new Rect(Screen.width / 2f - 18, Screen.height / 2f + 44, 36, 32), target.id);
                Shadowed(new Rect(Screen.width / 2f - 300, Screen.height / 2f + 80, 600, 22), $"[F] Take {t}", hintStyle, Color.white);
            }
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, Color color)
        {
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
        }
    }
}