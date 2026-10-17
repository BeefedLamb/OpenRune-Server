package org.rsmod.content.skills.farming

import java.nio.file.Files
import java.nio.file.Path
import java.util.Properties

enum class CropState { EMPTY, GROWING, HEALTH_CHECK, HARVESTABLE, DISEASED, DEAD, STUMP }

/** Persistent state of one patch for one player. */
class PatchData(
    val locId: Int,
    var x: Int,
    var z: Int,
    var level: Int,
    var varbit: Int,
    val kind: PatchKind,
) {
    var weeds: Int = 3
    var crop: String? = null
    var state: CropState = CropState.EMPTY
    var stage: Int = 0
    /** 0 none, 1 compost, 2 supercompost, 3 ultracompost. */
    var compost: Int = 0
    var watered: Boolean = false
    /** Harvest lives (allotment/herb/hops) or berries/fruit left (bush/fruit tree) or logs-left roll. */
    var lives: Int = 0
    var protected: Boolean = false
    /** Wall-clock minute (epoch) of the last processed growth step. */
    var lastMinute: Long = 0

    fun encode(): String = listOf(
        x, z, level, varbit, kind.name, weeds, crop ?: "-", state.name, stage, compost,
        if (watered) 1 else 0, lives, if (protected) 1 else 0, lastMinute,
    ).joinToString(",")

    companion object {
        fun decode(locId: Int, s: String): PatchData? = runCatching {
            val p = s.split(',')
            PatchData(locId, p[0].toInt(), p[1].toInt(), p[2].toInt(), p[3].toInt(), PatchKind.valueOf(p[4])).apply {
                weeds = p[5].toInt()
                crop = p[6].takeIf { it != "-" }
                state = CropState.valueOf(p[7])
                stage = p[8].toInt()
                compost = p[9].toInt()
                watered = p[10] == "1"
                lives = p[11].toInt()
                protected = p[12] == "1"
                lastMinute = p[13].toLong()
            }
        }.getOrNull()
    }
}

/**
 * Per-character farming save: `.data/farming/<characterId>.properties` (the `.data` folder is
 * git-ignored). Kept outside the varp tables on purpose: timestamps are 64-bit and patch state
 * must survive regardless of which transmit varbits the cache marks as perm.
 */
object FarmingStore {
    private val dir: Path = Path.of(".data", "farming")

    fun load(characterId: Int): MutableMap<Int, PatchData> {
        val file = dir.resolve("$characterId.properties")
        val out = HashMap<Int, PatchData>()
        if (!Files.exists(file)) return out
        val props = Properties()
        Files.newBufferedReader(file).use { props.load(it) }
        for (k in props.stringPropertyNames()) {
            val id = k.toIntOrNull() ?: continue
            PatchData.decode(id, props.getProperty(k))?.let { out[id] = it }
        }
        return out
    }

    fun save(characterId: Int, patches: Map<Int, PatchData>) {
        Files.createDirectories(dir)
        val props = Properties()
        for ((id, p) in patches) props.setProperty(id.toString(), p.encode())
        val tmp = dir.resolve("$characterId.properties.tmp")
        Files.newBufferedWriter(tmp).use { props.store(it, "OSRS-port farming patches") }
        Files.move(tmp, dir.resolve("$characterId.properties"),
            java.nio.file.StandardCopyOption.REPLACE_EXISTING, java.nio.file.StandardCopyOption.ATOMIC_MOVE)
    }
}
