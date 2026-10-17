package org.rsmod.content.skills.hunter

/**
 * Hunter data. Levels/XP are OSRS wiki values typed by hand (the cache has no hunter data
 * tables on this revision). Every rscm name below is validated at startup; any entry whose
 * names don't resolve is skipped and logged, so nothing here can crash boot.
 */
data class Loot(val obj: String, val min: Int = 1, val max: Int = min)

/** A creature that can wander into a laid/set trap. */
data class TrapCreature(
    val name: String,
    val level: Int,
    val xp: Double,
    /** npc rscm names that must be near the trap for a catch to be possible. */
    val npcs: List<String>,
    /** loc shown while the creature is caught. */
    val fullLoc: String,
    val loot: List<Loot>,
)

enum class TrapKind { BIRD_SNARE, BOX_TRAP, NET_TRAP }

data class LaidTrap(
    val kind: TrapKind,
    /** Item that is laid (bird snare / box trap). */
    val item: String,
    val setLoc: String,
    val failedLoc: String,
    val creatures: List<TrapCreature>,
)

/** Young-tree net traps: the tree loc itself is converted while the trap is set. */
data class NetTrapTree(
    val upLoc: String,
    val setLoc: String,
    val failedLoc: String,
    val creature: TrapCreature,
)

/** NPCs caught directly with a butterfly net into a jar (implings, butterflies). */
data class NetCatch(
    val name: String,
    val npc: String,
    val level: Int,
    val xp: Double,
    val emptyJar: String,
    val filledJar: String,
    /** Respawn delay after a catch. */
    val respawn: Int = 50,
)

object HunterTables {
    const val BONES = "obj.bones"

    val birdSnare =
        LaidTrap(
            kind = TrapKind.BIRD_SNARE,
            item = "obj.hunting_ojibway_bird_snare",
            setLoc = "loc.hunting_ojibway_trap",
            failedLoc = "loc.hunting_ojibway_trap_broken",
            creatures =
                listOf(
                    bird("Crimson swift", 1, 34.0, "jungle", "obj.hunting_jungle_feather"),
                    bird("Golden warbler", 5, 47.0, "desert", "obj.hunting_desert_feather"),
                    bird("Copper longtail", 9, 61.2, "woodland", "obj.hunting_woodland_feather"),
                    bird("Cerulean twitch", 11, 64.67, "polar", "obj.hunting_polar_feather"),
                    bird("Tropical wagtail", 19, 95.2, "coloured", "obj.hunting_stripy_bird_feather"),
                ),
        )

    private fun bird(name: String, level: Int, xp: Double, area: String, feather: String) =
        TrapCreature(
            name = name,
            level = level,
            xp = xp,
            npcs = listOf(if (area == "coloured") "npc.multicoloured_bird" else "npc.hunting_bird_$area"),
            fullLoc = "loc.hunting_ojibway_trap_full_$area",
            loot = listOf(Loot(BONES), Loot("obj.spit_raw_bird_meat"), Loot(feather, 5, 10)),
        )

    val boxTrap =
        LaidTrap(
            kind = TrapKind.BOX_TRAP,
            item = "obj.hunting_box_trap",
            setLoc = "loc.hunting_boxtrap_empty",
            failedLoc = "loc.hunting_boxtrap_failed",
            creatures =
                listOf(
                    TrapCreature("Ferret", 27, 115.0, listOf("npc.hunting_ferret"),
                        "loc.hunting_boxtrap_full_ferret", listOf(Loot("obj.hunting_ferret"))),
                    TrapCreature("Chinchompa", 53, 198.25, listOf("npc.hunting_chinchompa"),
                        "loc.hunting_boxtrap_full_chinchompa", listOf(Loot("obj.chinchompa_captured"))),
                    TrapCreature("Carnivorous chinchompa", 63, 265.0, listOf("npc.hunting_chinchompa_big"),
                        "loc.hunting_boxtrap_full_chinchompa_big", listOf(Loot("obj.chinchompa_big_captured"))),
                    TrapCreature("Black chinchompa", 73, 315.0, listOf("npc.hunting_chinchompa_black"),
                        "loc.hunting_boxtrap_full_chinchompa_black", listOf(Loot("obj.chinchompa_black"))),
                ),
        )

