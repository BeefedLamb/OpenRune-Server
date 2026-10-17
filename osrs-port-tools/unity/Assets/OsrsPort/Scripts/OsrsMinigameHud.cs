using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Generic minigame status HUD (top-right), driven by the server's `MGOVERLAY title;key|value;...`
    /// lines from the Wintertodt, Tempoross, Giants' Foundry and Guardians of the Rift scripts. Shown
    /// only while a minigame is reporting: a bare `MGOVERLAY` hides it, and it fades out after
    /// <see cref="HideAfterSeconds"/> without an update (you walked away).
    /// </summary>
    public class OsrsMinigameHud : MonoBehaviour
    {
        public float HideAfterSeconds = 90f;

        private OsrsServerLink link;
        private GUIStyle titleStyle, keyStyle, valueStyle;

        private void Start()
        {
            link = GetComponent<OsrsServerLink>();
        }

        private void OnGUI()
        {
            if (link == null || string.IsNullOrEmpty(link.MinigameTitle) || link.minigameValues.Count == 0) return;
            if (OsrsCharacterDesign.Active) return;
            float age = Time.time - link.MinigameUpdatedAt;
            if (age > HideAfterSeconds) return;
            if (titleStyle == null)
            {
                titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                titleStyle.normal.textColor = new Color(1f, 0.6f, 0.12f);
                keyStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                keyStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
                valueStyle.normal.textColor = Color.white;
            }
            float fade = Mathf.Clamp01((HideAfterSeconds - age) / 5f);
            float w = 230f, rowH = 18f;
            var box = new Rect(Screen.width - w - 12, 12, w, 30 + link.minigameValues.Count * rowH + 6);
            GUI.color = new Color(0.12f, 0.09f, 0.06f, 0.85f * fade);
            GUI.Box(box, GUIContent.none);
            GUI.color = new Color(1f, 1f, 1f, fade);
            GUI.Label(new Rect(box.x, box.y + 4, w, 22), link.MinigameTitle, titleStyle);
            for (int i = 0; i < link.minigameValues.Count; i++)
            {
                var (k, v) = link.minigameValues[i];
                var r = new Rect(box.x + 10, box.y + 28 + i * rowH, w - 20, rowH);
                GUI.Label(r, k, keyStyle);
                GUI.Label(r, v, valueStyle);
            }
            GUI.color = Color.white;
        }
    }
}
