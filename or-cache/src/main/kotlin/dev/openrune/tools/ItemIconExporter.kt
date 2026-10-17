package dev.openrune.tools

import dev.openrune.cache.tools.item.ItemSpriteFactory
import dev.openrune.filesystem.Cache
import java.awt.image.BufferedImage
import java.io.File
import java.nio.file.Path
import javax.imageio.ImageIO

/**
 * Renders every item's inventory icon with OpenRune's client-accurate [ItemSpriteFactory] and packs
 * them into sheets for the Unity client (osrs-port-tools/unity/Assets/StreamingAssets/items/).
 *
 * Output:
 *  - icons_<page>.png : 32 x 32 grid of 36x32 cells, 1024 item ids per sheet; cell = id % 1024 at
 *                       column cell % 32, row cell / 32 (row 0 at the top of the image).
 *  - icons.txt        : "<id>\t<name>" for every id that has a non-empty icon.
 *
 * Usage: exportItemIcons [cacheDir] [outDir]   (read-only on the cache)
 */
private const val CELL_W = 36
private const val CELL_H = 32
private const val PER_PAGE = 1024
private const val COLS = 32

fun main(args: Array<String>) {
    val cacheDir = Path.of(args.getOrElse(0) { ".data/cache/LIVE" })
    val outDir = File(args.getOrElse(1) { "osrs-port-tools/unity/Assets/StreamingAssets/items" })
    outDir.mkdirs()
    val cache = Cache.load(cacheDir)
    val factory = ItemSpriteFactory.fromCache(cache)
    val items = factory.items
    println("Rendering icons for ${items.size} item definitions from $cacheDir")

    val pages = HashMap<Int, BufferedImage>()
    val lines = ArrayList<String>()
    var failed = 0
    var empty = 0
    var oversize = 0
    for ((n, id) in items.keys.sorted().withIndex()) {
        val type = items[id] ?: continue
        val img =
            try {
                factory.item(id).create()
            } catch (t: Throwable) {
                failed++
                null
            } ?: continue
        if (img.width != CELL_W || img.height != CELL_H) oversize++
        var any = false
        loop@ for (y in 0 until img.height) for (x in 0 until img.width) {
            if ((img.getRGB(x, y) ushr 24) != 0) { any = true; break@loop }
        }
        if (!any) { empty++; continue }
        val page = pages.getOrPut(id / PER_PAGE) {
            BufferedImage(COLS * CELL_W, (PER_PAGE / COLS) * CELL_H, BufferedImage.TYPE_INT_ARGB)
        }
        val cell = id % PER_PAGE
        val g = page.createGraphics()
        g.drawImage(img, (cell % COLS) * CELL_W, (cell / COLS) * CELL_H, CELL_W, CELL_H, null)
        g.dispose()
        lines += "$id\t${type.name.replace('\t', ' ')}"
        if (n % 2000 == 0) println("  ... id $id, ${lines.size} icons so far")
    }
    for ((p, img) in pages) ImageIO.write(img, "png", File(outDir, "icons_$p.png"))
    File(outDir, "icons.txt").writeText(
        "# OSI1 cell=${CELL_W}x$CELL_H perPage=$PER_PAGE cols=$COLS\n" + lines.joinToString("\n") + "\n"
    )
    cache.close()
    println("Wrote ${lines.size} icons in ${pages.size} sheets to ${outDir.absolutePath} " +
        "($failed failed, $empty empty, $oversize not ${CELL_W}x$CELL_H)")
}
