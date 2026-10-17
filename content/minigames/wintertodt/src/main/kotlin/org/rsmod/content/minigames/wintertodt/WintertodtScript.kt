package org.rsmod.content.minigames.wintertodt

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
import org.rsmod.api.script.onOpHeld1
import org.rsmod.api.script.onOpHeldU
import org.rsmod.api.script.onOpLoc1
import org.rsmod.game.entity.Player
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import kotlin.random.Random

/**
 * Wintertodt (Firemaking minigame), single-player offline adaptation.
 *
 * Every id is a cache gameval symbol (loc.wint_*, obj.wint_*, ...) checked at startup; anything
 * unresolved is logged and its handler skipped instead of crashing boot.
 *
 * XP / points are OSRS wiki values: chop root 0.3 x WC, fletch kindling 0.6 x Fletching,
 * feed root 3 x FM (10 pts), feed kindling 3.8 x FM (25 pts), light brazier 6 x FM (25 pts),
 * fix brazier 4 x Construction (25 pts). 500 points are needed for a reward.
 *
 * Simplifications (no client UI yet): energy is a world counter reported via chat instead of
 * the wint_status overlay; Wintertodt cold/snowfall damage and brazier explosions are not
 * simulated except for random brazier breakage; supply crate contents are an approximation.
 */
