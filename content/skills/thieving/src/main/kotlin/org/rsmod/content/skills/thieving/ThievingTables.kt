package org.rsmod.content.skills.thieving

import org.rsmod.api.random.GameRandom

internal data class Loot(val obj: String, val min: Int = 1, val max: Int = min)

internal class LootTable(private val entries: List<Pair<Int, Loot>>, private val nothing: Int = 0) {
    private val total = entries.sumOf { it.first }

    /** Rolls including the "nothing" weight; null means no item this time. */
    fun rollOrNull(random: GameRandom): Loot? {
        if (nothing > 0 && random.of(0, total + nothing - 1) >= total) return null
        return roll(random)
    }

    fun roll(random: GameRandom): Loot {
        var pick = random.of(0, total - 1)
        for ((weight, loot) in entries) {
            if (pick < weight) return loot
            pick -= weight
        }
        return entries.last().second
    }
}

internal class Stall(
    val loc: String,
    val emptyLoc: String?,
    val level: Int,
    val xp: Double,
    val restockTicks: Int,
    val owners: List<String>,
    val guards: List<String>,
    val attemptMessage: String,
    val loot: LootTable,
)

internal class CoinPouch(val obj: String, val coins: Int, val coinsMax: Int = coins)

internal class Pickpocket(
    val npcs: List<String>,
    val level: Int,
    val xp: Double,
    val lowChance: Int,
    val highChance: Int,
    val stunDamage: Int,
    val caughtShout: String,
    val loot: LootTable? = null,
    val pouch: CoinPouch? = null,
    val lowercaseName: Boolean = false,
    /** Upper bound of the stun hit (OSRS rolls stunDamage..stunMax). */
    val stunMax: Int = stunDamage,
    /** NPC op slot carrying "Pickpocket" on this cache (3 for most, 1 for a few). */
    val op: Int = 3,
    /** Quest key (Quest.get) that must be completed first, e.g. "quest_sinsofthefather". */
    val quest: String? = null,
    val questName: String? = null,
)

internal object ThievingTables {
    private val seedStallLoot =
        LootTable(
            listOf(
                120 to Loot("obj.hammerstone_hop_seed"),
                119 to Loot("obj.potato_seed"),
                119 to Loot("obj.marigold_seed"),
                118 to Loot("obj.barley_seed"),
                89 to Loot("obj.onion_seed"),
                83 to Loot("obj.asgarnian_hop_seed"),
                71 to Loot("obj.cabbage_seed"),
                47 to Loot("obj.yanillian_hop_seed"),
                36 to Loot("obj.rosemary_seed"),
                35 to Loot("obj.nasturtium_seed"),
                35 to Loot("obj.tomato_seed"),
                35 to Loot("obj.jute_seed"),
                30 to Loot("obj.sweetcorn_seed"),
                24 to Loot("obj.krandorian_hop_seed"),
                18 to Loot("obj.strawberry_seed"),
                12 to Loot("obj.wildblood_hop_seed"),
                9 to Loot("obj.watermelon_seed"),
            )
        )

    private val wineStallLoot =
        LootTable(
            listOf(
                39 to Loot("obj.jug_empty"),
                20 to Loot("obj.jug_water"),
                17 to Loot("obj.grapes"),
                13 to Loot("obj.jug_wine"),
                11 to Loot("obj.rag_bottle_wine"),
            )
        )

    private val draynorGuards = listOf("npc.farming_market_guard")

    private val baseStalls: List<Stall> =
        listOf(
            Stall(
                loc = "loc.seed_stall",
                emptyLoc = null,
                level = 27,
                xp = 10.0,
                restockTicks = 4,
                owners = listOf("npc.seed_merchant"),
                guards = draynorGuards,
                attemptMessage = "You attempt to steal some seeds from the seed merchant's stall.",
                loot = seedStallLoot,
            ),
            Stall(
                loc = "loc.rag_market_stall",
                emptyLoc = "loc.rag_market_stall_empty",
                level = 22,
                xp = 27.0,
                restockTicks = 8,
                owners = listOf("npc.rag_wine_merchant"),
                guards = draynorGuards,
                attemptMessage = "You attempt to steal something from the wine merchant's stall.",
                loot = wineStallLoot,
            ),
        )

