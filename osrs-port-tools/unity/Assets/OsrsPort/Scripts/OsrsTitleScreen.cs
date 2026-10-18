using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Draws the title before the world loads. The world starts when Login is pressed.
    /// </summary>
    public class OsrsTitleScreen : MonoBehaviour
    {
        public static bool Released { get; private set; }
        public static string PendingName { get; private set; }
        public static bool Showing => !OsrsCharacterDesign.Active;

        private string nameInput = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindAnyObjectByType<OsrsTitleScreen>() != null) return;
            new GameObject("OsrsTitleScreen").AddComponent<OsrsTitleScreen>();
        }

        /// <summary>Start the world load with this login name. Used by the play-mode check.</summary>
        public static void Release(string name)
        {
            if (Released) return;
            PendingName = string.IsNullOrWhiteSpace(name) ? "Lamb" : name.Trim();
            Released = true;
            if (FindAnyObjectByType<OsrsWorldLoader>() != null) return;
            if (!System.IO.File.Exists(System.IO.Path.Combine(Application.streamingAssetsPath, "world", "index.bin"))) return;
            new GameObject("OsrsWorld").AddComponent<OsrsWorldLoader>();
        }

        private void OnGUI()
        {
            if (!Showing) return;
            OsrsTitle.Draw();
            bool caret = ((Time.frameCount / 20) & 1) == 0;
            OsrsTitle.DrawLogin(nameInput, caret, "Enter your username.", null);
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Backspace)
                {
                    if (nameInput.Length > 0) nameInput = nameInput.Substring(0, nameInput.Length - 1);
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.Use();
                    Release(Sanitise(nameInput));
                }
                else if (e.character >= 32 && nameInput.Length < 12 && Allowed(e.character))
                {
                    nameInput += e.character;
                    e.Use();
                }
            }
            if (e.type == EventType.MouseDown && e.button == 0 && OsrsTitle.LoginHit())
            {
                e.Use();
                Release(Sanitise(nameInput));
            }
        }

        private static bool Allowed(char c) => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-';

        private static string Sanitise(string raw)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (raw ?? "").Trim())
                if (Allowed(c)) sb.Append(c);
            var s = sb.ToString().Trim();
            if (s.Length > 12) s = s.Substring(0, 12).Trim();
            return s.Length == 0 ? "Lamb" : s;
        }
    }
}
