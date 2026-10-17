package org.rsmod.content.skills.construction

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import jakarta.inject.Inject
import org.rsmod.api.player.events.HouseLayoutEvent
import org.rsmod.api.player.events.HouseRoomView
import org.rsmod.api.player.output.mes
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.player.stat.basePrayerLvl
import org.rsmod.api.player.stat.prayerLvl
import org.rsmod.api.repo.loc.LocRepository
import org.rsmod.api.repo.region.RegionRepository
import org.rsmod.api.repo.region.RegionTemplate
import org.rsmod.api.script.onCommand
import org.rsmod.api.script.onOpLoc1
import org.rsmod.api.script.onOpLoc2
import org.rsmod.api.script.onOpLoc3
import org.rsmod.api.script.onOpLoc4
import org.rsmod.api.script.onOpLoc5
import org.rsmod.api.script.onPlayerLogout
import org.rsmod.events.EventBus
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.util.PathingEntityCommon
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.game.loc.LocEntity
import org.rsmod.game.loc.LocInfo
import org.rsmod.game.region.Region
import org.rsmod.map.CoordGrid
import org.rsmod.map.zone.ZoneKey
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import org.rsmod.routefinder.collision.CollisionFlagMap

/**
 * Construction + player-owned house (POH).
 *
 * The house is a real dynamic region: every room is the cache's own 8x8 template chunk (basic
 * wood style, map square 29,110) copied into a private small region via [RegionTemplate], rotated
 * per room. Furniture: the copied hotspot locs ("Chair space", op5 "Build") are swapped for the
 * cache's furniture loc (op5 "Remove") with the hotspot's own shape/angle; removing swaps back.
 * Layout + furniture persist in [HouseStore]; the instance is rebuilt from it on every entry and
 * after every room change (old region is reclaimed by the engine once empty).
 */