    private val masterFarmerLoot =
        LootTable(
            listOf(
                17700 to Loot("obj.potato_seed", 1, 4),
                13280 to Loot("obj.onion_seed", 1, 3),
                6944 to Loot("obj.cabbage_seed", 1, 3),
                6369 to Loot("obj.tomato_seed", 1, 2),
                2212 to Loot("obj.sweetcorn_seed", 1, 2),
                1106 to Loot("obj.strawberry_seed"),
                529 to Loot("obj.watermelon_seed"),
                385 to Loot("obj.snape_grass_seed"),
                5556 to Loot("obj.barley_seed", 1, 12),
                5556 to Loot("obj.hammerstone_hop_seed", 1, 9),
                4184 to Loot("obj.asgarnian_hop_seed", 1, 6),
                4149 to Loot("obj.jute_seed", 1, 9),
                2770 to Loot("obj.yanillian_hop_seed", 1, 6),
                1385 to Loot("obj.krandorian_hop_seed", 1, 6),
                704 to Loot("obj.wildblood_hop_seed", 1, 3),
                4587 to Loot("obj.marigold_seed"),
                3040 to Loot("obj.nasturtium_seed"),
                1965 to Loot("obj.rosemary_seed"),
                1451 to Loot("obj.woad_seed"),
                1159 to Loot("obj.limpwurt_seed"),
                3876 to Loot("obj.redberry_bush_seed"),
                2717 to Loot("obj.cadavaberry_bush_seed"),
                1942 to Loot("obj.dwellberry_bush_seed"),
                775 to Loot("obj.jangerberry_bush_seed"),
                282 to Loot("obj.whiteberry_bush_seed"),
                107 to Loot("obj.poisonivy_bush_seed"),
                203 to Loot("obj.mushroom_seed"),
                122 to Loot("obj.belladonna_seed"),
                81 to Loot("obj.cactus_seed"),
                53 to Loot("obj.seaweed_seed"),
                41 to Loot("obj.potato_cactus_seed"),
                1713 to Loot("obj.guam_seed"),
                1046 to Loot("obj.marrentill_seed"),
                714 to Loot("obj.tarromin_seed"),
                485 to Loot("obj.harralander_seed"),
                372 to Loot("obj.ranarr_seed"),
                226 to Loot("obj.toadflax_seed"),
                154 to Loot("obj.irit_seed"),
                106 to Loot("obj.avantoe_seed"),
                72 to Loot("obj.kwuarm_seed"),
                54 to Loot("obj.snapdragon_seed"),
                34 to Loot("obj.cadantine_seed"),
                24 to Loot("obj.lantadyme_seed"),
                14 to Loot("obj.dwarf_weed_seed"),
                11 to Loot("obj.torstol_seed"),
            )
        )

    private val basePickpockets: List<Pickpocket> =
        listOf(
            Pickpocket(
                npcs =
                    listOf(
                        "npc.man",
                        "npc.man2",
                        "npc.man3",
                        "npc.woman",
                        "npc.woman2",
                        "npc.woman3",
                    ),
                level = 1,
                xp = 8.0,
                lowChance = 180,
                highChance = 240,
                stunDamage = 1,
                caughtShout = "What do you think you're doing?",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_citizen", coins = 3),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs = listOf("npc.master_farmer_1", "npc.martin_the_master_farmer"),
                level = 38,
                xp = 43.0,
                lowChance = 90,
                highChance = 240,
                stunDamage = 3,
                caughtShout = "Cor blimey mate, what are ye doing in me pockets?",
                loot = masterFarmerLoot,
            )
        )

    // ---------------------------------------------------------------------------------------
    // Expanded OSRS pickpocket targets. Level / XP / stun damage are from the OSRS wiki
    // (Thieving#Pickpocketing, Oct 2026). The cache's thieving dbtables carry no usable
    // columns on this revision, so values are hardcoded here. lowChance/highChance are the
    // statRandom bounds (out of 256) approximating the wiki success curves.
    // ---------------------------------------------------------------------------------------

    private val farmerLoot = LootTable(listOf(1 to Loot("obj.potato_seed")), nothing = 9)

