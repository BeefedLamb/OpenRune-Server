package org.rsmod.content.skills.agility

import jakarta.inject.Inject
import java.util.WeakHashMap
import org.rsmod.api.player.hook.TeleportType
import org.rsmod.api.player.output.ChatType
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.player.stat.agilityLvl
import org.rsmod.api.player.stat.hitpoints
import org.rsmod.api.repo.obj.ObjRepository
import org.rsmod.api.script.onOpLoc1
import org.rsmod.content.quest.manager.Quest
import org.rsmod.content.quest.manager.QuestRequirements
import org.rsmod.game.entity.Player
import org.rsmod.game.hit.HitType
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.map.CoordGrid
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext

/**
 * Agility: courses (lap order, lap bonus, marks of grace, failures) and shortcuts, all driven by
 * [AgilityCourses] / [AgilityShortcuts]. Every obstacle is op1 on its loc.
 */
class AgilityScript @Inject constructor(private val objRepo: ObjRepository) : PluginScript() {
    private class Entry(val obstacle: Obstacle, val course: Course?, val index: Int)

    private class Lap(val course: Course, var next: Int, var clean: Boolean)

    private val laps = WeakHashMap<Player, Lap>()

    override fun ScriptContext.startup() {
        val byLoc = LinkedHashMap<String, MutableList<Entry>>()
        for (course in AgilityCourses.all) {
            course.obstacles.forEachIndexed { i, o -> byLoc.getOrPut(o.loc) { ArrayList() } += Entry(o, course, i) }
        }
        val gnome = AgilityCourses.gnome
        byLoc.getOrPut(AgilityCourses.gnomePipe2.loc) { ArrayList() } +=
            Entry(AgilityCourses.gnomePipe2, gnome, gnome.obstacles.lastIndex)
        byLoc.getOrPut(AgilityCourses.barbarianPipe.loc) { ArrayList() } +=
            Entry(AgilityCourses.barbarianPipe, null, -1)
        for (shortcut in AgilityShortcuts.all) {
            byLoc.getOrPut(shortcut.obstacle.loc) { ArrayList() } += Entry(shortcut.obstacle, null, -1)
        }
        for ((loc, entries) in byLoc) {
            onOpLoc1(loc) { attempt(it.loc, entries) }
        }
    }

    private suspend fun ProtectedAccess.attempt(loc: BoundLocInfo, entries: List<Entry>) {
        if (player.isFrozen) return
        val entry =
            entries.firstOrNull { it.obstacle.at == loc.coords }
                ?: entries.firstOrNull { it.obstacle.at == null }
                ?: return
        val obstacle = entry.obstacle
        val course = entry.course
        arriveDelay()
        val required = if (obstacle.level > 0) obstacle.level else course?.level ?: 1
        if (player.agilityLvl < required) {
            mes("You need an Agility level of $required to attempt this.")
            return
        }
        if (course?.quest != null && Quest.get(course.quest) != null &&
            !QuestRequirements.hasCompleted(player, course.quest)
        ) {
            mes("You need to complete ${course.questName ?: "a quest"} to use this course.")
            return
        }
        if (!oneWayOk(obstacle.oneWay, loc.coords)) {
            mes("You can't do that from here.")
            return
        }
        faceLoc(loc)
        obstacle.attempt?.let { mes(it, ChatType.Spam) }
        obstacle.approach?.let { if (coords != it) glide(it, ticks = 1, anim = null) }
        val origin = coords

        val fail = obstacle.fail
        if (fail != null && random.randomDouble() >= fail.successChance(player.agilityLvl)) {
            fall(fail, loc.coords, origin, obstacle)
            return
        }

        for (step in obstacle.steps) {
            val dest = step.dest.resolve(coords, loc.coords, origin)
            when (step.mode) {
                MoveMode.Glide -> glide(dest, step.ticks, step.anim)
                MoveMode.Jump -> {
                    step.anim?.let { anim(it) }
                    delay(step.ticks)
                    telejump(dest, TeleportType.Exempt)
                }
            }
        }
        resetAnim()
        if (obstacle.xp > 0.0) statAdvance(AGILITY, obstacle.xp)
        obstacle.success?.let { mes(it, ChatType.Spam) }
        if (course != null) advanceLap(course, entry.index)
        // Hold a delay after the last move so the Unity link follows the server-side position
        // (it only trusts server moves while the player is delayed).
        delay(1)
    }

