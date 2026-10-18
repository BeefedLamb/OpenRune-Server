using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace OsrsPort
{
    /// <summary>
    /// Crosshair targeting: whatever is under the centre of the screen is the target. E does its
    /// default (first) option; keys 1-9/0 fire hotbar abilities (Attack, Pickpocket, Talk-to...) on
    /// the hovered, Tab-locked or last-selected NPC; Shift+1-5 still picks a raw option. C toggles
    /// cursor mode: mouse look stops, a cursor appears, left click = default option and right click
    /// opens the OSRS option menu for whatever is under the cursor. The server checks the range; the client only sends
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
        private OsrsNpcRef lockedNpc;
        private int engagedNpc = -1;
        private float lastAimedAtEngaged;
        private float attackPressStart = -1f;
        private bool blocking;
        private float swingStart = -10f;

        /// <summary>Cursor mode (C): free mouse, hover + left/right click NPCs and objects.</summary>
        public static bool CursorMode { get; private set; }
        /// <summary>The last NPC you clicked or used an ability on; hotbar abilities fall back to it.</summary>
        private OsrsNpcRef selectedNpc;
        private readonly Dictionary<string, float> abilityReadyAt = new Dictionary<string, float>();
        private bool menuOpen;
        private Vector2 menuPos;
        private bool menuIsNpc;
        private OsrsNpcRef menuNpc;
        private OsrsWorldLoader.Placement menuLoc;
        private string menuName;
        private string[] menuOps;
        private GUIStyle menuStyle, slotStyle, keyStyle;
        private const float SlotSize = 48f, SlotGap = 4f;

        private void Start()
        {
            world = OsrsWorldLoader.Instance;
            link = GetComponent<OsrsServerLink>();
            Physics.queriesHitBackfaces = true;
        }

#if ENABLE_INPUT_SYSTEM
        private void OnEnable()
        {
            if (Keyboard.current != null) Keyboard.current.onTextInput += OnTextInput;
        }

        private void OnDisable()
        {
            if (Keyboard.current != null) Keyboard.current.onTextInput -= OnTextInput;
        }

        private void OnTextInput(char c)
        {
            if (!chatOpen || char.IsControl(c)) return;
            if (chatText.Length < 120) chatText += c;
        }
#endif

        private void Update()
        {
            if (cam == null) cam = GetComponentInChildren<Camera>();
            if (cam == null || world == null) return;
            ReadChatInput();
            if (chatOpen) { TextInputActive = true; return; }
            TextInputActive = false;
            if (OsrsPanels.UiOpen) { hasTarget = false; menuOpen = false; return; }
            ReadCursorToggle();
            ValidateSelection();
            if (link != null && link.CurrentDialogue != null)
            {
                ReadDialogueInput(link.CurrentDialogue);
                hasTarget = false;
                return;
            }
            ReadLockOnInput();
            FindTarget();
            ReadInput();
            ReadCursorClicks();
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
            if (StunBlocked()) return;
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
            if ((CursorMode || lockedNpc != null) && selectedNpc != null && selectedNpc.index == engagedNpc)
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
                if (enter && InputAllowed) { chatOpen = true; chatText = ""; }
                return;
            }
            if (escape) { chatOpen = false; return; }
#if ENABLE_INPUT_SYSTEM
            if (kb.backspaceKey.wasPressedThisFrame && chatText.Length > 0) chatText = chatText.Substring(0, chatText.Length - 1);
            if (kb.vKey.wasPressedThisFrame && kb.ctrlKey.isPressed) chatText += GUIUtility.systemCopyBuffer ?? "";
#else
            foreach (char c in Input.inputString)
            {
                if (c == '\b') { if (chatText.Length > 0) chatText = chatText.Substring(0, chatText.Length - 1); }
                else if (!char.IsControl(c) && chatText.Length < 120) chatText += c;
            }
#endif
            if (enter)
            {
                var text = chatText.Trim();
                chatOpen = false;
                if (text.Length == 0) return;
                if (link != null && link.LoggedIn) link.Send("CHAT " + text);
                else link?.messages.Add("Not connected to the server.");
            }
        }

        /// <summary>Space, E or a click continues a dialogue; number keys pick a menu option.</summary>
        private void ReadDialogueInput(OsrsServerLink.Dialogue d)
        {
            if (!InputAllowed) return;
            bool cont; int pick = -1;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current; var m = Mouse.current;
            if (kb == null) return;
            cont = kb.spaceKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame || (m != null && m.leftButton.wasPressedThisFrame);
            if (kb.digit1Key.wasPressedThisFrame) pick = 1;
            else if (kb.digit2Key.wasPressedThisFrame) pick = 2;
            else if (kb.digit3Key.wasPressedThisFrame) pick = 3;
            else if (kb.digit4Key.wasPressedThisFrame) pick = 4;
            else if (kb.digit5Key.wasPressedThisFrame) pick = 5;
            else if (kb.digit6Key.wasPressedThisFrame) pick = 6;
            else if (kb.digit7Key.wasPressedThisFrame) pick = 7;
            else if (kb.digit8Key.wasPressedThisFrame) pick = 8;
#else
            cont = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E) || Input.GetMouseButtonDown(0);
            for (int i = 1; i <= 8; i++) if (Input.GetKeyDown(KeyCode.Alpha0 + i)) pick = i;
