using UnityEditor;

namespace OsrsPort
{
    public static class VerifyBoot
    {
        public static void Run()
        {
            EditorApplication.update += Enter;
        }

        private static void Enter()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= Enter;
            EditorApplication.EnterPlaymode();
        }
    }
}
