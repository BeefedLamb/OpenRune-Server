package org.rsmod.content.skills.farming

/** Patch implementation families. Names match RuneLite's PatchImplementation (the varbit table key). */
enum class PatchKind(val tableKey: String, val display: String) {
    ALLOTMENT("ALLOTMENT", "allotment"),
    HERB("HERB", "herb patch"),
    FLOWER("FLOWER", "flower patch"),
    HOPS("HOPS", "hops patch"),
    BUSH("BUSH", "bush patch"),
    TREE("TREE", "tree patch"),
    FRUIT_TREE("FRUIT_TREE", "fruit tree patch");

    /** Crops that end in a "Check-health" stage instead of going straight to harvestable. */
    val perennial: Boolean get() = this == BUSH || this == TREE || this == FRUIT_TREE
    val waterable: Boolean get() = this == ALLOTMENT || this == FLOWER || this == HOPS
    /** Patches whose disease is cured with plant cure (others are pruned with secateurs). */
    val curable: Boolean get() = this == ALLOTMENT || this == HERB || this == FLOWER || this == HOPS
}

/**
 * One plantable crop.
 *
 * [key] is the RuneLite `Produce` name used as the key into the varbit table.
 * [tickMinutes]/[stages] are RuneLite's Produce growth values (stages counts seed..final stage).
 * [regrowMinutes]/[maxHarvest] are for bushes / fruit trees (berries/fruit regrow).
 * Levels and XP values are OSRS wiki values entered by hand (see report: spot-check).
 */
data class Crop(
    val key: String,
    val kind: PatchKind,
    val name: String,
    val seed: String,
    val seedCount: Int,
    val produce: String,
    val level: Int,
    val plantXp: Double,
    val harvestXp: Double,
    val healthXp: Double = 0.0,
    val tickMinutes: Int,
    val stages: Int,
    val regrowMinutes: Int = 0,
    val maxHarvest: Int = 0,
    /** Trees only: woodcutting level + xp per log. */
    val wcLevel: Int = 0,
    val wcXp: Double = 0.0,
)

object FarmingCrops {
    private fun allot(key: String, name: String, seed: String, produce: String, lvl: Int, p: Double, h: Double, tick: Int, st: Int) =
        Crop(key, PatchKind.ALLOTMENT, name, "obj.$seed", 3, "obj.$produce", lvl, p, h, tickMinutes = tick, stages = st)

    private fun herb(key: String, name: String, seed: String, produce: String, lvl: Int, p: Double, h: Double) =
        Crop(key, PatchKind.HERB, name, "obj.$seed", 1, "obj.$produce", lvl, p, h, tickMinutes = 20, stages = 5)

    private fun flower(key: String, name: String, seed: String, produce: String, lvl: Int, p: Double, h: Double) =
        Crop(key, PatchKind.FLOWER, name, "obj.$seed", 1, "obj.$produce", lvl, p, h, tickMinutes = 5, stages = 5)

    private fun hops(key: String, name: String, seed: String, n: Int, produce: String, lvl: Int, p: Double, h: Double, st: Int) =
        Crop(key, PatchKind.HOPS, name, "obj.$seed", n, "obj.$produce", lvl, p, h, tickMinutes = 10, stages = st)

    private fun bush(key: String, name: String, seed: String, produce: String, lvl: Int, p: Double, health: Double, h: Double, st: Int) =
        Crop(key, PatchKind.BUSH, name, "obj.$seed", 1, "obj.$produce", lvl, p, h, health, 20, st, 20, 4)

    private fun tree(key: String, name: String, sapling: String, logs: String, lvl: Int, p: Double, health: Double, st: Int, wcl: Int, wcx: Double) =
        Crop(key, PatchKind.TREE, name, "obj.$sapling", 1, "obj.$logs", lvl, p, 0.0, health, 40, st, wcLevel = wcl, wcXp = wcx)

    private fun fruit(key: String, name: String, sapling: String, produce: String, lvl: Int, p: Double, health: Double, h: Double) =
        Crop(key, PatchKind.FRUIT_TREE, name, "obj.$sapling", 1, "obj.$produce", lvl, p, h, health, 160, 7, 45, 6)