    private fun ProtectedAccess.oneWayOk(oneWay: OneWay?, loc: CoordGrid): Boolean =
        when (oneWay) {
            null -> true
            OneWay.FromEast -> coords.x >= loc.x
            OneWay.FromWest -> coords.x < loc.x
            OneWay.FromNorth -> coords.z >= loc.z
            OneWay.FromSouth -> coords.z < loc.z
        }

    private suspend fun ProtectedAccess.glide(dest: CoordGrid, ticks: Int, anim: String?) {
        anim?.let { anim(it) }
        val facing = facing(coords, dest)
        exactMove(coords, dest, delay1 = 0, delay2 = ticks * CLIENT_CYCLES_PER_TICK, dir = facing,
            teleportType = TeleportType.Exempt)
        delay(ticks)
    }

    private suspend fun ProtectedAccess.fall(fail: Fail, loc: CoordGrid, origin: CoordGrid, obstacle: Obstacle) {
        // Start the obstacle, then lose footing part-way.
        val first = obstacle.steps.firstOrNull()
        if (first != null && first.mode == MoveMode.Glide) {
            val partial = first.dest.resolve(coords, loc, origin)
            val mid = CoordGrid((coords.x + partial.x) / 2, (coords.z + partial.z) / 2, coords.level)
            if (mid != coords) glide(mid, ticks = 1, anim = first.anim)
        }
        anim(Seqs.FALL)
        delay(2)
        telejump(fail.landing, TeleportType.Exempt)
        resetAnim()
        // Rellekka wiki: 10% of current hitpoints, rounded down. Used for every failing obstacle
        // here (other courses' exact damage is not documented); minimum 1.
        val damage = (player.hitpoints / 10).coerceAtLeast(1)
        queueHit(delay = 1, type = HitType.Typeless, damage = damage)
        mes(fail.message)
        laps.remove(player)
        delay(1)
    }

    private fun ProtectedAccess.advanceLap(course: Course, index: Int) {
        val last = course.obstacles.lastIndex
        var lap = laps[player]
        if (index == 0 || lap == null || lap.course !== course) {
            lap = Lap(course, next = 0, clean = index == 0)
            laps[player] = lap
        }
        if (index != 0) {
            if (index == lap.next) lap.next++ else lap.clean = false
        } else {
            lap.next = 1
            lap.clean = true
        }
        if (index != last) return
        val complete = lap.clean && lap.next == last + 1
        laps.remove(player)
        if (!complete) return
        if (course.lapBonus > 0.0) statAdvance(AGILITY, course.lapBonus)
        if (course.lapStrengthBonus > 0.0) statAdvance(STRENGTH, course.lapStrengthBonus)
        rollMark(course)
    }

    /**
     * Marks of grace: the per-lap chance (1/3 most rooftops, 2/3 Canifis per the wiki) is
     * reduced once the player is 20+ levels above the course. The exact OSRS falloff is not
     * published; 0.75x is an approximation.
     */
    private fun ProtectedAccess.rollMark(course: Course) {
        if (course.markSpawns.isEmpty()) return
        var chance = course.markChance
        if (course.markFalloff && player.agilityLvl >= course.level + 20) chance *= MARK_FALLOFF
        if (random.randomDouble() >= chance) return
        val spawn = course.markSpawns[random.of(0, course.markSpawns.lastIndex)]
        objRepo.add(MARK_OF_GRACE, spawn, MARK_DURATION, receiver = player)
        mes("A Mark of Grace appears on the course.", ChatType.Spam)
    }

    private fun facing(from: CoordGrid, to: CoordGrid): Int {
        val dx = to.x - from.x
        val dz = to.z - from.z
        return when {
            kotlin.math.abs(dx) >= kotlin.math.abs(dz) && dx > 0 -> FACE_EAST
            kotlin.math.abs(dx) >= kotlin.math.abs(dz) && dx < 0 -> FACE_WEST
            dz > 0 -> FACE_NORTH
            else -> FACE_SOUTH
        }
    }

    private companion object {
        const val AGILITY = "stat.agility"
        const val STRENGTH = "stat.strength"
        const val MARK_OF_GRACE = "obj.grace"
        const val MARK_DURATION = 1000
        const val MARK_FALLOFF = 0.75
        const val CLIENT_CYCLES_PER_TICK = 30
        const val FACE_SOUTH = 0
        const val FACE_WEST = 512
        const val FACE_NORTH = 1024
        const val FACE_EAST = 1536
    }
}