    private val hamLoot =
        LootTable(
            listOf(
                10 to Loot("obj.ham_shirt"),
                10 to Loot("obj.ham_robe"),
                10 to Loot("obj.ham_hood"),
                10 to Loot("obj.ham_cloak"),
                10 to Loot("obj.ham_badge"),
                8 to Loot("obj.ham_gloves"),
                8 to Loot("obj.ham_boots"),
                20 to Loot("obj.bronze_arrowheads", 1, 15),
                12 to Loot("obj.steel_arrowheads", 1, 13),
                10 to Loot("obj.iron_dagger"),
                10 to Loot("obj.bronze_med_helm"),
                8 to Loot("obj.iron_med_helm"),
                10 to Loot("obj.coal"),
                10 to Loot("obj.iron_ore"),
                10 to Loot("obj.copper_ore"),
                10 to Loot("obj.tin_ore"),
                8 to Loot("obj.uncut_jade"),
                8 to Loot("obj.uncut_opal"),
                6 to Loot("obj.unidentified_guam"),
                5 to Loot("obj.unidentified_marentill"),
                4 to Loot("obj.unidentified_tarromin"),
                6 to Loot("obj.thread"),
                5 to Loot("obj.needle"),
                5 to Loot("obj.tinderbox"),
            ),
            nothing = 60,
        )

    private val rogueLoot =
        LootTable(
            listOf(
                8 to Loot("obj.airrune", 8),
                6 to Loot("obj.jug_wine"),
                5 to Loot("obj.lockpick"),
                4 to Loot("obj.iron_dagger_p"),
            ),
            nothing = 77,
        )

    private val caveGoblinLoot =
        LootTable(
            listOf(
                6 to Loot("obj.iron_ore"),
                6 to Loot("obj.swamp_tar"),
                5 to Loot("obj.tinderbox"),
                3 to Loot("obj.oil_can"),
            ),
            nothing = 80,
        )

    private val desertBanditLoot =
        LootTable(
            listOf(6 to Loot("obj.1doseantipoison"), 6 to Loot("obj.lockpick")),
            nothing = 88,
        )

    private val watchmanLoot = LootTable(listOf(1 to Loot("obj.bread")), nothing = 9)

    private val paladinLoot = LootTable(listOf(1 to Loot("obj.chaosrune", 2)), nothing = 9)

    private val gnomeLoot =
        LootTable(
            listOf(
                10 to Loot("obj.earthrune"),
                8 to Loot("obj.fire_orb"),
                6 to Loot("obj.gold_ore"),
                6 to Loot("obj.swamp_toad"),
                6 to Loot("obj.king_worm"),
                6 to Loot("obj.steel_arrowheads", 6),
            ),
            nothing = 58,
        )

    private val heroLoot =
        LootTable(
            listOf(
                6 to Loot("obj.deathrune", 2),
                6 to Loot("obj.bloodrune"),
                6 to Loot("obj.jug_wine"),
                5 to Loot("obj.fire_orb"),
                4 to Loot("obj.gold_ore"),
                1 to Loot("obj.diamond"),
            ),
            nothing = 72,
        )

    private val vyreLoot =
        LootTable(
            listOf(
                8 to Loot("obj.deathrune", 2),
                5 to Loot("obj.ruby"),
                3 to Loot("obj.diamond"),
                4 to Loot("obj.uncut_ruby"),
            ),
            nothing = 80,
        )

    private val elfLoot =
        LootTable(
            listOf(
                8 to Loot("obj.deathrune", 2),
                8 to Loot("obj.naturerune", 3),
                5 to Loot("obj.gold_ore"),
                5 to Loot("obj.fire_orb"),
                2 to Loot("obj.diamond"),
                1 to Loot("obj.prif_crystal_shard"),
            ),
            nothing = 71,
        )

    private val tzhaarLoot =
        LootTable(
            listOf(
                60 to Loot("obj.tzhaar_token", 3, 14),
                8 to Loot("obj.uncut_sapphire"),
                6 to Loot("obj.uncut_emerald"),
                4 to Loot("obj.uncut_ruby"),
                2 to Loot("obj.uncut_diamond"),
            ),
            nothing = 20,
        )