    val all: List<Crop> = listOf(
        // Allotments (3 seeds). tick/stages from RuneLite Produce.
        allot("POTATO", "potato", "potato_seed", "potato", 1, 8.0, 9.0, 10, 5),
        allot("ONION", "onion", "onion_seed", "onion", 5, 9.5, 10.5, 10, 5),
        allot("CABBAGE", "cabbage", "cabbage_seed", "cabbage", 7, 10.0, 11.5, 10, 5),
        allot("TOMATO", "tomato", "tomato_seed", "tomato", 12, 12.5, 14.0, 10, 5),
        allot("SWEETCORN", "sweetcorn", "sweetcorn_seed", "sweetcorn", 20, 17.0, 19.0, 10, 7),
        allot("STRAWBERRY", "strawberry", "strawberry_seed", "strawberry", 31, 26.0, 29.0, 10, 7),
        allot("WATERMELON", "watermelon", "watermelon_seed", "watermelon", 47, 48.5, 54.5, 10, 9),
        allot("SNAPE_GRASS", "snape grass", "snape_grass_seed", "snape_grass", 61, 82.0, 82.0, 10, 8),
        // Herbs (produce = grimy herb).
        herb("GUAM", "guam", "guam_seed", "unidentified_guam", 9, 11.0, 12.5),
        herb("MARRENTILL", "marrentill", "marrentill_seed", "unidentified_marentill", 14, 13.5, 15.0),
        herb("TARROMIN", "tarromin", "tarromin_seed", "unidentified_tarromin", 19, 16.0, 18.0),
        herb("HARRALANDER", "harralander", "harralander_seed", "unidentified_harralander", 26, 21.5, 24.0),
        herb("RANARR", "ranarr", "ranarr_seed", "unidentified_ranarr", 32, 27.0, 30.5),
        herb("TOADFLAX", "toadflax", "toadflax_seed", "unidentified_toadflax", 38, 34.0, 38.5),
        herb("IRIT", "irit", "irit_seed", "unidentified_irit", 44, 43.0, 48.5),
        herb("AVANTOE", "avantoe", "avantoe_seed", "unidentified_avantoe", 50, 54.5, 61.5),
        herb("KWUARM", "kwuarm", "kwuarm_seed", "unidentified_kwuarm", 56, 69.0, 78.0),
        herb("SNAPDRAGON", "snapdragon", "snapdragon_seed", "unidentified_snapdragon", 62, 87.5, 98.5),
        herb("CADANTINE", "cadantine", "cadantine_seed", "unidentified_cadantine", 67, 106.5, 120.0),
        herb("LANTADYME", "lantadyme", "lantadyme_seed", "unidentified_lantadyme", 73, 134.5, 151.5),
        herb("DWARF_WEED", "dwarf weed", "dwarf_weed_seed", "unidentified_dwarf_weed", 79, 170.5, 192.0),
        herb("TORSTOL", "torstol", "torstol_seed", "unidentified_torstol", 85, 199.5, 224.5),
        // Flowers.
        flower("MARIGOLD", "marigold", "marigold_seed", "marigold", 2, 8.5, 47.0),
        flower("ROSEMARY", "rosemary", "rosemary_seed", "rosemary", 11, 12.0, 66.5),
        flower("NASTURTIUM", "nasturtium", "nasturtium_seed", "nasturtium", 24, 19.5, 111.0),
        flower("WOAD", "woad", "woad_seed", "woadleaf", 25, 20.5, 115.5),
        flower("LIMPWURT", "limpwurt", "limpwurt_seed", "limpwurt_root", 26, 21.5, 120.0),
        flower("WHITE_LILY", "white lily", "white_lily_seed", "whitelilly", 58, 42.0, 250.0),
        // Hops.
        hops("BARLEY", "barley", "barley_seed", 4, "barley", 3, 8.5, 9.5, 5),
        hops("HAMMERSTONE", "hammerstone hops", "hammerstone_hop_seed", 4, "hammerstone_hops", 4, 9.0, 10.0, 5),
        hops("ASGARNIAN", "asgarnian hops", "asgarnian_hop_seed", 4, "asgarnian_hops", 8, 10.9, 12.0, 6),
        hops("JUTE", "jute", "jute_seed", 3, "jute_fibre", 13, 13.0, 14.5, 6),
        hops("YANILLIAN", "yanillian hops", "yanillian_hop_seed", 4, "yanillian_hops", 16, 14.5, 16.0, 7),
        hops("KRANDORIAN", "krandorian hops", "krandorian_hop_seed", 4, "krandorian_hops", 21, 17.5, 19.5, 8),
        hops("WILDBLOOD", "wildblood hops", "wildblood_hop_seed", 4, "wildblood_hops", 28, 23.0, 26.0, 9),
        // Bushes (plant, check-health, per-berry xp).
        bush("REDBERRIES", "redberry bush", "redberry_bush_seed", "redberries", 10, 11.5, 64.0, 4.5, 6),
        bush("CADAVABERRIES", "cadavaberry bush", "cadavaberry_bush_seed", "cadavaberries", 22, 18.0, 102.5, 7.0, 7),
        bush("DWELLBERRIES", "dwellberry bush", "dwellberry_bush_seed", "dwellberries", 36, 31.5, 177.5, 12.0, 8),
        bush("JANGERBERRIES", "jangerberry bush", "jangerberry_bush_seed", "jangerberries", 48, 50.5, 284.5, 19.0, 9),
        bush("WHITEBERRIES", "whiteberry bush", "whiteberry_bush_seed", "white_berries", 59, 78.0, 437.5, 29.0, 9),
        bush("POISON_IVY", "poison ivy bush", "poisonivy_bush_seed", "poisonivy_berries", 70, 120.0, 675.0, 45.0, 9),
        // Trees (sapling, plant xp, check-health xp; woodcutting level/xp per log).
        tree("OAK", "oak", "plantpot_oak_sapling", "oak_logs", 15, 14.0, 467.3, 5, 15, 37.5),
        tree("WILLOW", "willow", "plantpot_willow_sapling", "willow_logs", 30, 25.0, 1456.5, 7, 30, 67.5),
        tree("MAPLE", "maple", "plantpot_maple_sapling", "maple_logs", 45, 45.0, 3403.4, 9, 45, 100.0),
        tree("YEW", "yew", "plantpot_yew_sapling", "yew_logs", 60, 81.0, 7069.9, 11, 60, 175.0),
        tree("MAGIC", "magic", "plantpot_magic_tree_sapling", "magic_logs", 75, 145.5, 13768.3, 13, 75, 250.0),
        // Fruit trees (plant, check-health, per-fruit xp).
        fruit("APPLE", "apple tree", "plantpot_apple_sapling", "cooking_apple", 27, 22.0, 1199.5, 8.5),
        fruit("BANANA", "banana tree", "plantpot_banana_sapling", "banana", 33, 28.0, 1750.5, 10.5),
        fruit("ORANGE", "orange tree", "plantpot_orange_sapling", "orange", 39, 35.5, 2470.2, 13.5),
        fruit("CURRY", "curry tree", "plantpot_curry_sapling", "curry_leaf", 42, 40.0, 2906.9, 15.0),
        fruit("PINEAPPLE", "pineapple plant", "plantpot_pineapple_sapling", "pineapple", 51, 57.0, 4605.7, 21.5),
        fruit("PAPAYA", "papaya tree", "plantpot_papaya_sapling", "papaya", 57, 72.0, 6146.4, 27.0),
        fruit("PALM", "palm tree", "plantpot_palm_sapling", "coconut", 68, 110.5, 10150.1, 41.5),
        fruit("DRAGONFRUIT", "dragonfruit tree", "plantpot_dragonfruit_sapling", "dragonfruit", 81, 140.0, 17335.0, 70.0),
    )

