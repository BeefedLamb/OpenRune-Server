using System.IO;
using UnityEditor;

namespace OsrsPort
{
    [InitializeOnLoad]
    public static class VerifyBoot
    {
        static VerifyBoot()
        {
            EditorApplication.update += Watch;
        }

        public static void Run()
        {
            EditorApplication.update += Enter;
        }

        private static void Watch()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            bool shot = File.Exists(OsrsVerify.ShotPath);
            bool trig = File.Exists(OsrsVerify.TriggerPath);
            if (!shot && !trig) return;
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (shot) FitGameView();
            EditorApplication.EnterPlaymode();
        }

        private static void FitGameView()
        {
            var gv = typeof(Editor).Assembly.GetType("UnityEditor.GameView");
            if (gv == null) return;
            var win = EditorWindow.GetWindow(gv);
            if (win != null) win.maximized = true;
        }

        private static void Enter()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= Enter;
            EditorApplication.EnterPlaymode();
        }
    }
}