class ConstructionScript
@Inject
constructor(
    private val regionRepo: RegionRepository,
    private val locRepo: LocRepository,
    private val collision: CollisionFlagMap,
    private val eventBus: EventBus,
) : PluginScript() {
    private val logger = InlineLogger()

    private class Session(val house: HouseLayout, var region: Region, var buildMode: Boolean)

    private val sessions = HashMap<Int, Session>()
    private val houses = HashMap<Int, HouseLayout>()
    private val furnitureById = HashMap<Int, Pair<Int, Furniture>>() // furnLocId -> (any hotspotId, furn)
    private val hotspots = HashMap<Int, List<Furniture>>()
    private val nails = ArrayList<String>()

    override fun ScriptContext.startup() {
        nails += ConstructionData.NAILS.filter { obj(it) }
        if (nails.isEmpty()) logger.warn { "Construction: no nail item names resolved" }

        var furnCount = 0
        var skipped = 0
        for ((hotspot, raw) in ConstructionData.hotspots) {
            val list = raw.map { f -> f.copy(mats = f.mats.map { it.copy(obj = resolve(it.obj)) }) }
            val ok = list.filter { f ->
                val good = f.mats.all { it.obj == ConstructionData.NAIL_TOKEN || obj(it.obj) } &&
                    ServerCacheManager.getObject(f.locId) != null
                if (!good) { skipped++; logger.warn { "Construction: skipping ${f.name} (unresolved material or loc ${f.locId})" } }
                good
            }
            if (ok.isEmpty() || ServerCacheManager.getObject(hotspot) == null) continue
            hotspots[hotspot] = ok
            ok.forEach { furnitureById.putIfAbsent(it.locId, hotspot to it) }
            furnCount += ok.size
        }

        var regs = 0
        fun reg(id: Int, what: String, block: (dev.openrune.types.ObjectServerType) -> Unit) {
            val type = ServerCacheManager.getObject(id) ?: return
            runCatching { block(type); regs++ }
                .onFailure { logger.warn { "Construction: could not register $what on loc $id: ${it.message}" } }
        }

        for (id in hotspots.keys) reg(id, "build") { t -> onOpLoc5(t) { build(it.loc) } }
        for (id in furnitureById.keys) reg(id, "remove") { t -> onOpLoc5(t) { remove(it.loc) } }
        for (id in ConstructionData.DOOR_HOTSPOTS) reg(id, "door build") { t -> onOpLoc5(t) { doorBuild(it.loc) } }
        for (id in ConstructionData.ALTAR_IDS) reg(id, "pray") { t -> onOpLoc1(t) { pray() } }
        reg(ConstructionData.EXIT_PORTAL, "exit portal") { t -> onOpLoc1(t) { leave() } }
        reg(ConstructionData.HOUSE_PORTAL, "house portal enter") { t -> onOpLoc1(t) { enterHouse(player, build = false) } }
        reg(ConstructionData.HOUSE_PORTAL, "house portal home") { t -> onOpLoc2(t) { enterHouse(player, build = false) } }
        reg(ConstructionData.HOUSE_PORTAL, "house portal build") { t -> onOpLoc3(t) { enterHouse(player, build = true) } }
        reg(ConstructionData.HOUSE_PORTAL, "house portal friend") { t -> onOpLoc4(t) { mes("Visiting a friend's house isn't available yet.") } }

        onCommand("poh") {
            desc = "Enter your player-owned house (::poh build for build mode, ::poh leave)"
            cheat {
                when (args.firstOrNull()) {
                    "leave" -> leaveDirect(player)
                    "build" -> enterHouse(player, build = true)
                    else -> enterHouse(player, build = false)
                }
            }
        }

        onPlayerLogout {
            val id = player.characterId
            houses[id]?.let { h -> runCatching { HouseStore.save(id, h) } }
            val s = sessions.remove(id)
            if (s != null && inside(player.coords, s.region)) player.coords = RIMMINGTON
            houses.remove(id)
        }

        logger.info {
            "Construction: ${RoomType.entries.size} room types, ${hotspots.size} hotspots / $furnCount furniture " +
                "($skipped skipped), $regs loc ops registered, nails=${nails.size}."
        }
        if (System.getProperty("construction.verify") != null || System.getenv("CONSTRUCTION_VERIFY") != null) dump()
    }

    // ------------------------------------------------------------------ house instance

    private fun house(player: Player): HouseLayout =
        houses.getOrPut(player.characterId) {
            runCatching { HouseStore.load(player.characterId) }
                .onFailure { logger.warn { "Construction: failed to load house for ${player.username}: $it" } }
                .getOrNull() ?: HouseLayout.starter()
        }

    private fun template(house: HouseLayout) =
        RegionTemplate.create {
            for (r in house.rooms.values) {
                val zone = ZoneKey(r.type.chunkX, r.type.chunkZ, 0)
                this[GRID_OFFSET + r.gx, GRID_OFFSET + r.gz, 0] = when (r.rot and 3) {
                    1 -> zone.rotate90()
                    2 -> zone.rotate180()
                    3 -> zone.rotate270()
                    else -> org.rsmod.game.region.zone.RegionZoneCopy(zone, 0, null)
                }
            }
        }

    /** Builds a fresh region for [house], restores furniture and door openings. */
    private fun build(house: HouseLayout, buildMode: Boolean): Region? {
        val region = regionRepo.add(template(house)) ?: return null
        val sw = region.southWest
        // Furniture.
        val stale = ArrayList<FurnKey>()
        for ((k, f) in house.furniture) {
            val coords = CoordGrid(sw.x + k.dx, sw.z + k.dz, k.level)
            val loc = locRepo.findAll(coords).firstOrNull { it.layer == k.layer && it.id == f.hotspotId }
            if (loc == null) { stale += k; continue }
            swap(loc, f.furnId)
        }
        stale.forEach { house.furniture.remove(it) }
        // Doors between two adjacent rooms are opened (hotspot wall removed on both sides).
        for (r in house.rooms.values) {
            for ((dx, dz) in DIRS) {
                if (house.room(r.gx + dx, r.gz + dz) == null) continue
                for (loc in edgeLocs(region, r.gx, r.gz, dx, dz)) {
                    if (loc.id in ConstructionData.DOOR_HOTSPOTS) locRepo.del(loc, Int.MAX_VALUE)
                }
            }
        }
        if (!buildMode) hideHotspots(region, house)
        return region
    }

    private fun edgeLocs(region: Region, gx: Int, gz: Int, dx: Int, dz: Int): List<LocInfo> {
        val zx = region.southWest.x + (GRID_OFFSET + gx) * 8
        val zz = region.southWest.z + (GRID_OFFSET + gz) * 8
        val out = ArrayList<LocInfo>()
        for (i in 0 until 8) {
            val (lx, lz) = when {
                dx == -1 -> 0 to i
                dx == 1 -> 7 to i
                dz == -1 -> i to 0
                else -> i to 7
            }
            out += locRepo.findAll(CoordGrid(zx + lx, zz + lz, 0)).toList()
        }
        return out
    }

    private fun hideHotspots(region: Region, house: HouseLayout) {
        for (r in house.rooms.values) {
            val zone = ZoneKey.from(CoordGrid(region.southWest.x + (GRID_OFFSET + r.gx) * 8, region.southWest.z + (GRID_OFFSET + r.gz) * 8, 0))
            for (loc in locRepo.findAll(zone).toList()) {
                val name = ServerCacheManager.getObject(loc.id)?.name ?: continue
                if (name.endsWith(" space", ignoreCase = true) || name.startsWith("Space") ||
                    name.endsWith(" space 1") || name.endsWith(" space 2")) {
                    locRepo.del(loc, Int.MAX_VALUE)
                }
            }
        }
    }

    private fun swap(loc: LocInfo, intoId: Int) {
        locRepo.del(loc, Int.MAX_VALUE)
        locRepo.add(LocInfo(loc.layer, loc.coords, LocEntity(intoId, loc.shapeId, loc.angleId)), Int.MAX_VALUE)
    }

    private fun enterHouse(player: Player, build: Boolean) {
        val id = player.characterId
        sessions.remove(id)
        val house = house(player)
        val region = build(house, build)
        if (region == null) {
            player.mesDirect("There is no room for your house right now. Try again shortly.")
            return
        }
        val session = Session(house, region, build)
        sessions[id] = session
        PathingEntityCommon.telejump(player, collision, spawn(house, region))
        publishLayout(player, session)
        player.mesDirect(if (build) "You enter your house in building mode." else "You enter your house.")
    }

    private fun spawn(house: HouseLayout, region: Region): CoordGrid {
        val start = house.rooms.values.firstOrNull { it.type == RoomType.GARDEN } ?: house.rooms.values.first()
        val (lx, lz) = rotateLocal(SPAWN_LOCAL.first, SPAWN_LOCAL.second, start.rot)
        return CoordGrid(region.southWest.x + (GRID_OFFSET + start.gx) * 8 + lx, region.southWest.z + (GRID_OFFSET + start.gz) * 8 + lz, 0)
    }

    private fun ProtectedAccess.leave() {
        leaveDirect(player)
    }

    private fun leaveDirect(player: Player) {
        val s = sessions.remove(player.characterId)
        houses[player.characterId]?.let { h -> runCatching { HouseStore.save(player.characterId, h) } }
        if (s == null || !inside(player.coords, s.region)) {
            player.mesDirect("You're not in your house.")
            publishLayout(player, null)
            return
        }
        PathingEntityCommon.telejump(player, collision, RIMMINGTON)
        publishLayout(player, null)
    }

    /**
     * Publishes the assembled house (one entry per room: instance chunk, template chunk, rotation)
     * for front-ends such as the Unity link; [s] null = the player left the house.
     */
    private fun publishLayout(player: Player, s: Session?) {
        val rooms =
            if (s == null) emptyList()
            else {
                val sw = s.region.southWest
                s.house.rooms.values.map { r ->
                    HouseRoomView(
                        dstX = sw.x + (GRID_OFFSET + r.gx) * 8,
                        dstZ = sw.z + (GRID_OFFSET + r.gz) * 8,
                        level = 0,
                        srcX = r.type.chunkX * 8,
                        srcZ = r.type.chunkZ * 8,
                        rotation = r.rot and 3,
                        name = r.type.display,
                    )
                }
            }
        eventBus.publish(HouseLayoutEvent(player, rooms))
    }

    private fun session(player: Player): Session? {
        val s = sessions[player.characterId] ?: return null
        if (!inside(player.coords, s.region)) { sessions.remove(player.characterId); return null }
        return s
    }

    private fun inside(c: CoordGrid, r: Region) =
        c.x >= r.southWest.x && c.z >= r.southWest.z && c.x < r.northEast.x && c.z < r.northEast.z

    // ------------------------------------------------------------------ furniture

    private suspend fun ProtectedAccess.build(loc: BoundLocInfo) {
        val s = session(player) ?: return mes("You can only build in your own house.")
        if (!s.buildMode) return mes("You need to be in building mode to do that.")
        val options = hotspots[loc.id] ?: return mes("Nothing can be built here yet.")
        val level = statBase(STAT)
        val labels = options.map { f -> "${f.name} (lvl ${f.level})" + if (level < f.level) " - too low" else "" }
        val idx = pick(labels, "Build ${ServerCacheManager.getObject(loc.id)?.name ?: "furniture"}") ?: return
        val furn = options[idx]
        if (level < furn.level) return mes("You need a Construction level of ${furn.level} to build that.")
        // Group furniture (curtains) fills every hotspot of the same id in the room.
        val targets = if (furn.group) groupLocs(s, loc) else listOf(LocInfo(loc.layer, loc.coords, loc.entity))
        val mult = targets.size.coerceAtLeast(1)
        if (!inv.contains(ConstructionData.HAMMER)) return mes("You need a hammer to build furniture.")
        if (furn.mats.any { it.obj in ConstructionData.WOOD || it.obj == ConstructionData.NAIL_TOKEN } &&
            !inv.contains(ConstructionData.SAW)) return mes("You need a saw to build furniture.")
        for (m in furn.mats) {
            if (have(m) < m.count * mult) return mes("You need ${m.count * mult} x ${matName(m)} to build that.")
        }
        for (m in furn.mats) take(m, m.count * mult)
        anim("seq.human_pohbuild_hammer".takeIf { seqExists(it) } ?: "seq.human_smithing")
        val sw = s.region.southWest
        for (t in targets) {
            swap(t, furn.locId)
            s.house.furniture[FurnKey(t.coords.x - sw.x, t.coords.z - sw.z, t.coords.level, t.layer)] = BuiltFurn(t.id, furn.locId)
        }
        statAdvance(STAT, furn.xp * mult)
        mes("You build a ${furn.name.lowercase()}.")
        save(player)
    }

    private suspend fun ProtectedAccess.remove(loc: BoundLocInfo) {
        val s = session(player) ?: return
        if (!s.buildMode) return mes("You need to be in building mode to do that.")
        val sw = s.region.southWest
        val key = FurnKey(loc.coords.x - sw.x, loc.coords.z - sw.z, loc.coords.level, loc.layer)
        val built = s.house.furniture[key] ?: return mes("You can't remove that.")
        val name = furnitureById[built.furnId]?.second?.name ?: "furniture"
        val ok = choice2("Yes", true, "No", false, title = "Really remove the ${name.lowercase()}?")
        if (!ok) return
        val group = furnitureById[built.furnId]?.second?.group == true
        val keys = if (group) s.house.furniture.filter { (k, f) -> f.furnId == built.furnId && sameRoom(k, key) }.keys.toList() else listOf(key)
        for (k in keys) {
            val f = s.house.furniture.remove(k) ?: continue
            val c = CoordGrid(sw.x + k.dx, sw.z + k.dz, k.level)
            locRepo.findAll(c).firstOrNull { it.layer == k.layer && it.id == f.furnId }?.let { swap(it, f.hotspotId) }
        }
        mes("You remove the ${name.lowercase()}.")
        save(player)
    }

    private fun sameRoom(a: FurnKey, b: FurnKey) = a.dx / 8 == b.dx / 8 && a.dz / 8 == b.dz / 8 && a.level == b.level

    private fun groupLocs(s: Session, loc: BoundLocInfo): List<LocInfo> {
        val zone = ZoneKey.from(loc.coords)
        return locRepo.findAll(zone).filter { it.id == loc.id }.toList()
    }

    private suspend fun ProtectedAccess.pray() {
        if (player.prayerLvl >= player.basePrayerLvl) return mes("You already have full Prayer Points.")
        anim("seq.human_pray")
        statRestore("stat.prayer")
        mes("You recharge your Prayer Points.")
    }

    // ------------------------------------------------------------------ rooms

    private suspend fun ProtectedAccess.doorBuild(loc: BoundLocInfo) {
        val s = session(player) ?: return mes("You can only build in your own house.")
        if (!s.buildMode) return mes("You need to be in building mode to do that.")
        val sw = s.region.southWest
        val rx = loc.coords.x - sw.x
        val rz = loc.coords.z - sw.z
        val gx = rx / 8 - GRID_OFFSET
        val gz = rz / 8 - GRID_OFFSET
        val lx = rx % 8
        val lz = rz % 8
        val (dx, dz) = when {
            lx == 0 -> -1 to 0
            lx == 7 -> 1 to 0
            lz == 0 -> 0 to -1
            else -> 0 to 1
        }
        val tx = gx + dx
        val tz = gz + dz
        if (tx !in 0 until GRID || tz !in 0 until GRID) return mes("You can't build any further in that direction.")
        val existing = s.house.room(tx, tz)
        if (existing != null) {
            val rem = choice2("Remove the ${existing.type.display.lowercase()}", true, "Cancel", false)
            if (!rem) return
            if (s.house.rooms.size <= 1) return mes("You can't remove your last room.")
            removeRoom(s, existing)
            rebuild(s)
            mes("You remove the ${existing.type.display.lowercase()}.")
            return
        }
        if (s.house.rooms.size >= maxRooms(statBase(STAT))) return mes("You can't build any more rooms at your Construction level.")
        val level = statBase(STAT)
        val types = RoomType.entries.filter { it.level <= level }
        val labels = types.map { "${it.display} (${it.cost} gp)" }
        val idx = pick(labels, "Build a room") ?: return
        val type = types[idx]
        val rot = choice4("Facing north (0)", 0, "Rotated 90", 1, "Rotated 180", 2, "Rotated 270", 3, title = "Room rotation")
        if (inv.count(ConstructionData.COINS) < type.cost) return mes("You need ${type.cost} coins to build a ${type.display.lowercase()}.")
        invDel(inv, ConstructionData.COINS, type.cost)
        s.house.rooms[tx to tz] = PlacedRoom(tx, tz, type, rot)
        rebuild(s)
        mes("You build a ${type.display.lowercase()}.")
    }

    private fun removeRoom(s: Session, r: PlacedRoom) {
        s.house.rooms.remove(r.gx to r.gz)
        val minX = (GRID_OFFSET + r.gx) * 8
        val minZ = (GRID_OFFSET + r.gz) * 8
        s.house.furniture.keys.removeIf { it.dx in minX until minX + 8 && it.dz in minZ until minZ + 8 }
    }

    private fun ProtectedAccess.rebuild(s: Session) {
        val old = s.region
        val rel = player.coords.let { CoordGrid(it.x - old.southWest.x, it.z - old.southWest.z, it.level) }
        val region = build(s.house, s.buildMode) ?: return mes("Your house could not be rebuilt right now.")
        s.region = region
        save(player)
        PathingEntityCommon.telejump(player, collision, CoordGrid(region.southWest.x + rel.x, region.southWest.z + rel.z, rel.level))
        publishLayout(player, s)
    }

    /** OSRS room cap by Construction level. Not verified against the wiki table: see report. */
    private fun maxRooms(level: Int): Int = (20 + (level - 50).coerceAtLeast(0) / 2).coerceAtMost(GRID * GRID)

    // ------------------------------------------------------------------ helpers

    private suspend fun ProtectedAccess.pick(labels: List<String>, title: String): Int? {
        var page = 0
        while (true) {
            val start = page * 4
            val slice = labels.subList(start, minOf(labels.size, start + 4))
            val more = start + 4 < labels.size
            val opts = slice.mapIndexed { i, l -> l to (start + i) }.toMutableList()
            if (more) opts += "More..." to -1
            if (opts.size == 1) return opts[0].second
            val r = when (opts.size) {
                2 -> choice2(opts[0].first, opts[0].second, opts[1].first, opts[1].second, title = title)
                3 -> choice3(opts[0].first, opts[0].second, opts[1].first, opts[1].second, opts[2].first, opts[2].second, title = title)
                4 -> choice4(opts[0].first, opts[0].second, opts[1].first, opts[1].second, opts[2].first, opts[2].second, opts[3].first, opts[3].second, title = title)
                else -> choice5(opts[0].first, opts[0].second, opts[1].first, opts[1].second, opts[2].first, opts[2].second, opts[3].first, opts[3].second, opts[4].first, opts[4].second, title = title)
            }
            if (r >= 0) return r
            page = if (more) page + 1 else 0
        }
    }

    private fun ProtectedAccess.have(m: Mat): Int =
        if (m.obj == ConstructionData.NAIL_TOKEN) nails.sumOf { inv.count(it) } else inv.count(m.obj)

    private fun ProtectedAccess.take(m: Mat, count: Int) {
        if (m.obj != ConstructionData.NAIL_TOKEN) { invDel(inv, m.obj, count); return }
        var left = count
        for (n in nails) {
            if (left <= 0) break
            val c = minOf(inv.count(n), left)
            if (c > 0) { invDel(inv, n, c); left -= c }
        }
    }

    private fun matName(m: Mat): String =
        if (m.obj == ConstructionData.NAIL_TOKEN) "nails"
        else runCatching { ServerCacheManager.getItem(m.obj.asRSCM(RSCMType.OBJ))?.name }.getOrNull() ?: m.obj.removePrefix("obj.")

    private fun save(player: Player) {
        val h = houses[player.characterId] ?: return
        runCatching { HouseStore.save(player.characterId, h) }
            .onFailure { logger.warn { "Construction: save failed for ${player.username}: $it" } }
    }

    private fun Player.mesDirect(text: String) {
        mes(text)
    }

    private fun dump() {
        val sb = StringBuilder("Construction verification:\n")
        val mats = ConstructionData.hotspots.values.flatten().flatMap { it.mats }.map { it.obj }.toSet() +
            ConstructionData.NAILS + listOf(ConstructionData.HAMMER, ConstructionData.SAW, ConstructionData.COINS)
        for (m in mats.map { resolve(it) }) {
            val id = runCatching { m.asRSCM(RSCMType.OBJ) }.getOrNull()
            sb.append("  $m -> ${id ?: "<UNRESOLVED>"} '${id?.let { ServerCacheManager.getItem(it)?.name }}'\n")
        }
        for ((h, list) in ConstructionData.hotspots) {
            sb.append("  hotspot $h '${ServerCacheManager.getObject(h)?.name}': ")
            sb.append(list.joinToString { "${it.locId}='${ServerCacheManager.getObject(it.locId)?.name}'" }).append('\n')
        }
        for (r in RoomType.entries) sb.append("  room ${r.name} zone ${r.chunkX},${r.chunkZ}\n")
        logger.info { sb.toString() }
        runCatching { selfTest() }.onFailure { logger.warn(it) { "Construction self-test crashed" } }
    }

    /** Builds a real house region (no player) and checks rooms/hotspots/furniture swap. */
    private fun selfTest() {
        val house = HouseLayout.starter().apply { rooms[2 to 4] = PlacedRoom(2, 4, RoomType.KITCHEN, 1) }
        val region = build(house, buildMode = true) ?: return logger.warn { "Construction self-test: no region space" }
        val sb = StringBuilder("Construction self-test: region sw=${region.southWest} ne=${region.northEast}\n")
        for (r in house.rooms.values) {
            val zone = ZoneKey.from(CoordGrid(region.southWest.x + (GRID_OFFSET + r.gx) * 8, region.southWest.z + (GRID_OFFSET + r.gz) * 8, 0))
            val names = locRepo.findAll(zone).map { ServerCacheManager.getObject(it.id)?.name ?: "?" }.groupingBy { it }.eachCount()
            sb.append("  ${r.type} rot=${r.rot} @${r.gx},${r.gz}: $names\n")
        }
        val pz = ZoneKey.from(CoordGrid(region.southWest.x + (GRID_OFFSET + 3) * 8, region.southWest.z + (GRID_OFFSET + 4) * 8, 0))
        val chair = locRepo.findAll(pz).firstOrNull { it.id == 4517 }
        if (chair == null) sb.append("  FAIL: no chair space (4517) in parlour zone\n") else {
            swap(chair, 6752)
            val built = locRepo.findAll(chair.coords).any { it.id == 6752 }
            val gone = locRepo.findAll(chair.coords).none { it.id == 4517 }
            sb.append("  chair swap at ${chair.coords} shape=${chair.shapeId} angle=${chair.angleId}: built=$built hotspotGone=$gone\n")
            locRepo.findAll(chair.coords).firstOrNull { it.id == 6752 }?.let { swap(it, 4517) }
            sb.append("  revert ok=${locRepo.findAll(chair.coords).any { it.id == 4517 }}\n")
        }
        logger.info { sb.toString() }
    }

    /** "#1234" -> the cache's RSCM name for item 1234 (used where the internal name isn't known). */
    private fun resolve(s: String): String {
        if (!s.startsWith("#")) return s
        val id = s.substring(1).toInt()
        return runCatching { "obj." + dev.openrune.rscm.RSCM.getReverseMapping(RSCMType.OBJ, id).removePrefix("obj.") }
            .getOrDefault(s)
    }

    private fun obj(name: String) = runCatching { name.asRSCM(RSCMType.OBJ) }.isSuccess

    private fun seqExists(name: String) = runCatching { name.asRSCM(RSCMType.SEQ) }.isSuccess

    private companion object {
        const val STAT = "stat.construction"
        /** Rooms sit at zones GRID_OFFSET..GRID_OFFSET+GRID-1 inside a 16x16-zone small region. */
        const val GRID_OFFSET = 4
        const val GRID = 7
        /** Free floor tile in an unrotated garden (west of the centrepiece). */
        val SPAWN_LOCAL = 1 to 4
        /** Outside the Rimmington house portal (portal loc 15478 at 2951,3222). Approximate. */
        val RIMMINGTON = CoordGrid(2954, 3224, 0)
        val DIRS = listOf(-1 to 0, 1 to 0, 0 to -1, 0 to 1)

        /** Local tile (0..7) rotated clockwise [rot] quarter-turns within an 8x8 zone. */
        fun rotateLocal(x: Int, z: Int, rot: Int): Pair<Int, Int> = when (rot and 3) {
            1 -> z to 7 - x
            2 -> 7 - x to 7 - z
            3 -> 7 - z to x
            else -> x to z
        }
    }
}