    val byKey: Map<String, Crop> = all.associateBy { it.key }

    /**
     * Flower protection (OSRS: a fully grown flower in the adjacent flower patch stops the
     * allotment crop from becoming diseased). White lily protects every allotment crop.
     * Scarecrow/sweetcorn is not modelled. Mapping from the OSRS wiki; spot-check.
     */
    val flowerProtects: Map<String, Set<String>> = mapOf(
        "MARIGOLD" to setOf("POTATO", "ONION", "TOMATO"),
        "ROSEMARY" to setOf("CABBAGE"),
        "NASTURTIUM" to setOf("WATERMELON"),
        "WHITE_LILY" to setOf("POTATO", "ONION", "CABBAGE", "TOMATO", "SWEETCORN", "STRAWBERRY", "WATERMELON", "SNAPE_GRASS"),
    )

    // Tools / consumables (resolved at startup; first candidate that exists wins).
    val RAKE = listOf("obj.rake")
    val DIBBER = listOf("obj.dibber")
    val SPADE = listOf("obj.spade")
    val SECATEURS = listOf("obj.secateurs", "obj.fairy_enchanted_secateurs")
    val PLANT_CURE = listOf("obj.plantcure", "obj.plant_cure")
    val WEEDS = listOf("obj.weeds")
    val BUCKET = listOf("obj.bucket_empty")
    val VIAL = listOf("obj.vial_empty")
    val PLANT_POT = listOf("obj.plantpot_empty")
    val COMPOST = listOf("obj.bucket_compost")
    val SUPERCOMPOST = listOf("obj.bucket_supercompost", "obj.supercompost")
    val ULTRACOMPOST = listOf("obj.bucket_ultracompost", "obj.ultracompost")
    /** Index = charges; index 0 = empty can. */
    val WATERING_CANS = (0..8).map { "obj.watering_can_$it" }
    val EMPTY_CAN_FALLBACK = "obj.watering_can"
}
