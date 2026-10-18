using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Login name entry plus OSRS Tutorial Island style "Character Design" screen.
    /// Flow: type a name -> connect (HELLO name) -> if the server sends CREATE, design the look
    /// (gender, 7 body parts from the server's KITS lists, 5 colours) -> APPEAR -> CREATED -> world.
    /// Returning accounts (LOOK without CREATE) go straight into the world.
    /// </summary>
    public class OsrsCharacterDesign : MonoBehaviour
    {
        private enum Stage { EnterName, Connecting, Design, Submitting, Done }

        /// <summary>True while the login/design screen owns the mouse and keyboard.</summary>
        public static bool Active { get; private set; }

        private static readonly string[] SlotNames = { "Head", "Jaw", "Torso", "Arms", "Hands", "Legs", "Feet" };
        private static readonly string[] ColourNames = { "Hair", "Torso", "Legs", "Feet", "Skin" };
        /// <summary>Exclusive upper bound per colour in the UI (skin limited to the 8 standard tones).</summary>
        private static readonly int[] ColourCounts = { 25, 29, 29, 6, 8 };
        private const float ReturningAccountWait = 0.5f;
        private const float NoDesignSupportWait = 5f;
        private const float SubmitTimeout = 10f;

        private Stage stage = Stage.EnterName;
        private OsrsServerLink link;
        private string nameInput = "";
        private float stageTime;
        private int[] look; // 13 ints: bodyType, 7 parts, 5 colours
        private GUIStyle titleStyle, labelStyle, valueStyle, buttonStyle, fieldStyle, smallStyle;

        private void Awake()
        {
            Active = true;
        }

        private void Start()
        {
            link = GetComponent<OsrsServerLink>();
            if (link == null) { Finish(); return; }
            if (!string.IsNullOrEmpty(OsrsTitleScreen.PendingName))
            {
                nameInput = OsrsTitleScreen.PendingName;
                SubmitName();
            }
            else nameInput = link.playerName ?? "";
        }

        private void SetStage(Stage s)
        {
            stage = s;
            stageTime = Time.time;
        }

        private void Finish()
        {
            SetStage(Stage.Done);
            Active = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void OnDestroy()
        {
            Active = false;
        }

        /// <summary>Login and confirm the default look. Used by the automated play-mode check only.</summary>
        public void AutoSubmitForVerify()
        {
            if (stage == Stage.EnterName) SubmitName();
            else if (stage == Stage.Design && look != null && link.SendAppearance((int[])look.Clone()))
                SetStage(Stage.Submitting);
        }

        private void Update()
        {
            if (stage == Stage.Done) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (stage == Stage.Connecting)
            {
                if (link.CreatePending && HasKits())
                    BeginDesign();
                else if (link.LoggedIn && !link.CreatePending && link.Look != null && Time.time - stageTime > ReturningAccountWait)
                    Finish(); // returning account with a saved look
                else if (link.LoggedIn && !link.CreatePending && Time.time - stageTime > NoDesignSupportWait)
                    Finish(); // server without character design support
            }
            else if (stage == Stage.Submitting && link.CreateDone)
            {
                Finish();
            }
        }

        private bool HasKits()
        {
            for (int s = 0; s < 7; s++)
                if (link.kits[0][s] == null || link.kits[1][s] == null) return false;
            return true;
        }

        private static string SanitiseName(string raw)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (raw ?? "").Trim())
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-') sb.Append(c);
            var s = sb.ToString().Trim();
            return s.Length > 12 ? s.Substring(0, 12).Trim() : s;
        }

        private void SubmitName()
        {
            var name = SanitiseName(nameInput);
            if (name.Length == 0) name = SanitiseName(link.playerName);
            if (name.Length == 0) name = "Lamb";
            nameInput = name;
            link.BeginConnect(name);
            SetStage(Stage.Connecting);
        }

        private void BeginDesign()
        {
            look = link.Look != null && link.Look.Length == 13 ? (int[])link.Look.Clone() : new int[13];
            look[0] = look[0] == 1 ? 1 : 0;
            for (int s = 0; s < 7; s++) EnsureLegal(s);
            for (int c = 0; c < 5; c++) look[8 + c] = Mathf.Clamp(look[8 + c], 0, ColourCounts[c] - 1);
            SetStage(Stage.Design);
        }

        private int[] Kits(int slot) => link.kits[look[0]][slot] ?? new int[0];

        /// <summary>Resets a part to the current gender's first legal id if it isn't legal (empty list: keep, server keeps current).</summary>
        private void EnsureLegal(int slot)
        {
            var ids = Kits(slot);
            if (ids.Length == 0) return;
            if (System.Array.IndexOf(ids, look[1 + slot]) < 0) look[1 + slot] = ids[0];
        }

        private void SetGender(int bodyType)
        {
            if (look[0] == bodyType) return;
            look[0] = bodyType;
            for (int s = 0; s < 7; s++) EnsureLegal(s);
        }

        private void CyclePart(int slot, int dir)
        {
            var ids = Kits(slot);
            if (ids.Length == 0) return;
            int i = System.Array.IndexOf(ids, look[1 + slot]);
            if (i < 0) i = 0;
            i = ((i + dir) % ids.Length + ids.Length) % ids.Length;
            look[1 + slot] = ids[i];
        }

        private void CycleColour(int c, int dir)
        {
            int n = ColourCounts[c];
            look[8 + c] = ((look[8 + c] + dir) % n + n) % n;
        }

        private static bool EnterPressed()
        {
            var e = Event.current;
            return e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
        }

        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            titleStyle.normal.textColor = new Color(1f, 0.6f, 0.12f);
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
            labelStyle.normal.textColor = Color.white;
            valueStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleCenter };
            valueStyle.normal.textColor = new Color(1f, 1f, 0.4f);
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            smallStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15 };
            fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
        }

        private void OnGUI()
        {
            if (stage == Stage.Done || link == null) return;
            EnsureStyles();
            OsrsTitle.Draw();

            switch (stage)
            {
                case Stage.EnterName: DrawEnterName(); break;
                case Stage.Connecting: DrawConnecting(); break;
                case Stage.Design: DrawDesign(false); break;
                case Stage.Submitting: DrawDesign(true); break;
            }
        }

        private static Rect Panel(float w, float h)
        {
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.color = new Color(0.24f, 0.19f, 0.13f, 1f);
            GUI.Box(r, GUIContent.none);
            GUI.color = Color.white;
            return r;
        }

        private void DrawEnterName()
        {
            bool caret = ((Time.frameCount / 20) & 1) == 0;
            OsrsTitle.DrawLogin(nameInput, caret, "Enter your username.", null);
            if (EnterPressed() || (Event.current.type == EventType.MouseDown && OsrsTitle.LoginHit()))
            {
                Event.current.Use();
                SubmitName();
            }
        }

        private void DrawConnecting()
        {
            OsrsTitle.DrawLogin(nameInput, false, "Enter your username.", link.Status);
            string st = link.Status ?? "";
            bool failed = !link.LoggedIn && (st.Contains("offline") || st.Contains("Disconnected") || st.StartsWith("Server:"));
            if (failed && Event.current.type == EventType.MouseDown && OsrsTitle.LoginHit())
            {
                Event.current.Use();
                Finish();
            }
        }

        private void DrawDesign(bool submitting)
        {
            var r = Panel(560, 560);
            GUI.Label(new Rect(r.x, r.y + 10, r.width, 30), "Character Design", titleStyle);
            GUI.Label(new Rect(r.x, r.y + 40, r.width, 20), "Welcome, " + link.playerName + ". Design your character.", smallStyle);
            GUI.enabled = !submitting;

            float y = r.y + 72;
            GUI.Label(new Rect(r.x + 30, y, 120, 28), "Gender", labelStyle);
            if (GUI.Toggle(new Rect(r.x + 200, y, 100, 28), look[0] == 0, "Male", buttonStyle)) SetGender(0);
            if (GUI.Toggle(new Rect(r.x + 310, y, 100, 28), look[0] == 1, "Female", buttonStyle)) SetGender(1);

            float colX = r.x + 30, colW = 240;
            y = r.y + 118;
            GUI.Label(new Rect(colX, y, colW, 22), "Design", valueStyle);
            for (int s = 0; s < 7; s++)
            {
                float ry = y + 28 + s * 38;
                var ids = Kits(s);
                bool has = ids.Length > 0;
                GUI.Label(new Rect(colX + 40, ry, 80, 30), SlotNames[s], labelStyle);
                GUI.enabled = !submitting && has;
                if (GUI.Button(new Rect(colX, ry, 34, 30), "<", buttonStyle)) CyclePart(s, -1);
                if (GUI.Button(new Rect(colX + colW - 34, ry, 34, 30), ">", buttonStyle)) CyclePart(s, 1);
                GUI.enabled = !submitting;
                int idx = System.Array.IndexOf(ids, look[1 + s]);
                GUI.Label(new Rect(colX + 120, ry, colW - 160, 30), has ? $"{idx + 1}/{ids.Length}" : "n/a", valueStyle);
            }

            float cx = r.x + 290, cw = 240;
            GUI.Label(new Rect(cx, y, cw, 22), "Colour", valueStyle);
            for (int c = 0; c < 5; c++)
            {
                float ry = y + 28 + c * 52;
                int max = ColourCounts[c] - 1;
                GUI.Label(new Rect(cx + 40, ry, 80, 26), ColourNames[c], labelStyle);
                if (GUI.Button(new Rect(cx, ry, 34, 26), "<", buttonStyle)) CycleColour(c, -1);
                if (GUI.Button(new Rect(cx + cw - 34, ry, 34, 26), ">", buttonStyle)) CycleColour(c, 1);
                GUI.Label(new Rect(cx + 120, ry, 80, 26), $"{look[8 + c] + 1}/{ColourCounts[c]}", valueStyle);
                float f = GUI.HorizontalSlider(new Rect(cx + 40, ry + 30, cw - 80, 16), look[8 + c], 0, max);
                look[8 + c] = Mathf.Clamp(Mathf.RoundToInt(f), 0, max);
            }

            GUI.Label(new Rect(r.x + 20, r.yMax - 96, r.width - 40, 36),
                "No character preview yet (first-person client). Numbers are style/colour choices.", smallStyle);
            if (GUI.Button(new Rect(r.x + 190, r.yMax - 56, r.width - 380, 40), submitting ? "Saving..." : "Confirm", buttonStyle))
            {
                if (link.SendAppearance((int[])look.Clone())) SetStage(Stage.Submitting);
            }
            GUI.enabled = true;

            if (submitting && Time.time - stageTime > SubmitTimeout)
            {
                GUI.Label(new Rect(r.x, r.yMax - 120, r.width, 20), "No reply from server.", smallStyle);
                if (GUI.Button(new Rect(r.x + 20, r.yMax - 56, 150, 40), "Retry", buttonStyle)) SetStage(Stage.Design);
                if (GUI.Button(new Rect(r.xMax - 170, r.yMax - 56, 150, 40), "Enter world", buttonStyle)) Finish();
            }
        }
    }
}
