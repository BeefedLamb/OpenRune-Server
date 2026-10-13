using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// Crosshair targeting: whatever is under the centre of the screen is the target. E does its
    /// first option, keys 1-5 pick an option. The server checks the range; the client only sends
    /// when the player is standing next to the target (OSRS rules: adjacent to the footprint).
    /// Also draws the HUD: crosshair, target label, health, chat log and hit numbers.
    ///
    /// Combat: left click attacks the NPC under the crosshair (the server keeps swinging at the
    /// weapon's tick speed while the fight lasts); holding the button for a moment before releasing
    /// arms the weapon's special attack first (a "power attack"); holding right mouse blocks with a
    /// shield. Looking away from the enemy for a while breaks off the fight.
    /// </summary>
    public class OsrsInteraction : MonoBehaviour
    {
        public float reach = 7f;
        public int opRange = 1;
        public float powerAttackHold = 0.35f;
        public float disengageAfter = 2.0f;

        private Camera cam;
        private OsrsWorldLoader world;
        private OsrsServerLink link;
        private GUIStyle labelStyle, smallStyle, bigStyle;

        private string targetName;
        private string[] targetOps;
        private bool targetIsNpc;
        private OsrsNpcRef targetNpc;
        private OsrsWorldLoader.Placement targetLoc;
        private bool hasTarget;
        private bool inRange;
        /// <summary>True while the chat box has keyboard focus; movement scripts ignore input then.</summary>
        public static bool TextInputActive { get; private set; }
        private bool chatOpen;
        private string chatText = "";
        private bool focusChat;
        private OsrsNpcRef lockedNpc;
        private int engagedNpc = -1;
        private float lastAimedAtEngaged;
        private float attackPressStart = -1f;
        private bool blocking;
        private float swingStart = -10f;

        private void Start()
        {
            world = OsrsWorldLoader.Instance;
            link = GetComponent<OsrsServerLink>();
            Physics.queriesHitBackfaces = true;
        }

        private void Update()
        {
            if (cam == null) cam = GetComponentInChildren<Camera>();
            if (cam == null || world == null) return;
            ReadChatInput();
            if (chatOpen) { TextInputActive = true; return; }
            TextInputActive = false;
            ReadLockOnInput();
            FindTarget();
            ReadInput();
            ReadCombatInput();
            UpdateEngagement();
            if (link != null && link.LastAnimTime > swingStart) swingStart = link.LastAnimTime;
            AnimateCamera();
        }

        private int AttackOp()
        {
            if (!targetIsNpc || targetOps == null) return -1;
            for (int i = 0; i < targetOps.Length; i++)
                if (targetOps[i] == "Attack") return i + 1;
            return -1;
        }

        private void ReadCombatInput()
        {
            if (Cursor.lockState != CursorLockMode.Locked || link == null) return;
            bool attackDown, attackUp, blockHeld;
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m == null) return;
            attackDown = m.leftButton.wasPressedThisFrame;
            attackUp = m.leftButton.wasReleasedThisFrame;
            blockHeld = m.rightButton.isPressed;
#else
            attackDown = Input.GetMouseButtonDown(0);
            attackUp = Input.GetMouseButtonUp(0);
            blockHeld = Input.GetMouseButton(1);
#endif
            if (blockHeld != blocking)
            {
                blocking = blockHeld;
                link.Send(blocking ? "BLOCK 1" : "BLOCK 0");
            }
            if (attackDown) attackPressStart = Time.time;
            if (attackUp && attackPressStart >= 0f)
            {
                bool power = Time.time - attackPressStart >= powerAttackHold;
                attackPressStart = -1f;
                if (!blocking) Attack(power);
            }
        }

        private void Attack(bool power)
        {
            int op = AttackOp();
            if (op < 0 || targetNpc == null)
            {
                if (hasTarget && !targetIsNpc) return;
                swingStart = Time.time;
                return;
            }
            if (!link.LoggedIn) { link.messages.Add("Not connected to the server."); return; }
            if (!inRange) { link.messages.Add($"You need to be next to the {targetName.ToLowerInvariant()} to attack it."); return; }
            if (power) link.Send("SPEC");
            link.Send($"OPNPC {targetNpc.index} {op}");
            engagedNpc = targetNpc.index;
            lastAimedAtEngaged = Time.time;
            swingStart = Time.time;
        }

        /// <summary>Breaks off a fight when the crosshair has been off the enemy for a while.</summary>
        private void UpdateEngagement()
        {
            if (engagedNpc < 0) return;
            if (targetIsNpc && targetNpc != null && targetNpc.index == engagedNpc)
                lastAimedAtEngaged = Time.time;
            bool gone = true;
            foreach (var npc in OsrsNpcRef.All) if (npc.index == engagedNpc) { gone = false; break; }
            if (gone || Time.time - lastAimedAtEngaged > disengageAfter)
            {
                if (!gone) link?.Send("STOP");
                engagedNpc = -1;
            }
        }

        /// <summary>A short camera kick on each swing and a lowered stance while blocking.</summary>
        private void AnimateCamera()
        {
            var fpc = GetComponent<FirstPersonController>();
            if (fpc == null) return;
            float swing = Mathf.Clamp01((Time.time - swingStart) / 0.3f);
            float kick = Mathf.Sin(swing * Mathf.PI) * 2.5f;
            var target = new Vector3(kick - (blocking ? 1.5f : 0f), 0f, blocking ? 2f : -kick * 0.4f);
            fpc.cameraOffsetEuler = Vector3.Lerp(fpc.cameraOffsetEuler, target, blocking ? 0.2f : 1f);
        }

        /// <summary>Enter opens the chat box; Enter again sends (a `::` prefix runs a server command), Escape cancels.</summary>
        private void ReadChatInput()
        {
            bool enter, escape;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            enter = kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame);
            escape = kb != null && kb.escapeKey.wasPressedThisFrame;
