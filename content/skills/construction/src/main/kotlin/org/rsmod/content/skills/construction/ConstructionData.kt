package org.rsmod.content.skills.construction

/**
 * Construction data. Every loc id here was checked against the rev-240 cache with the
 * osrs-port-tools exporter (loc name + "Build"/"Remove" op + the POH template map at
 * map squares 29..32,110). Levels / XP / materials follow the OSRS wiki (typed from reference,
 * spot-check recommended). See ConstructionScript for what is and isn't wired.
 */

/** One house room type. [chunkX]/[chunkZ] = zone (8x8 chunk) of the level-0 template copy. */
enum class RoomType(
    val display: String,
    val level: Int,
    val cost: Int,
    val chunkX: Int,
    val chunkZ: Int,
    val outdoor: Boolean = false,
) {
    // Template zones verified by the hotspot locs found inside each chunk (basic wood style,
    // map square 29,110). The other 3 map squares (30..32,110) are the other house styles.
    PARLOUR("Parlour", 1, 1_000, 232, 887),
    GARDEN("Garden", 1, 1_000, 232, 881, outdoor = true),
    KITCHEN("Kitchen", 5, 5_000, 234, 887),
    DINING_ROOM("Dining room", 10, 5_000, 236, 887),
    WORKSHOP("Workshop", 15, 10_000, 232, 885),
    BEDROOM("Bedroom", 20, 10_000, 238, 887),
    SKILL_HALL("Skill hall", 25, 15_000, 233, 886),
    GAMES_ROOM("Games room", 30, 25_000, 237, 884),
    COMBAT_ROOM("Combat room", 32, 25_000, 235, 884),
    QUEST_HALL("Quest hall", 35, 25_000, 237, 886),
    MENAGERIE("Menagerie", 37, 30_000, 239, 880, outdoor = true),
    STUDY("Study", 40, 50_000, 236, 885),
    COSTUME_ROOM("Costume room", 42, 50_000, 238, 881),
    CHAPEL("Chapel", 45, 50_000, 234, 885),
    PORTAL_CHAMBER("Portal chamber", 50, 100_000, 233, 884),
    FORMAL_GARDEN("Formal garden", 55, 75_000, 234, 881, outdoor = true),
    THRONE_ROOM("Throne room", 60, 150_000, 238, 885),
    SUPERIOR_GARDEN("Superior garden", 65, 75_000, 237, 880, outdoor = true),
    OUBLIETTE("Oubliette", 65, 150_000, 238, 883),
    TREASURE_ROOM("Treasure room", 75, 250_000, 239, 884),
    ;
}

data class Mat(val obj: String, val count: Int)

data class Furniture(
    val name: String,
    val locId: Int,
    val level: Int,
    val xp: Double,
    val mats: List<Mat>,
    /** Build every hotspot of the same id in the room at once (curtains), costing per hotspot. */
    val group: Boolean = false,
)

object ConstructionData {
    // Item names (rev 240 gamevals). Resolved/validated at startup; unresolved -> furniture skipped.
    const val PLANK = "obj.woodplank"
    const val OAK = "obj.plank_oak"
    const val TEAK = "obj.plank_teak"
    const val MAHOGANY = "obj.plank_mahogany"
    const val CLOTH = "obj.cloth" // Bolt of cloth (verified by name at startup)
    const val CLAY = "obj.softclay"
    const val LIMESTONE = "obj.limestonebrick"
    const val MARBLE = "obj.marble_block"
    const val IRON = "obj.iron_bar"
    const val STEEL = "obj.steel_bar"
    const val GOLD_LEAF = "obj.gold_leaf"
    const val WOOL = "obj.ball_of_wool"
    const val PLANT1 = "#8431" // Bagged plant 1 (item id verified in cache; name reverse-mapped at startup)
    const val PLANT2 = "#8433"
    const val PLANT3 = "#8435"
    const val HAMMER = "obj.hammer"
    const val SAW = "obj.poh_saw"
    const val COINS = "obj.coins"

    /** Any of these count as "nails" (one consumed per nail in the recipe). */
    val NAILS = listOf(
        "obj.nails_bronze", "obj.nails_iron", "obj.nails_black", "obj.nails_mithril",
        "obj.nails_adamant", "obj.nails_rune", "obj.nails_dragon", "obj.nails",
    )
    const val NAIL_TOKEN = "nails"

    val WOOD = setOf(PLANK, OAK, TEAK, MAHOGANY)

    private fun p(n: Int) = Mat(PLANK, n)
    private fun nl(n: Int) = Mat(NAIL_TOKEN, n)
    private fun oak(n: Int) = Mat(OAK, n)
    private fun teak(n: Int) = Mat(TEAK, n)
    private fun mah(n: Int) = Mat(MAHOGANY, n)