    val laidTraps = listOf(birdSnare, boxTrap)

    const val ROPE = "obj.rope"
    const val SMALL_NET = "obj.net"

    val netTrees =
        listOf(
            netTree("swamp", "Swamp lizard", 29, 152.0, "npc.salamander_green", "obj.green_salamander", fullSuffix = "green"),
            netTree("orange", "Orange salamander", 47, 224.0, "npc.salamander_orange", "obj.orange_salamander"),
            netTree("red", "Red salamander", 59, 272.0, "npc.salamander_red", "obj.red_salamander"),
            netTree("black", "Black salamander", 67, 319.2, "npc.salamander_black", "obj.black_salamander"),
            netTree("mountain", "Tecu salamander", 79, 344.0, "npc.salamander_mountain", "obj.mountain_salamander"),
        )

    private fun netTree(
        suffix: String,
        name: String,
        level: Int,
        xp: Double,
        npc: String,
        obj: String,
        fullSuffix: String = suffix,
    ) =
        NetTrapTree(
            upLoc = "loc.hunting_sapling_up_$suffix",
            setLoc = "loc.hunting_sapling_set_$suffix",
            failedLoc = "loc.hunting_sapling_failed_$suffix",
            creature = TrapCreature(name, level, xp, listOf(npc), "loc.hunting_sapling_full_$fullSuffix", listOf(Loot(obj))),
        )

    const val BUTTERFLY_NET = "obj.hunting_butterfly_net"
    const val MAGIC_NET = "obj.ii_magic_butterfly_net"
    const val IMPLING_JAR = "obj.ii_impling_jar"
    const val BUTTERFLY_JAR = "obj.butterfly_jar"

    /** Gielinor (non Puro-Puro) impling levels/xp. Barehanded needs +10 levels. */
    val implings =
        listOf(
            imp(1, "Baby impling", 17, 18.0),
            imp(2, "Young impling", 22, 20.0),
            imp(3, "Gourmet impling", 28, 22.0),
            imp(4, "Earth impling", 36, 25.0),
            imp(5, "Essence impling", 42, 27.0),
            imp(6, "Eclectic impling", 50, 30.0),
            imp(7, "Nature impling", 58, 34.0),
            imp(8, "Magpie impling", 65, 44.0),
            imp(9, "Ninja impling", 74, 50.0),
            imp(10, "Dragon impling", 83, 65.0),
            imp(11, "Lucky impling", 89, 80.0),
        )

    private fun imp(n: Int, name: String, level: Int, xp: Double) =
        NetCatch(name, "npc.ii_impling_type_$n", level, xp, IMPLING_JAR, "obj.ii_captured_impling_$n", respawn = 100)

    val butterflies =
        listOf(
            NetCatch("Ruby harvest", "npc.butterfly_ruby", 15, 24.0, BUTTERFLY_JAR, "obj.butterfly_jar_ruby"),
            NetCatch("Sapphire glacialis", "npc.butterfly_glacialis", 25, 34.0, BUTTERFLY_JAR, "obj.butterfly_jar_glacialis"),
            NetCatch("Snowy knight", "npc.butterfly_snowy", 35, 44.0, BUTTERFLY_JAR, "obj.butterfly_jar_snowy"),
            NetCatch("Black warlock", "npc.butterfly_warlock", 45, 54.0, BUTTERFLY_JAR, "obj.butterfly_jar_warlock"),
        )

    /** OSRS: 1 trap, +1 at 20/40/60/80 (Wilderness +1 not modelled). */
    fun maxTraps(level: Int): Int = 1 + listOf(20, 40, 60, 80).count { level >= it }
}