    private val expandedPickpockets: List<Pickpocket> =
        listOf(
            Pickpocket(
                npcs =
                    listOf(
                        "npc.varrock_man1", "npc.man4", "npc.man4_for_musa_point",
                        "npc.al_kharid_man", "npc.falador_man2", "npc.falador_man3",
                        "npc.ardougnian_male1", "npc.karamja_man", "npc.death_man_indoors1",
                        "npc.man_indoor", "npc.zeah_man", "npc.zeah_man2", "npc.zeah_man3",
                        "npc.shayzien_man_1", "npc.shayzien_man_2", "npc.man5",
                        "npc.falador_man1", "npc.varrock_woman1", "npc.falador_woman2",
                        "npc.ardougnian_female1", "npc.zeah_woman", "npc.zeah_woman2",
                        "npc.zeah_woman3", "npc.zeah_woman_outside", "npc.shayzien_woman_1",
                        "npc.shayzien_woman_2", "npc.woman4",
                    ),
                level = 1, xp = 8.0, lowChance = 180, highChance = 240, stunDamage = 1,
                caughtShout = "What do you think you're doing?",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_citizen", coins = 3),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs =
                    listOf(
                        "npc.farmer1", "npc.farmer2", "npc.farmer3", "npc.farmer1_f",
                        "npc.farmer2_f", "npc.farmer3_f", "npc.farmer4",
                        "npc.varlamore_farmer_m_1", "npc.varlamore_farmer_m_2",
                        "npc.varlamore_farmer_m_3", "npc.varlamore_farmer_m_4",
                        "npc.varlamore_farmer_f_1", "npc.varlamore_farmer_f_2",
                        "npc.varlamore_farmer_f_3", "npc.varlamore_farmer_f_4",
                        "npc.kastori_farmer_m_1", "npc.kastori_farmer_m_2",
                        "npc.kastori_farmer_f_1", "npc.kastori_farmer_f_2", "npc.tal_teklan_farmer",
                    ),
                level = 10, xp = 14.5, lowChance = 150, highChance = 240, stunDamage = 1,
                caughtShout = "What do you think you're doing?",
                loot = farmerLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_farmer", coins = 9),
                lowercaseName = true,
            ),
            // H.A.M. members. Only the two generic H.A.M. civilians carry Pickpocket on this
            // cache, on op1. Wiki: female lvl 15 / 18.5xp, male lvl 20 / 22.2xp; stun 1-3.
            Pickpocket(
                npcs = listOf("npc.favour_female_ham_civilian"),
                level = 15, xp = 18.5, lowChance = 135, highChance = 239,
                stunDamage = 1, stunMax = 3, op = 1,
                caughtShout = "Stop! Thief!",
                loot = hamLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_ham", coins = 1, coinsMax = 21),
            ),
            Pickpocket(
                npcs = listOf("npc.favour_male_ham_civilian"),
                level = 20, xp = 22.2, lowChance = 135, highChance = 239,
                stunDamage = 1, stunMax = 3, op = 1,
                caughtShout = "Stop! Thief!",
                loot = hamLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_ham", coins = 1, coinsMax = 21),
            ),
            Pickpocket(
                npcs =
                    listOf(
                        "npc.warrior_woman", "npc.warrior_woman_variant01",
                        "npc.warrior_woman_variant02", "npc.warrior_man",
                        "npc.warrior_man_variant01", "npc.warrior_man_variant02",
                        "npc.al_kharid_warrior",
                    ),
                level = 25, xp = 26.0, lowChance = 100, highChance = 240, stunDamage = 2,
                caughtShout = "What do you think you're doing?",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_warrior", coins = 18),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs = listOf("npc.rogue"),
                level = 32, xp = 36.5, lowChance = 74, highChance = 240, stunDamage = 2,
                caughtShout = "What do you think you're doing?",
                loot = rogueLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_rogue", coins = 25, coinsMax = 120),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs =
                    (1..9).map { "npc.dorgesh_male_$it" } + (1..9).map { "npc.dorgesh_female_$it" },
                level = 36, xp = 40.0, lowChance = 72, highChance = 240, stunDamage = 1,
                caughtShout = "Oi! Get your hands out of there!",
                loot = caveGoblinLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_cavegoblin", coins = 10, coinsMax = 50),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs =
                    listOf(
                        "npc.hos_town_guard_01", "npc.hos_town_guard_02", "npc.hos_town_guard_03",
                        "npc.hos_town_guard_04", "npc.jail_guard_1", "npc.jail_guard_2",
                        "npc.jail_guard_3", "npc.jail_guard_4", "npc.jail_guard_5",
                        "npc.fai_varrock_guard", "npc.fai_varrock_guard_captain", "npc.guard1",
                        "npc.fai_falador_guard1", "npc.fai_falador_guard2", "npc.fai_falador_guard3",
                        "npc.fai_falador_guard4", "npc.fai_falador_guard5", "npc.fai_falador_guard6",
                        "npc.falador_doric_area_guard", "npc.ardougne_guard",
                        "npc.kourend_guard_m1", "npc.kourend_guard_m2", "npc.kourend_guard_m3",
                        "npc.kourend_guard_m4", "npc.kourend_guard_f1", "npc.kourend_guard_f2",
                        "npc.kourend_guard_f3", "npc.kourend_guard_f4", "npc.fai_varrock_guard02",
                        "npc.fai_varrock_guard02_variant01", "npc.fai_varrock_guard02_variant02",
                        "npc.fai_varrock_guard02_f", "npc.fai_varrock_guard02_f_variant01",
                        "npc.fai_varrock_guard02_f_variant02", "npc.fai_varrock_guard_captain02",
                        "npc.guard1_variant01", "npc.guard1_f", "npc.guard1_f_variant01",
                        "npc.ardougne_guard_variant01", "npc.ardougne_guard_f",
                        "npc.ardougne_guard_f_variant01", "npc.fai_falador_guard1_variant01",
                        "npc.fai_falador_guard1_f", "npc.fai_falador_guard1_variant02",
                        "npc.fai_falador_guard2_f", "npc.fai_falador_guard3_f",
                        "npc.fai_falador_guard4_f",
                    ) +
                        (1..5).flatMap { listOf("npc.varlamore_guard_m_$it", "npc.varlamore_guard_f_$it") } +
                        (1..5).flatMap { listOf("npc.aldarin_guard_m_$it", "npc.aldarin_guard_f_$it") } +
                        (1..4).flatMap { listOf("npc.auburnvale_guard_m_$it", "npc.auburnvale_guard_f_$it") } +
                        (1..4).flatMap { listOf("npc.tlati_guard_m_$it", "npc.tlati_guard_f_$it") } +
                        (1..4).flatMap { listOf("npc.kourend_guard_m${it}_big", "npc.kourend_guard_f${it}_big") },
                level = 40, xp = 46.8, lowChance = 50, highChance = 240, stunDamage = 2,
                caughtShout = "What do you think you're doing?",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_guard", coins = 30),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs =
                    listOf(
                        "npc.viking_man", "npc.viking_man2", "npc.viking_man3", "npc.viking_man4",
                        "npc.viking_man5", "npc.viking_woman_indoors", "npc.viking_woman2",
                        "npc.viking_woman3", "npc.viking_woman4",
                    ),
                level = 45, xp = 65.0, lowChance = 65, highChance = 240, stunDamage = 2,
                caughtShout = "You stinking outerlander, how dare you!",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_fremennik", coins = 40),
            ),
            // Bandit Camp desert bandits.
            Pickpocket(
                npcs =
                    listOf("npc.fourdiamonds_sword_bandit_1", "npc.fourdiamonds_sword_bandit_free"),
                level = 53, xp = 79.4, lowChance = 50, highChance = 240, stunDamage = 3,
                caughtShout = "What do you think you're doing?",
                loot = desertBanditLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_desertbandit", coins = 30),
                lowercaseName = true,
            ),
            // Wealthy citizens (Civitas illa Fortis), op1 on this cache.
            Pickpocket(
                npcs = listOf("a", "b", "c", "d").map { "npc.varlamore_wealthy_citizen_$it" },
                level = 50, xp = 96.0, lowChance = 50, highChance = 240, stunDamage = 3, op = 1,
                caughtShout = "How dare you!",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_varlamore_wealthy", coins = 30, coinsMax = 90),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs =
                    listOf(
                        "npc.knight_of_ardougne", "npc.knight_of_ardougne2",
                        "npc.knight_of_ardougne_west_vis", "npc.knight_of_ardougne_f_west_vis",
                        "npc.knight_of_ardougne_f",
                    ),
                level = 55, xp = 84.3, lowChance = 50, highChance = 240, stunDamage = 3,
                caughtShout = "What do you think you're doing?",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_knight", coins = 50),
            ),
            // Pollnivneach bandits (The Feud).
            Pickpocket(
                npcs =
                    listOf(
                        "npc.feud_arabian_guard1_1", "npc.feud_arabian_guard1_2",
                        "npc.feud_arabian_guard2_1", "npc.feud_arabian_guard2_2",
                    ),
                level = 55, xp = 84.3, lowChance = 50, highChance = 240, stunDamage = 5,
                caughtShout = "What do you think you're doing?",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_bandit", coins = 50),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs = listOf("npc.yanille_watchman"),
                level = 65, xp = 137.5, lowChance = 15, highChance = 160, stunDamage = 3,
                caughtShout = "What do you think you're doing?",
                loot = watchmanLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_watchman", coins = 60),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs = listOf("npc.feud_egyptian_doorman_2"),
                level = 65, xp = 137.5, lowChance = 25, highChance = 245, stunDamage = 5,
                caughtShout = "What do you think you're doing?",
                pouch = CoinPouch("obj.pickpocket_coin_pouch_menaphite", coins = 60),
            ),
            Pickpocket(
                npcs =
                    listOf(
                        "npc.paladin", "npc.paladin2", "npc.paladin_west_vis",
                        "npc.paladin_west_f_vis", "npc.paladin_variant01", "npc.paladin_variant02",
                        "npc.paladin_f", "npc.paladin_f_variant01",
                    ),
                level = 70, xp = 131.8, lowChance = 50, highChance = 150, stunDamage = 3,
                caughtShout = "What do you think you're doing?",
                loot = paladinLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_paladin", coins = 80),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs =
                    listOf(
                        "npc.gnome", "npc.browclothedgnome", "npc.darkskinned_gnome",
                        "npc.gnomefemale", "npc.gnomefemale_dskinned", "npc.gnomechildgreen",
                        "npc.gnomechildblue", "npc.gnomechild",
                    ),
                level = 75, xp = 133.3, lowChance = 8, highChance = 120, stunDamage = 1,
                caughtShout = "What do you think you're doing?",
                loot = gnomeLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_gnome", coins = 300),
                lowercaseName = true,
            ),
            Pickpocket(
                npcs = listOf("npc.hero", "npc.hero_variant01", "npc.hero_f"),
                level = 80, xp = 163.3, lowChance = 6, highChance = 100, stunDamage = 3,
                caughtShout = "What do you think you're doing?",
                loot = heroLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_hero", coins = 200, coinsMax = 300),
                lowercaseName = true,
            ),
            // Vyres of Darkmeyer: require Sins of the Father.
            Pickpocket(
                npcs =
                    listOf(
                        "alek_constantine", "caninelle_draynar", "carnivus_belamorta",
                        "crimsonette_van_marr", "diphylla_bechstein", "draconis_sanguine",
                        "episcula_helsing", "grigor_rasputin", "haemas_lamescus",
                        "lasenna_rasputin", "misdrievus_shadum", "mort_nightshade",
                        "mortina_daubenton", "nakasa_jovkai", "natalidae_shadum",
                        "noctillion_lugosi", "pipistrelle_draynar", "remus_kaninus",
                        "valentin_rasputin", "valentina_diaemus", "vallessia_dracyula",
                        "vallessia_von_pitt", "vampyressa_van_von", "vampyrus_diaemus",
                        "violetta_sanguine", "vlad_bechstein", "vlad_diaemus", "von_van_von",
                        "vonnetta_varnis", "vormar_vakan",
                    ).map { "npc.$it" },
                level = 82, xp = 306.9, lowChance = 6, highChance = 90, stunDamage = 5,
                caughtShout = "How dare you, mortal!",
                loot = vyreLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_vyre", coins = 250),
                quest = "quest_sinsofthefather",
                questName = "Sins of the Father",
            ),
            // Elves of Lletya / Prifddinas: require Mourning's End Part I.
            Pickpocket(
                npcs =
                    listOf(
                        "anaire", "aranwe", "aredhel", "caranthir", "celebrian", "celegorm",
                        "cirdan", "curufin", "earwen", "edrahil", "elenwe", "elladan", "enel",
                        "enelye", "enerdhil", "erestor", "feanor", "findis", "finduilas",
                        "fingolfin", "fingon", "galathil", "gelmir", "glorfindel", "guilin",
                        "hendor", "idril", "imin", "iminye", "indis", "ingwe", "ingwion",
                        "lenwe", "lindir", "maeglin", "mahtan", "miriel", "mithrellas",
                        "nellas", "nerdanel", "nimloth", "oropher", "orophin", "saeros",
                        "salgant", "tatie", "thingol", "turgon", "vaire",
                    ).map { "npc.prif_citizen_$it" } +
                        listOf(
                            "npc.mourning_town_elf_1", "npc.mourning_town_elf_3",
                            "npc.mourning_town_elf_4", "npc.mourning_town_elf_5_vis",
                        ),
                level = 85, xp = 353.3, lowChance = 6, highChance = 90, stunDamage = 5,
                caughtShout = "What do you think you're doing?",
                loot = elfLoot,
                pouch = CoinPouch("obj.pickpocket_coin_pouch_elf", coins = 280, coinsMax = 350),
                quest = "quest_mourningsendpart1",
                questName = "Mourning's End Part I",
            ),
            Pickpocket(
                npcs = (1..6).map { "npc.tzhaar_hur_city$it" },
                level = 90, xp = 103.4, lowChance = 40, highChance = 151, stunDamage = 4,
                caughtShout = "JalYt, you steal from TzHaar? Bad idea!",
                loot = tzhaarLoot,
            ),
        )