class WintertodtScript @Inject constructor(
    private val locRepo: LocRepository,
    private val eventBus: EventBus,
) : PluginScript() {
    private val logger = InlineLogger()

    private val points = HashMap<Player, Int>()
    private var energy = MAX_ENERGY
    private var lastAnnounced = MAX_ENERGY

    /** Pushes the Wintertodt HUD (energy / points) to front-ends such as the Unity link. */
    private fun ProtectedAccess.overlay() {
        val pts = points[player] ?: 0
        eventBus.publish(
            MinigameOverlayEvent(
                player,
                "Wintertodt",
                listOf("Energy" to "$energy%", "Points" to "$pts / $REWARD_POINTS"),
            )
        )
    }


    override fun ScriptContext.startup() {
        val missing = REQUIRED.filter { !exists(it) }
        if (missing.isNotEmpty()) {
            logger.warn { "Wintertodt: disabled, unresolved symbols: $missing" }
            return
        }
        onOpLoc1(ROOTS) { chopRoots(it.loc); overlay() }
        onOpLoc1(HERB_ROOTS) { pickHerb(it.loc); overlay() }
        onOpLoc1(BRAZIER) { lightBrazier(it.loc); overlay() }
        onOpLoc1(BRAZIER_LIT) { feedBrazier(it.loc); overlay() }
        onOpLoc1(BRAZIER_BROKEN) { fixBrazier(it.loc); overlay() }
        for ((chest, item) in CHESTS) {
            if (exists(chest) && exists(item)) onOpLoc1(chest) { takeSupply(it.loc, item); overlay() }
        }
        onOpHeldU(KNIFE, ROOT) { fletchKindling(); overlay() }
        onOpHeldU(HERB, VIAL) { mixPotion(); overlay() }
        onOpHeld1(REWARD_BOX) { openCrate(); overlay() }
        logger.info { "Wintertodt: registered (${describe()})" }
    }

    // ------------------------------------------------------------------ gathering

    private suspend fun ProtectedAccess.chopRoots(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (!checkEntry()) return
        if (AXES.none { exists(it) && (inv.count(it) > 0 || player.worn.count(it) > 0) }) {
            mes("You need an axe to chop the bruma roots.")
            return
        }
        mes("You swing your axe at the roots.", ChatType.Spam)
        while (inv.freeSpace() > 0) {
            anim(WC_SEQ)
            delay(4)
            if (Random.nextDouble() < chopChance(stat(WOODCUTTING))) {
                invAdd(inv, ROOT, 1)
                statAdvance(WOODCUTTING, 0.3 * stat(WOODCUTTING))
                mes("You get a bruma root.", ChatType.Spam)
            }
        }
        mes("Your inventory is too full to hold any more roots.")
    }

    private suspend fun ProtectedAccess.pickHerb(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (inv.freeSpace() == 0) {
            mes("You don't have enough inventory space.")
            return
        }
        anim(PICK_SEQ)
        delay(3)
        invAdd(inv, HERB, 1)
        mes("You pick a bruma herb.", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.fletchKindling() {
        if (!checkEntry()) return
        while (inv.count(ROOT) > 0) {
            anim(FLETCH_SEQ)
            delay(3)
            if (invDel(inv, ROOT, 1).failure) return
            invAdd(inv, KINDLING, 1)
            statAdvance(FLETCHING, 0.6 * stat(FLETCHING))
        }
    }

    private suspend fun ProtectedAccess.mixPotion() {
        if (invDel(inv, VIAL, 1).failure) return
        if (invDel(inv, HERB, 1).failure) {
            invAdd(inv, VIAL, 1)
            return
        }
        invAdd(inv, POTION4, 1)
        statAdvance(HERBLORE, 0.1 * stat(HERBLORE))
        mes("You mix the bruma herb into the potion.", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.takeSupply(loc: BoundLocInfo, item: String) {
        arriveDelay()
        faceLoc(loc)
        if (inv.freeSpace() == 0) {
            mes("You don't have enough inventory space.")
            return
        }
        invAdd(inv, item, 1)
    }

    // ------------------------------------------------------------------ braziers

    private suspend fun ProtectedAccess.lightBrazier(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (!checkEntry()) return
        if (inv.count(TINDERBOX) == 0) {
            mes("You need a tinderbox to light the brazier.")
            return
        }
        anim(LIGHT_SEQ)
        delay(2)
        locRepo.change(loc, BRAZIER_LIT, LIT_DURATION)
        statAdvance(FIREMAKING, 6.0 * stat(FIREMAKING))
        addPoints(25)
        mes("You light the brazier.", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.fixBrazier(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (!checkEntry()) return
        if (inv.count(HAMMER) == 0) {
            mes("You need a hammer to fix the brazier.")
            return
        }
        anim(FIX_SEQ)
        delay(3)
        locRepo.change(loc, BRAZIER, BROKEN_DURATION)
        statAdvance(CONSTRUCTION, 4.0 * stat(CONSTRUCTION))
        addPoints(25)
        mes("You fix the brazier.", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.feedBrazier(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (!checkEntry()) return
        while (true) {
            val kindling = inv.count(KINDLING) > 0
            val item = if (kindling) KINDLING else if (inv.count(ROOT) > 0) ROOT else break
            anim(FEED_SEQ)
            delay(2)
            if (invDel(inv, item, 1).failure) break
            val fm = stat(FIREMAKING)
            statAdvance(FIREMAKING, if (kindling) 3.8 * fm else 3.0 * fm)
            addPoints(if (kindling) 25 else 10)
            damageWintertodt(if (kindling) 2 else 1)
            if (energy == MAX_ENERGY) return // round just ended
            if (Random.nextInt(BREAK_ONE_IN) == 0) {
                locRepo.change(loc, BRAZIER_BROKEN, BROKEN_DURATION)
                mes("The brazier has broken.")
                return
            }
        }
        mes("You have run out of bruma roots.", ChatType.Spam)
    }

    // ------------------------------------------------------------------ round / rewards

    private fun ProtectedAccess.addPoints(n: Int) {
        val total = (points[player] ?: 0) + n
        points[player] = total
        if (total >= REWARD_POINTS && total - n < REWARD_POINTS) {
            mes("You have earned enough points for a reward!")
        }
        overlay()
    }

    private fun ProtectedAccess.damageWintertodt(amount: Int) {
        energy = (energy - amount).coerceAtLeast(0)
        overlay()
        if (energy == 0) {
            endRound()
            return
        }
        if (lastAnnounced - energy >= 10) {
            lastAnnounced = energy
            mes("Wintertodt's energy: $energy%", ChatType.Spam)
        }
    }

    private fun ProtectedAccess.endRound() {
        mes("The Wintertodt has been subdued!")
        val snapshot = HashMap(points)
        points.clear()
        energy = MAX_ENERGY
        lastAnnounced = MAX_ENERGY
        for ((p, pts) in snapshot) {
            if (p !== player) continue // single-player: rewards for the acting player only
            if (pts < REWARD_POINTS) {
                mes("You didn't earn enough points ($pts/$REWARD_POINTS) for a reward.")
                continue
            }
            // Wiki: 500 pts = 2 rewards, +1 per further 500, remainder = linear chance of one more.
            val over = pts - REWARD_POINTS
            val crates = 2 + over / CRATE_STEP + if (Random.nextInt(CRATE_STEP) < over % CRATE_STEP) 1 else 0
            invAddOrDrop(objRepoFallback = null, crates)
            statAdvance(FIREMAKING, 100.0 * stat(FIREMAKING))
            mes("You receive $crates supply crate${if (crates > 1) "s" else ""}. ($pts points)")
        }
    }

    @Suppress("UNUSED_PARAMETER")
    private fun ProtectedAccess.invAddOrDrop(objRepoFallback: Any?, count: Int) {
        if (invAdd(inv, REWARD_BOX, count).failure) {
            mes("Your inventory is full; the crate could not be added.")
        }
    }

    private suspend fun ProtectedAccess.openCrate() {
        if (inv.freeSpace() < 3) {
            mes("You need at least 3 free inventory spaces to open the crate.")
            return
        }
        if (invDel(inv, REWARD_BOX, 1).failure) return
        repeat(2) {
            val roll = CRATE_TABLE.filter { exists(it.obj) }.random()
            invAdd(inv, roll.obj, Random.nextInt(roll.min, roll.max + 1))
        }
        invAdd(inv, COINS, Random.nextInt(500, 2001))
        mes("You open the supply crate.")
    }

    private fun ProtectedAccess.checkEntry(): Boolean {
        if (statBase(FIREMAKING) < 50) {
            mes("You need a Firemaking level of at least 50 to help subdue the Wintertodt.")
            return false
        }
        return true
    }

    private fun chopChance(level: Int): Double = (0.35 + 0.006 * (level - 1)).coerceIn(0.35, 0.95)

    private fun describe(): String = buildString {
        for (n in listOf(ROOTS, HERB_ROOTS, BRAZIER, BRAZIER_LIT, BRAZIER_BROKEN) + CHESTS.keys) {
            val id = idOf(n, RSCMType.LOC)
            val t = id?.let { ServerCacheManager.getObject(it) }
            append(n).append('=').append(id).append(' ').append(t?.name).append(' ').append(t?.actions).append("; ")
        }
    }

    private data class Roll(val obj: String, val min: Int, val max: Int)

    private companion object {
        const val MAX_ENERGY = 100
        const val REWARD_POINTS = 500
        const val CRATE_STEP = 500
        const val BREAK_ONE_IN = 60
        const val LIT_DURATION = 1000
        const val BROKEN_DURATION = 1000

        const val FIREMAKING = "stat.firemaking"
        const val WOODCUTTING = "stat.woodcutting"
        const val FLETCHING = "stat.fletching"
        const val CONSTRUCTION = "stat.construction"
        const val HERBLORE = "stat.herblore"

        const val ROOTS = "loc.wint_roots"
        const val HERB_ROOTS = "loc.wint_herb_roots"
        const val BRAZIER = "loc.wint_brazier"
        const val BRAZIER_LIT = "loc.wint_brazier_lit"
        const val BRAZIER_BROKEN = "loc.wint_brazier_broken"

        const val ROOT = "obj.wint_bruma_root"
        const val KINDLING = "obj.wint_bruma_kindling"
        const val HERB = "obj.wint_herb"
        const val VIAL = "obj.wint_vial"
        const val POTION4 = "obj.wint_potion4"
        const val REWARD_BOX = "obj.wint_reward_box"
        const val KNIFE = "obj.knife"
        const val TINDERBOX = "obj.tinderbox"
        const val HAMMER = "obj.hammer"
        const val COINS = "obj.coins"

        const val WC_SEQ = "seq.human_woodcutting_bronze_axe"
        const val PICK_SEQ = "seq.human_pickupfloor"
        const val FLETCH_SEQ = "seq.human_fletching"
        const val LIGHT_SEQ = "seq.wint_light_brazier_bruma"
        const val FEED_SEQ = "seq.wint_light_brazier_bruma"
        const val FIX_SEQ = "seq.human_pickupfloor"

        val AXES = listOf(
            "obj.bronze_axe", "obj.iron_axe", "obj.steel_axe", "obj.black_axe", "obj.mithril_axe",
            "obj.adamant_axe", "obj.rune_axe", "obj.dragon_axe", "obj.infernal_axe", "obj.crystal_axe",
            "obj.3a_axe",
        )

        val CHESTS = linkedMapOf(
            "loc.wint_chest_hammer" to "obj.hammer",
            "loc.wint_chest_knife" to "obj.knife",
            "loc.wint_chest_axe" to "obj.bronze_axe",
            "loc.wint_chest_tinderbox" to "obj.tinderbox",
            "loc.wint_chest_vial" to "obj.wint_vial",
        )

        /** Approximation of the supply crate table (common wiki rewards, quantities simplified). */
        val CRATE_TABLE = listOf(
            Roll("obj.cert_oak_logs", 10, 40), Roll("obj.cert_willow_logs", 10, 40),
            Roll("obj.cert_maple_logs", 8, 30), Roll("obj.cert_yew_logs", 5, 20),
            Roll("obj.cert_coal", 10, 40), Roll("obj.cert_iron_ore", 10, 40),
            Roll("obj.cert_mithril_ore", 4, 15), Roll("obj.cert_raw_tuna", 5, 20),
            Roll("obj.cert_raw_lobster", 5, 15), Roll("obj.cert_raw_swordfish", 3, 12),
            Roll("obj.cert_uncut_sapphire", 2, 6), Roll("obj.cert_uncut_emerald", 1, 4),
            Roll("obj.pure_essence", 0, 0),
        ).filter { it.max > 0 }

        val REQUIRED = listOf(
            ROOTS, BRAZIER, BRAZIER_LIT, BRAZIER_BROKEN, HERB_ROOTS, ROOT, KINDLING, HERB, VIAL,
            POTION4, REWARD_BOX, KNIFE, TINDERBOX, HAMMER, COINS, WC_SEQ, PICK_SEQ, FLETCH_SEQ,
            LIGHT_SEQ,
        )

        fun idOf(name: String, type: RSCMType): Int? = runCatching { name.asRSCM(type) }.getOrNull()

        fun exists(name: String): Boolean {
            val type = when (name.substringBefore('.')) {
                "loc" -> RSCMType.LOC
                "obj" -> RSCMType.OBJ
                "npc" -> RSCMType.NPC
                "seq" -> RSCMType.SEQ
                else -> return false
            }
            return idOf(name, type) != null
        }
    }
}
