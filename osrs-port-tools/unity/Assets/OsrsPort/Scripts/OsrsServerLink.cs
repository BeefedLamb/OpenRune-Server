using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Connects the first-person player to the local OpenRune server through the osrs-port
    /// "unity-link" listener (127.0.0.1:43610). Logs in with <see cref="playerName"/>, moves to the
    /// tile the server has saved, then reports the player's tile whenever it changes.
    /// If the server isn't running, the game keeps working offline.
    /// </summary>
    public class OsrsServerLink : MonoBehaviour
    {
        public string host = "127.0.0.1";
        public int port = 43610;
        public string playerName = "Lamb";
        public bool showHud = true;

        private OsrsWorldLoader world;
        private TcpClient client;
        private StreamWriter writer;
        private Thread readThread;
        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private volatile bool connected;
        private volatile string status = "Connecting to server...";
        private bool loggedIn;
        private Vector3Int lastSentTile = new Vector3Int(int.MinValue, 0, 0);
        private Vector3Int serverTile;
        private bool hasServerTile;
        private bool pendingGroundSnap;

        private void Start()
        {
            world = FindFirstObjectByType<OsrsWorldLoader>();
            var connectThread = new Thread(Connect) { IsBackground = true, Name = "osrs-link-connect" };
            connectThread.Start();
        }

        private void Connect()
        {
            try
            {
                client = new TcpClient();
                var attempt = client.BeginConnect(host, port, null, null);
                if (!attempt.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3)) || !client.Connected)
                {
                    status = "Server offline - playing without it";
                    return;
                }
                client.EndConnect(attempt);
                client.NoDelay = true;
                var stream = client.GetStream();
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                connected = true;
                status = "Connected, logging in...";
                Send("HELLO " + playerName);
                readThread = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "osrs-link-read" };
                readThread.Start();
            }
            catch (Exception e)
            {
                status = "Server offline - playing without it";
                Debug.Log("OsrsPort: server link unavailable: " + e.Message);
            }
        }

        private void ReadLoop(NetworkStream stream)
        {
            try
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        incoming.Enqueue(line);
                }
            }
            catch (Exception)
            {
            }
            connected = false;
            status = "Disconnected from server";
        }

        private void Send(string line)
        {
            try
            {
                lock (this)
                {
                    writer?.WriteLine(line);
                }
            }
            catch (Exception)
            {
                connected = false;
                status = "Disconnected from server";
            }
        }

        private void Update()
        {
            while (incoming.TryDequeue(out var line))
                Handle(line);

            if (pendingGroundSnap && world != null && world.GroundReadyAt(transform.position))
            {
                pendingGroundSnap = false;
                var probe = transform.position; probe.y = 60f;
                if (Physics.Raycast(probe, Vector3.down, out var hit, 120f))
                {
                    var body = GetComponent<CharacterController>();
                    bool wasEnabled = body != null && body.enabled;
                    if (body != null) body.enabled = false;
                    transform.position = new Vector3(probe.x, hit.point.y + 0.1f, probe.z);
                    if (body != null) body.enabled = wasEnabled;
                }
            }

            if (!connected || !loggedIn || world == null)
                return;

            var tile = CurrentTile();
            if (tile != lastSentTile)
            {
                lastSentTile = tile;
                Send($"POS {tile.x} {tile.y} {tile.z}");
            }
        }

        private Vector3Int CurrentTile()
        {
            var p = transform.position;
            int x = world.BaseX + Mathf.FloorToInt(p.x);
            int z = world.BaseY + Mathf.FloorToInt(p.z);
            return new Vector3Int(x, z, 0);
        }

        private void Handle(string line)
        {
            var parts = line.Split(' ');
            switch (parts[0])
            {
                case "WELCOME":
                    loggedIn = true;
                    status = $"Logged in as {playerName}";
                    if (TryParseTile(parts, out var spawn))
                    {
                        serverTile = spawn;
                        hasServerTile = true;
                        MoveToTile(spawn);
                        lastSentTile = spawn;
                    }
                    break;
                case "TILE":
                    if (TryParseTile(parts, out var tile))
                    {
                        serverTile = tile;
                        hasServerTile = true;
                    }
                    break;
                case "ERROR":
                    status = "Server: " + line.Substring(Math.Min(line.Length, 6));
                    break;
            }
        }

        private static bool TryParseTile(string[] parts, out Vector3Int tile)
        {
            tile = default;
            if (parts.Length < 4) return false;
            if (!int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int z) || !int.TryParse(parts[3], out int level))
                return false;
            tile = new Vector3Int(x, z, level);
            return true;
        }

        private void MoveToTile(Vector3Int tile)
        {
            if (world == null) return;
            var target = new Vector3(tile.x - world.BaseX + 0.5f, 60f, tile.y - world.BaseY + 0.5f);
            if (Physics.Raycast(target, Vector3.down, out var hit, 120f))
                target.y = hit.point.y + 0.1f;
            else
                pendingGroundSnap = true;
            var body = GetComponent<CharacterController>();
            bool wasEnabled = body != null && body.enabled;
            if (body != null) body.enabled = false;
            transform.position = target;
            if (body != null) body.enabled = wasEnabled;
        }

        private void OnGUI()
        {
            if (!showHud) return;
            var text = status;
            if (hasServerTile)
                text += $"\nServer tile: {serverTile.x}, {serverTile.y}, level {serverTile.z}";
            GUI.Label(new Rect(10, 10, 600, 60), text);
        }

        private void OnDestroy()
        {
            if (connected) Send("BYE");
            connected = false;
            try { client?.Close(); } catch (Exception) { }
        }
    }
}
