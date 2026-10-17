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
            /// <summary>Set for other players (bots); they draw with a stand-in model and no options.</summary>
            public string playerName;
            /// <summary>Players only: the action seq the server reported this tick (-1 = none).</summary>
            public int anim;
        }

        public static OsrsServerLink Instance { get; private set; }

        public string host = "127.0.0.1";
        public int port = 43610;
        public string playerName = "Lamb";

        public bool LoggedIn { get; private set; }
        public int Hp { get; private set; } = -1;
        public int MaxHp { get; private set; } = -1;
        /// <summary>Special attack energy in tenths of a percent (0-1000).</summary>
        public int SpecEnergy { get; private set; } = -1;
        /// <summary>Time of the player's last attack animation, for first-person swing feedback.</summary>
        public float LastAnimTime { get; private set; } = -10f;
        public int LastAnimId { get; private set; } = -1;
        public string Status => status;
        public Vector3Int ServerTile => serverTile;
        public bool HasServerTile => hasServerTile;
        public readonly List<string> messages = new List<string>();
        public readonly List<NpcState> npcs = new List<NpcState>();

        /// <summary>Latest public chat line per speaker, for overhead text: name -> (text, time).</summary>
        public readonly Dictionary<string, (string text, float time)> overheadChat = new Dictionary<string, (string, float)>();

        public class Dialogue
        {
            public string kind, title, text, continueText;
            public int npcId;
            public string[] options;
        }

        /// <summary>The chatbox dialogue currently open on the server, or null.</summary>
        public Dialogue CurrentDialogue { get; private set; }

        public class Item
        {
            public int slot, id, count;
            public string name;
            public string[] ops;
        }

        public class Stat
        {
            public int id, level, baseLevel, xp;
            public string name;
        }

        public readonly List<Item> inventory = new List<Item>();
        public readonly List<Item> equipment = new List<Item>();
        public readonly List<Stat> stats = new List<Stat>();

        /// <summary>Bank contents (from `BANK`), only meaningful while <see cref="BankOpen"/>.</summary>
        public readonly List<Item> bank = new List<Item>();
        /// <summary>True between the server's `BANKOPEN` and `BANKCLOSE`.</summary>
        public bool BankOpen { get; private set; }
        /// <summary>Withdraw-as-note mode (`BANKNOTE`).</summary>
        public bool BankNote { get; private set; }

        /// <summary>One shop stock entry from `SHOP` (price = current single-item buy price).</summary>
        public class ShopItem
        {
            public int slot, id, count, price;
            public string name;
        }

        /// <summary>Shop stock (from `SHOP`), only meaningful while <see cref="ShopOpen"/>.</summary>
        public readonly List<ShopItem> shop = new List<ShopItem>();
        /// <summary>True between the server's `SHOPOPEN` and `SHOPCLOSE`.</summary>
        public bool ShopOpen { get; private set; }
        /// <summary>The shop's currency name (e.g. "coins"), from the first `SHOP` field.</summary>
        public string ShopCurrency { get; private set; } = "coins";
        /// <summary>Name of the NPC we last traded with, used as the shop title.</summary>
        public string ShopTitle = "Shop";

        /// <summary>True between the server's `FROZEN 1` and `FROZEN 0` (pickpocket stun, binds).</summary>
        public bool Frozen { get; private set; }
        /// <summary>Time.time when the current stun/freeze began.</summary>
        public float FrozenSince { get; private set; } = -100f;

        /// <summary>A ground item the server says we can see (from `GITEMS`).</summary>
        public class GroundItem
        {
            public int id, count, x, z, level;
            public string name;
        }

        /// <summary>Visible ground items on our floor nearby; replaced whole on every `GITEMS` line.</summary>
        public readonly List<GroundItem> groundItems = new List<GroundItem>();
        public int GroundRevision { get; private set; }
        public int InventoryRevision { get; private set; }

        /// <summary>One spell from `SPELLS` (id = spell obj id, also its icon id).</summary>
        public class Spell
        {
            public int id, level;
            public string name, type;
            /// <summary>'s' self-cast, 'i' backpack-item target, 'n' npc/combat, 'x' unavailable.</summary>
            public char target;
            public bool levelOk, runesOk;
            public readonly List<(int id, int count)> runes = new List<(int, int)>();
        }

        /// <summary>Current spellbook's spells (from `SPELLS`), re-sent by the server on change.</summary>
        public readonly List<Spell> spells = new List<Spell>();
        /// <summary>standard / ancients / lunars / arceuus.</summary>
        public string SpellbookName { get; private set; } = "";

        /// <summary>Minigame HUD title from `MGOVERLAY` ("" = hidden).</summary>
        public string MinigameTitle { get; private set; } = "";
        /// <summary>Minigame HUD rows from `MGOVERLAY`, in server order.</summary>
        public readonly List<(string key, string value)> minigameValues = new List<(string, string)>();
        /// <summary>Time.time of the last `MGOVERLAY` line.</summary>
        public float MinigameUpdatedAt { get; private set; } = -1000f;
        public int NpcRevision { get; private set; }

        /// <summary>Damage taken by the player this session, newest last: (time, damage).</summary>
        public readonly List<(float time, int damage)> hitsTaken = new List<(float, int)>();
        /// <summary>True once the server sent a per-hit `HIT npc` line (then hp-diff hitsplats are off).</summary>
        public bool ServerNpcHits { get; private set; }
        /// <summary>Last time the player hit or was hit, for the "in combat" HUD.</summary>
        public float LastCombatTime { get; private set; } = -100f;

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
        private int currentLevel = 0;

        /// <summary>The plane (0-3) the server last placed us on.</summary>
        public int CurrentLevel => currentLevel;
        private bool pendingGroundSnap;

        private void Awake()
        {
            Instance = this;
        }

        // ---- Character Design (Tutorial Island style) state, fed by KITS / LOOK / CREATE / CREATED ----

        /// <summary>Legal body-part ids per slot (0 hair .. 6 feet) for bodyType 0 (male) and 1 (female).</summary>
        public readonly int[][][] kits = { new int[7][], new int[7][] };
        /// <summary>Latest look from the server: bodyType, 7 parts, 5 colours (13 ints), or null.</summary>
        public int[] Look { get; private set; }
        /// <summary>True once the server asked us to open the design screen (new account).</summary>
        public bool CreatePending { get; private set; }
        /// <summary>Set when the server confirmed the design (CREATED).</summary>
        public bool CreateDone { get; private set; }
        public bool ConnectStarted { get; private set; }

        /// <summary>Our own resolved body from `PLOOK me` (bodyType, 5 colours, 12 slots), or null.</summary>
        public int[] SelfLook { get; private set; }
        /// <summary>Other players' resolved bodies by NpcState index (PlayerIndexOffset + slot).</summary>
        public readonly Dictionary<int, int[]> playerLooks = new Dictionary<int, int[]>();
        /// <summary>Bumped whenever any PLOOK arrives.</summary>
        public int LookRevision { get; private set; }
        /// <summary>Our base animation set from BAS me (ready, turn, walkF, walkB, walkL, walkR, run), or null.</summary>
        public int[] SelfBas { get; private set; }
        /// <summary>Other players' base animation sets by NpcState index.</summary>
        public readonly Dictionary<int, int[]> playerBas = new Dictionary<int, int[]>();

        private void Start()
        {
            world = FindAnyObjectByType<OsrsWorldLoader>();
            // With the login/design screen present, it calls BeginConnect once a name is entered.
            if (GetComponent<OsrsCharacterDesign>() == null)
                BeginConnect(playerName);
        }

        /// <summary>Connects and logs in as <paramref name="name"/> (blank keeps the default name).</summary>
        public void BeginConnect(string name)
        {
            if (ConnectStarted) return;
            ConnectStarted = true;
            if (!string.IsNullOrWhiteSpace(name)) playerName = name.Trim();
            status = "Connecting to server...";
            var connectThread = new Thread(Connect) { IsBackground = true, Name = "osrs-link-connect" };
            connectThread.Start();
        }

        /// <summary>Sends the chosen look (13 ints) to the server.</summary>
        public bool SendAppearance(int[] look)
        {
            if (look == null || look.Length != 13) return false;
            return Send("APPEAR " + string.Join(" ", look));
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
            return new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z), currentLevel);
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
                        currentLevel = spawn.z;
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
                        if (tile.z != currentLevel || Mathf.Abs(tile.x - mine.x) > 3 || Mathf.Abs(tile.y - mine.y) > 3)
                        {
                            currentLevel = tile.z;
                            MoveToTile(tile);
                            lastSentTile = tile;
                        }
                    }
                    break;
                case "FORCEMOVE":
                    // Server-driven movement (agility obstacles, scripted steps): always follow,
                    // even for the short moves TILE ignores.
                    if (TryParseTile(rest, out var forced))
                    {
                        serverTile = forced;
                        hasServerTile = true;
                        currentLevel = forced.z;
                        MoveToTile(forced);
                        lastSentTile = forced;
                    }
                    break;
                case "MSG":
                    {
                        int sp2 = rest.IndexOf(' ');
                        int.TryParse(sp2 < 0 ? rest : rest.Substring(0, sp2), out int msgType);
                        string msgText = sp2 < 0 ? rest : rest.Substring(sp2 + 1);
                        if (msgType == 2)
                        {
                            int colon = msgText.IndexOf(": ");
                            if (colon > 0) overheadChat[msgText.Substring(0, colon)] = (msgText.Substring(colon + 2), Time.time);
                        }
                        if (msgType != 114 && msgType != 115)
                            AddMessage(msgText);
                        break;
                    }
                case "ME":
                    {
                        var p = rest.Split(' ');
                        if (p.Length >= 2 && int.TryParse(p[0], out int hp) && int.TryParse(p[1], out int max))
                        {
                            Hp = hp; MaxHp = max;
                        }
                        if (p.Length >= 3 && int.TryParse(p[2], out int spec)) SpecEnergy = spec;
                        break;
                    }
                case "ANIM":
                    if (int.TryParse(rest, out int anim)) { LastAnimId = anim; LastAnimTime = Time.time; if (OsrsAnimator.Local != null) OsrsAnimator.Local.Play(anim); }
                    break;
                case "NANIM":
                    {
                        var p = rest.Split(' ');
                        if (p.Length >= 2 && int.TryParse(p[0], out int idx) && int.TryParse(p[1], out int seq))
                            foreach (var npc in OsrsNpcRef.All)
                                if (npc.index == idx) { npc.lastAnimTime = Time.time; npc.lastAnimId = seq; var an = npc.GetComponent<OsrsAnimator>(); if (an != null) an.Play(seq); }
                        break;
                    }
                case "HIT":
                    {
                        // HIT me 0 damage | HIT npc index damage mine
                        var p = rest.Split(' ');
                        if (p.Length >= 3 && int.TryParse(p[2], out int dmg))
                        {
                            if (p[0] == "me")
                            {
                                hitsTaken.Add((Time.time, dmg));
                                if (hitsTaken.Count > 16) hitsTaken.RemoveAt(0);
                                LastCombatTime = Time.time;
                            }
                            else if (p[0] == "npc" && int.TryParse(p[1], out int hidx))
                            {
                                ServerNpcHits = true;
                                bool mine = p.Length >= 4 && p[3] == "1";
                                if (mine) LastCombatTime = Time.time;
                                foreach (var npc in OsrsNpcRef.All)
                                    if (npc.index == hidx && !npc.isPlayer) npc.RecordHit(dmg);
                            }
                        }
                        break;
                    }
                case "NDIE":
                    if (int.TryParse(rest, out int didx))
                        foreach (var npc in OsrsNpcRef.All)
                            if (npc.index == didx && !npc.isPlayer) npc.MarkDying();
                    break;
                case "NPCS":
                    ParseNpcs(rest);
                    break;
                case "PLAYERS":
                    ParsePlayers(rest);
                    break;
                case "LOC":
                    HandleLoc(rest);
                    break;
                case "DLG":
                    HandleDialogue(rest);
                    break;
                case "INV":
                    ParseItems(rest, inventory);
                    break;
                case "WORN":
                    ParseItems(rest, equipment);
                    break;
                case "BANKOPEN":
                    BankOpen = true;
                    bank.Clear();
                    break;
                case "BANKCLOSE":
                    BankOpen = false;
                    bank.Clear();
                    break;
                case "BANK":
                    ParseItems(rest, bank);
                    break;
                case "BANKNOTE":
                    BankNote = rest.Trim() == "1";
                    break;
                case "SHOPOPEN":
                    ShopOpen = true;
                    shop.Clear();
                    break;
                case "SHOPCLOSE":
                    ShopOpen = false;
                    shop.Clear();
                    break;
                case "SHOP":
                    ParseShop(rest);
                    break;
                case "SPELLS":
                    ParseSpells(rest);
                    break;
                case "PATCH":
                    world?.Patch(rest);
                    break;
                case "HOUSE":
                    world?.SetHouse(rest);
                    break;
                case "HLOCS":
                    world?.SetHouseLocs(rest);
                    break;
                case "MGOVERLAY":
                    ParseMinigameOverlay(rest);
                    break;
                case "FROZEN":
                    {
                        bool f = rest.Trim() == "1";
                        if (f && !Frozen) FrozenSince = Time.time;
                        Frozen = f;
                        break;
                    }
                case "GITEMS":
                    ParseGroundItems(rest);
                    break;
                case "STATS":
                    ParseStats(rest);
                    break;
                case "KITS":
                    {
                        var p = rest.Split(' ');
                        if (p.Length >= 3 && int.TryParse(p[0], out int slot) && slot >= 0 && slot < 7)
                        {
                            kits[0][slot] = ParseIdList(p[1]);
                            kits[1][slot] = ParseIdList(p[2]);
                        }
                        break;
                    }
                case "LOOK":
                    {
                        var p = rest.Split(' ');
                        if (p.Length >= 13)
                        {
                            var look = new int[13];
                            bool ok = true;
                            for (int i = 0; i < 13; i++) ok &= int.TryParse(p[i], out look[i]);
                            if (ok) Look = look;
                        }
                        break;
                    }
                case "BAS":
                    {
                        // BAS key ready turn walkF walkB walkL walkR run (seq ids, -1 none)
                        var p = rest.Split(' ');
                        if (p.Length < 8) break;
                        var bas = new int[7];
                        bool ok = true;
                        for (int i = 0; i < 7; i++) ok &= int.TryParse(p[1 + i], out bas[i]);
                        if (!ok) break;
                        if (p[0] == "me") SelfBas = bas;
                        else if (int.TryParse(p[0], out int bslot)) playerBas[PlayerIndexOffset + bslot] = bas;
                        break;
                    }
                case "PLOOK":
                    {
                        // PLOOK key bodyType c0..c4 s0..s11 (slot: -1 none, 256+kit, 512+item)
                        var p = rest.Split(' ');
                        if (p.Length < 1 + OsrsPlayerModels.LookLength) break;
                        var look = new int[OsrsPlayerModels.LookLength];
                        bool ok = true;
                        for (int i = 0; i < look.Length; i++) ok &= int.TryParse(p[1 + i], out look[i]);
                        if (!ok) break;
                        if (p[0] == "me") SelfLook = look;
                        else if (int.TryParse(p[0], out int lslot)) playerLooks[PlayerIndexOffset + lslot] = look;
                        else break;
                        LookRevision++;
                        break;
                    }
                case "CREATE":
                    if (rest.Length > 0) playerName = rest;
                    CreatePending = true;
                    break;
                case "CREATED":
                    CreatePending = false;
                    CreateDone = true;
                    break;
                case "ERROR":
                    status = "Server: " + rest;
                    AddMessage("Server error: " + rest);
                    break;
            }
        }

        private void ParseItems(string rest, List<Item> into)
        {
            into.Clear();
            foreach (var e in rest.Split(';'))
            {
                if (e.Length == 0) continue;
                var f = e.Split('|');
                if (f.Length < 5) continue;
                into.Add(new Item
                {
                    slot = int.Parse(f[0]), id = int.Parse(f[1]), count = int.Parse(f[2]), name = f[3],
                    ops = f[4].Split('/'),
                });
            }
            InventoryRevision++;
        }

        /// <summary>`SPELLS book;objId|name|level|type|target|levelOk|runesOk|runes;...` (runes = id:count,... or -).</summary>
        private void ParseSpells(string rest)
        {
            spells.Clear();
            var entries = rest.Split(';');
            SpellbookName = entries.Length > 0 ? entries[0].Trim() : "";
            for (int i = 1; i < entries.Length; i++)
            {
                var f = entries[i].Split('|');
                if (f.Length < 8) continue;
                if (!int.TryParse(f[0], out int id) || !int.TryParse(f[2], out int level)) continue;
                var sp = new Spell
                {
                    id = id, name = f[1], level = level, type = f[3],
                    target = f[4].Length > 0 ? f[4][0] : 'x',
                    levelOk = f[5] == "1", runesOk = f[6] == "1",
                };
                if (f[7] != "-")
                {
                    foreach (var r in f[7].Split(','))
                    {
                        var rc = r.Split(':');
                        if (rc.Length == 2 && int.TryParse(rc[0], out int rid) && int.TryParse(rc[1], out int cnt))
                            sp.runes.Add((rid, cnt));
                    }
                }
                spells.Add(sp);
            }
        }

        /// <summary>`MGOVERLAY title;key|value;...`, or bare `MGOVERLAY` to hide the HUD.</summary>
        private void ParseMinigameOverlay(string rest)
        {
            minigameValues.Clear();
            var entries = rest.Split(';');
            MinigameTitle = entries.Length > 0 ? entries[0].Trim() : "";
            for (int i = 1; i < entries.Length; i++)
            {
                int bar = entries[i].IndexOf('|');
                if (bar < 0) continue;
                minigameValues.Add((entries[i].Substring(0, bar), entries[i].Substring(bar + 1)));
            }
            MinigameUpdatedAt = Time.time;
        }

        private void ParseShop(string rest)
        {
            shop.Clear();
            var entries = rest.Split(';');
            if (entries.Length > 0 && entries[0].Length > 0) ShopCurrency = entries[0].Replace('_', ' ');
            for (int i = 1; i < entries.Length; i++)
            {
                var f = entries[i].Split('|');
                if (f.Length < 5) continue;
                if (!int.TryParse(f[0], out int slot) || !int.TryParse(f[1], out int id)
                    || !int.TryParse(f[2], out int count) || !int.TryParse(f[4], out int price)) continue;
                shop.Add(new ShopItem { slot = slot, id = id, count = count, name = f[3], price = price });
            }
            InventoryRevision++;
        }

        private void ParseGroundItems(string rest)
        {
            groundItems.Clear();
            foreach (var e in rest.Split(';'))
            {
                if (e.Length == 0) continue;
                var f = e.Split('|');
                if (f.Length < 6) continue;
                if (!int.TryParse(f[0], out int id) || !int.TryParse(f[1], out int count) || !int.TryParse(f[2], out int x)
                    || !int.TryParse(f[3], out int z) || !int.TryParse(f[4], out int level)) continue;
                groundItems.Add(new GroundItem { id = id, count = count, x = x, z = z, level = level, name = f[5] });
            }
            GroundRevision++;
        }

        private void ParseStats(string rest)
        {
            stats.Clear();
            foreach (var e in rest.Split(';'))
            {
                if (e.Length == 0) continue;
                var f = e.Split('|');
                if (f.Length < 5) continue;
                stats.Add(new Stat
                {
                    id = int.Parse(f[0]), name = f[1], level = int.Parse(f[2]), baseLevel = int.Parse(f[3]),
                    xp = int.Parse(f[4]),
                });
            }
        }

        private void HandleDialogue(string rest)
        {
            if (rest == "close") { CurrentDialogue = null; return; }
            var p = rest.Split('|');
            if (p.Length < 5) return;
            var d = new Dialogue { kind = p[0], title = p[1], text = p[2], continueText = p[3] };
            int.TryParse(p[4], out d.npcId);
            d.options = new string[p.Length - 5];
            for (int i = 5; i < p.Length; i++) d.options[i - 5] = p[i];
            CurrentDialogue = d;
        }

        private void HandleLoc(string rest)
        {
            if (world == null) return;
            var p = rest.Split(' ');
            try
            {
                if (p[0] == "add" && p.Length >= 7)
                    world.LocAdd(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]), int.Parse(p[6]));
                else if (p[0] == "del" && p.Length >= 6)
                    world.LocDel(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]));
            }
            catch (Exception e)
            {
                Debug.LogWarning("OsrsPort: bad LOC line '" + rest + "': " + e.Message);
            }
        }

        public void AddMessage(string text)
        {
            messages.Add(text);
            if (messages.Count > 60) messages.RemoveAt(0);
        }

        /// <summary>Other players near us; slots are offset so they never collide with NPC indices.</summary>
        public const int PlayerIndexOffset = 1000000;
        public const int PlayerAvatarNpcId = 3106;
        private readonly List<NpcState> players = new List<NpcState>();

        private void ParsePlayers(string rest)
        {
            players.Clear();
            foreach (var e in rest.Split(';'))
            {
                if (e.Length == 0) continue;
                var f = e.Split('|');
                if (f.Length < 7) continue;
                players.Add(new NpcState
                {
                    index = PlayerIndexOffset + int.Parse(f[0]), id = PlayerAvatarNpcId, playerName = f[1],
                    x = int.Parse(f[2]), z = int.Parse(f[3]), level = int.Parse(f[4]), hp = int.Parse(f[5]), maxHp = int.Parse(f[6]),
                    anim = f.Length >= 8 && int.TryParse(f[7], out int pa) ? pa : -1,
                });
            }
            MergeNpcLists();
        }

        private readonly List<NpcState> npcOnly = new List<NpcState>();

        private void MergeNpcLists()
        {
            npcs.Clear();
            npcs.AddRange(npcOnly);
            npcs.AddRange(players);
            NpcRevision++;
        }

        private void ParseNpcs(string rest)
        {
            npcOnly.Clear();
            var entries = rest.Split(';');
            foreach (var e in entries)
            {
                if (e.Length == 0) continue;
                var f = e.Split(',');
                if (f.Length < 7) continue;
                npcOnly.Add(new NpcState
                {
                    index = int.Parse(f[0]), id = int.Parse(f[1]), x = int.Parse(f[2]), z = int.Parse(f[3]),
                    level = int.Parse(f[4]), hp = int.Parse(f[5]), maxHp = int.Parse(f[6]),
                    anim = f.Length >= 8 && int.TryParse(f[7], out int pa) ? pa : -1,
                });
            }
            MergeNpcLists();
        }

        private static int[] ParseIdList(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "-") return new int[0];
            var list = new List<int>();
            foreach (var e in s.Split(','))
                if (int.TryParse(e, out int v)) list.Add(v);
            return list.ToArray();
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
            var terrain = world.TerrainHeight(target.x, target.z, tile.z);
            if (terrain.HasValue)
                target.y = terrain.Value + 0.1f;
            else
                pendingGroundSnap = true;
            Teleport(target);
        }

        private void SnapToGround()
        {
            var probe = transform.position;
            var terrain = world.TerrainHeight(probe.x, probe.z, currentLevel);
            if (terrain.HasValue)
                Teleport(new Vector3(probe.x, terrain.Value + 0.1f, probe.z));
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
