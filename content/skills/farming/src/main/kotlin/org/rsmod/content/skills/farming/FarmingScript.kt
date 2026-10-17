package org.rsmod.content.skills.farming

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import dev.openrune.types.ItemServerType
import dev.openrune.types.ObjectServerType
import jakarta.inject.Inject
import org.rsmod.api.game.process.GameLifecycle
import org.rsmod.api.player.events.FarmingPatchEvent
import org.rsmod.api.player.events.FarmingPatchView
import org.rsmod.api.player.output.ChatType
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.player.vars.VarPlayerIntMapSetter
import org.rsmod.api.script.onEvent
import org.rsmod.api.script.onOpLoc1
import org.rsmod.api.script.onOpLoc2
import org.rsmod.api.script.onOpLoc3
import org.rsmod.api.script.onOpLoc4
import org.rsmod.api.script.onOpLoc5
import org.rsmod.api.script.onOpLocU
import org.rsmod.api.script.onPlayerLogin
import org.rsmod.api.script.onPlayerLogout
import org.rsmod.api.repo.loc.LocRepository
import org.rsmod.events.EventBus
import org.rsmod.game.loc.LocInfo
import org.rsmod.map.zone.ZoneKey
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.PlayerList
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.map.CoordGrid
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import kotlin.random.Random

/**
 * Farming: allotments, herbs, flowers, hops, bushes, trees and fruit trees.
 *
 * - Patches are the cache's real multiloc patch locs (`loc.farming_*_patch_*`). Each one is
 *   driven by its own `multiVarBit` (the shared `farming_transmit_*` varbits), read from the cache
 *   at startup. Ops are registered on the base patch loc, which receives every op because the
 *   morphed variants have no handlers of their own.
 * - Per-player patch state lives in [FarmingStore] (`.data/farming/<characterId>.properties`)
 *   with a wall-clock timestamp, so crops keep growing while the player is logged out: on login
 *   and every in-game minute, [process] replays every crop-cycle boundary that has passed.
 * - Growth is aligned to the OSRS-style global clock: a crop with a 10 minute cycle advances at
 *   every wall-clock multiple of 10 minutes (RuneLite Produce tick rates / stage counts).
 * - The nearest patch per transmit varbit (same plane, within [SYNC_RANGE]) is written to the
 *   player's varbits, so a real OSRS client morphs the patch locs; ops resolve through the same
 *   varbits server-side, so the correct Rake/Harvest/Cure/etc. option is used.
 */