#else
            enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            escape = Input.GetKeyDown(KeyCode.Escape);
#endif
            if (!chatOpen)
            {
                if (enter && Cursor.lockState == CursorLockMode.Locked) { chatOpen = true; chatText = ""; focusChat = true; }
                return;
            }
            if (escape) { chatOpen = false; return; }
            if (enter)
            {
                var text = chatText.Trim();
                chatOpen = false;
                if (text.Length == 0) return;
                if (link != null && link.LoggedIn) link.Send("CHAT " + text);
                else link?.messages.Add("Not connected to the server.");
            }
        }

        /// <summary>Tab locks the camera onto the nearest enemy in view (Tab again cycles, Tab with none in view releases).</summary>
        private void ReadLockOnInput()
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            bool tab;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            tab = kb != null && kb.tabKey.wasPressedThisFrame;
#else
            tab = Input.GetKeyDown(KeyCode.Tab);
#endif
            if (lockedNpc != null && (!lockedNpc.isActiveAndEnabled || lockedNpc.hp == 0))
                lockedNpc = null;
            if (tab)
                lockedNpc = NextLockTarget(lockedNpc);
            if (lockedNpc != null)
            {
                var fpc = GetComponent<FirstPersonController>();
                var aim = lockedNpc.transform.position + Vector3.up * (lockedNpc.height * 0.6f);
                if (fpc != null) fpc.LookToward(aim, 10f * Time.deltaTime);
            }
        }

        private OsrsNpcRef NextLockTarget(OsrsNpcRef current)
        {
            OsrsNpcRef best = null;
            float bestScore = float.MaxValue;
            var fwd = cam.transform.forward; fwd.y = 0f; fwd.Normalize();
            foreach (var npc in OsrsNpcRef.All)
            {
                if (npc == current || npc.ops == null || System.Array.IndexOf(npc.ops, "Attack") < 0) continue;
                var to = npc.transform.position - transform.position;
                float dist = new Vector3(to.x, 0f, to.z).magnitude;
                if (dist > 12f) continue;
                float ang = Vector3.Angle(fwd, new Vector3(to.x, 0f, to.z));
                if (current == null && ang > 70f) continue;
                float score = dist + ang * 0.05f;
                if (score < bestScore) { bestScore = score; best = npc; }
            }
            return best;
        }

        private void FindTarget()
        {
            hasTarget = false;
            targetNpc = null;
            if (lockedNpc != null)
            {
                var me0 = link != null ? link.CurrentTile() : new Vector3Int(Mathf.FloorToInt(transform.position.x), Mathf.FloorToInt(transform.position.z), 0);
                targetNpc = lockedNpc;
                targetIsNpc = true;
                targetName = lockedNpc.combatLevel > 0 ? $"{lockedNpc.displayName} (level {lockedNpc.combatLevel})" : lockedNpc.displayName;
                targetOps = lockedNpc.ops;
                hasTarget = true;
                int lx = Mathf.FloorToInt(lockedNpc.transform.position.x - lockedNpc.size * 0.5f);
                int lz = Mathf.FloorToInt(lockedNpc.transform.position.z - lockedNpc.size * 0.5f);
                inRange = Distance(me0.x, me0.y, lx, lz, lockedNpc.size, lockedNpc.size) <= opRange;
                return;
            }
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!Physics.Raycast(ray, out var hit, reach))
                return;
            var me = link != null ? link.CurrentTile() : new Vector3Int(Mathf.FloorToInt(transform.position.x), Mathf.FloorToInt(transform.position.z), 0);
            var npc = hit.collider.GetComponentInParent<OsrsNpcRef>();
            if (npc != null)
            {
                targetNpc = npc;
                targetIsNpc = true;
                targetName = npc.combatLevel > 0 ? $"{npc.displayName} (level {npc.combatLevel})" : npc.displayName;
                targetOps = npc.ops;
                hasTarget = true;
                int nx = Mathf.FloorToInt(npc.transform.position.x - npc.size * 0.5f);
                int nz = Mathf.FloorToInt(npc.transform.position.z - npc.size * 0.5f);
                inRange = Distance(me.x, me.y, nx, nz, npc.size, npc.size) <= opRange;
                return;
            }
            var locRef = hit.collider.GetComponentInParent<OsrsLocRef>();
            if (locRef != null && locRef.def != null)
            {
                targetLoc = new OsrsWorldLoader.Placement
                {
                    locId = locRef.locId, x = locRef.x, z = locRef.z, level = locRef.level,
                    sizeX = locRef.sizeX, sizeZ = locRef.sizeZ,
                };
                targetIsNpc = false;
                targetName = locRef.def.name;
                targetOps = locRef.def.ops;
                hasTarget = true;
                inRange = Distance(me.x, me.y, locRef.x, locRef.z, locRef.sizeX, locRef.sizeZ) <= opRange;
                return;
            }
            var inside = hit.point - hit.normal * 0.08f;
            var outside = hit.point + hit.normal * 0.08f;
            var found = world.FindPlacement(Mathf.FloorToInt(inside.x), Mathf.FloorToInt(inside.z), 0)
                        ?? world.FindPlacement(Mathf.FloorToInt(outside.x), Mathf.FloorToInt(outside.z), 0);
            if (found == null) return;
            var pl = found.Value;
            if (!world.Data.locs.TryGetValue(pl.locId, out var def)) return;
            targetLoc = pl;
            targetIsNpc = false;
            targetName = def.name;
            targetOps = def.ops;
            hasTarget = true;
            inRange = Distance(me.x, me.y, pl.x, pl.z, pl.sizeX, pl.sizeZ) <= opRange;
        }

        private static int Distance(int px, int pz, int x, int z, int w, int l)
        {
            int dx = Mathf.Max(x - px, px - (x + w - 1), 0);
            int dz = Mathf.Max(z - pz, pz - (z + l - 1), 0);
            return Mathf.Max(dx, dz);
        }

        private int FirstOp()
        {
            if (targetOps == null) return -1;
            for (int i = 0; i < targetOps.Length; i++)
                if (!string.IsNullOrEmpty(targetOps[i])) return i + 1;
            return -1;
        }

        private void ReadInput()
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            int op = -1;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.eKey.wasPressedThisFrame) op = FirstOp();
            else if (kb.digit1Key.wasPressedThisFrame) op = 1;
            else if (kb.digit2Key.wasPressedThisFrame) op = 2;
            else if (kb.digit3Key.wasPressedThisFrame) op = 3;
            else if (kb.digit4Key.wasPressedThisFrame) op = 4;
            else if (kb.digit5Key.wasPressedThisFrame) op = 5;
