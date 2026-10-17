package org.rsmod.content.minigames.giantsfoundry

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import org.rsmod.api.player.output.ChatType
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.events.EventBus
import org.rsmod.api.player.events.MinigameOverlayEvent
import jakarta.inject.Inject
import org.rsmod.api.script.onOpLoc1
import org.rsmod.api.script.onOpLocU
import org.rsmod.api.script.onOpNpc1
import org.rsmod.game.entity.Npc
import org.rsmod.game.entity.Player
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import kotlin.random.Random

/**
 * Giants' Foundry (Smithing minigame), single-player offline adaptation.
 *
 * Loc/npc/obj/seq ids are cache gameval symbols verified at startup. Both the original and the
 * "_multi" variants of the crucible / jig are registered when present (whichever is placed in
 * the map is the one that responds).
 *
 * Flow: talk to Kovac for a commission -> use bars on the crucible (28 bars, 2+ metal types)
 * -> set up a mould on the jig -> pour the crucible -> take the preform -> work it at the trip
 * hammer (hot), grindstone (warm) and polishing wheel (cool), managing heat with the lava pool
 * (heat up) and waterfall (cool down) -> hand the finished sword to Kovac for XP + reputation.
 *
 * HAND-TYPED / APPROXIMATED: 15 Smithing requirement, heat bands, progress per action,
 * XP formula (bar value x quality), reputation numbers. Real mould selection UI, commission
 * sword properties and bonus "flash" stages are not simulated.
 */
class GiantsFoundryScript @Inject constructor(private val eventBus: EventBus) : PluginScript() {
    private val logger = InlineLogger()

    private class State {
        var commission = false
        val crucible = LinkedHashMap<String, Int>()
        var mould = false
        var poured = false
        var heat = 0
        var progress = 0
        var quality = 0
        var barValue = 0
        var reputation = 0
    }

    private val states = HashMap<Player, State>()

    /** Pushes the Giants' Foundry HUD (crucible / heat / progress / quality) to the Unity link. */
    private fun ProtectedAccess.overlay() {
        val s = states[player] ?: State()
        eventBus.publish(
            MinigameOverlayEvent(
                player,
                "Giants' Foundry",
                listOf(
                    "Commission" to if (s.commission) "Yes" else "No",
                    "Crucible" to "${s.crucible.values.sum()} / $CRUCIBLE_SIZE",
                    "Heat" to "${s.heat}%",
                    "Progress" to "${s.progress} / $MAX_PROGRESS",
                    "Quality" to "${s.quality}",
                    "Reputation" to "${s.reputation}",
                ),
            )
        )
    }


    override fun ScriptContext.startup() {
        val missing = REQUIRED.filter { !exists(it) }
        if (missing.isNotEmpty()) {
            logger.warn { "Giants' Foundry: disabled, unresolved symbols: $missing" }
            return
        }
        for (k in KOVAC.filter { exists(it) }) onOpNpc1(k) { kovac(it.npc); overlay() }
        for (c in CRUCIBLES.filter { exists(it) }) {
            onOpLoc1(c) { pour(it.loc); overlay() }
            for (bar in BARS.keys) onOpLocU(c, bar) { addBars(it.loc, bar); overlay() }
        }
        for (j in JIGS.filter { exists(it) }) onOpLoc1(j) { jig(it.loc); overlay() }
        onOpLoc1(LAVA) { adjustHeat(it.loc, +1); overlay() }
        onOpLoc1(WATERFALL) { adjustHeat(it.loc, -1); overlay() }
        onOpLoc1(TRIP_HAMMER) { work(it.loc, Tool.HAMMER); overlay() }
        onOpLoc1(GRINDSTONE) { work(it.loc, Tool.GRIND); overlay() }
        onOpLoc1(POLISHER) { work(it.loc, Tool.POLISH); overlay() }
        logger.info { "Giants' Foundry: registered" }
    }

    private fun ProtectedAccess.state() = states.getOrPut(player) { State() }

    private fun ProtectedAccess.checkLevel(): Boolean {
        if (statBase(SMITHING) < 15) {
            mes("You need a Smithing level of at least 15 to work in the Giants' Foundry.")
            return false
        }
        return true
    }

