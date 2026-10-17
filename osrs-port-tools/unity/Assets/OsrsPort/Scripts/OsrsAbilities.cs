using UnityEngine;

namespace OsrsPort
{
    /// <summary>
    /// One hotbar ability. An NPC ability is valid on a target whose definition has an option with
    /// exactly <see cref="opName"/>; using it sends that option (OPNPC) so the server runs the real
    /// OSRS interaction (combat, thieving, dialogue...). Add new abilities to <see cref="Hotbar"/>.
    /// </summary>
    public class OsrsAbility
    {
        public string id;
        public string name;
        public string shortName;
        /// <summary>The NPC option this ability fires, matched by name against the npc def's op1-op5.</summary>
        public string opName;
        /// <summary>Client-side throttle between uses, seconds (the server still enforces its own timing).</summary>
        public float cooldown;
        public Color color = Color.white;
        /// <summary>Starts the combat engagement tracking (attack-style abilities).</summary>
        public bool engages;
    }

    public static class OsrsAbilities
    {
        public static readonly OsrsAbility Attack = new OsrsAbility
        {
            id = "attack", name = "Attack", shortName = "Attack", opName = "Attack",
            cooldown = 0.6f, color = new Color(0.85f, 0.25f, 0.2f), engages = true,
        };

        public static readonly OsrsAbility Pickpocket = new OsrsAbility
        {
            id = "pickpocket", name = "Pickpocket", shortName = "Pick-\npocket", opName = "Pickpocket",
            cooldown = 1.8f, color = new Color(0.55f, 0.35f, 0.75f),
        };

        public static readonly OsrsAbility Talk = new OsrsAbility
        {
            id = "talk", name = "Talk-to", shortName = "Talk", opName = "Talk-to",
            cooldown = 0.6f, color = new Color(0.9f, 0.8f, 0.3f),
        };

        public static readonly OsrsAbility Trade = new OsrsAbility
        {
            id = "trade", name = "Trade", shortName = "Trade", opName = "Trade",
            cooldown = 0.6f, color = new Color(0.35f, 0.7f, 0.35f),
        };

        /// <summary>Hotbar slots bound to keys 1-9 and 0. Null slots are empty.</summary>
        public static readonly OsrsAbility[] Hotbar = new OsrsAbility[10]
        {
            Attack, Pickpocket, Talk, Trade, null, null, null, null, null, null,
        };

        public static string KeyLabel(int slot) => slot == 9 ? "0" : (slot + 1).ToString();

        /// <summary>1-based op index of the option called <paramref name="opName"/>, or -1.</summary>
        public static int OpIndex(string[] ops, string opName)
        {
            if (ops == null || string.IsNullOrEmpty(opName)) return -1;
            for (int i = 0; i < ops.Length; i++)
                if (string.Equals(ops[i], opName, System.StringComparison.OrdinalIgnoreCase)) return i + 1;
            return -1;
        }
    }
}
