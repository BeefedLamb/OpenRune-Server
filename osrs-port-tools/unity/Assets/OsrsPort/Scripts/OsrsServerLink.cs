using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// Connects the first-person player to the local OpenRune server through the osrs-port
    /// "unity-link" listener (127.0.0.1:43610). Logs in with <see cref="playerName"/>, reports the
    /// player's tile as it changes, sends interactions, and receives chat, health and nearby NPCs.
    /// If the server isn't running, the game keeps working offline.
    /// </summary>
    public class OsrsServerLink : MonoBehaviour
    {
        public struct NpcState
        {
            public int index, id, x, z, level, hp, maxHp;
        }

        public static OsrsServerLink Instance { get; private set; }

        public string host = "127.0.0.1";
        public int port = 43610;
        public string playerName = "Lamb";

        public bool LoggedIn { get; private set; }
        public int Hp { get; private set; } = -1;
        public int MaxHp { get; private set; } = -1;
        public string Status => status;
        public Vector3Int ServerTile => serverTile;
        public bool HasServerTile => hasServerTile;
        public readonly List<string> messages = new List<string>();
        public readonly List<NpcState> npcs = new List<NpcState>();
        public int NpcRevision { get; private set; }

        /// <summary>Damage taken by the player this session, newest last: (time, damage).</summary>
        public readonly List<(float time, int damage)> hitsTaken = new List<(float, int)>();

        private OsrsWorldLoader world;
        private TcpClient client;
        private StreamWriter writer;
        private Thread readThread;
        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private volatile bool connected;
        private volatile string status = "Connecting to server...";
        private Vector3Int lastSentTile = new Vector3Int(int.MinValue, 0, 0);
        private Vector3Int serverTile;
        private bool hasServerTile;
        private bool pendingGroundSnap;

        private void Awake()
        {
            Instance = this;
        }

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

        public bool Send(string line)
        {
            if (!connected) return false;
            try
            {
                lock (this)
                {
                    writer?.WriteLine(line);
                }
                return true;
            }
            catch (Exception)
            {
                connected = false;
                status = "Disconnected from server";
                return false;
            }
        }

        private void Update()
        {
            while (incoming.TryDequeue(out var line))
                Handle(line);

            if (pendingGroundSnap && world != null && world.GroundReadyAt(transform.position))
            {
                pendingGroundSnap = false;
                SnapToGround();
            }

            if (!connected || !LoggedIn || world == null)
                return;

            var tile = CurrentTile();
            if (tile != lastSentTile)
            {
                lastSentTile = tile;
                Send($"POS {tile.x} {tile.y} {tile.z}");
            }
        }

        public Vector3Int CurrentTile()
        {
            var p = transform.position;
            return new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z), 0);
        }

        private void Handle(string line)
        {
            int sp = line.IndexOf(' ');
            string head = sp < 0 ? line : line.Substring(0, sp);
            string rest = sp < 0 ? "" : line.Substring(sp + 1);
            switch (head)
            {
                case "WELCOME":
                    LoggedIn = true;
                    status = $"Logged in as {playerName}";
                    if (TryParseTile(rest, out var spawn))
                    {
                        serverTile = spawn;
                        hasServerTile = true;
                        MoveToTile(spawn);
                        lastSentTile = spawn;
                    }
                    break;
                case "TILE":
                    if (TryParseTile(rest, out var tile))
                    {
                        serverTile = tile;
                        hasServerTile = true;
                        var mine = CurrentTile();
                        if (Mathf.Abs(tile.x - mine.x) > 3 || Mathf.Abs(tile.y - mine.y) > 3)
                        {
                            MoveToTile(tile);
                            lastSentTile = tile;
                        }
                    }
                    break;
                case "MSG":
                    {
                        int sp2 = rest.IndexOf(' ');
                        AddMessage(sp2 < 0 ? rest : rest.Substring(sp2 + 1));
                        break;
                    }
                case "ME":
                    {
                        var p = rest.Split(' ');
                        if (p.Length >= 2 && int.TryParse(p[0], out int hp) && int.TryParse(p[1], out int max))
                        {
                            Hp = hp; MaxHp = max;
                        }
                        break;
                    }
                case "HIT":
                    {
                        var p = rest.Split(' ');
                        if (p.Length >= 3 && p[0] == "me" && int.TryParse(p[2], out int dmg))
                            hitsTaken.Add((Time.time, dmg));
                        break;
                    }
                case "NPCS":
                    ParseNpcs(rest);
                    break;
                case "ERROR":
                    status = "Server: " + rest;
                    AddMessage("Server error: " + rest);
                    break;
            }
        }

        private void AddMessage(string text)
        {
            messages.Add(text);
            if (messages.Count > 60) messages.RemoveAt(0);
        }

        private void ParseNpcs(string rest)
        {
            npcs.Clear();
            var entries = rest.Split(';');
            foreach (var e in entries)
            {
                if (e.Length == 0) continue;
                var f = e.Split(',');
                if (f.Length < 7) continue;
                npcs.Add(new NpcState
                {
                    index = int.Parse(f[0]), id = int.Parse(f[1]), x = int.Parse(f[2]), z = int.Parse(f[3]),
                    level = int.Parse(f[4]), hp = int.Parse(f[5]), maxHp = int.Parse(f[6]),
                });
            }
            NpcRevision++;
        }

        private static bool TryParseTile(string rest, out Vector3Int tile)
        {
            tile = default;
            var parts = rest.Split(' ');
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int z) || !int.TryParse(parts[2], out int level))
                return false;
            tile = new Vector3Int(x, z, level);
            return true;
        }

        private void MoveToTile(Vector3Int tile)
        {
            if (world == null) return;
            var target = new Vector3(tile.x + 0.5f, 60f, tile.y + 0.5f);
            if (Physics.Raycast(target, Vector3.down, out var hit, 120f, OsrsNpcManager.WorldMask))
                target.y = hit.point.y + 0.1f;
            else
                pendingGroundSnap = true;
            Teleport(target);
        }

        private void SnapToGround()
        {
            var probe = transform.position; probe.y = 60f;
            if (Physics.Raycast(probe, Vector3.down, out var hit, 120f, OsrsNpcManager.WorldMask))
                Teleport(new Vector3(probe.x, hit.point.y + 0.1f, probe.z));
        }

        private void Teleport(Vector3 target)
        {
            var body = GetComponent<CharacterController>();
            bool wasEnabled = body != null && body.enabled;
            if (body != null) body.enabled = false;
            transform.position = target;
            if (body != null) body.enabled = wasEnabled;
        }

        private void OnDestroy()
        {
            if (connected) Send("BYE");
            connected = false;
            try { client?.Close(); } catch (Exception) { }
        }
    }
}