class FarmingScript
@Inject
constructor(
    private val playerList: PlayerList,
    private val eventBus: EventBus,
    private val locRepo: LocRepository,
) : PluginScript() {
    private val logger = InlineLogger()

    private val farms = HashMap<Int, MutableMap<Int, PatchData>>()
    private val dirty = HashSet<Int>()
    private val lastSync = HashMap<Int, CoordGrid>()
    private val patchKinds = HashMap<Int, PatchKind>()
    private val crops = ArrayList<Crop>()
    private val tools = HashMap<String, String>()
    /** Patch map locs per zone (static map data, scanned once per zone on demand). */
    private val patchLocs = HashMap<Int, List<LocInfo>>()
    private var cycle = 0

    override fun ScriptContext.startup() {
        for ((key, cands) in listOf(
            "rake" to FarmingCrops.RAKE, "dibber" to FarmingCrops.DIBBER, "spade" to FarmingCrops.SPADE,
            "secateurs" to FarmingCrops.SECATEURS, "plantcure" to FarmingCrops.PLANT_CURE,
            "weeds" to FarmingCrops.WEEDS, "bucket" to FarmingCrops.BUCKET, "vial" to FarmingCrops.VIAL,
            "pot" to FarmingCrops.PLANT_POT, "compost" to FarmingCrops.COMPOST,
            "supercompost" to FarmingCrops.SUPERCOMPOST, "ultracompost" to FarmingCrops.ULTRACOMPOST,
        )) {
            val hit = cands.firstOrNull { obj(it) }
            if (hit == null) logger.warn { "Farming: tool/item '$key' unresolved (${cands.joinToString()})" } else tools[key] = hit
        }
        for (c in FarmingCrops.all) {
            if (obj(c.seed) && obj(c.produce)) crops += c
            else logger.warn { "Farming: skipping crop ${c.key} (seed ${c.seed} ok=${obj(c.seed)}, produce ${c.produce} ok=${obj(c.produce)})" }
        }
        var registered = 0
        for ((name, kind) in patchNames()) {
            val id = idOf(name, RSCMType.LOC) ?: continue
            val type = ServerCacheManager.getObject(id)
            if (type == null || type.multiVarBit <= 0) {
                logger.warn { "Farming: $name has no multivarbit, skipped" }
                continue
            }
            patchKinds[id] = kind
            onOpLoc1(name) { op(it.loc, it.type, 1) }
            onOpLoc2(name) { op(it.loc, it.type, 2) }
            onOpLoc3(name) { op(it.loc, it.type, 3) }
            onOpLoc4(name) { op(it.loc, it.type, 4) }
            onOpLoc5(name) { op(it.loc, it.type, 5) }
            onOpLocU(name) { use(it.loc, it.objType) }
            registered++
        }
        onPlayerLogin { farm(player) ; lastSync.remove(player.characterId) }
        onPlayerLogout {
            val id = player.characterId
            farms[id]?.let { runCatching { FarmingStore.save(id, it) } }
            farms.remove(id); dirty.remove(id); lastSync.remove(id)
        }
        onEvent<GameLifecycle.LateCycle> { tick() }
        logger.info {
            "Farming: registered $registered patches, ${crops.size}/${FarmingCrops.all.size} crops, " +
                "${FarmingPatchStates.rowCount} varbit states."
        }
        if (System.getProperty("farming.verify") != null || System.getenv("FARMING_VERIFY") != null) dump()
    }

    // ------------------------------------------------------------------ state + clock

    private fun farm(player: Player): MutableMap<Int, PatchData> =
        farms.getOrPut(player.characterId) {
            runCatching { FarmingStore.load(player.characterId) }
                .onFailure { e -> logger.warn { "Farming: failed to load save for ${player.username}: $e" } }
                .getOrDefault(HashMap())
                .also { m -> val now = nowMinute(); m.values.forEach { process(m, it, now) } }
        }

    private fun patch(player: Player, loc: BoundLocInfo): PatchData? {
        val kind = patchKinds[loc.id] ?: return null
        val base = ServerCacheManager.getObject(loc.id) ?: return null
        val m = farm(player)
        val p = m.getOrPut(loc.id) {
            PatchData(loc.id, loc.coords.x, loc.coords.z, loc.coords.level, base.multiVarBit, kind)
                .also { it.lastMinute = nowMinute() }
        }
        p.x = loc.coords.x; p.z = loc.coords.z; p.level = loc.coords.level; p.varbit = base.multiVarBit
        process(m, p, nowMinute())
        return p
    }

    private fun tick() {
        cycle++
        if (cycle % 5 != 0) return
        val growth = cycle % GROWTH_CHECK_CYCLES == 0
        val now = nowMinute()
        for (player in playerList) {
            val id = player.characterId
            val m = farms[id] ?: continue
            var changed = false
            if (growth) for (p in m.values) if (process(m, p, now)) changed = true
            if (changed) dirty += id
            val last = lastSync[id]
            if (changed || last == null || last.level != player.coords.level || last.chebyshevDistance(player.coords) >= 8) {
                sync(player)
            }
        }
        if (cycle % SAVE_CYCLES == 0) flush()
    }

    private fun flush() {
        for (id in dirty.toList()) farms[id]?.let { runCatching { FarmingStore.save(id, it) } }
        dirty.clear()
    }

    private fun changed(player: Player) {
        val id = player.characterId
        farms[id]?.let { m -> runCatching { FarmingStore.save(id, m) }.onFailure { dirty += id } }
        sync(player)
    }

    /** Replays every growth-cycle boundary since [PatchData.lastMinute]. Returns true on change. */
    private fun process(m: Map<Int, PatchData>, p: PatchData, now: Long): Boolean {
        if (p.lastMinute <= 0) { p.lastMinute = now; return false }
        var changed = false
        var guard = 0
        while (guard++ < 10_000) {
            val crop = p.crop?.let { FarmingCrops.byKey[it] }
            val rate: Int = when (p.state) {
                CropState.EMPTY -> if (p.weeds >= 3) break else WEED_MINUTES
                CropState.GROWING, CropState.DISEASED -> crop?.tickMinutes ?: break
                CropState.HARVESTABLE ->
                    if (crop != null && crop.regrowMinutes > 0 && p.lives < crop.maxHarvest) crop.regrowMinutes else break
                else -> break
            }
            // Crops follow the global clock (every wall-clock multiple of their cycle); weeds
            // creeping back into an empty patch need a full cycle after the last rake/harvest.
            val next = if (p.state == CropState.EMPTY) p.lastMinute + rate else (p.lastMinute / rate + 1) * rate
            if (next > now) break
            p.lastMinute = next
            step(m, p, crop)
            changed = true
        }
        return changed
    }

    private fun step(m: Map<Int, PatchData>, p: PatchData, crop: Crop?) {
        when (p.state) {
            CropState.EMPTY -> p.weeds = (p.weeds + 1).coerceAtMost(3)
            CropState.DISEASED -> p.state = CropState.DEAD
            CropState.HARVESTABLE -> if (crop != null) p.lives = (p.lives + 1).coerceAtMost(crop.maxHarvest)
            CropState.GROWING -> {
                crop ?: return
                if (p.stage >= 1 && !p.watered && !isProtected(m, p, crop) && Random.nextDouble() < diseaseChance(p)) {
                    p.state = CropState.DISEASED
                    return
                }
                p.stage++
                p.watered = false
                if (p.stage >= crop.stages - 1) {
                    p.stage = crop.stages - 1
                    if (crop.kind.perennial) {
                        p.state = CropState.HEALTH_CHECK
                    } else {
                        p.state = CropState.HARVESTABLE
                        p.lives = if (crop.kind == PatchKind.FLOWER) 1 else BASE_LIVES + p.compost
                    }
                }
            }
            else -> {}
        }
    }

    /** Approximate per-cycle disease chance (see report: not the exact OSRS table). */
    private fun diseaseChance(p: PatchData): Double {
        val base = when (p.kind) {
            PatchKind.TREE, PatchKind.FRUIT_TREE, PatchKind.BUSH -> 0.07
            else -> 0.10
        }
        return base * when (p.compost) { 1 -> 0.5; 2 -> 0.2; 3 -> 0.1; else -> 1.0 }
    }

    private fun isProtected(m: Map<Int, PatchData>, p: PatchData, crop: Crop): Boolean {
        if (p.protected) return true
        if (p.kind != PatchKind.ALLOTMENT) return false
        return m.values.any { f ->
            f.kind == PatchKind.FLOWER && f.state == CropState.HARVESTABLE && f.level == p.level &&
                maxOf(kotlin.math.abs(f.x - p.x), kotlin.math.abs(f.z - p.z)) <= FLOWER_RANGE &&
                FarmingCrops.flowerProtects[f.crop]?.contains(crop.key) == true
        }
    }

    // ------------------------------------------------------------------ varbit sync

    private fun value(p: PatchData): Int {
        val c = p.crop
        val v = if (c == null) null else when (p.state) {
            CropState.GROWING -> FarmingPatchStates.growing(p.kind, c, p.stage, p.watered)
                ?: FarmingPatchStates.growing(p.kind, c, p.stage, false)
            CropState.HEALTH_CHECK -> FarmingPatchStates.checkHealth(p.kind, c)
            CropState.HARVESTABLE -> FarmingPatchStates.harvestable(p.kind, c, p.lives)
            CropState.DISEASED -> FarmingPatchStates.diseased(p.kind, c, p.stage)
            CropState.DEAD -> FarmingPatchStates.dead(p.kind, c, p.stage)
            CropState.STUMP -> FarmingPatchStates.stump(p.kind, c)
            CropState.EMPTY -> null
        }
        return v ?: FarmingPatchStates.weeds(p.kind, if (c == null) p.weeds else 0)
    }

    private fun sync(player: Player) {
        val m = farms[player.characterId] ?: return
        lastSync[player.characterId] = player.coords
        val c = player.coords
        for ((varbitId, group) in m.values.groupBy { it.varbit }) {
            val type = ServerCacheManager.getVarbit(varbitId) ?: continue
            val near = group
                .filter { it.level == c.level && maxOf(kotlin.math.abs(it.x - c.x), kotlin.math.abs(it.z - c.z)) <= SYNC_RANGE }
                .minByOrNull { maxOf(kotlin.math.abs(it.x - c.x), kotlin.math.abs(it.z - c.z)) }
            val v = near?.let { value(it) } ?: 0
            if (player.vars[type] != v) VarPlayerIntMapSetter.set(player, type, v)
        }
        publishPatches(player, m, c)
    }

    /**
     * Tells front-ends (Unity link) which variant loc every patch within [SYNC_RANGE] shows. Each
     * patch uses its own saved state (untouched patches = value 0, fully weeded), resolved through
     * the base loc's cache multiloc table exactly like the OSRS client resolves the varbit.
     */
    private fun publishPatches(player: Player, m: Map<Int, PatchData>, c: CoordGrid) {
        val views = ArrayList<FarmingPatchView>()
        for (loc in patchLocsNear(c)) {
            val lc = loc.coords
            if (lc.level != c.level) continue
            val base = ServerCacheManager.getObject(loc.id) ?: continue
            val p = m[loc.id]?.takeIf { it.x == lc.x && it.z == lc.z && it.level == lc.level }
            val v = if (p != null) value(p) else 0
            val raw = if (v in base.multiLoc.indices) base.multiLoc[v] and 0xFFFF else base.multiDefault and 0xFFFF
            val variant = if (raw == 0xFFFF) -1 else raw
            views += FarmingPatchView(loc.id, variant, v, lc.x, lc.z, lc.level, loc.shapeId, loc.angleId)
        }
        eventBus.publish(FarmingPatchEvent(player, views))
    }

    private fun patchLocsNear(c: CoordGrid): List<LocInfo> {
        val centre = ZoneKey.from(c)
        val r = SYNC_RANGE / 8
        val out = ArrayList<LocInfo>()
        for (dx in -r..r) {
            for (dz in -r..r) {
                val zx = centre.x + dx
                val zz = centre.z + dz
                if (zx < 0 || zz < 0 || zx >= 2048 || zz >= 2048) continue
                val zone = ZoneKey(zx, zz, c.level)
                out += patchLocs.getOrPut(zone.packed) {
                    locRepo.findAll(zone).filter { it.id in patchKinds }.toList()
                }
            }
        }
        return out
    }

    // ------------------------------------------------------------------ ops

    private suspend fun ProtectedAccess.op(loc: BoundLocInfo, vis: ObjectServerType, op: Int) {
        val p = patch(player, loc) ?: return
        sync(player)
        // The event's type is the *base* patch loc (its morphs have no handlers), so resolve the
        // current variant from the player's transmit varbit to read the real op name.
        val action = morph(loc.id)?.actions?.getOpOrNull(op - 1)?.lowercase()?.trim()
            ?: vis.actions.getOpOrNull(op - 1)?.lowercase()?.trim() ?: ""
        when {
            action == "rake" -> rake(p)
            action == "inspect" -> inspect(p)
            action == "guide" -> mes("The Farming guide isn't available yet.")
            action == "harvest" || action == "pick" || action.startsWith("pick-") -> harvest(p)
            action == "cure" -> cure(p, tools["plantcure"])
            action == "prune" -> cure(p, tools["secateurs"])
            action == "clear" -> clear(p)
            action == "check-health" -> checkHealth(p)
            action == "chop down" || action == "chop-down" -> chop(p)
            else -> mes("Nothing interesting happens.")
        }
    }

    private fun ProtectedAccess.morph(baseId: Int): ObjectServerType? {
        val base = ServerCacheManager.getObject(baseId) ?: return null
        val varbit = ServerCacheManager.getVarbit(base.multiVarBit) ?: return null
        val v = player.vars[varbit]
        val id = if (v in base.multiLoc.indices) base.multiLoc[v] and 0xFFFF else base.multiDefault
        return ServerCacheManager.getObject(id)
    }

    private suspend fun ProtectedAccess.use(loc: BoundLocInfo, item: ItemServerType) {
        val p = patch(player, loc) ?: return
        val name = runCatching { dev.openrune.rscm.RSCM.getReverseMapping(RSCMType.OBJ, item.id) }.getOrNull() ?: ""
        val canIndex = FarmingCrops.WATERING_CANS.indexOf(name)
        val crop = crops.firstOrNull { it.seed == name }
        when {
            name == tools["rake"] -> rake(p)
            name == tools["spade"] -> clear(p, anyCrop = true)
            crop != null -> plant(p, crop)
            name == tools["compost"] -> compost(p, name, 1)
            name == tools["supercompost"] -> compost(p, name, 2)
            name == tools["ultracompost"] -> compost(p, name, 3)
            canIndex >= 0 -> water(p, canIndex)
            name == tools["plantcure"] || name == tools["secateurs"] -> cure(p, name)
            name == tools["dibber"] -> mes("Use a seed on the patch to plant it.")
            crops.any { it.seed == name } -> mes("You can't plant that here.")
            else -> mes("Nothing interesting happens.")
        }
    }

    private fun ProtectedAccess.has(tool: String?): Boolean =
        tool != null && (inv.count(tool) > 0 || player.worn.count(tool) > 0)

    private suspend fun ProtectedAccess.rake(p: PatchData) {
        if (p.crop != null) { mes("This patch doesn't need weeding."); return }
        if (p.weeds <= 0) { mes("This patch is already clear of weeds."); return }
        if (!has(tools["rake"])) { mes("You need a rake to weed a farming patch."); return }
        while (p.weeds > 0) {
            anim(PICK_SEQ)
            delay(2)
            if (Random.nextInt(4) == 0) continue // weeds resist a swing now and then
            p.weeds--
            p.lastMinute = nowMinute()
            tools["weeds"]?.let { if (inv.freeSpace() > 0 || inv.count(it) > 0) invAdd(inv, it, 1) }
            statAdvance(FARMING, RAKE_XP)
            changed(player)
        }
        mes("You successfully clear all the weeds.", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.plant(p: PatchData, crop: Crop) {
        if (crop.kind != p.kind) { mes("You can't plant ${article(crop.name)} in this patch."); return }
        if (p.crop != null) { mes("You can only plant ${crop.name} in an empty patch."); return }
        if (p.weeds > 0) { mes("This patch needs weeding first."); return }
        if (stat(FARMING) < crop.level) { mes("You need a Farming level of ${crop.level} to plant that."); return }
        val sapling = crop.kind == PatchKind.TREE || crop.kind == PatchKind.FRUIT_TREE
        if (sapling && !has(tools["spade"])) { mes("You need a spade to plant a sapling."); return }
        if (!sapling && !has(tools["dibber"])) { mes("You need a seed dibber to plant seeds."); return }
        if (inv.count(crop.seed) < crop.seedCount) {
            mes("You need ${crop.seedCount} seeds to plant ${article(p.kind.display)}.")
            return
        }
        anim(PICK_SEQ)
        delay(2)
        if (invDel(inv, crop.seed, crop.seedCount).failure) return
        if (sapling) tools["pot"]?.let { invAdd(inv, it, 1) }
        p.crop = crop.key
        p.state = CropState.GROWING
        p.stage = 0
        p.watered = false
        p.lives = 0
        p.lastMinute = nowMinute()
        statAdvance(FARMING, crop.plantXp)
        mes("You plant ${if (crop.seedCount > 1) "${crop.seedCount} seeds" else article(crop.name)} in the ${p.kind.display}.", ChatType.Spam)
        changed(player)
    }

    private suspend fun ProtectedAccess.harvest(p: PatchData) {
        val crop = p.crop?.let { FarmingCrops.byKey[it] } ?: return
        if (p.state != CropState.HARVESTABLE) { mes("This isn't ready to harvest yet."); return }
        when (crop.kind) {
            PatchKind.ALLOTMENT, PatchKind.HOPS, PatchKind.HERB -> {
                if (crop.kind != PatchKind.HERB && !has(tools["spade"])) { mes("You need a spade to harvest this patch."); return }
                mes("You begin to harvest the ${p.kind.display}.", ChatType.Spam)
                while (p.lives > 0) {
                    if (inv.freeSpace() <= 0 && inv.count(crop.produce) == 0) { mes("You don't have enough inventory space."); break }
                    if (inv.freeSpace() <= 0) { mes("You don't have enough inventory space."); break }
                    anim(PICK_SEQ)
                    delay(2)
                    invAdd(inv, crop.produce, 1)
                    statAdvance(FARMING, crop.harvestXp)
                    if (Random.nextDouble() >= saveChance(stat(FARMING), crop)) p.lives--
                    if (p.lives <= 0) emptyPatch(p)
                    changed(player)
                }
                if (p.crop == null) mes("The ${p.kind.display} is now empty.", ChatType.Spam)
            }
            PatchKind.FLOWER -> {
                if (inv.freeSpace() <= 0) { mes("You don't have enough inventory space."); return }
                anim(PICK_SEQ)
                delay(2)
                invAdd(inv, crop.produce, if (crop.key == "LIMPWURT") 3 else 1)
                statAdvance(FARMING, crop.harvestXp)
                emptyPatch(p)
                changed(player)
            }
            PatchKind.BUSH, PatchKind.FRUIT_TREE -> {
                while (p.lives > 0) {
                    if (inv.freeSpace() <= 0) { mes("You don't have enough inventory space."); break }
                    anim(PICK_SEQ)
                    delay(2)
                    invAdd(inv, crop.produce, 1)
                    statAdvance(FARMING, crop.harvestXp)
                    p.lives--
                    p.lastMinute = nowMinute()
                    changed(player)
                }
                if (p.lives <= 0) mes("There's nothing left to pick right now.", ChatType.Spam)
            }
            PatchKind.TREE -> chop(p)
        }
    }

    private suspend fun ProtectedAccess.chop(p: PatchData) {
        val crop = p.crop?.let { FarmingCrops.byKey[it] } ?: return
        if (p.state != CropState.HARVESTABLE) return
        if (crop.kind == PatchKind.FRUIT_TREE) { clear(p, anyCrop = true); return }
        if (stat(WOODCUTTING) < crop.wcLevel) { mes("You need a Woodcutting level of ${crop.wcLevel} to chop down this tree."); return }
        if (!hasAxe()) { mes("You need an axe to chop down this tree."); return }
        mes("You swing your axe at the tree.", ChatType.Spam)
        while (p.state == CropState.HARVESTABLE) {
            if (inv.freeSpace() <= 0) { mes("Your inventory is too full to hold any more logs."); return }
            delay(4)
            val chance = (0.3 + 0.01 * (stat(WOODCUTTING) - crop.wcLevel)).coerceIn(0.3, 0.9)
            if (Random.nextDouble() >= chance) continue
            invAdd(inv, crop.produce, 1)
            statAdvance(WOODCUTTING, crop.wcXp)
            if (Random.nextInt(STUMP_ODDS) == 0) {
                p.state = CropState.STUMP
                mes("You get some logs, and the tree falls to a stump.", ChatType.Spam)
            }
            changed(player)
        }
    }

    private fun ProtectedAccess.hasAxe(): Boolean {
        fun isAxe(id: Int): Boolean {
            val n = ServerCacheManager.getItem(id)?.name?.lowercase() ?: return false
            return n.endsWith(" axe") || n == "axe" || n.endsWith(" felling axe")
        }
        return inv.any { it != null && isAxe(it.id) } || player.worn.any { it != null && isAxe(it.id) }
    }

    private suspend fun ProtectedAccess.checkHealth(p: PatchData) {
        val crop = p.crop?.let { FarmingCrops.byKey[it] } ?: return
        if (p.state != CropState.HEALTH_CHECK) return
        anim(PICK_SEQ)
        delay(1)
        statAdvance(FARMING, crop.healthXp)
        p.state = CropState.HARVESTABLE
        p.lives = if (crop.kind == PatchKind.TREE) 1 else crop.maxHarvest
        p.lastMinute = nowMinute()
        mes("You examine the ${crop.name} for signs of disease and find that it is in perfect health.")
        changed(player)
    }

    private suspend fun ProtectedAccess.cure(p: PatchData, tool: String?) {
        if (p.state != CropState.DISEASED) { mes("This patch doesn't need curing."); return }
        if (p.kind.curable) {
            val cure = tools["plantcure"]
            if (cure == null || inv.count(cure) == 0) { mes("You need a plant cure to cure this patch."); return }
            anim(PICK_SEQ); delay(2)
            if (invDel(inv, cure, 1).failure) return
            tools["vial"]?.let { invAdd(inv, it, 1) }
            mes("You treat the patch with the plant cure. It is restored to health.")
        } else {
            if (!has(tools["secateurs"])) { mes("You need secateurs to prune this plant."); return }
            anim(PICK_SEQ); delay(2)
            mes("You prune away the diseased leaves. The plant is restored to health.")
        }
        p.state = CropState.GROWING
        changed(player)
    }

    private suspend fun ProtectedAccess.clear(p: PatchData, anyCrop: Boolean = false) {
        if (p.crop == null) { mes(if (p.weeds > 0) "You need a rake to clear weeds." else "This patch is empty."); return }
        if (!anyCrop && p.state != CropState.DEAD && p.state != CropState.STUMP) { mes("Nothing interesting happens."); return }
        if (!has(tools["spade"])) { mes("You need a spade to do that."); return }
        anim(PICK_SEQ)
        delay(3)
        emptyPatch(p)
        mes("You have successfully cleared this patch for new crops.", ChatType.Spam)
        changed(player)
    }

    private suspend fun ProtectedAccess.compost(p: PatchData, item: String, level: Int) {
        if (p.crop != null || p.weeds > 0) { mes("Compost can only be added to an empty, weeded patch."); return }
        if (p.compost > 0) { mes("This patch has already been treated with compost."); return }
        anim(PICK_SEQ); delay(2)
        if (invDel(inv, item, 1).failure) return
        tools["bucket"]?.let { invAdd(inv, it, 1) }
        p.compost = level
        statAdvance(FARMING, when (level) { 1 -> 18.0; 2 -> 26.0; else -> 36.0 })
        mes("You treat the ${p.kind.display} with ${when (level) { 1 -> "compost"; 2 -> "supercompost"; else -> "ultracompost" }}.", ChatType.Spam)
        changed(player)
    }

    private suspend fun ProtectedAccess.water(p: PatchData, charges: Int) {
        if (!p.kind.waterable) { mes("This patch doesn't need watering."); return }
        if (p.state != CropState.GROWING || p.crop == null) { mes("This patch doesn't need watering."); return }
        if (p.watered) { mes("This patch has already been watered."); return }
        if (charges <= 0) { mes("Your watering can is empty."); return }
        val from = FarmingCrops.WATERING_CANS[charges]
        val to = FarmingCrops.WATERING_CANS[charges - 1].takeIf { obj(it) } ?: FarmingCrops.EMPTY_CAN_FALLBACK
        anim(PICK_SEQ); delay(2)
        if (invDel(inv, from, 1).failure) return
        if (obj(to)) invAdd(inv, to, 1)
        p.watered = true
        mes("You water the ${p.kind.display}.", ChatType.Spam)
        changed(player)
    }

    private fun ProtectedAccess.inspect(p: PatchData) {
        val soil = when (p.compost) { 1 -> "compost"; 2 -> "supercompost"; 3 -> "ultracompost"; else -> null }
        val soilText = if (soil == null) "The soil has not been treated." else "The soil has been treated with $soil."
        val crop = p.crop?.let { FarmingCrops.byKey[it] }
        val stateText = when {
            crop == null && p.weeds > 0 -> "The patch needs weeding."
            crop == null -> "The patch is empty and weeded."
            p.state == CropState.GROWING -> "The ${crop.name} ${if (p.watered) "(watered) " else ""}is growing (stage ${p.stage + 1} of ${crop.stages})."
            p.state == CropState.HEALTH_CHECK -> "The ${crop.name} is fully grown and ready for a health check."
            p.state == CropState.HARVESTABLE -> "The ${crop.name} is ready to harvest."
            p.state == CropState.DISEASED -> "The ${crop.name} is diseased!"
            p.state == CropState.DEAD -> "The ${crop.name} has died."
            p.state == CropState.STUMP -> "There is a ${crop.name} tree stump here."
            else -> ""
        }
        mes("This is ${article(p.kind.display)}. $soilText $stateText${if (p.protected) " A gardener is protecting it." else ""}")
    }

    private fun emptyPatch(p: PatchData) {
        p.crop = null
        p.state = CropState.EMPTY
        p.stage = 0
        p.lives = 0
        p.compost = 0
        p.watered = false
        p.weeds = 0
        p.lastMinute = nowMinute()
    }

    /** Approximate OSRS "chance to save a harvest life" (level-scaled; exact per-crop table deferred). */
    private fun saveChance(level: Int, crop: Crop): Double =
        (0.35 + 0.005 * (level - crop.level)).coerceIn(0.35, 0.8)

    // ------------------------------------------------------------------ helpers

    private fun patchNames(): List<Pair<String, PatchKind>> = buildList {
        (1..17).forEach { add("loc.farming_veg_patch_$it" to PatchKind.ALLOTMENT) }
        (1..8).forEach { add("loc.farming_herb_patch_$it" to PatchKind.HERB) }
        (1..9).forEach { add("loc.farming_flower_patch_$it" to PatchKind.FLOWER) }
        add("loc.farming_hops_patch" to PatchKind.HOPS)
        (1..5).forEach { add("loc.farming_hops_patch_$it" to PatchKind.HOPS) }
        (1..5).forEach { add("loc.farming_bush_patch_$it" to PatchKind.BUSH) }
        (1..7).forEach { add("loc.farming_tree_patch_$it" to PatchKind.TREE) }
        (1..7).forEach { add("loc.farming_fruit_tree_patch_$it" to PatchKind.FRUIT_TREE) }
    }

    private fun dump() {
        val out = StringBuilder("Farming name verification:\n")
        for ((name, kind) in patchNames()) {
            val id = idOf(name, RSCMType.LOC)
            val t = id?.let { ServerCacheManager.getObject(it) }
            val vb = t?.multiVarBit ?: -1
            val vbName = if (vb > 0) runCatching { dev.openrune.rscm.RSCM.getReverseMapping(RSCMType.VARBIT, vb) }.getOrNull() else null
            out.append("  $name [$kind] -> ${id ?: "<UNRESOLVED>"} '${t?.name}' varbit=$vb $vbName multi=${t?.multiLoc?.size}\n")
        }
        for (c in FarmingCrops.all) out.append("  crop ${c.key}: ${c.seed}=${idOf(c.seed, RSCMType.OBJ)} ${c.produce}=${idOf(c.produce, RSCMType.OBJ)}\n")
        for (n in FarmingCrops.WATERING_CANS + FarmingCrops.EMPTY_CAN_FALLBACK) out.append("  $n=${idOf(n, RSCMType.OBJ)}\n")
        out.append("  tools=$tools\n")
        logger.info { out.toString() }
    }

    private fun article(name: String): String = (if (name.first().lowercaseChar() in "aeiou") "an " else "a ") + name

    private companion object {
        const val FARMING = "stat.farming"
        const val WOODCUTTING = "stat.woodcutting"
        const val PICK_SEQ = "seq.human_pickupfloor"
        const val RAKE_XP = 4.0
        const val BASE_LIVES = 3
        const val WEED_MINUTES = 5
        const val SYNC_RANGE = 64
        const val FLOWER_RANGE = 16
        const val STUMP_ODDS = 8
        /** ~1 in-game minute (100 cycles x 0.6s). */
        const val GROWTH_CHECK_CYCLES = 100
        const val SAVE_CYCLES = 500

        fun nowMinute(): Long = System.currentTimeMillis() / 60_000L

        fun idOf(name: String, type: RSCMType): Int? = runCatching { name.asRSCM(type) }.getOrNull()

        fun obj(name: String) = idOf(name, RSCMType.OBJ) != null
    }
}
