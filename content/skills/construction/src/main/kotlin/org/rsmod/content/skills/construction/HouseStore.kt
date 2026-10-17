package org.rsmod.content.skills.construction

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.util.Properties

data class PlacedRoom(val gx: Int, val gz: Int, val type: RoomType, val rot: Int)

/** Built furniture keyed by the hotspot tile, relative to the house region's south-west. */
data class FurnKey(val dx: Int, val dz: Int, val level: Int, val layer: Int)

data class BuiltFurn(val hotspotId: Int, val furnId: Int)

class HouseLayout {
    val rooms = LinkedHashMap<Pair<Int, Int>, PlacedRoom>()
    val furniture = LinkedHashMap<FurnKey, BuiltFurn>()

    fun room(gx: Int, gz: Int): PlacedRoom? = rooms[gx to gz]

    companion object {
        /** OSRS starter house: a garden with a parlour to its north. */
        fun starter(): HouseLayout = HouseLayout().apply {
            rooms[3 to 3] = PlacedRoom(3, 3, RoomType.GARDEN, 0)
            rooms[3 to 4] = PlacedRoom(3, 4, RoomType.PARLOUR, 0)
        }
    }
}

/**
 * Per-character house save: `.data/construction/<characterId>.properties` (git-ignored `.data`),
 * same pattern as FarmingStore. Keys:
 *   room.<gx>.<gz>   = ROOMTYPE,rotation
 *   furn.<dx>.<dz>.<level>.<layer> = hotspotLocId,furnitureLocId
 */
object HouseStore {
    private val dir: Path = Path.of(".data", "construction")

    fun load(characterId: Int): HouseLayout? {
        val file = dir.resolve("$characterId.properties")
        if (!Files.exists(file)) return null
        val props = Properties()
        Files.newBufferedReader(file).use { props.load(it) }
        val out = HouseLayout()
        for (k in props.stringPropertyNames()) {
            val parts = k.split('.')
            val v = props.getProperty(k).split(',')
            runCatching {
                when (parts[0]) {
                    "room" -> {
                        val gx = parts[1].toInt(); val gz = parts[2].toInt()
                        out.rooms[gx to gz] = PlacedRoom(gx, gz, RoomType.valueOf(v[0]), v[1].toInt())
                    }
                    "furn" -> out.furniture[FurnKey(parts[1].toInt(), parts[2].toInt(), parts[3].toInt(), parts[4].toInt())] =
                        BuiltFurn(v[0].toInt(), v[1].toInt())
                }
            }
        }
        return if (out.rooms.isEmpty()) null else out
    }

    fun save(characterId: Int, house: HouseLayout) {
        Files.createDirectories(dir)
        val props = Properties()
        for (r in house.rooms.values) props.setProperty("room.${r.gx}.${r.gz}", "${r.type.name},${r.rot}")
        for ((k, f) in house.furniture) props.setProperty("furn.${k.dx}.${k.dz}.${k.level}.${k.layer}", "${f.hotspotId},${f.furnId}")
        val tmp = dir.resolve("$characterId.properties.tmp")
        Files.newBufferedWriter(tmp).use { props.store(it, "OSRS-port player-owned house") }
        Files.move(tmp, dir.resolve("$characterId.properties"), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE)
    }
}
