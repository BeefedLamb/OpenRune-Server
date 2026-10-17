package dev.openrune.tools

import dev.openrune.cache.tools.item.ItemSpriteFactory
import dev.openrune.filesystem.Cache
import java.io.File
import java.nio.file.Path

/**
 * Dumps the worn-model info of every item that has a male/female body model, for the Unity client
 * to draw equipment on players (read-only on the cache). Identkits (body parts) are decoded by
 * export_player_models.py instead: the library's identkit decoder rejects rev 240's i32 model
 * opcodes (5 / 70-79) and silently drops most kits.
 *
 * Output: player_items.txt, tab-separated, one record per line:
 *  I  itemId  male(m0,m1,m2)  female(f0,f1,f2)  maleOffset  femaleOffset  recolours  retextures
 * (-1 = no model). export_player_models.py then writes the referenced models to player_models.bin.gz.
 *
 * Usage: exportPlayerParts [cacheDir] [outFile]
 */
fun main(args: Array<String>) {
    val cacheDir = Path.of(args.getOrElse(0) { ".data/cache/LIVE" })
    val out = File(args.getOrElse(1) { "osrs-port-tools/unity/Assets/StreamingAssets/world/player_items.txt" })
    out.parentFile.mkdirs()
    val cache = Cache.load(cacheDir)
    val items = ItemSpriteFactory.fromCache(cache).items

    fun pairs(a: List<Int>?, b: List<Int>?): String {
        if (a == null || b == null) return ""
        return a.indices.filter { it < b.size }.joinToString(",") { "${a[it] and 0xFFFF}:${b[it] and 0xFFFF}" }
    }

    val lines = ArrayList<String>()
    var itemCount = 0
    for (id in items.keys.sorted()) {
        val t = items[id] ?: continue
        if (t.maleModel0 < 0 && t.femaleModel0 < 0 && t.maleModel1 < 0 && t.femaleModel1 < 0) continue
        itemCount++
        lines += listOf(
            "I", id,
            "${t.maleModel0},${t.maleModel1},${t.maleModel2}",
            "${t.femaleModel0},${t.femaleModel1},${t.femaleModel2}",
            t.maleOffset, t.femaleOffset,
            pairs(t.originalColours, t.modifiedColours),
            pairs(t.originalTextureColours, t.modifiedTextureColours),
        ).joinToString("\t")
    }
    out.writeText("# OPI1\n" + lines.joinToString("\n") + "\n")
    cache.close()
    println("Wrote $itemCount wearable items to ${out.absolutePath}")
}
