package org.rsmod.content.minigames.gotr

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import jakarta.inject.Inject
import org.rsmod.api.player.output.ChatType
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.events.EventBus
import org.rsmod.api.player.events.MinigameOverlayEvent
import org.rsmod.api.repo.loc.LocRepository
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
 * Guardians of the Rift game logic (Runecraft minigame), single-player offline adaptation.
 *
 * Does NOT touch what already exists in the fork: tote_portal_to_gotr_parent / gotr_entry
 * (WizardsTowerLocs.kt), Cordelia pouch repair (PouchRepairEvents.kt), guardian essence in
 * pouches / combo altars, Rift Guardian pet tables.
 *
 * Flow: mine guardian remains (gotr_essence_tier_*) for fragments -> workbench turns fragments
 * into guardian essence -> enter a guardian (gotr_portal_<rune> loc) to craft runes in place
 * (simplification: no altar teleport) which yields guardian stones + elemental/catalytic points
 * and charges an uncharged cell -> place charged cells on cell tiles -> power the Great Guardian
 * with stones -> at 100% the rift closes and points become reward-rift searches.
 *
 * HAND-TYPED (wiki): rune levels / XP (standard altar values), points, power per stone,
 * reward table, 27 Runecraft requirement. Guardian rotation, portal timers, rift-guardian
 * health, barriers and abyssal creatures are not simulated.
 */
