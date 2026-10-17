package org.rsmod.content.skills.hunter

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import jakarta.inject.Inject
import org.rsmod.api.player.output.ChatType
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.repo.loc.LocRepository
import org.rsmod.api.repo.npc.NpcRepository
import org.rsmod.api.repo.obj.ObjRepository
import org.rsmod.api.script.onOpHeld1
import org.rsmod.api.script.onOpLoc1
import org.rsmod.api.script.onOpLoc2
import org.rsmod.api.script.onOpNpc1
import org.rsmod.game.entity.Npc
import org.rsmod.game.entity.Player
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.game.loc.LocAngle
import org.rsmod.game.loc.LocShape
import org.rsmod.map.CoordGrid
import org.rsmod.map.zone.ZoneKey
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import kotlin.random.Random

/**
 * Hunter: bird snares, box traps (ferrets/chinchompas), young-tree net traps (swamp lizards,
 * salamanders), and butterfly-net catching of implings and butterflies.
 *
 * Traps are real spawned locs (LocRepository), so they stream to clients through the normal
 * loc add/del path. The loc spawn's own despawn timer is used as the trap's clock: when the
 * "set" loc expires, [resolve] rolls whether a nearby matching creature walked in.
 */
class HunterScript
@Inject
constructor(
    private val locRepo: LocRepository,
    private val npcRepo: NpcRepository,
    private val objRepo: ObjRepository,
) : PluginScript() {
    private val logger = InlineLogger()

    private class Trap(
        val owner: Player,
        val coords: CoordGrid,
        val laid: LaidTrap?,
        val tree: NetTrapTree?,
        val hunterLevel: Int,
    ) {
        var angle: LocAngle = LocAngle.West
        var shape: LocShape = LocShape.CentrepieceStraight
        var ticksElapsed: Int = 0
        var state: State = State.SET
        var caught: TrapCreature? = null
    }

    private enum class State { SET, FULL, FAILED }

    private val traps = HashMap<CoordGrid, Trap>()

    override fun ScriptContext.startup() {
        var ok = 0
        var skipped = 0
        for (laid in HunterTables.laidTraps) {
            val baseOk = obj(laid.item) && loc(laid.setLoc) && loc(laid.failedLoc)
            val creatures = laid.creatures.filter { c -> creatureOk(c) }
            skipped += laid.creatures.size - creatures.size
            if (!baseOk || creatures.isEmpty()) {
                logger.warn { "Hunter: skipping ${laid.kind} (unresolved trap names)" }
                continue
            }
            val trap = laid.copy(creatures = creatures)
            onOpHeld1(trap.item) { layTrap(trap) }
            for (l in listOf(trap.setLoc, trap.failedLoc) + creatures.map { it.fullLoc }.distinct()) {
                onOpLoc1(l) { checkTrap(it.loc) }
                onOpLoc2(l) { checkTrap(it.loc) }
            }
            ok += creatures.size
        }
        if (!obj(HunterTables.ROPE) || !obj(HunterTables.SMALL_NET)) {
            logger.warn { "Hunter: rope/net names unresolved, net traps disabled" }
        } else {
            for (tree in HunterTables.netTrees) {
                if (!loc(tree.upLoc) || !loc(tree.setLoc) || !loc(tree.failedLoc) || !creatureOk(tree.creature)) {
                    skipped++
                    logger.warn { "Hunter: skipping net trap ${tree.creature.name}" }
                    continue
                }
                onOpLoc1(tree.upLoc) { setNetTrap(it.loc, tree) }
                for (l in listOf(tree.setLoc, tree.creature.fullLoc)) {
                    onOpLoc1(l) { checkTrap(it.loc) }
                }
                ok++
            }
        }
        val netCatches = HunterTables.implings + HunterTables.butterflies
        for (c in netCatches) {
            if (!npc(c.npc) || !obj(c.emptyJar) || !obj(c.filledJar)) {
                skipped++
                logger.warn { "Hunter: skipping ${c.name} (unresolved npc/jar)" }
                continue
            }
            onOpNpc1(c.npc) { netCatch(it.npc, c) }
            ok++
        }
        logger.info { "Hunter: registered $ok catchable creatures ($skipped skipped)." }
        if (System.getProperty("hunter.verify") != null || System.getenv("HUNTER_VERIFY") != null) dumpNames()
    }

    // ---------------------------------------------------------------- laying traps

    private suspend fun ProtectedAccess.layTrap(trap: LaidTrap) {
        val level = stat(HUNTER)
        val min = trap.creatures.minOf { it.level }
        if (level < min) {
            mes("You need a Hunter level of at least $min to use this trap.")
            return
        }
        if (!canPlaceMore(level)) return
        val coords = player.coords
        if (traps.containsKey(coords) || locRepo.findAll(coords).any()) {
            mes("You can't lay a trap here.")
            return
        }
        if (invDel(inv, trap.item, 1).failure) return
        mes("You begin setting up the trap.", ChatType.Spam)
        anim(LAY_SEQ)
        delay(2)
        val t = Trap(player, coords, trap, null, level)
        traps[coords] = t
        spawnSet(t)
    }

    private suspend fun ProtectedAccess.setNetTrap(loc: BoundLocInfo, tree: NetTrapTree) {
        arriveDelay()
        faceLoc(loc)
        val level = stat(HUNTER)
        if (level < tree.creature.level) {
            mes("You need a Hunter level of at least ${tree.creature.level} to set a trap here.")
            return
        }
        if (!canPlaceMore(level)) return
        if (traps.containsKey(loc.coords)) return
        if (inv.count(HunterTables.ROPE) == 0 || inv.count(HunterTables.SMALL_NET) == 0) {
            mes("You need a rope and a small fishing net to set a net trap.")
            return
        }
        invDel(inv, HunterTables.ROPE, 1)
        invDel(inv, HunterTables.SMALL_NET, 1)
        anim(LAY_SEQ)
        delay(2)
        val t = Trap(player, loc.coords, null, tree, level)
        t.angle = loc.angle
        t.shape = loc.shape
        traps[loc.coords] = t
        spawnSet(t)
    }

    private fun ProtectedAccess.canPlaceMore(level: Int): Boolean {
        val max = HunterTables.maxTraps(level)
        if (traps.values.count { it.owner === player } >= max) {
            mes("You don't have a high enough Hunter level to set up more than $max trap${if (max > 1) "s" else ""}.")
            return false
        }
        return true
    }

    // ---------------------------------------------------------------- trap clock

    private fun spawnSet(t: Trap) {
        val setLoc = t.laid?.setLoc ?: t.tree!!.setLoc
        val wait = Random.nextInt(MIN_WAIT, MAX_WAIT + 1)
        spawn(t, setLoc, wait) { resolve(t) }
    }

    private fun resolve(t: Trap) {
        if (traps[t.coords] !== t || t.state != State.SET) return
        val creatures = t.laid?.creatures ?: listOf(t.tree!!.creature)
        val nearby =
            creatures.filter { c -> c.level <= t.hunterLevel && creatureNearby(c, t.coords) }
        if (nearby.isEmpty()) {
            if (t.ticksElapsed >= TRAP_LIFETIME) {
                collapse(t)
            } else {
                spawnSet(t)
            }
            return
        }
        val creature = nearby.random()
        if (Random.nextDouble() < catchChance(t.hunterLevel, creature.level)) {
            t.state = State.FULL
            t.caught = creature
            spawn(t, creature.fullLoc, RESULT_TICKS) { collapse(t) }
        } else {
            t.state = State.FAILED
            // Box traps fall over / snares break and wait to be dismantled; a sprung net trap
            // (no ops in the cache) drops its rope and net shortly after, as in OSRS.
            val failed = t.laid?.failedLoc ?: t.tree!!.failedLoc
            spawn(t, failed, if (t.laid != null) RESULT_TICKS else NET_FAIL_TICKS) { collapse(t) }
        }
    }

    /** Trap left unattended: it falls down and the trap items drop for the owner. */
    private fun collapse(t: Trap) {
        if (traps[t.coords] !== t) return
        traps.remove(t.coords)
        val items = t.laid?.let { listOf(it.item) } ?: listOf(HunterTables.ROPE, HunterTables.SMALL_NET)
        for (item in items) objRepo.add(item, t.coords, GROUND_TICKS, t.owner)
    }

    private fun spawn(t: Trap, loc: String, ticks: Int, onDespawn: () -> Unit) {
        locRepo.add(t.coords, loc, ticks, t.angle, t.shape, onDespawn)
        t.ticksElapsed += ticks
    }

    private fun creatureNearby(c: TrapCreature, coords: CoordGrid): Boolean =
        npcRepo.findAll(ZoneKey.from(coords), zoneRadius = 1).any { npc ->
            npc.coords.level == coords.level &&
                npc.coords.chebyshevDistance(coords) <= LURE_RANGE &&
                c.npcs.any { npc.type.isType(it) }
        }

    // ---------------------------------------------------------------- checking traps

    private suspend fun ProtectedAccess.checkTrap(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        val t = traps[loc.coords]
        if (t == null) {
            mes("This isn't your trap.")
            return
        }
        if (t.owner !== player) {
            mes("This isn't your trap.")
            return
        }
        val caught = t.caught
        if (t.state == State.FULL && caught != null && inv.freeSpace() < caught.loot.size) {
            mes("You don't have enough inventory space.")
            return
        }
        anim(LAY_SEQ)
        delay(1)
        if (traps[loc.coords] !== t) return
        traps.remove(loc.coords)
        locRepo.del(loc, Int.MAX_VALUE)
        val laid = t.laid
        if (laid != null) invAdd(inv, laid.item, 1) else {
            invAdd(inv, HunterTables.ROPE, 1)
            invAdd(inv, HunterTables.SMALL_NET, 1)
        }
        when {
            t.state == State.FULL && caught != null -> {
                for (l in caught.loot) invAdd(inv, l.obj, random.of(l.min, l.max))
                statAdvance(HUNTER, caught.xp)
                mes("You've caught ${article(caught.name)}.", ChatType.Spam)
            }
            t.state == State.FAILED -> mes("The trap has been triggered but nothing was caught.", ChatType.Spam)
            else -> mes("You dismantle the trap.", ChatType.Spam)
        }
    }

    // ---------------------------------------------------------------- net catching

    private suspend fun ProtectedAccess.netCatch(npc: Npc, c: NetCatch) {
        val wieldingNet =
            player.worn.count(HunterTables.BUTTERFLY_NET) > 0 ||
                (obj(HunterTables.MAGIC_NET) && player.worn.count(HunterTables.MAGIC_NET) > 0)
        val level = stat(HUNTER)
        val required = if (wieldingNet) c.level else c.level + BAREHANDED_BONUS
        if (level < required) {
            val how = if (wieldingNet) "" else " barehanded"
            mes("You need a Hunter level of at least $required to catch ${article(c.name)}$how.")
            return
        }
        if (inv.count(c.emptyJar) == 0) {
            mes("You need an empty jar to catch ${article(c.name)}.")
            return
        }
        faceEntitySquare(npc)
        anim(NET_SEQ)
        delay(1)
        if (Random.nextDouble() >= catchChance(level, required)) {
            mes("You fail to catch ${article(c.name)}.", ChatType.Spam)
            return
        }
        if (invDel(inv, c.emptyJar, 1).failure) return
        invAdd(inv, c.filledJar, 1)
        statAdvance(HUNTER, c.xp)
        npcRepo.del(npc, c.respawn)
        mes("You manage to catch ${article(c.name)}.", ChatType.Spam)
    }

    // ---------------------------------------------------------------- helpers

    /**
     * Approximation of OSRS's level-scaled catch rate: 50% at the requirement, +2% per level
     * above it, capped at 95%.
     */
    private fun catchChance(level: Int, required: Int): Double =
        (0.5 + 0.02 * (level - required)).coerceIn(0.5, 0.95)

    private fun article(name: String): String {
        val lower = name.lowercase()
        return (if (lower.first() in "aeiou") "an " else "a ") + lower
    }

    private fun creatureOk(c: TrapCreature): Boolean =
        loc(c.fullLoc) && c.npcs.all { npc(it) } && c.loot.all { obj(it.obj) }

    private fun dumpNames() {
        val out = StringBuilder("Hunter name verification:\n")
        fun line(type: String, name: String, display: String?) {
            out.append("  ").append(name).append(" -> ").append(display ?: "<UNRESOLVED>").append('\n')
        }
        val objs = HashSet<String>()
        val locs = HashSet<String>()
        val npcs = HashSet<String>()
        for (t in HunterTables.laidTraps) {
            objs += t.item; locs += t.setLoc; locs += t.failedLoc
            t.creatures.forEach { c -> locs += c.fullLoc; npcs += c.npcs; objs += c.loot.map { it.obj } }
        }
        for (tr in HunterTables.netTrees) {
            locs += listOf(tr.upLoc, tr.setLoc, tr.failedLoc, tr.creature.fullLoc)
            npcs += tr.creature.npcs; objs += tr.creature.loot.map { it.obj }
        }
        for (c in HunterTables.implings + HunterTables.butterflies) {
            npcs += c.npc; objs += c.emptyJar; objs += c.filledJar
        }
        objs += listOf(HunterTables.ROPE, HunterTables.SMALL_NET, HunterTables.BUTTERFLY_NET, HunterTables.MAGIC_NET)
        for (n in objs.sorted()) line("obj", n, idOf(n, RSCMType.OBJ)?.let { "${it} ${ServerCacheManager.getItem(it)?.name}" })
        for (n in locs.sorted()) line("loc", n, idOf(n, RSCMType.LOC)?.let { "${it} ${ServerCacheManager.getObject(it)?.name} ops=${ServerCacheManager.getObject(it)?.actions}" })
        for (n in npcs.sorted()) line("npc", n, idOf(n, RSCMType.NPC)?.let { "${it} ${ServerCacheManager.getNpc(it)?.name} ops=${ServerCacheManager.getNpc(it)?.actions}" })
        logger.info { out.toString() }
    }

    private companion object {
        const val HUNTER = "stat.hunter"
        const val LAY_SEQ = "seq.human_pickupfloor"
        const val NET_SEQ = "seq.human_butterflynet_swing"
        const val MIN_WAIT = 8
        const val MAX_WAIT = 20
        const val RESULT_TICKS = 100
        const val TRAP_LIFETIME = 200
        const val GROUND_TICKS = 200
        const val LURE_RANGE = 5
        const val BAREHANDED_BONUS = 10
        const val NET_FAIL_TICKS = 3

        fun idOf(name: String, type: RSCMType): Int? = runCatching { name.asRSCM(type) }.getOrNull()

        fun obj(name: String) = idOf(name, RSCMType.OBJ) != null

        fun loc(name: String) = idOf(name, RSCMType.LOC) != null

        fun npc(name: String) = idOf(name, RSCMType.NPC) != null
    }
}
