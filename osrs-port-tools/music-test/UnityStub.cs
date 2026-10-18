using System;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static void DontDestroyOnLoad(Object o) { }
        public static T FindFirstObjectByType<T>() where T : class => null;
        public static T FindAnyObjectByType<T>() where T : class => null;
        public T AddComponent<T>() where T : class, new() => new T();
        public T GetComponent<T>() where T : class => null;
    }

    public class Component : Object
    {
        public GameObject gameObject;
    }

    public class Behaviour : Component
    {
        public bool isActiveAndEnabled = true;
    }

    public class MonoBehaviour : Behaviour
    {
        public Transform transform = new Transform();
        public void StartCoroutine(System.Collections.IEnumerator routine) { }
    }

    public class GameObject : Object
    {
        public Transform transform = new Transform();
        public GameObject(string n) { name = n; }
        public new T AddComponent<T>() where T : class, new() => new T();
    }

    public class Transform : Component
    {
        public Vector3 position;
        public void SetParent(Transform parent, bool worldPositionStays) { }
    }

    public struct Vector3
    {
        public float x, y, z;
        public static Vector3 zero;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public class Camera : Behaviour
    {
        public static Camera main;
    }

    public class AudioClip
    {
        public float length;
        public static AudioClip Create(string name, int length, int channels, int rate, bool stream) => new AudioClip();
        public static AudioClip Create(string name, int length, int channels, int rate, bool stream, Action<float[]> read) => new AudioClip();
        public bool SetData(float[] data, int offset) => true;
    }

    public class WaitForSeconds
    {
        public WaitForSeconds(float seconds) { }
    }

    public enum AudioRolloffMode { Linear }

    public class AudioSource : Behaviour
    {
        public AudioClip clip;
        public bool loop;
        public float volume = 1f;
        public float spatialBlend;
        public float minDistance;
        public float maxDistance;
        public float dopplerLevel;
        public bool playOnAwake;
        public AudioRolloffMode rolloffMode;
        public Transform transform = new Transform();
        public bool isPlaying;
        public void Stop() { }
        public void Play() { }
        public void PlayDelayed(float delay) { }
    }

    public class AudioListener : Behaviour { }

    public static class Debug
    {
        public static void Log(object m) => Console.WriteLine(m);
        public static void LogWarning(object m) => Console.WriteLine(m);
    }

    public static class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
    }

    public class Application
    {
        public static string streamingAssetsPath;
    }

    public class RangeAttribute : Attribute
    {
        public RangeAttribute(float min, float max) { }
    }
}