class GuardiansOfTheRiftScript @Inject constructor(
    private val locRepo: LocRepository,
    private val eventBus: EventBus,
) : PluginScript() {
    private val logger = InlineLogger()

    private class State {
        var elemental = 0
        var catalytic = 0
        var searches = 0
    }

    private val states = HashMap<Player, State>()
    private var power = 0
    /** Stones fed to the Great Guardian this game (wiki: 250 per player in the temple = 100%). */
    private var charge = 0

    /** Pushes the GotR HUD (guardian power / points / searches) to front-ends such as the Unity link. */
    private fun ProtectedAccess.overlay() {
        val s = states[player] ?: State()
        eventBus.publish(
            MinigameOverlayEvent(
                player,
                "Guardians of the Rift",
                listOf(
                    "Guardian power" to "$power%",
                    "Elemental energy" to "${s.elemental}",
                    "Catalytic energy" to "${s.catalytic}",
                    "Reward searches" to "${s.searches}",
                ),
            )
        )
    }


    override fun ScriptContext.startup() {
        val missing = REQUIRED.filter { !exists(it) }
        if (missing.isNotEmpty()) {
            logger.warn { "GotR: disabled, unresolved symbols: $missing" }
            return
        }
        for (r in REMAINS) onOpLoc1(r) { mine(it.loc); overlay() }
        onOpLoc1(WORKBENCH) { workbench(it.loc); overlay() }
        onOpLoc1(UNCHARGED_CELLS) { takeCell(it.loc); overlay() }
        for (rune in RUNES.filter { exists(it.portal) && exists(it.obj) }) {
            onOpLoc1(rune.portal) { craft(it.loc, rune); overlay() }
        }
        for ((tier, cell) in CELLS.withIndex()) {
            onOpLocU(CELL_TILE_INACTIVE, cell) { placeCell(it.loc, cell, tier); overlay() }
        }
        onOpNpc1(GREAT_GUARDIAN) { powerUp(it.npc); overlay() }
        onOpLoc1(REWARD_RIFT) { searchRift(it.loc); overlay() }
        logger.info { "GotR: registered (${describe()})" }
    }

    private fun ProtectedAccess.state() = states.getOrPut(player) { State() }

    private fun ProtectedAccess.checkLevel(): Boolean {
        if (statBase(RUNECRAFT) < 27) {
            mes("You need a Runecraft level of at least 27 to help the Great Guardian.")
            return false
        }
        return true
    }

    private suspend fun ProtectedAccess.mine(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (!checkLevel()) return
        if (PICKAXES.none { inv.count(it) > 0 || player.worn.count(it) > 0 }) {
            mes("You need a pickaxe to mine the guardian remains.")
            return
        }
        while (true) {
            anim(MINE_SEQ)
            delay(4)
            if (Random.nextDouble() < 0.5 + stat(MINING) / 250.0) {
                if (invAdd(inv, FRAGMENT, 1).failure) {
                    mes("You can't carry any more fragments.")
                    return
                }
            }
            if (inv.count(FRAGMENT) >= FRAGMENT_TARGET) {
                mes("You have mined plenty of guardian fragments.", ChatType.Spam)
                return
            }
        }
    }

    private suspend fun ProtectedAccess.workbench(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (!checkLevel()) return
        while (inv.count(FRAGMENT) > 0 && inv.freeSpace() > 0) {
            anim(CHISEL_SEQ)
            delay(2)
            if (invDel(inv, FRAGMENT, 1).failure) return
            invAdd(inv, ESSENCE, 1)
            statAdvance(CRAFTING, 0.5) // hand-typed approximation
        }
        if (inv.count(FRAGMENT) > 0) mes("You need more inventory space for guardian essence.")
    }

    private suspend fun ProtectedAccess.takeCell(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (invAdd(inv, CELL_UNCHARGED, 1).failure) mes("You don't have enough inventory space.")
    }

    private suspend fun ProtectedAccess.craft(loc: BoundLocInfo, rune: Rune) {
        arriveDelay()
        faceLoc(loc)
        if (!checkLevel()) return
        if (statBase(RUNECRAFT) < rune.level) {
            mes("You need a Runecraft level of ${rune.level} to craft ${rune.name} runes.")
            return
        }
        val n = inv.count(ESSENCE)
        if (n == 0) {
            mes("You need guardian essence to craft runes here.")
            return
        }
        anim(RC_SEQ)
        delay(3)
        if (invDel(inv, ESSENCE, n).failure) return
        invAdd(inv, rune.obj, n)
        statAdvance(RUNECRAFT, rune.xp * n)
        val s = state()
        // Wiki: one guardian stone per essence imbued; energy is granted when stones are fed in.
        invAdd(inv, if (rune.elemental) STONE_ELEMENTAL else STONE_CATALYTIC, n)
        if (inv.count(CELL_UNCHARGED) > 0 && invDel(inv, CELL_UNCHARGED, 1).failure.not()) {
            invAdd(inv, CELLS[rune.cellTier], 1)
        }
        mes("You bind the essence into ${rune.name} runes. Energy: ${s.elemental} elemental / ${s.catalytic} catalytic.")
    }

    private suspend fun ProtectedAccess.placeCell(loc: BoundLocInfo, cell: String, tier: Int) {
        arriveDelay()
        faceLoc(loc)
        if (invDel(inv, cell, 1).failure) return
        locRepo.change(loc, CELL_TILES[tier], CELL_DURATION)
        val s = state()
        // Wiki: placing a cell on an empty tile gives 17/20/24/30 energy of each type (weak..overcharged).
        val e = CELL_PLACE_ENERGY[tier]
        s.elemental = (s.elemental + e).coerceAtMost(ENERGY_CAP)
        s.catalytic = (s.catalytic + e).coerceAtMost(ENERGY_CAP)
        mes("You place the cell on the tile.", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.powerUp(npc: Npc) {
        faceEntitySquare(npc)
        if (!checkLevel()) return
        val s0 = state()
        var stones = 0
        for ((stone, value) in STONE_POWER) {
            val n = inv.count(stone)
            if (n <= 0 || invDel(inv, stone, n).failure) continue
            stones += n
            // Wiki: elemental/catalytic stone = 2 energy of its type, polyelemental = 3 elemental.
            if (stone == STONE_CATALYTIC) s0.catalytic = (s0.catalytic + n * value).coerceAtMost(ENERGY_CAP)
            else s0.elemental = (s0.elemental + n * value).coerceAtMost(ENERGY_CAP)
        }
        if (stones == 0) {
            mes("You have no guardian stones to power up the Great Guardian.")
            return
        }
        statAdvance(RUNECRAFT, stones * 5.0) // wiki: 5 Runecraft XP per stone
        charge = (charge + stones).coerceAtMost(SOLO_CHARGE)
        power = charge * 100 / SOLO_CHARGE
        mes("The Great Guardian's power is now $power%.")
        if (power >= 100) {
            power = 0
            charge = 0
            val s = state()
            // Wiki: 1 point per 100 energy of each type (remainder = % chance of one more);
            // a search costs one elemental + one catalytic point. 45 x RC level if >= 300 energy.
            val ep = points(s.elemental)
            val cp = points(s.catalytic)
            val earned = minOf(ep, cp)
            s.searches += earned
            if (s.elemental + s.catalytic >= MIN_ENERGY_FOR_XP) statAdvance(RUNECRAFT, 45.0 * stat(RUNECRAFT))
            mes("The Great Guardian closes the rift! You earn $earned reward search${if (earned == 1) "" else "es"}.")
            s.elemental = 0
            s.catalytic = 0
            for (f in listOf(FRAGMENT, ESSENCE)) {
                val c = inv.count(f)
                if (c > 0) invDel(inv, f, c)
            }
        }
    }

    private fun points(energy: Int): Int =
        energy / POINTS_PER_SEARCH + if (Random.nextInt(POINTS_PER_SEARCH) < energy % POINTS_PER_SEARCH) 1 else 0

    private suspend fun ProtectedAccess.searchRift(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        val s = state()
        if (s.searches <= 0) {
            mes("You have no rewards to claim.")
            return
        }
        while (s.searches > 0 && inv.freeSpace() > 0) {
            delay(1)
            s.searches--
            overlay()
            val roll = REWARDS.random()
            invAdd(inv, roll.obj, Random.nextInt(roll.min, roll.max + 1))
            if (Random.nextInt(INTRICATE_ONE_IN) == 0 && exists(INTRICATE_POUCH)) invAdd(inv, INTRICATE_POUCH, 1)
        }
        mes("You have ${s.searches} reward search${if (s.searches == 1) "" else "es"} left.")
    }

    private fun describe(): String = RUNES.joinToString("; ") {
        val id = runCatching { it.portal.asRSCM(RSCMType.LOC) }.getOrNull()
        val t = id?.let { i -> ServerCacheManager.getObject(i) }
        "${it.portal}=$id ${t?.name} ${t?.actions}"
    }

    private data class Rune(
        val name: String, val portal: String, val obj: String, val level: Int, val xp: Double,
        val elemental: Boolean, val cellTier: Int,
    )

    private data class Roll(val obj: String, val min: Int, val max: Int)

    private companion object {
        const val FRAGMENT_TARGET = 120
        const val CELL_DURATION = 500
        const val POINTS_PER_SEARCH = 100
        const val SOLO_CHARGE = 250
        const val ENERGY_CAP = 1000
        const val MIN_ENERGY_FOR_XP = 300
        val CELL_PLACE_ENERGY = intArrayOf(17, 20, 24, 30)
        const val INTRICATE_ONE_IN = 50

        const val RUNECRAFT = "stat.runecraft"
        const val MINING = "stat.mining"
        const val CRAFTING = "stat.crafting"

        const val FRAGMENT = "obj.gotr_guardian_fragment"
        const val ESSENCE = "obj.gotr_guardian_essence"
        const val STONE_ELEMENTAL = "obj.gotr_guardian_stone_elemental"
        const val STONE_CATALYTIC = "obj.gotr_guardian_stone_catalytic"
        const val STONE_POLY = "obj.gotr_guardian_stone_polyelemental"
        const val CELL_UNCHARGED = "obj.gotr_cell_uncharged"
        const val INTRICATE_POUCH = "obj.gotr_intricate_pouch"

        const val WORKBENCH = "loc.gotr_workbench"
        const val UNCHARGED_CELLS = "loc.gotr_uncharged_cells"
        const val CELL_TILE_INACTIVE = "loc.gotr_cell_tile_inactive"
        const val REWARD_RIFT = "loc.gotr_reward_rift"
        const val GREAT_GUARDIAN = "npc.gotr_great_guardian"

        const val MINE_SEQ = "seq.human_mining_bronze_pickaxe"
        const val CHISEL_SEQ = "seq.gotr_chisel_essence"
        const val RC_SEQ = "seq.human_runecraft"

        val REMAINS = listOf("loc.gotr_essence_tier_1", "loc.gotr_essence_tier_1_large")
        val CELLS = (1..4).map { "obj.gotr_cell_tier$it" }
        val CELL_TILES = (1..4).map { "loc.gotr_cell_tile_tier$it" }
        val STONE_POWER = linkedMapOf(STONE_ELEMENTAL to 2, STONE_CATALYTIC to 2, STONE_POLY to 3)

        val PICKAXES = listOf(
            "obj.bronze_pickaxe", "obj.iron_pickaxe", "obj.steel_pickaxe", "obj.black_pickaxe",
            "obj.mithril_pickaxe", "obj.adamant_pickaxe", "obj.rune_pickaxe", "obj.dragon_pickaxe",
            "obj.infernal_pickaxe", "obj.crystal_pickaxe", "obj.3a_pickaxe",
        ).filter { exists(it) }

        // Standard-altar levels/XP from the wiki (hand-typed).
        val RUNES = listOf(
            Rune("air", "loc.gotr_portal_air", "obj.airrune", 1, 5.0, true, 0),
            Rune("mind", "loc.gotr_portal_mind", "obj.mindrune", 2, 5.5, false, 0),
            // Cell tiers per wiki: air/mind/body weak, water/cosmic/chaos medium, earth/nature/law strong,
            // fire/death/blood overcharged.
            Rune("water", "loc.gotr_portal_water", "obj.waterrune", 5, 6.0, true, 1),
            Rune("earth", "loc.gotr_portal_earth", "obj.earthrune", 9, 6.5, true, 2),
            Rune("fire", "loc.gotr_portal_fire", "obj.firerune", 14, 7.0, true, 3),
            Rune("body", "loc.gotr_portal_body", "obj.bodyrune", 20, 7.5, false, 0),
            Rune("cosmic", "loc.gotr_portal_cosmic", "obj.cosmicrune", 27, 8.0, false, 1),
            Rune("chaos", "loc.gotr_portal_chaos", "obj.chaosrune", 35, 8.5, false, 1),
            Rune("nature", "loc.gotr_portal_nature", "obj.naturerune", 44, 9.0, false, 2),
            Rune("law", "loc.gotr_portal_law", "obj.lawrune", 54, 9.5, false, 2),
            Rune("death", "loc.gotr_portal_death", "obj.deathrune", 65, 10.0, false, 3),
            Rune("blood", "loc.gotr_portal_blood", "obj.bloodrune", 77, 10.5, false, 3),
        )

        /** Approximation of the reward rift table. */
        val REWARDS = listOf(
            Roll("obj.airrune", 50, 150), Roll("obj.waterrune", 50, 150),
            Roll("obj.earthrune", 50, 150), Roll("obj.firerune", 50, 150),
            Roll("obj.mindrune", 30, 100), Roll("obj.chaosrune", 15, 50),
            Roll("obj.naturerune", 10, 30), Roll("obj.lawrune", 8, 25),
            Roll("obj.deathrune", 5, 20), Roll("obj.abyssal_pearl", 1, 3),
        )

        val REQUIRED: List<String> =
            listOf(
                FRAGMENT, ESSENCE, STONE_ELEMENTAL, STONE_CATALYTIC, STONE_POLY, CELL_UNCHARGED,
                WORKBENCH, UNCHARGED_CELLS, CELL_TILE_INACTIVE, REWARD_RIFT, GREAT_GUARDIAN,
                MINE_SEQ, CHISEL_SEQ, RC_SEQ,
            ) + REMAINS + CELLS + CELL_TILES + REWARDS.map { it.obj }

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
