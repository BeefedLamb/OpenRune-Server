using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace OsrsPort
{
    /// <summary>
    /// Play-mode check for open-door collision, Tab lock-on and synth playback.
    /// Runs only when the editor was launched with <c>-osrsVerify</c>.
    /// </summary>
    public class OsrsVerify : MonoBehaviour
    {
        public const string Flag = "-osrsVerify";
        public const string ResultPath = @"E:\osrs\verify-result.txt";
        public const string TriggerPath = @"E:\osrs\run-verify.txt";
        public const string ShotPath = @"E:\osrs\run-shot.txt";

        private static string shotFile;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            shotFile = null;
            if (File.Exists(ShotPath))
            {
                try { shotFile = File.ReadAllText(ShotPath).Trim(); } catch { shotFile = ""; }
                try { File.Delete(ShotPath); } catch { }
                if (string.IsNullOrEmpty(shotFile)) shotFile = @"E:\osrs\arms-shot.png";
            }
            bool armed = shotFile != null || Array.IndexOf(Environment.GetCommandLineArgs(), Flag) >= 0 || File.Exists(TriggerPath);
            if (!armed) return;
            try { if (File.Exists(TriggerPath)) File.Delete(TriggerPath); } catch { }
            var go = new GameObject("OsrsVerify");
            DontDestroyOnLoad(go);
            go.AddComponent<OsrsVerify>();
        }

        private readonly List<string> log = new List<string>();
        private int msgCursor;

        private void Start()
        {
            StartCoroutine(shotFile != null ? Shot() : Run());
        }

        /// <summary>
        /// Login, wear a sword and shield, and save one first-person frame of the Lumbridge fountain.
        /// The png path is the contents of <see cref="ShotPath"/>.
        /// </summary>
        private IEnumerator Shot()
        {
            Note("shot start " + shotFile);
            var link = OsrsServerLink.Instance;
            float until = Time.realtimeSinceStartup + 90f;
            while (link == null && Time.realtimeSinceStartup < until)
            {
                link = OsrsServerLink.Instance;
                yield return null;
            }
            if (link == null)
            {
                Finish("FAIL no OsrsServerLink");
                yield break;
            }
            var design = link.GetComponent<OsrsCharacterDesign>();
            until = Time.realtimeSinceStartup + 45f;
            while (OsrsCharacterDesign.Active && Time.realtimeSinceStartup < until)
            {
                if (design != null) design.AutoSubmitForVerify();
                yield return null;
            }
            until = Time.realtimeSinceStartup + 30f;
            while (!link.LoggedIn && Time.realtimeSinceStartup < until) yield return null;
            if (!link.LoggedIn)
            {
                Finish("FAIL not logged in");
                yield break;
            }
            yield return Wear(link, "bronze_sword", "sword", 3);
            yield return Wear(link, "wooden_shield", "shield", 5);
            yield return Arrive(link, 3222, 3212, "courtyard");
            var fpc = link.GetComponent<FirstPersonController>();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var aim = new Vector3(3222.5f, 2.2f, 3218.5f);
            if (fpc != null) fpc.LookToward(aim, 1f);
            yield return null;
            if (fpc != null) fpc.LookToward(aim, 1f);
            yield return new WaitForEndOfFrame();
            if (fpc != null) Note("look yaw=" + link.transform.eulerAngles.y.ToString("0") + " pitch=" + fpc.GetType().GetField("pitch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(fpc));
            var cam = Camera.main;
            if (cam == null)
            {
                Finish("FAIL no camera");
                yield break;
            }
            var rt = new RenderTexture(1280, 720, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            cam.targetTexture = prev;
            RenderTexture.active = null;
            File.WriteAllBytes(shotFile, tex.EncodeToPNG());
            Destroy(tex);
            Destroy(rt);
            Note("wrote " + shotFile);
            Finish("shot done");
        }

        private IEnumerator Wear(OsrsServerLink link, string spawn, string namePart, int wearSlot)
        {
            foreach (var it in link.equipment)
                if (it.slot == wearSlot && it.name != null && it.name.IndexOf(namePart, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Note("already worn " + it.name + " slot " + wearSlot);
                    yield break;
                }
            int before = link.messages.Count;
            link.Send("CHAT ::item " + spawn);
            OsrsServerLink.Item held = null;
            float until = Time.realtimeSinceStartup + 4f;
            while (held == null && Time.realtimeSinceStartup < until)
            {
                foreach (var it in link.inventory)
                    if (it.name != null && it.name.IndexOf(namePart, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        held = it;
                yield return null;
            }
            if (held == null)
            {
                DumpNew(link, before);
                Note("FAIL no " + spawn);
                yield break;
            }
            for (int op = 1; op <= 3 && !Wearing(link, wearSlot); op++)
            {
                link.Send("OPHELD " + held.slot + " " + op);
                float wait = Time.realtimeSinceStartup + 1.5f;
                while (!Wearing(link, wearSlot) && Time.realtimeSinceStartup < wait) yield return null;
            }
            string worn = "?";
            foreach (var it in link.equipment)
                if (it.slot == wearSlot) worn = it.name + " id=" + it.id;
            Note("wear " + wearSlot + " " + worn);
        }

        private static bool Wearing(OsrsServerLink link, int wearSlot)
        {
            foreach (var it in link.equipment)
                if (it.slot == wearSlot) return true;
            return false;
        }

        private IEnumerator Run()
        {
            Note("verify start");
            var link = OsrsServerLink.Instance;
            float until = Time.realtimeSinceStartup + 90f;
            while (link == null && Time.realtimeSinceStartup < until)
            {
                link = OsrsServerLink.Instance;
                yield return null;
            }
            if (link == null)
            {
                Finish("FAIL no OsrsServerLink");
                yield break;
            }

            var design = link.GetComponent<OsrsCharacterDesign>();
            until = Time.realtimeSinceStartup + 45f;
            while (OsrsCharacterDesign.Active && Time.realtimeSinceStartup < until)
            {
                if (design != null) design.AutoSubmitForVerify();
                yield return null;
            }
            until = Time.realtimeSinceStartup + 30f;
            while (!link.LoggedIn && Time.realtimeSinceStartup < until) yield return null;
            Note("loggedIn=" + link.LoggedIn + " status=" + link.Status + " designActive=" + OsrsCharacterDesign.Active);
            if (!link.LoggedIn)
            {
                Finish("FAIL not logged in");
                yield break;
            }

            var body = link.GetComponent<CharacterController>();
            Note("capsule radius=" + (body != null ? body.radius.ToString("0.00") : "none")
                + " height=" + (body != null ? body.height.ToString("0.00") : "none"));

            yield return TestDoor(link, body, 1535, 3226, 3214, 3225, 3214, "single 3226,3214");
            yield return TestDoor(link, body, 12348, 3207, 3217, 3208, 3217, "castle west 3207,3217");
            yield return TestDoor(link, body, 12349, 3213, 3221, 3212, 3221, "castle large 3213,3221");

            yield return TestLockOn(link);
            yield return TestSound(link);
            Finish("done");
        }

        private IEnumerator TestDoor(OsrsServerLink link, CharacterController body, int locId, int x, int z, int standX, int standZ, string label)
        {
            Note("--- door " + label + " ---");
            if (body == null)
            {
                Note("SKIP no capsule");
                yield break;
            }
            int before = link.messages.Count;
            yield return Arrive(link, standX, standZ, "stood");
            if (!arrived)
            {
                Note("FAIL " + label + " never reached the stand tile");
                DumpNew(link, before);
                yield break;
            }
            DumpNew(link, before);

            OsrsLocRef door = null;
            float until = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < until)
            {
                door = FindLoc(locId, x, z);
                if (door != null) break;
                yield return null;
            }
            if (door == null)
            {
                Note("FAIL loc " + locId + " at " + x + "," + z + " never spawned");
                yield break;
            }
            var col = door.GetComponent<MeshCollider>();
            string ops = door.def != null ? string.Join(",", door.def.ops) : "";
            bool hadCol = col != null && col.enabled;
            var closedBounds = hadCol ? col.bounds : new Bounds();
            Note("closed name=" + door.name + " ops=[" + ops + "] collider=" + hadCol
                + (hadCol ? " bounds=" + closedBounds.size : ""));
            bool blockedClosed = false;
            if (hadCol)
            {
                blockedClosed = !Cross(body, closedBounds, out var detail);
                Note("closed cross " + detail);
            }
            else Note("closed has no collider (already open, or not solid)");

            var beside = link.transform.position;
            int dist = Mathf.Max(Mathf.Abs(Mathf.FloorToInt(beside.x) - x), Mathf.Abs(Mathf.FloorToInt(beside.z) - z));
            Note("beside door dist=" + dist + " at " + Mathf.FloorToInt(beside.x) + "," + Mathf.FloorToInt(beside.z));
            if (dist > 1)
            {
                before = link.messages.Count;
                yield return Arrive(link, standX, standZ, "back");
                if (!arrived)
                {
                    Note("FAIL " + label + " not back beside the door");
                    DumpNew(link, before);
                    yield break;
                }
                DumpNew(link, before);
            }
            yield return new WaitForSeconds(0.4f);

            before = link.messages.Count;
            int plays = OsrsSound.Ensure().Plays;
            link.Send("OPLOC " + locId + " " + x + " " + z + " 0 1");
            until = Time.realtimeSinceStartup + 8f;
            bool heard = false;
            while (Time.realtimeSinceStartup < until)
            {
                if (OsrsSound.Instance != null && OsrsSound.Instance.Plays > plays) heard = true;
                var now = FindLoc(locId, x, z);
                if (now == null || now.GetComponent<MeshCollider>() == null) break;
                yield return null;
            }
            yield return new WaitForSeconds(0.6f);
            if (OsrsSound.Instance != null && OsrsSound.Instance.Plays > plays) heard = true;
            var after = FindLoc(locId, x, z);
            var afterCol = after != null ? after.GetComponent<MeshCollider>() : null;
            Note("after open present=" + (after != null) + " collider=" + (afterCol != null && afterCol.enabled)
                + " sound=" + heard + " lastSynth=" + (OsrsSound.Instance != null ? OsrsSound.Instance.LastSynth.ToString() : "-"));
            DumpNew(link, before);

            bool blockedOpen = true;
            if (hadCol)
            {
                blockedOpen = !Cross(body, closedBounds, out var detail);
                Note("open cross " + detail);
            }
            bool pass = hadCol && blockedClosed && !blockedOpen;
            Note(pass ? "PASS " + label : "FAIL " + label + " blockedClosed=" + blockedClosed + " blockedOpen=" + blockedOpen);
        }

        private static OsrsLocRef FindLoc(int locId, int x, int z)
        {
            var all = FindObjectsByType<OsrsLocRef>(FindObjectsSortMode.None);
            foreach (var loc in all)
                if (loc.locId == locId && loc.x == x && loc.z == z && loc.level == 0) return loc;
            return null;
        }

        private bool arrived;

        /// <summary>Teleport, and try once more if the move has not landed. Sets <see cref="arrived"/>.</summary>
        private IEnumerator Arrive(OsrsServerLink link, int x, int z, string label)
        {
            arrived = false;
            for (int attempt = 0; attempt < 2 && !arrived; attempt++)
            {
                link.Send("CHAT ::tele " + x + " " + z);
                float until = Time.realtimeSinceStartup + 8f;
                while (Time.realtimeSinceStartup < until)
                {
                    var p = link.transform.position;
                    if (Mathf.FloorToInt(p.x) == x && Mathf.FloorToInt(p.z) == z)
                    {
                        arrived = true;
                        Note(label + " " + x + "," + z + " y=" + p.y.ToString("0.00"));
                        yield break;
                    }
                    yield return null;
                }
            }
            var at = link.transform.position;
            Note(label + " MISSED stood " + Mathf.FloorToInt(at.x) + "," + Mathf.FloorToInt(at.z)
                + " y=" + at.y.ToString("0.00") + " want " + x + "," + z);
        }

        /// <summary>
        /// Walks the capsule through <paramref name="bounds"/> along its thin axis, starting a
        /// clear tile before the slab and using the ground height at that point.
        /// </summary>
        private static bool Cross(CharacterController body, Bounds bounds, out string detail)
        {
            bool alongX = bounds.size.x <= bounds.size.z;
            var start = bounds.center;
            Vector3 dir;
            float travel;
            if (alongX)
            {
                start.x = bounds.min.x - 0.9f;
                dir = Vector3.right;
                travel = bounds.size.x + 1.8f;
            }
            else
            {
                start.z = bounds.min.z - 0.9f;
                dir = Vector3.forward;
                travel = bounds.size.z + 1.8f;
            }
            var world = OsrsWorldLoader.Instance;
            var ground = world != null ? world.TerrainHeight(start.x, start.z, 0) : null;
            start.y = (ground ?? body.transform.position.y) + 0.1f;
            var fpc = body.GetComponent<FirstPersonController>();
            if (fpc != null) fpc.enabled = false;
            var watch = body.gameObject.AddComponent<HitWatch>();
            body.enabled = false;
            body.transform.position = start;
            body.enabled = true;
            var before = body.transform.position;
            float moved = 0f;
            while (moved < travel)
            {
                body.Move(dir * 0.05f);
                moved += 0.05f;
            }
            var after = body.transform.position;
            string hit = watch.first;
            UnityEngine.Object.Destroy(watch);
            if (fpc != null) fpc.enabled = true;
            float progress = alongX ? after.x - before.x : after.z - before.z;
            bool crossed = alongX ? after.x > bounds.center.x + 0.05f : after.z > bounds.center.z + 0.05f;
            detail = "axis=" + (alongX ? "x" : "z") + " y=" + start.y.ToString("0.00")
                + " progress=" + progress.ToString("0.00")
                + " need>" + (alongX ? bounds.center.x - before.x : bounds.center.z - before.z).ToString("0.00")
                + " end=" + after.x.ToString("0.00") + "," + after.z.ToString("0.00")
                + " hit=" + (hit ?? "-") + " crossed=" + crossed;
            return crossed;
        }

        private sealed class HitWatch : MonoBehaviour
        {
            public string first;

            private void OnControllerColliderHit(ControllerColliderHit hit)
            {
                if (first == null && hit != null && hit.collider != null) first = hit.collider.name;
            }
        }

        private IEnumerator TestLockOn(OsrsServerLink link)
        {
            Note("--- lock-on ---");
            int before = link.messages.Count;
            link.Send("CHAT ::tele 3222 3218");
            float until = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < until)
            {
                var p = link.transform.position;
                if (Mathf.FloorToInt(p.x) == 3222 && Mathf.FloorToInt(p.z) == 3218) break;
                yield return null;
            }
            link.Send("CHAT ::npc 400 goblin");
            yield return new WaitForSeconds(0.4f);
            link.Send("CHAT ::right 3");
            yield return new WaitForSeconds(0.8f);
            link.Send("CHAT ::npc 400 goblin");
            yield return new WaitForSeconds(1.2f);
            DumpNew(link, before);

            var npcs = new List<OsrsNpcRef>();
            foreach (var npc in OsrsNpcRef.All)
                if (npc != null && npc.isActiveAndEnabled && npc.ops != null && Array.IndexOf(npc.ops, "Attack") >= 0
                    && Vector3.Distance(npc.transform.position, link.transform.position) < 14f)
                    npcs.Add(npc);
            Note("attackable nearby=" + npcs.Count);
            if (npcs.Count == 0)
            {
                Note("FAIL no attackable npc");
                yield break;
            }

            var fpc = link.GetComponent<FirstPersonController>();
            var inter = link.GetComponent<OsrsInteraction>();
            var field = typeof(OsrsInteraction).GetField("lockedNpc", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fpc != null) fpc.LookToward(npcs[0].transform.position + Vector3.up, 1f);
            Cursor.lockState = CursorLockMode.Locked;

            yield return Press(Key.Tab);
            var locked = field != null ? field.GetValue(inter) as OsrsNpcRef : null;
            Note("tab lock=" + (locked != null ? locked.displayName : "null"));
            bool tabOn = locked != null;

            yield return Press(Key.Tab, Key.LeftShift);
            var next = field != null ? field.GetValue(inter) as OsrsNpcRef : null;
            Note("shift-tab lock=" + (next != null ? next.displayName : "null") + " changed=" + (next != locked && next != null));

            yield return Press(Key.Escape);
            var released = field != null ? field.GetValue(inter) as OsrsNpcRef : null;
            Note("escape lock=" + (released != null ? released.displayName : "null"));

            if (fpc != null && npcs.Count > 0) fpc.LookToward(npcs[0].transform.position + Vector3.up, 1f);
            yield return Press(Key.Tab);
            yield return Flick();
            var flicked = field != null ? field.GetValue(inter) as OsrsNpcRef : null;
            Note("flick lock=" + (flicked != null ? flicked.displayName : "null"));

            bool pass = tabOn && released == null && flicked == null;
            Note(pass ? "PASS lock-on" : "FAIL lock-on");
        }

        private static IEnumerator Press(params Key[] keys)
        {
            yield return new WaitForEndOfFrame();
            var kb = Keyboard.current;
            if (kb == null)
            {
                Debug.Log("OsrsVerify: no keyboard device");
                yield break;
            }
            InputSystem.QueueStateEvent(kb, new KeyboardState(keys));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            yield return null;
        }

        private static IEnumerator Flick()
        {
            yield return new WaitForEndOfFrame();
            var mouse = Mouse.current;
            if (mouse == null) yield break;
            InputSystem.QueueStateEvent(mouse, new MouseState { delta = new Vector2(800f, 0f) });
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState());
            yield return null;
        }

        private IEnumerator TestSound(OsrsServerLink link)
        {
            Note("--- sound ---");
            var sound = OsrsSound.Ensure();
            int beforePlays = sound.Plays;
            int before = link.messages.Count;
            link.Send("CHAT ::synth door_open");
            float until = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < until && sound.Plays == beforePlays) yield return null;
            DumpNew(link, before);
            Note("plays " + beforePlays + " -> " + sound.Plays + " last=" + sound.LastSynth);
            DescribeClip(sound, sound.LastSynth);
            DescribeClip(sound, 62);
            bool pass = sound.Plays > beforePlays;
            var clip = sound.Clip(sound.LastSynth);
            if (clip != null)
            {
                var data = new float[Mathf.Min(clip.samples, 22050)];
                clip.GetData(data, 0);
                float peak = 0f;
                for (int i = 0; i < data.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));
                Note("playback peak=" + peak.ToString("0.000"));
                if (peak < 0.01f) pass = false;
            }
            else if (sound.Plays > beforePlays) pass = false;
            Note(pass ? "PASS sound data" : "FAIL sound data");
            Note("cannot judge how it sounds from here");
        }

        private void DescribeClip(OsrsSound sound, int synth)
        {
            var clip = sound.Clip(synth);
            if (clip == null)
            {
                Note("clip " + synth + " missing");
                return;
            }
            int n = Mathf.Min(clip.samples, 44100);
            var data = new float[n];
            clip.GetData(data, 0);
            float peak = 0f, sum = 0f;
            int zeros = 0;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.Abs(data[i]);
                if (a > peak) peak = a;
                sum += data[i] * data[i];
                if (a < 0.0001f) zeros++;
            }
            float rms = Mathf.Sqrt(sum / Mathf.Max(1, n));
            Note("clip " + synth + " samples=" + clip.samples + " sec=" + clip.length.ToString("0.00")
                + " peak=" + peak.ToString("0.000") + " rms=" + rms.ToString("0.000")
                + " silentFrac=" + ((float)zeros / n).ToString("0.00"));
        }

        private void DumpNew(OsrsServerLink link, int from)
        {
            for (int i = from; i < link.messages.Count; i++)
                Note("msg: " + link.messages[i]);
            msgCursor = link.messages.Count;
        }

        private void Note(string line)
        {
            log.Add(line);
            Debug.Log("OsrsVerify: " + line);
        }

        private void Finish(string tail)
        {
            Note(tail);
            try
            {
                var sb = new StringBuilder();
                foreach (var line in log) sb.AppendLine(line);
                File.WriteAllText(ResultPath, sb.ToString());
            }
            catch (Exception e)
            {
                Debug.LogError("OsrsVerify write failed: " + e.Message);
            }
            Application.Quit();
        }
    }
}