#endif
            if (d.kind == "menu")
            {
                if (pick >= 1 && pick <= d.options.Length) link.Send($"DLGS {pick}");
            }
            else if (cont && !string.IsNullOrEmpty(d.continueText))
                link.Send("DLGC");
        }

        private void DrawDialogue(OsrsServerLink.Dialogue d)
        {
            float w = Mathf.Min(620f, Screen.width - 40f), h = 150f;
            float x = (Screen.width - w) / 2f, y = Screen.height - h - 70f;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.Box(new Rect(x, y, w, h), GUIContent.none);
            GUI.color = Color.white;
            string title = d.title;
            if (string.IsNullOrEmpty(title) && d.kind == "npc" && world.Data.npcs.TryGetValue(d.npcId, out var npc)) title = npc.name;
            if (d.kind == "player" && string.IsNullOrEmpty(title)) title = link.playerName;
            if (!string.IsNullOrEmpty(title))
                Shadowed(new Rect(x + 16, y + 10, w - 32, 22), title, labelStyle, TextAnchor.UpperLeft);
            if (d.kind == "menu")
            {
                for (int i = 0; i < d.options.Length; i++)
                    Shadowed(new Rect(x + 24, y + 38 + i * 20, w - 48, 20), $"{i + 1}.  {d.options[i]}", smallStyle, TextAnchor.UpperLeft);
            }
            else
            {
                var body = new GUIStyle(smallStyle) { wordWrap = true, fontSize = 14 };
                Shadowed(new Rect(x + 16, y + 38, w - 32, h - 70), System.Text.RegularExpressions.Regex.Replace(d.text.Replace("<br>", "\n"), "<[^>]+>", ""), body, TextAnchor.UpperLeft);
                if (!string.IsNullOrEmpty(d.continueText))
                    Shadowed(new Rect(x, y + h - 26, w, 20), "[Space] " + d.continueText, smallStyle, TextAnchor.UpperCenter);
            }
        }

        /// <summary>
        /// Tab locks the camera onto the nearest enemy in view and Tab again releases it;
        /// Shift+Tab cycles targets. A hard mouse flick, Escape or the target dying or
        /// moving far away also releases the lock.
        /// </summary>
        private void ReadLockOnInput()
        {
            if (!InputAllowed) return;
            bool tab, shift, escape;
            float flick;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            tab = kb != null && kb.tabKey.wasPressedThisFrame;
            shift = kb != null && kb.shiftKey.isPressed;
            escape = kb != null && kb.escapeKey.wasPressedThisFrame;
            flick = Mouse.current != null ? Mouse.current.delta.ReadValue().magnitude : 0f;
#else
            tab = Input.GetKeyDown(KeyCode.Tab);
            shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            escape = Input.GetKeyDown(KeyCode.Escape);
            flick = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")).magnitude * 20f;
#endif
            if (lockedNpc != null && (!lockedNpc.isActiveAndEnabled || lockedNpc.hp == 0
                || Vector3.Distance(lockedNpc.transform.position, transform.position) > LockBreakDistance))
                lockedNpc = null;
            if (tab)
                lockedNpc = lockedNpc != null && !shift ? null : NextLockTarget(lockedNpc);
            else if (lockedNpc != null && (escape || (!CursorMode && flick > LockBreakFlick)))
                lockedNpc = null;
            if (lockedNpc != null)
            {
                var fpc = GetComponent<FirstPersonController>();
                var aim = lockedNpc.transform.position + Vector3.up * (lockedNpc.height * 0.6f);
                if (fpc != null) fpc.LookToward(aim, 10f * Time.deltaTime);
            }
        }

        private const float LockBreakDistance = 16f;
        private const float LockBreakFlick = 60f;

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
            if (lockedNpc != null && !CursorMode)
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
            if (CursorMode && (menuOpen || OverHotbar(MouseGuiPos()) || OsrsMinimap.Hit(MouseGuiPos()))) return;
            var ray = CursorMode ? cam.ScreenPointToRay(MouseScreenPos()) : new Ray(cam.transform.position, cam.transform.forward);
            float extra = Vector3.Distance(cam.transform.position, transform.position);
            if (!Physics.Raycast(ray, out var hit, reach + extra, ~(1 << OsrsNpcManager.PlayerLayer)))
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

        private bool InputAllowed => Cursor.lockState == CursorLockMode.Locked || CursorMode;

        /// <summary>E = default option of the target; 1-9/0 = hotbar abilities; Shift+1-5 = raw option.</summary>
        private void ReadInput()
        {
            if (!InputAllowed || menuOpen) return;
            int op = -1, slot = -1;
            bool shift;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (kb.eKey.wasPressedThisFrame) op = FirstOp();
            else
            {
                var digits = new[] { kb.digit1Key, kb.digit2Key, kb.digit3Key, kb.digit4Key, kb.digit5Key,
                                     kb.digit6Key, kb.digit7Key, kb.digit8Key, kb.digit9Key, kb.digit0Key };
                for (int i = 0; i < digits.Length; i++)
                    if (digits[i].wasPressedThisFrame) { slot = i; break; }
            }
#else
            shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Input.GetKeyDown(KeyCode.E)) op = FirstOp();
            else for (int i = 0; i < 10; i++) if (Input.GetKeyDown(i == 9 ? KeyCode.Alpha0 : KeyCode.Alpha1 + i)) { slot = i; break; }