    private val chairs = listOf(
        Furniture("Crude wooden chair", 6752, 1, 58.0, listOf(p(2), nl(2))),
        Furniture("Wooden chair", 6753, 8, 87.0, listOf(p(3), nl(3))),
        Furniture("Rocking chair", 6754, 14, 87.0, listOf(p(3), nl(3))),
        Furniture("Oak chair", 6755, 19, 120.0, listOf(oak(2))),
        Furniture("Oak armchair", 6756, 26, 180.0, listOf(oak(3))),
        Furniture("Teak armchair", 6757, 35, 180.0, listOf(teak(2))),
        Furniture("Mahogany armchair", 6758, 50, 280.0, listOf(mah(2))),
    )

    private fun bag(m: String) = listOf(Mat(m, 1))

    /** Hotspot loc id -> buildable furniture (OSRS order). */
    val hotspots: Map<Int, List<Furniture>> = buildMap {
        // ---------------- Parlour (232,887)
        put(4515, chairs); put(4516, chairs); put(4517, chairs)
        put(4521, listOf(
            Furniture("Wooden bookcase", 6768, 4, 115.0, listOf(p(4), nl(4))),
            Furniture("Oak bookcase", 6769, 29, 180.0, listOf(oak(3))),
            Furniture("Mahogany bookcase", 6770, 40, 420.0, listOf(mah(3))),
        ))
        put(4523, listOf(
            Furniture("Clay fireplace", 6780, 3, 30.0, listOf(Mat(CLAY, 3))),
            Furniture("Stone fireplace", 6782, 33, 40.0, listOf(Mat(LIMESTONE, 2))),
            Furniture("Marble fireplace", 6784, 63, 500.0, listOf(Mat(MARBLE, 1))),
        ))
        put(4524, listOf(
            Furniture("Torn curtains", 6774, 2, 132.0, listOf(p(3), Mat(CLOTH, 3), nl(3)), group = true),
            Furniture("Curtains", 6775, 18, 225.0, listOf(oak(3), Mat(CLOTH, 3)), group = true),
            Furniture("Opulent curtains", 6776, 40, 315.0, listOf(teak(3), Mat(CLOTH, 3)), group = true),
        ))
        // ---------------- Garden (232,881)
        put(15361, listOf(
            Furniture("Exit portal", 4525, 1, 100.0, listOf(Mat(IRON, 10))),
            Furniture("Decorative rock", 4526, 5, 100.0, listOf(Mat(LIMESTONE, 5))),
            Furniture("Pond", 4527, 10, 100.0, listOf(Mat(CLAY, 10))),
            Furniture("Imp statue", 4528, 15, 150.0, listOf(Mat(LIMESTONE, 5), Mat(CLAY, 5))),
            Furniture("Dungeon entrance", 4529, 70, 500.0, listOf(Mat(MARBLE, 1))),
        ))
        // Big tree: bagged trees 8417..8429 (cache item names verified) -> locs 4531..4537.
        put(15362, listOf(
            Furniture("Dead tree", 4531, 5, 31.0, bag("#8417")),
            Furniture("Nice tree", 4532, 10, 44.0, bag("#8419")),
            Furniture("Oak tree", 4533, 15, 70.0, bag("#8421")),
            Furniture("Willow tree", 4534, 30, 100.0, bag("#8423")),
            Furniture("Maple tree", 4535, 45, 122.0, bag("#8425")),
            Furniture("Yew tree", 4536, 60, 141.0, bag("#8427")),
            Furniture("Magic tree", 4537, 75, 223.0, bag("#8429")),
        ))
        put(15364, listOf(
            Furniture("Fern", 5128, 1, 31.0, bag(PLANT1)),
            Furniture("Bush", 5129, 6, 70.0, bag(PLANT2)),
            Furniture("Tall plant", 5130, 12, 100.0, bag(PLANT3)),
        ))
        put(15365, listOf(
            Furniture("Short plant", 5131, 1, 31.0, bag(PLANT1)),
            Furniture("Large-leaf plant", 5132, 6, 70.0, bag(PLANT2)),
            Furniture("Huge plant", 5133, 12, 100.0, bag(PLANT3)),
        ))
        put(15366, listOf(
            Furniture("Plant", 5134, 1, 31.0, bag(PLANT1)),
            Furniture("Small fern", 5135, 6, 70.0, bag(PLANT2)),
            Furniture("Fern", 5136, 12, 100.0, bag(PLANT3)),
        ))
        put(15367, listOf(
            Furniture("Dock leaf", 5137, 1, 31.0, bag(PLANT1)),
            Furniture("Thistle", 5138, 6, 70.0, bag(PLANT2)),
            Furniture("Reeds", 5139, 12, 100.0, bag(PLANT3)),
        ))
        // ---------------- Workshop (232,885)
        put(15439, listOf(
            Furniture("Wooden workbench", 6791, 17, 143.0, listOf(p(5), nl(5))),
            Furniture("Oak workbench", 6792, 32, 300.0, listOf(oak(5))),
            Furniture("Steel framed workbench", 6793, 46, 440.0, listOf(oak(6), Mat(STEEL, 4))),
            Furniture("Bench with vice", 6794, 62, 140.0, listOf(oak(2), Mat(STEEL, 1))),
            Furniture("Bench with lathe", 6795, 77, 140.0, listOf(oak(2), Mat(STEEL, 1))),
        ))
        put(15448, listOf(
            Furniture("Repair bench", 6800, 15, 120.0, listOf(oak(2))),
            Furniture("Whetstone", 6801, 35, 260.0, listOf(oak(4), Mat(LIMESTONE, 1))),
            Furniture("Armour stand", 6802, 55, 500.0, listOf(oak(8), Mat(LIMESTONE, 1))),
        ))
        // ---------------- Kitchen (234,887)
        put(15403, listOf(
            Furniture("Wooden larder", 13565, 9, 228.0, listOf(p(8), nl(8))),
            Furniture("Oak larder", 13566, 33, 480.0, listOf(oak(8))),
            Furniture("Teak larder", 13567, 43, 750.0, listOf(teak(8))),
        ))
        put(15398, listOf(
            Furniture("Firepit", 13528, 5, 40.0, listOf(Mat(STEEL, 1), Mat(CLAY, 2))),
            Furniture("Firepit with hook", 13529, 11, 60.0, listOf(Mat(STEEL, 2), Mat(CLAY, 2))),
            Furniture("Firepit with pot", 13531, 17, 80.0, listOf(Mat(STEEL, 3), Mat(CLAY, 2))),
            Furniture("Small oven", 13533, 24, 80.0, listOf(Mat(STEEL, 4))),
            Furniture("Large oven", 13536, 29, 100.0, listOf(Mat(STEEL, 5))),
            Furniture("Steel range", 13539, 34, 120.0, listOf(Mat(STEEL, 6))),
            Furniture("Fancy range", 13542, 42, 160.0, listOf(Mat(STEEL, 8))),
        ))
        put(15404, listOf(
            Furniture("Pump and drain", 13559, 7, 100.0, listOf(Mat(STEEL, 5))),
            Furniture("Pump and tub", 13561, 27, 200.0, listOf(Mat(STEEL, 10))),
            Furniture("Sink", 13563, 47, 300.0, listOf(Mat(STEEL, 15))),
        ))
        put(15405, listOf(
            Furniture("Wood kitchen table", 13577, 12, 87.0, listOf(p(3), nl(3))),
            Furniture("Oak kitchen table", 13578, 32, 180.0, listOf(oak(3))),
            Furniture("Teak kitchen table", 13579, 52, 270.0, listOf(teak(3))),
        ))
        put(15402, listOf(
            Furniture("Cat blanket", 13574, 5, 15.0, listOf(Mat(CLOTH, 1))),
            Furniture("Cat basket", 13575, 19, 58.0, listOf(p(2), nl(2))),
            Furniture("Cushioned basket", 13576, 33, 58.0, listOf(p(2), nl(2), Mat(WOOL, 2))),
        ))
        put(15401, listOf(
            Furniture("Beer barrel", 13568, 7, 87.0, listOf(p(3), nl(3))),
        ))
        // ---------------- Chapel (234,885). Each altar has 3 ids (same body model, 3 icon
        // models); the first of each triple is used.
        put(15270, listOf(
            Furniture("Oak altar", 13179, 45, 240.0, listOf(oak(4))),
            Furniture("Teak altar", 13182, 50, 360.0, listOf(teak(4))),
            Furniture("Cloth-covered altar", 13185, 56, 390.0, listOf(teak(4), Mat(CLOTH, 2))),
            Furniture("Mahogany altar", 13188, 60, 590.0, listOf(mah(4))),
            Furniture("Limestone altar", 13191, 64, 910.0, listOf(mah(6), Mat(CLOTH, 2), Mat(LIMESTONE, 2))),
            Furniture("Marble altar", 13194, 70, 1_030.0, listOf(Mat(MARBLE, 2), Mat(CLOTH, 2))),
            Furniture("Gilded altar", 13197, 75, 2_230.0, listOf(Mat(MARBLE, 2), Mat(CLOTH, 2), Mat(GOLD_LEAF, 4))),
        ))
    }

    /** Ground-floor door hotspots (wall locs on room edges). */
    val DOOR_HOTSPOTS = listOf(15313, 15314)

    /** Altars (all 21 icon variants) -> "Pray" restores prayer. */
    val ALTAR_IDS = (13179..13199).toList()

    /** Rimmington house portal (verified at 2951,3222 in the cache map). */
    const val HOUSE_PORTAL = 15478
    const val EXIT_PORTAL = 4525
}