    private suspend fun ProtectedAccess.kovac(npc: Npc) {
        faceEntitySquare(npc)
        if (!checkLevel()) return
        val s = state()
        if (inv.count(SWORD) > 0) {
            invDel(inv, SWORD, 1)
            // OSRS wiki: xp = (floor(q^2/73) + floor(1.5q) + 1) * 30; reputation = quality.
            val q = s.quality.coerceAtLeast(0)
            val xp = ((q * q) / 73 + (3 * q) / 2 + 1) * 30.0
            statAdvance(SMITHING, xp)
            val rep = q
            s.reputation += rep
            mes("Kovac inspects your sword (quality ${s.quality}). You gain $rep reputation (total ${s.reputation}).")
            states[player] = State().also { it.reputation = s.reputation }
            return
        }
        if (!s.commission) {
            s.commission = true
            mes("Kovac: I need a new giant's sword. Fill the crucible with 28 bars and get to work!")
        } else {
            mes("Kovac: You already have a commission. Crucible: ${s.crucible.values.sum()}/28 bars.")
        }
    }

    private suspend fun ProtectedAccess.addBars(loc: BoundLocInfo, bar: String) {
        arriveDelay()
        faceLoc(loc)
        val s = state()
        if (!s.commission) {
            mes("You should get a commission from Kovac first.")
            return
        }
        if (s.poured) {
            mes("You have already poured this crucible.")
            return
        }
        val need = BAR_LEVEL.getValue(bar)
        if (stat(SMITHING) < need) {
            mes("You need a Smithing level of $need to add that metal to the crucible.")
            return
        }
        val room = CRUCIBLE_SIZE - s.crucible.values.sum()
        val n = minOf(room, inv.count(bar))
        if (n <= 0) {
            mes("The crucible is full.")
            return
        }
        invDel(inv, bar, n)
        s.crucible[bar] = (s.crucible[bar] ?: 0) + n
        mes("You add $n bars to the crucible (${s.crucible.values.sum()}/$CRUCIBLE_SIZE).")
    }

    private suspend fun ProtectedAccess.jig(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        val s = state()
        if (s.poured && inv.count(PREFORM) == 0 && inv.count(SWORD) == 0) {
            if (inv.freeSpace() == 0) {
                mes("You need a free inventory space to take the preform.")
                return
            }
            invAdd(inv, PREFORM, 1)
            mes("You take the preform from the mould.")
            return
        }
        s.mould = true
        mes("You set up a mould on the jig.")
    }

    private suspend fun ProtectedAccess.pour(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        val s = state()
        if (s.crucible.values.sum() < CRUCIBLE_SIZE) {
            mes("The crucible needs $CRUCIBLE_SIZE bars (it has ${s.crucible.values.sum()}).")
            return
        }
        if (!s.mould) {
            mes("You need to set up a mould on the jig first.")
            return
        }
        anim(POUR_SEQ)
        delay(4)
        s.barValue = s.crucible.entries.sumOf { (bar, n) -> BARS.getValue(bar) * n }
        val metalScore = metalScore(s.crucible)
        s.crucible.clear()
        s.poured = true
        s.heat = 100
        s.progress = 0
        // Quality = metal score (mould score not simulated: no mould-selection UI yet).
        s.quality = metalScore
        mes("You pour the molten metal into the mould. Collect the preform from the jig.")
    }

    private suspend fun ProtectedAccess.adjustHeat(loc: BoundLocInfo, dir: Int) {
        arriveDelay()
        faceLoc(loc)
        val s = state()
        if (inv.count(PREFORM) == 0) {
            mes("You have nothing to ${if (dir > 0) "heat" else "cool"}.")
            return
        }
        repeat(3) {
            delay(1)
            s.heat = (s.heat + dir * 10).coerceIn(0, 100)
            overlay()
        }
        mes("Preform heat: ${s.heat}%", ChatType.Spam)
    }

    /** OSRS wiki per-action XP: trip hammer 4.5, grindstone 3.2, polishing wheel 3.3. */
    private enum class Tool(val minHeat: Int, val maxHeat: Int, val label: String, val xp: Double) {
        HAMMER(67, 100, "hammer", 4.5),
        GRIND(34, 66, "grind", 3.2),
        POLISH(0, 33, "polish", 3.3),
    }