#else
            if (Input.GetKeyDown(KeyCode.E)) op = FirstOp();
            else for (int i = 1; i <= 5; i++) if (Input.GetKeyDown(KeyCode.Alpha0 + i)) op = i;
#endif
            if (op < 1 || !hasTarget || targetOps == null || op > targetOps.Length || string.IsNullOrEmpty(targetOps[op - 1]))
                return;
            if (link == null || !link.LoggedIn)
            {
                link?.messages.Add("Not connected to the server.");
                return;
            }
            if (!inRange)
            {
                link.messages.Add($"You need to be next to the {targetName.ToLowerInvariant()} to do that.");
                return;
            }
            if (targetIsNpc && targetNpc != null)
                link.Send($"OPNPC {targetNpc.index} {op}");
            else
                link.Send($"OPLOC {targetLoc.locId} {targetLoc.x} {targetLoc.z} {targetLoc.level} {op}");
        }

        private void OnGUI()
        {
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.UpperCenter };
                labelStyle.normal.textColor = Color.white;
                smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
                smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
                bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            GUI.color = hasTarget ? (inRange ? Color.yellow : Color.white) : new Color(1f, 1f, 1f, 0.7f);
            GUI.Box(new Rect(cx - 3, cy - 3, 6, 6), GUIContent.none);
            GUI.color = Color.white;

            if (hasTarget)
            {
                int first = FirstOp();
                int atk = AttackOp();
                string line = first > 0 ? $"[E] {targetOps[first - 1]} {targetName}" : targetName;
                if (atk > 0) line = $"[Click] Attack {targetName}" + (first > 0 && first != atk ? $"   [E] {targetOps[first - 1]}" : "");
                if (!inRange && first > 0) line += "  (move closer)";
                Shadowed(new Rect(cx - 300, cy + 14, 600, 24), line, labelStyle);
                var extra = new List<string>();
                for (int i = 0; i < targetOps.Length; i++)
                    if (!string.IsNullOrEmpty(targetOps[i]) && i + 1 != first && i + 1 != atk) extra.Add($"{i + 1}: {targetOps[i]}");
                if (extra.Count > 0)
                    Shadowed(new Rect(cx - 300, cy + 38, 600, 20), string.Join("   ", extra), smallStyle, TextAnchor.UpperCenter);
                if (targetIsNpc && targetNpc != null && targetNpc.maxHp > 0)
                    Shadowed(new Rect(cx - 300, cy + 58, 600, 20), $"HP {targetNpc.hp}/{targetNpc.maxHp}", smallStyle, TextAnchor.UpperCenter);
            }

            if (chatOpen)
            {
                GUI.SetNextControlName("osrs_chat");
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.Box(new Rect(8, Screen.height - 212, 704, 26), GUIContent.none);
                GUI.color = Color.white;
                chatText = GUI.TextField(new Rect(12, Screen.height - 209, 696, 20), chatText, 120);
                if (focusChat) { GUI.FocusControl("osrs_chat"); focusChat = false; }
            }
            if (link != null)
            {
                if (link.Hp >= 0)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.5f);
                    GUI.Box(new Rect(10, Screen.height - 40, 204, 24), GUIContent.none);
                    GUI.color = link.Hp * 3 <= link.MaxHp ? Color.red : Color.green;
                    GUI.Box(new Rect(12, Screen.height - 38, 200f * Mathf.Clamp01(link.Hp / (float)Mathf.Max(1, link.MaxHp)), 20), GUIContent.none);
                    GUI.color = Color.white;
                    Shadowed(new Rect(12, Screen.height - 38, 200, 20), $"HP {link.Hp}/{link.MaxHp}", smallStyle, TextAnchor.MiddleCenter);
                }
                if (link.SpecEnergy >= 0)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.5f);
                    GUI.Box(new Rect(10, Screen.height - 62, 204, 18), GUIContent.none);
                    GUI.color = new Color(0.3f, 0.6f, 1f);
                    GUI.Box(new Rect(12, Screen.height - 60, 200f * Mathf.Clamp01(link.SpecEnergy / 1000f), 14), GUIContent.none);
                    GUI.color = Color.white;
                    Shadowed(new Rect(12, Screen.height - 61, 200, 16), $"Special {link.SpecEnergy / 10}%", smallStyle, TextAnchor.MiddleCenter);
                }
                if (lockedNpc != null)
                {
                    var sp = cam.WorldToScreenPoint(lockedNpc.transform.position + Vector3.up * (lockedNpc.height * 0.6f));
                    if (sp.z > 0)
                    {
                        GUI.color = new Color(1f, 0.85f, 0.2f);
                        GUI.Box(new Rect(sp.x - 7, Screen.height - sp.y - 7, 14, 14), GUIContent.none);
                        GUI.color = Color.white;
                    }
                    Shadowed(new Rect(cx - 100, cy + 112, 200, 20), "[Tab] locked on", smallStyle, TextAnchor.UpperCenter);
                }
                if (blocking)
                    Shadowed(new Rect(cx - 100, cy + 90, 200, 24), "Blocking", labelStyle);
                else if (attackPressStart >= 0f && Time.time - attackPressStart >= powerAttackHold)
                    Shadowed(new Rect(cx - 100, cy + 90, 200, 24), "Power attack ready", labelStyle);
                int shown = Mathf.Min(6, link.messages.Count);
                for (int i = 0; i < shown; i++)
                {
                    var msg = link.messages[link.messages.Count - shown + i];
                    Shadowed(new Rect(10, Screen.height - 180 + i * 20, 700, 20), msg, smallStyle);
                }
                foreach (var h in link.hitsTaken)
                {
                    float age = Time.time - h.time;
                    if (age > 1.2f) continue;
                    GUI.color = new Color(1f, 0.3f, 0.3f, 1f - age / 1.2f);
                    GUI.Label(new Rect(cx - 60, cy - 90 - age * 30f, 120, 30), h.damage == 0 ? "0" : $"-{h.damage}", bigStyle);
                }
                GUI.color = Color.white;
            }

            foreach (var npc in OsrsNpcRef.All)
            {
                foreach (var h in npc.hits)
                {
                    float age = Time.time - h.time;
                    if (age > 1.2f) continue;
                    var wp = npc.transform.position + Vector3.up * (npc.height + 0.2f + age * 0.5f);
                    var sp = cam.WorldToScreenPoint(wp);
                    if (sp.z < 0) continue;
                    GUI.color = new Color(1f, 0.9f, 0.2f, 1f - age / 1.2f);
                    GUI.Label(new Rect(sp.x - 40, Screen.height - sp.y - 15, 80, 30), h.damage == 0 ? "0" : h.damage.ToString(), bigStyle);
                }
            }
            GUI.color = Color.white;
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, TextAnchor? anchor = null)
        {
            var old = style.alignment;
            if (anchor.HasValue) style.alignment = anchor.Value;
            var c = style.normal.textColor;
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, style);
            style.normal.textColor = c;
            GUI.Label(r, text, style);
            style.alignment = old;
        }
    }
}