    // ---------------------------------------------------------------------------------------
    // Expanded OSRS stalls. Level / XP / respawn from the OSRS wiki (respawn seconds -> ticks
    // at 0.6s). Stalls with no dedicated empty loc on this cache use the timer-based restock.
    // ---------------------------------------------------------------------------------------

    private val ardougneGuards =
        listOf(
            "npc.ardougne_guard", "npc.ardougne_guard_variant01", "npc.ardougne_guard_f",
            "npc.ardougne_guard_f_variant01", "npc.knight_of_ardougne", "npc.knight_of_ardougne_f",
            "npc.paladin", "npc.paladin2",
        )

    private fun stallGroup(
        locs: List<String>,
        level: Int,
        xp: Double,
        restockTicks: Int,
        noun: String,
        loot: LootTable,
        owners: List<String> = emptyList(),
        guards: List<String> = emptyList(),
    ): List<Stall> =
        locs.map {
            Stall(
                loc = it,
                emptyLoc = null,
                level = level,
                xp = xp,
                restockTicks = restockTicks,
                owners = owners,
                guards = guards,
                attemptMessage = "You attempt to steal from the $noun stall.",
                loot = loot,
            )
        }

    private val expandedStalls: List<Stall> =
        stallGroup(
            listOf("loc.misc_veg_market", "loc.etc_veg_market"),
            level = 2, xp = 10.0, restockTicks = 2, noun = "vegetable",
            loot = LootTable(listOf(
                1 to Loot("obj.potato"), 1 to Loot("obj.onion"), 1 to Loot("obj.cabbage"),
                1 to Loot("obj.tomato"), 1 to Loot("obj.garlic"),
            )),
        ) +
            stallGroup(
                listOf("loc.cakethiefstall", "loc.hos_stall_bread", "loc.fortis_market_stall_bakers",
                    "loc.dwarf_market_bakery"),
                level = 5, xp = 16.0, restockTicks = 4, noun = "baker's",
                loot = LootTable(listOf(
                    6 to Loot("obj.cake"), 3 to Loot("obj.bread"), 1 to Loot("obj.chocolate_slice"),
                )),
                guards = ardougneGuards,
            ) +
            stallGroup(
                listOf("loc.tea_stall", "loc.icthalarins_tea_stall", "loc.contact_tea_stall"),
                level = 5, xp = 16.0, restockTicks = 4, noun = "tea",
                loot = LootTable(listOf(1 to Loot("obj.cup_of_tea"))),
            ) +
            // Ape Atoll stalls. TODO: OSRS requires monkey form (greegree) to steal here.
            stallGroup(
                listOf("loc.mm_stall_food"), level = 5, xp = 16.0, restockTicks = 6, noun = "food",
                loot = LootTable(listOf(1 to Loot("obj.banana"))),
            ) +
            stallGroup(
                listOf("loc.mm_stall_crafting", "loc.dwarf_market_crafting"),
                level = 5, xp = 20.0, restockTicks = 8, noun = "crafting",
                loot = LootTable(listOf(
                    1 to Loot("obj.chisel"), 1 to Loot("obj.ring_mould"), 1 to Loot("obj.necklace_mould"),
                )),
            ) +
            stallGroup(
                listOf("loc.mm_stall_general"), level = 5, xp = 25.0, restockTicks = 8, noun = "general",
                loot = LootTable(listOf(
                    1 to Loot("obj.pot_empty"), 1 to Loot("obj.tinderbox"), 1 to Loot("obj.hammer"),
                )),
            ) +
            stallGroup(
                listOf("loc.silkthiefstall", "loc.prif_marketstall_silk", "loc.fortis_market_stall_silk"),
                level = 20, xp = 24.0, restockTicks = 8, noun = "silk",
                loot = LootTable(listOf(1 to Loot("obj.silk"))),
                owners = listOf("npc.silk_merchant"), guards = ardougneGuards,
            ) +
            stallGroup(
                listOf("loc.hos_fruit_stall_02"), level = 25, xp = 28.5, restockTicks = 4, noun = "fruit",
                loot = LootTable(listOf(
                    40 to Loot("obj.cooking_apple"), 35 to Loot("obj.banana"), 20 to Loot("obj.strawberry"),
                    5 to Loot("obj.jangerberries"), 5 to Loot("obj.lemon"), 5 to Loot("obj.redberries"),
                    5 to Loot("obj.pineapple"), 5 to Loot("obj.lime"), 2 to Loot("obj.golovanova_top"),
                    2 to Loot("obj.papaya"),
                )),
            ) +
            stallGroup(
                listOf("loc.furthiefstall", "loc.viking_fur_market", "loc.fortis_market_stall_fur"),
                level = 35, xp = 45.0, restockTicks = 12, noun = "fur",
                loot = LootTable(listOf(3 to Loot("obj.fur"), 1 to Loot("obj.grey_wolf_fur"))),
                guards = ardougneGuards,
            ) +
            stallGroup(
                listOf("loc.viking_fish_market", "loc.misc_fish_market", "loc.etc_fish_market",
                    "loc.fish_stall_warrens"),
                level = 42, xp = 42.0, restockTicks = 12, noun = "fish",
                loot = LootTable(listOf(
                    5 to Loot("obj.raw_salmon"), 3 to Loot("obj.raw_tuna"), 2 to Loot("obj.raw_lobster"),
                )),
            ) +
            stallGroup(
                listOf("loc.xbows_dwarf_market"), level = 49, xp = 52.0, restockTicks = 8, noun = "crossbow",
                loot = LootTable(listOf(
                    1 to Loot("obj.xbows_crossbow_bolts_bronze_unfeathered", 3),
                    1 to Loot("obj.xbows_crossbow_limbs_bronze"), 1 to Loot("obj.xbows_crossbow_stock_wood"),
                )),
            ) +
            stallGroup(
                listOf("loc.silverthiefstall", "loc.dwarf_market_silver", "loc.prif_marketstall_silver"),
                level = 50, xp = 205.0, restockTicks = 32, noun = "silver",
                loot = LootTable(listOf(1 to Loot("obj.silver_ore"))),
                guards = ardougneGuards,
            ) +
            stallGroup(
                listOf("loc.spicethiefstall", "loc.prif_marketstall_spice", "loc.fortis_market_stall_spice"),
                level = 65, xp = 92.0, restockTicks = 10, noun = "spice",
                loot = LootTable(listOf(1 to Loot("obj.spicespot"))),
                guards = ardougneGuards,
            ) +
            stallGroup(
                listOf("loc.mm_stall_magic"), level = 65, xp = 90.0, restockTicks = 12, noun = "magic",
                loot = LootTable(listOf(
                    1 to Loot("obj.airrune"), 1 to Loot("obj.earthrune"), 1 to Loot("obj.firerune"),
                    1 to Loot("obj.waterrune"), 1 to Loot("obj.lawrune"),
                )),
            ) +
            stallGroup(
                listOf("loc.mm_stall_scimitar"), level = 65, xp = 210.0, restockTicks = 32, noun = "scimitar",
                loot = LootTable(listOf(19 to Loot("obj.iron_scimitar"), 1 to Loot("obj.steel_scimitar"))),
            ) +
            stallGroup(
                listOf("loc.gemthiefstall", "loc.dwarf_market_gems", "loc.prif_marketstall_gem",
                    "loc.fortis_market_stall_gems"),
                level = 75, xp = 408.0, restockTicks = 100, noun = "gem",
                loot = LootTable(listOf(
                    105 to Loot("obj.uncut_sapphire"), 17 to Loot("obj.uncut_emerald"),
                    5 to Loot("obj.uncut_ruby"), 1 to Loot("obj.uncut_diamond"),
                )),
                owners = listOf("npc.gem_merchant"), guards = ardougneGuards,
            )

    val stalls: List<Stall> by lazy { baseStalls + expandedStalls }

    val pickpockets: List<Pickpocket> by lazy { basePickpockets + expandedPickpockets }
}