    /** OSRS wiki alloy formula: V_n = 10*tier_n*amount_n/28, score = floor((floor(10V1)+floor(10V2)+floor(V1*V2))/10). */
    private fun metalScore(crucible: Map<String, Int>): Int {
        val top = crucible.entries.sortedWith(compareByDescending<Map.Entry<String, Int>> { it.value }
            .thenByDescending { BARS.getValue(it.key) }).take(2)
        val v = top.map { 10.0 * BARS.getValue(it.key) * it.value / 28.0 }
        val v1 = v.getOrElse(0) { 0.0 }
        val v2 = v.getOrElse(1) { 0.0 }
        return (Math.floor(10 * v1) + Math.floor(10 * v2) + Math.floor(v1 * v2)).toInt() / 10
    }

    private suspend fun ProtectedAccess.work(loc: BoundLocInfo, tool: Tool) {
        arriveDelay()
        faceLoc(loc)
        if (!checkLevel()) return
        val s = state()
        if (inv.count(PREFORM) == 0) {
            mes("You need a preform to work on.")
            return
        }
        while (s.progress < MAX_PROGRESS) {
            if (s.heat !in tool.minHeat..tool.maxHeat) {
                mes("The preform is too ${if (s.heat > tool.maxHeat) "hot" else "cold"} to ${tool.label}.")
                return
            }
            anim(WORK_SEQ)
            delay(3)
            s.progress += 5
            s.heat = (s.heat - Random.nextInt(2, 6)).coerceAtLeast(0)
            statAdvance(SMITHING, tool.xp)
            overlay()
        }
        invDel(inv, PREFORM, 1)
        invAdd(inv, SWORD, 1)
        mes("You finish the sword. Hand it to Kovac.")
    }

    private companion object {
        const val CRUCIBLE_SIZE = 28
        const val MAX_PROGRESS = 100

        const val SMITHING = "stat.smithing"
        const val PREFORM = "obj.giants_foundry_preform"
        const val SWORD = "obj.giants_foundry_colossal_blade"

        const val LAVA = "loc.giants_foundry_lava_pool"
        const val WATERFALL = "loc.giants_foundry_waterfall"
        const val TRIP_HAMMER = "loc.giants_foundry_trip_hammer"
        const val GRINDSTONE = "loc.giants_foundry_grindstone"
        const val POLISHER = "loc.giants_foundry_polishing_wheel"

        const val POUR_SEQ = "seq.giants_foundry_crucible_pour01"
        const val WORK_SEQ = "seq.giants_foundry_hammer01"

        val KOVAC = listOf("npc.giants_foundry_kovac", "npc.giants_foundry_kovac_multi")
        val CRUCIBLES = listOf(
            "loc.giants_foundry_crucible_empty", "loc.giants_foundry_crucible_partial",
            "loc.giants_foundry_crucible_full", "loc.giants_foundry_crucible_multi",
        )
        val JIGS = listOf(
            "loc.giants_foundry_jig_empty", "loc.giants_foundry_jig_mould",
            "loc.giants_foundry_jig_mould_cooled", "loc.giants_foundry_mould_jig",
        )

        /** Bar -> metal tier (wiki: bronze 1 .. rune 6). */
        val BARS = linkedMapOf(
            "obj.bronze_bar" to 1, "obj.iron_bar" to 2, "obj.steel_bar" to 3,
            "obj.mithril_bar" to 4, "obj.adamantite_bar" to 5, "obj.runite_bar" to 6,
        )

        /** Wiki: adding a metal needs its smelting level (bronze/iron gated by the 15 entry req). */
        val BAR_LEVEL = mapOf(
            "obj.bronze_bar" to 15, "obj.iron_bar" to 15, "obj.steel_bar" to 30,
            "obj.mithril_bar" to 50, "obj.adamantite_bar" to 70, "obj.runite_bar" to 85,
        )

        val REQUIRED: List<String> =
            listOf(
                PREFORM, SWORD, LAVA, WATERFALL, TRIP_HAMMER, GRINDSTONE, POLISHER, POUR_SEQ, WORK_SEQ,
                KOVAC[0], CRUCIBLES[0], JIGS[0],
            ) + BARS.keys

        fun exists(name: String): Boolean {
            val type = when (name.substringBefore('.')) {
                "loc" -> RSCMType.LOC
                "obj" -> RSCMType.OBJ
                "npc" -> RSCMType.NPC
                "seq" -> RSCMType.SEQ
                else -> return false
            }
            return runCatching { name.asRSCM(type) }.isSuccess
        }
    }
}