#endif
            if (slot >= 0 && shift && slot < 5) op = slot + 1;
            else if (slot >= 0) { UseAbility(slot); return; }
            if (op < 1 || !hasTarget) return;
            if (targetIsNpc && targetNpc != null) selectedNpc = targetNpc;
            SendOp(targetIsNpc, targetNpc, targetLoc, targetName, targetOps, op);
        }

        /// <summary>Sends one option (1-5) on an NPC or loc after the usual connection/range checks.</summary>
        private void SendOp(bool isNpc, OsrsNpcRef npc, OsrsWorldLoader.Placement loc, string name, string[] ops, int op)
        {
            if (op < 1 || ops == null || op > ops.Length || string.IsNullOrEmpty(ops[op - 1])) return;
            if (isNpc && npc == null) return;
            if (link == null || !link.LoggedIn)
            {
                link?.messages.Add("Not connected to the server.");
                return;
            }
            bool near = isNpc ? NpcInRange(npc) : LocInRange(loc);
            if (!near)
            {
                link.messages.Add($"You need to be next to the {StripLevel(name).ToLowerInvariant()} to do that.");
                return;
            }
            if (StunBlocked()) return;
            if (isNpc)
            {
                link.ShopTitle = npc.displayName;
                link.Send($"OPNPC {npc.index} {op}");
                if (ops[op - 1] == "Attack")
                {
                    engagedNpc = npc.index;
                    lastAimedAtEngaged = Time.time;
                    swingStart = Time.time;
                }
            }
            else
                link.Send($"OPLOC {loc.locId} {loc.x} {loc.z} {loc.level} {op}");
        }

        private float lastStunNotice = -10f;

        /// <summary>True (with a throttled chat notice) while the server has us stunned/frozen.</summary>
        private bool StunBlocked()
        {
            if (link == null || !link.Frozen) return false;
            if (Time.time - lastStunNotice > 1f)
            {
                lastStunNotice = Time.time;
                link.messages.Add("You're stunned!");
            }
            return true;
        }

        /// <summary>OSRS-style stun feedback: birds/stars circling over the view, a pulsing banner and a tint.</summary>
        private void DrawStun()
        {
            if (link == null || !link.Frozen) return;
            float t = Time.time - link.FrozenSince;
            float cx = Screen.width / 2f, top = Screen.height * 0.22f;
            float flash = Mathf.Clamp01(1f - t * 2f);
            GUI.color = new Color(1f, 0.9f, 0.3f, 0.10f + 0.25f * flash);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            const int stars = 5;
            for (int i = 0; i < stars; i++)
            {
                float a = t * 3.2f + i * Mathf.PI * 2f / stars;
                float sx = cx + Mathf.Cos(a) * 70f, sy = top + Mathf.Sin(a) * 16f;
                GUI.color = new Color(1f, 0.95f, 0.3f, Mathf.Sin(a) > 0f ? 1f : 0.55f);
                Shadowed(new Rect(sx - 12, sy - 12, 24, 24), "*", bigStyle);
            }
            float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * 8f);
            GUI.color = new Color(1f, 0.85f, 0.2f, pulse);
            Shadowed(new Rect(cx - 200, top + 22, 400, 30), "Stunned!", bigStyle);
            GUI.color = Color.white;
        }

        private static string StripLevel(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int i = name.IndexOf(" (level ", System.StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : name;
        }

        private Vector3Int MyTile() => link != null ? link.CurrentTile()
            : new Vector3Int(Mathf.FloorToInt(transform.position.x), Mathf.FloorToInt(transform.position.z), 0);

        private bool NpcInRange(OsrsNpcRef npc)
        {
            if (npc == null) return false;
            var me = MyTile();
            int nx = Mathf.FloorToInt(npc.transform.position.x - npc.size * 0.5f);
            int nz = Mathf.FloorToInt(npc.transform.position.z - npc.size * 0.5f);
            return Distance(me.x, me.y, nx, nz, npc.size, npc.size) <= opRange;
        }

        private bool LocInRange(OsrsWorldLoader.Placement pl)
        {
            var me = MyTile();
            return Distance(me.x, me.y, pl.x, pl.z, pl.sizeX, pl.sizeZ) <= opRange;
        }

        // ---------------------------------------------------------------- abilities

        /// <summary>The NPC hotbar abilities act on: hovered, else Tab-locked, else last selected.</summary>
        private OsrsNpcRef AbilityTarget()
        {
            if (hasTarget && targetIsNpc && targetNpc != null && !targetNpc.isPlayer) return targetNpc;
            if (lockedNpc != null) return lockedNpc;
            return selectedNpc;
        }

        private void ValidateSelection()
        {
            if (selectedNpc == null) return;
            if (!selectedNpc.isActiveAndEnabled || selectedNpc.dying || selectedNpc.hp == 0
                || Vector3.Distance(selectedNpc.transform.position, transform.position) > 20f)
                selectedNpc = null;
        }

        private static string NpcLabel(OsrsNpcRef npc) =>
            npc.combatLevel > 0 ? $"{npc.displayName} (level {npc.combatLevel})" : npc.displayName;

        private void UseAbility(int slot)
        {
            var ab = slot >= 0 && slot < OsrsAbilities.Hotbar.Length ? OsrsAbilities.Hotbar[slot] : null;
            if (ab == null || link == null) return;
            var npc = AbilityTarget();
            if (npc == null) { link.messages.Add($"{ab.name}: select a target first."); return; }
            int op = OsrsAbilities.OpIndex(npc.ops, ab.opName);
            if (op < 0) { link.messages.Add($"You can't {ab.name.ToLowerInvariant()} the {npc.displayName.ToLowerInvariant()}."); return; }
            if (abilityReadyAt.TryGetValue(ab.id, out var ready) && Time.time < ready) return;
            if (!link.LoggedIn) { link.messages.Add("Not connected to the server."); return; }
            if (!NpcInRange(npc)) { link.messages.Add($"You need to be next to the {npc.displayName.ToLowerInvariant()} to do that."); return; }
            if (StunBlocked()) return;
            selectedNpc = npc;
            abilityReadyAt[ab.id] = Time.time + ab.cooldown;
            link.ShopTitle = npc.displayName;
            link.Send($"OPNPC {npc.index} {op}");
            if (ab.engages)
            {
                engagedNpc = npc.index;
                lastAimedAtEngaged = Time.time;
                swingStart = Time.time;
            }
        }

        // ---------------------------------------------------------------- cursor mode

        /// <summary>C toggles cursor mode; Escape closes the menu, then leaves cursor mode.</summary>
        private void ReadCursorToggle()
        {
            bool toggle, escape;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            toggle = kb != null && kb.cKey.wasPressedThisFrame;
            escape = kb != null && kb.escapeKey.wasPressedThisFrame;
#else
            toggle = Input.GetKeyDown(KeyCode.C);
            escape = Input.GetKeyDown(KeyCode.Escape);
#endif
            // Something else re-locked the mouse (a panel closing, a click while paused): cursor mode is over.
            if (CursorMode && Cursor.lockState == CursorLockMode.Locked) { CursorMode = false; menuOpen = false; }
            if (CursorMode && escape)
            {
                if (menuOpen) menuOpen = false;
                else SetCursorMode(false, relock: false);
                return;
            }
            if (toggle) SetCursorMode(!CursorMode, relock: true);
        }

        private void SetCursorMode(bool on, bool relock)
        {
            CursorMode = on;
            menuOpen = false;
            attackPressStart = -1f;
            if (on)
            {
                if (blocking) { blocking = false; link?.Send("BLOCK 0"); }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (relock)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private static Vector2 MouseScreenPos()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }

        private static Vector2 MouseGuiPos()
        {
            var p = MouseScreenPos();
            return new Vector2(p.x, Screen.height - p.y);
        }

        /// <summary>Left click = default option (and selects the NPC); right click = option menu.</summary>
        private void ReadCursorClicks()
        {
            if (!CursorMode || menuOpen) return;
            bool left, right;
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m == null) return;
            left = m.leftButton.wasPressedThisFrame;
            right = m.rightButton.wasPressedThisFrame;
#else
            left = Input.GetMouseButtonDown(0);
            right = Input.GetMouseButtonDown(1);
#endif
            if (!left && !right) return;
            var gp = MouseGuiPos();
            if (OverHotbar(gp) || OsrsMinimap.Hit(gp)) return;
            if (!hasTarget)
            {
                if (left) selectedNpc = null;
                return;
            }
            if (targetIsNpc && targetNpc != null && !targetNpc.isPlayer) selectedNpc = targetNpc;
            if (left)
            {
                SendOp(targetIsNpc, targetNpc, targetLoc, targetName, targetOps, FirstOp());
                return;
            }
            menuOpen = true;
            menuPos = gp;
            menuIsNpc = targetIsNpc;
            menuNpc = targetNpc;
            menuLoc = targetLoc;
            menuName = targetName;
            menuOps = targetOps != null ? (string[])targetOps.Clone() : new string[5];
        }

        private Rect HotbarRect()
        {
            int n = OsrsAbilities.Hotbar.Length;
            float w = n * SlotSize + (n - 1) * SlotGap;
            return new Rect((Screen.width - w) / 2f, Screen.height - SlotSize - 10f, w, SlotSize);
        }

        private bool OverHotbar(Vector2 guiPos) => HotbarRect().Contains(guiPos);

        private void DrawMouseover()
        {
            var font = OsrsTitle.Plain != null ? OsrsTitle.Plain : OsrsTitle.Bold;
            if (font == null) return;
            int op = FirstOp();
            string line = op > 0 ? targetOps[op - 1] + " " + targetName : targetName;
            int s = OsrsUi.Scale;
            font.DrawScreen(line, 4 * s, 16 * s, 0xFFFF00, true);
        }

        private static string Fit(OsrsFont font, string text, int width)
        {
            if (text == null || font.Width(text) <= width) return text;
            while (text.Length > 1 && font.Width(text) > width) text = text.Substring(0, text.Length - 1);
            return text;
        }

        public void DrawGameMessages()
        {
            if (link == null || link.CurrentDialogue != null) return;
            var font = OsrsTitle.Plain != null ? OsrsTitle.Plain : OsrsTitle.Bold;
            if (font == null) return;
            var lines = new string[6];
            int n = 0;
            for (int i = link.messages.Count - 1; i >= 0 && n < 6; i--)
            {
                var msg = link.messages[i];
                if (string.IsNullOrEmpty(msg) || msg.StartsWith("Teleported to CoordGrid")) continue;
                lines[n++] = msg;
            }
            int y = 448 - (n - 1) * 14;
            for (int i = n - 1; i >= 0; i--)
            {
                font.Draw(Fit(font, lines[i], 480), 8, y, 0x000000, false, false);
                y += 14;
            }
            bool caret = chatOpen && Mathf.FloorToInt(Time.time * 2f) % 2 == 0;
            string typed = chatOpen ? chatText + (caret ? "|" : "") : "*";
            font.Draw(link.playerName + ": " + typed, 5, 470, 0x000000, false, false);
        }

        private void DrawHotbar()
        {
            if (slotStyle == null)
            {
                slotStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                slotStyle.normal.textColor = Color.white;
                keyStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.UpperLeft };
                keyStyle.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
            }
            var bar = HotbarRect();
            var npc = AbilityTarget();
            bool near = npc != null && NpcInRange(npc);
            for (int i = 0; i < OsrsAbilities.Hotbar.Length; i++)
            {
                var ab = OsrsAbilities.Hotbar[i];
                var r = new Rect(bar.x + i * (SlotSize + SlotGap), bar.y, SlotSize, SlotSize);
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                if (ab != null)
                {
                    bool valid = npc != null && OsrsAbilities.OpIndex(npc.ops, ab.opName) > 0;
                    var c = ab.color;
                    float a = valid ? (near ? 0.95f : 0.6f) : 0.18f;
                    GUI.color = new Color(c.r, c.g, c.b, a);
                    GUI.DrawTexture(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), Texture2D.whiteTexture);
                    if (abilityReadyAt.TryGetValue(ab.id, out var ready) && Time.time < ready)
                    {
                        float frac = Mathf.Clamp01((ready - Time.time) / Mathf.Max(0.01f, ab.cooldown));
                        GUI.color = new Color(0f, 0f, 0f, 0.6f);
                        GUI.DrawTexture(new Rect(r.x + 2, r.y + 2, r.width - 4, (r.height - 4) * frac), Texture2D.whiteTexture);
                    }
                    GUI.color = valid ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                    Shadowed(new Rect(r.x, r.y + 6, r.width, r.height - 6), ab.shortName, slotStyle, TextAnchor.MiddleCenter);
                    if (CursorMode && GUI.Button(r, GUIContent.none, GUIStyle.none)) UseAbility(i);
                }
                GUI.color = Color.white;
                Shadowed(new Rect(r.x + 3, r.y + 1, 20, 14), OsrsAbilities.KeyLabel(i), keyStyle, TextAnchor.UpperLeft);
            }
            GUI.color = Color.white;
            string sel = npc != null ? $"Target: {NpcLabel(npc)}" + (near ? "" : "  (move closer)") : "No target";
            string mode = CursorMode ? "[C] Cursor mode ON - LMB default, RMB options" : "[C] Cursor mode";
            Shadowed(new Rect(bar.x, bar.y - 40, bar.width, 20), sel, smallStyle, TextAnchor.MiddleCenter);
            Shadowed(new Rect(bar.x, bar.y - 22, bar.width, 20), mode, smallStyle, TextAnchor.MiddleCenter);
            if (npc != null && npc != targetNpc)
            {
                var sp = cam.WorldToScreenPoint(npc.transform.position + Vector3.up * (npc.height + 0.5f));
                if (sp.z > 0)
                {
                    GUI.color = new Color(0.4f, 0.9f, 1f, 0.9f);
                    GUI.DrawTexture(new Rect(sp.x - 5, Screen.height - sp.y - 5, 10, 10), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
            }
        }

        /// <summary>The OSRS right-click menu: "Choose Option", every real option by name, Cancel.</summary>
        private void DrawMenu()
        {
            if (menuStyle == null)
            {
                menuStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft, richText = true };
                menuStyle.normal.textColor = Color.white;
                menuStyle.hover.textColor = Color.yellow;
            }
            var entries = new List<(string label, int op)>();
            string nameColor = menuIsNpc ? "#ffff00" : "#00ffff";
            string plain = StripLevel(menuName);
            for (int i = 0; i < menuOps.Length; i++)
                if (!string.IsNullOrEmpty(menuOps[i]))
                    entries.Add(($"{menuOps[i]} <color={nameColor}>{plain}</color>", i + 1));
            entries.Add(("Cancel", 0));
            float rowH = 20f, w = 220f, h = 22f + entries.Count * rowH;
            float x = Mathf.Clamp(menuPos.x - w / 2f, 0, Screen.width - w), y = Mathf.Clamp(menuPos.y - 4f, 0, Screen.height - h);
            var box = new Rect(x, y, w, h);
            var e = Event.current;
            if (e.type == EventType.MouseDown && !box.Contains(e.mousePosition)) { menuOpen = false; e.Use(); return; }
            GUI.color = new Color(0.36f, 0.32f, 0.24f, 0.95f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(x + 1, y + 20, w - 2, h - 21), Texture2D.whiteTexture);
            GUI.color = Color.white;
            Shadowed(new Rect(x + 4, y + 1, w - 8, 18), "Choose Option", smallStyle, TextAnchor.MiddleLeft);
            for (int i = 0; i < entries.Count; i++)
            {
                var r = new Rect(x + 4, y + 21 + i * rowH, w - 8, rowH);
                bool hover = r.Contains(e.mousePosition);
                if (hover)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.12f);
                    GUI.DrawTexture(r, Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                if (GUI.Button(r, entries[i].label, menuStyle))
                {
                    menuOpen = false;
                    if (entries[i].op > 0) SendOp(menuIsNpc, menuNpc, menuLoc, menuName, menuOps, entries[i].op);
                }
            }
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
            int guiDepth = GUI.depth;
            GUI.depth = 20;
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            if (!CursorMode)
            {
                GUI.color = hasTarget ? (inRange ? Color.yellow : Color.white) : new Color(1f, 1f, 1f, 0.7f);
                GUI.Box(new Rect(cx - 3, cy - 3, 6, 6), GUIContent.none);
                GUI.color = Color.white;
            }

            if (hasTarget)
                DrawMouseover();

            DrawStun();
            if (link != null && link.CurrentDialogue != null)
                DrawDialogue(link.CurrentDialogue);
            if (link != null && !OsrsPanels.UiOpen && link.CurrentDialogue == null
                && !OsrsFrame.Showing)
                DrawHotbar();
            if (chatOpen && !OsrsFrame.Showing)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.Box(new Rect(8, Screen.height - 212, 704, 26), GUIContent.none);
                GUI.color = Color.white;
                bool caret = Mathf.FloorToInt(Time.time * 2f) % 2 == 0;
                Shadowed(new Rect(12, Screen.height - 209, 696, 20), "> " + chatText + (caret ? "_" : ""), smallStyle, TextAnchor.MiddleLeft);
            }
            if (link != null)
                {
                if (lockedNpc != null)
                {
                    var sp = cam.WorldToScreenPoint(lockedNpc.transform.position + Vector3.up * (lockedNpc.height * 0.6f));
                    if (sp.z > 0)
                    {
                        GUI.color = new Color(1f, 0.85f, 0.2f);
                        GUI.Box(new Rect(sp.x - 7, Screen.height - sp.y - 7, 14, 14), GUIContent.none);
                        GUI.color = Color.white;
                    }
                    Shadowed(new Rect(cx - 160, cy + 112, 320, 20), "locked on  [Tab] release  [Shift+Tab] next", smallStyle, TextAnchor.UpperCenter);
                }
                if (blocking)
                    Shadowed(new Rect(cx - 100, cy + 90, 200, 24), "Blocking", labelStyle);
                else if (attackPressStart >= 0f && Time.time - attackPressStart >= powerAttackHold)
                    Shadowed(new Rect(cx - 100, cy + 90, 200, 24), "Power attack ready", labelStyle);
                if (!OsrsFrame.Showing) DrawGameMessages();
                // Own hitsplats, stacked under the crosshair like OSRS splats on your character.
                int slotIdx = 0;
                for (int i = link.hitsTaken.Count - 1; i >= 0 && slotIdx < 4; i--)
                {
                    var h = link.hitsTaken[i];
                    float age = Time.time - h.time;
                    if (age > HitsplatTime) continue;
                    DrawHitsplat(new Vector2(cx + (slotIdx % 2 == 0 ? -1 : 1) * (slotIdx > 0 ? 22f : 0f), cy + 70 - (slotIdx / 2) * 26f), h.damage, age, 1.2f);
                    slotIdx++;
                }
                GUI.color = Color.white;
                if (link.LoggedIn && link.Hp == 0 && link.MaxHp > 0)
                {
                    GUI.color = new Color(0f, 0f, 0f, 0.55f);
                    GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none);
                    GUI.color = Color.white;
                    Shadowed(new Rect(0, cy - 40, Screen.width, 40), "Oh dear, you are dead!", bigStyle, TextAnchor.MiddleCenter);
                }
            }

            foreach (var npc in OsrsNpcRef.All)
            {
                if (npc.isPlayer && link != null)
                {
                    float dist = Vector3.Distance(npc.transform.position, cam.transform.position);
                    var head = cam.WorldToScreenPoint(npc.transform.position + Vector3.up * (npc.height + 0.35f));
                    if (head.z > 0 && dist < 25f)
                    {
                        float y = Screen.height - head.y;
                        Shadowed(new Rect(head.x - 100, y - 10, 200, 20), npc.displayName, smallStyle, TextAnchor.MiddleCenter);
                        if (link.overheadChat.TryGetValue(npc.displayName, out var said) && Time.time - said.time < 5f)
                        {
                            var chatStyle = new GUIStyle(smallStyle) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                            chatStyle.normal.textColor = Color.yellow;
                            Shadowed(new Rect(head.x - 200, y - 30, 400, 20), said.text, chatStyle);
                        }
                    }
                }
                if (npc.isPlayer || cam == null) continue;
                float ndist = Vector3.Distance(npc.transform.position, cam.transform.position);
                if (ndist > 30f) continue;
                // OSRS health bar: over the head while in combat (a few seconds after the last hit).
                bool showBar = npc.maxHp > 0 && npc.hp >= 0 && (Time.time - npc.lastHitTime < HealthBarTime || npc.dying);
                var top = cam.WorldToScreenPoint(npc.transform.position + Vector3.up * (npc.height + 0.25f));
                if (top.z <= 0) continue;
                float ty = Screen.height - top.y;
                if (showBar && !(npc.dying && Time.time - npc.deathTime > 0.6f))
                {
                    float bw = Mathf.Clamp(60f * (8f / Mathf.Max(4f, ndist)), 30f, 70f), bh = 6f;
                    float frac = Mathf.Clamp01(npc.hp / (float)npc.maxHp);
                    GUI.color = new Color(0.8f, 0f, 0f, 1f);
                    GUI.DrawTexture(new Rect(top.x - bw / 2f, ty - bh - 2f, bw, bh), Texture2D.whiteTexture);
                    GUI.color = new Color(0f, 0.85f, 0f, 1f);
                    GUI.DrawTexture(new Rect(top.x - bw / 2f, ty - bh - 2f, bw * frac, bh), Texture2D.whiteTexture);
                }
                // Hitsplats on the body: up to 4 at once, OSRS layout (centre, above, left, right).
                int shownSplats = 0;
                var mid = cam.WorldToScreenPoint(npc.transform.position + Vector3.up * (npc.height * 0.55f));
                if (mid.z <= 0) continue;
                for (int i = npc.hits.Count - 1; i >= 0 && shownSplats < 4; i--)
                {
                    var h = npc.hits[i];
                    float age = Time.time - h.time;
                    if (age > HitsplatTime) continue;
                    var off = SplatOffsets[shownSplats];
                    DrawHitsplat(new Vector2(mid.x + off.x, Screen.height - mid.y + off.y), h.damage, age, 1f);
                    shownSplats++;
                }
            }
            GUI.color = Color.white;
            if (menuOpen && CursorMode && !OsrsPanels.UiOpen) DrawMenu();
            else menuOpen = false;
            GUI.depth = guiDepth;
        }

        private const float HitsplatTime = 1.2f;
        private const float HealthBarTime = 6f;
        private static readonly Vector2[] SplatOffsets = { new Vector2(0, 0), new Vector2(0, -22), new Vector2(-22, -11), new Vector2(22, -11) };
        private GUIStyle splatStyle;

        /// <summary>An OSRS hitsplat: red with the damage, or blue "0" for a miss/block.</summary>
        private void DrawHitsplat(Vector2 centre, int damage, float age, float scale)
        {
            if (splatStyle == null)
            {
                splatStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                splatStyle.normal.textColor = Color.white;
            }
            float alpha = age < HitsplatTime - 0.25f ? 1f : Mathf.Clamp01((HitsplatTime - age) / 0.25f);
            float size = 24f * scale;
            var r = new Rect(centre.x - size / 2f, centre.y - size / 2f, size, size);
            GUI.color = new Color(0f, 0f, 0f, 0.6f * alpha);
            GUI.DrawTexture(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), Texture2D.whiteTexture);
            GUI.color = damage > 0 ? new Color(0.75f, 0.05f, 0.05f, alpha) : new Color(0.15f, 0.35f, 0.85f, alpha);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            Shadowed(r, damage.ToString(), splatStyle, TextAnchor.MiddleCenter);
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
