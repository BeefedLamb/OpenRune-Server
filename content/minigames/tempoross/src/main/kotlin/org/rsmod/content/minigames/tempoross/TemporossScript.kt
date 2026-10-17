package org.rsmod.content.minigames.tempoross

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import org.rsmod.api.player.output.ChatType
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.events.EventBus
import org.rsmod.api.player.events.MinigameOverlayEvent
import jakarta.inject.Inject
import org.rsmod.api.script.onOpHeld1
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
 * Tempoross (Fishing minigame), single-player offline adaptation.
 *
 * All loc/npc/obj/seq ids are cache gameval symbols verified at startup; unresolved ones disable
 * the module (core) or skip the individual handler (optional) with a warning.
 *
 * Flow: take tools from crates -> harpoon fish at the harpoonfish spots (35 Fishing) -> optionally
 * cook at the shrine fire -> load fish into an ammunition crate (lowers Tempoross energy) ->
 * at 0 energy the boss becomes vulnerable: harpoon it (op1 on the whirlpool/vulnerable boss npc)
 * to deplete essence -> victory grants reward permits -> fish the reward pool for caskets.
 * Tether to a mast/totem with a rope to survive waves; waves otherwise knock fish out of your
 * inventory. Pump water with a bucket to douse... (fires are not simulated).
 *
 * HAND-TYPED / APPROXIMATED (wiki-based, not cache): level 35, fish/energy numbers, XP rates,
 * points, permit thresholds, casket table.
 */
class TemporossScript @Inject constructor(private val eventBus: EventBus) : PluginScript() {
    private val logger = InlineLogger()

    private class State {
        var points = 0
        var tethered = false
        var permits = 0
    }

    private val states = HashMap<Player, State>()
    private var energy = MAX_ENERGY
    private var essence = MAX_ESSENCE
    private var vulnerable = false

    /** Pushes the Tempoross HUD (energy / essence / points) to front-ends such as the Unity link. */
    private fun ProtectedAccess.overlay() {
        val s = states[player] ?: State()
        eventBus.publish(
            MinigameOverlayEvent(
                player,
                "Tempoross",
                listOf(
                    "Energy" to "$energy%",
                    "Essence" to "$essence%",
                    "Status" to if (vulnerable) "VULNERABLE" else "Load the cannons",
                    "Points" to "${s.points}",
                    "Tethered" to if (s.tethered) "Yes" else "No",
                    "Permits" to "${s.permits}",
                ),
            )
        )
    }


    override fun ScriptContext.startup() {
        val missing = REQUIRED.filter { !exists(it) }
        if (missing.isNotEmpty()) {
            logger.warn { "Tempoross: disabled, unresolved symbols: $missing" }
            return
        }
        for ((crate, item) in CRATES) onOpLoc1(crate) { takeTool(it.loc, item); overlay() }
        for (spot in SPOTS) onOpNpc1(spot) { fish(it.npc); overlay() }
        for (crate in AMMO_CRATE_NPCS) onOpNpc1(crate) { loadCrate(it.npc); overlay() }
        for (boss in VULNERABLE_BOSS) onOpNpc1(boss) { harpoonBoss(it.npc); overlay() }
        onOpLoc1(SHRINE_FIRE) { cook(it.loc); overlay() }
        onOpLocU(SHRINE_FIRE, RAW_FISH) { cook(it.loc); overlay() }
        for (t in TETHERS) onOpLoc1(t) { tether(it.loc); overlay() }
        for (p in PUMPS) onOpLoc1(p) { pump(it.loc); overlay() }
        onOpLoc1(REWARD_POOL) { fishRewardPool(it.loc); overlay() }
        onOpHeld1(CASKET) { openCasket(); overlay() }
        logger.info { "Tempoross: registered (${SPOTS.size} spots, ${AMMO_CRATE_NPCS.size} crates)" }
    }

    private fun ProtectedAccess.state() = states.getOrPut(player) { State() }

    private fun ProtectedAccess.hasHarpoon(): Boolean =
        HARPOONS.any { inv.count(it) > 0 || player.worn.count(it) > 0 }

    private suspend fun ProtectedAccess.takeTool(loc: BoundLocInfo, item: String) {
        arriveDelay()
        faceLoc(loc)
        if (inv.freeSpace() == 0) {
            mes("You don't have enough inventory space.")
            return
        }
        invAdd(inv, item, 1)
    }

    private fun ProtectedAccess.checkLevel(): Boolean {
        if (statBase(FISHING) < 35) {
            mes("You need a Fishing level of at least 35 to help subdue Tempoross.")
            return false
        }
        return true
    }

    private suspend fun ProtectedAccess.fish(npc: Npc) {
        faceEntitySquare(npc)
        if (!checkLevel()) return
        if (!hasHarpoon()) {
            mes("You need a harpoon to catch harpoonfish.")
            return
        }
        mes("You cast your harpoon into the water.", ChatType.Spam)
        while (inv.freeSpace() > 0) {
            anim(HARPOON_SEQ)
            delay(4)
            if (Random.nextDouble() < catchChance(stat(FISHING))) {
                invAdd(inv, RAW_FISH, 1)
                statAdvance(FISHING, xpBase(statBase(FISHING)) / 100.0)
                state().points += 5
                overlay()
            }
            if (Random.nextInt(WAVE_ONE_IN) == 0 && wave()) return
        }
        mes("Your inventory is too full to catch any more fish.")
    }

    /** Returns true if the action was interrupted. */
    private fun ProtectedAccess.wave(): Boolean {
        val s = state()
        if (s.tethered) {
            mes("A colossal wave crashes into the ship, but you are tethered.")
            s.tethered = false
            s.points += 10
            return false
        }
        mes("A colossal wave crashes into you! Some of your fish are washed away.")
        val lost = minOf(inv.count(RAW_FISH), Random.nextInt(1, 4))
        if (lost > 0) invDel(inv, RAW_FISH, lost)
        return true
    }

    private suspend fun ProtectedAccess.cook(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (!checkLevel()) return
        while (inv.count(RAW_FISH) > 0) {
            anim(COOK_SEQ)
            delay(3)
            if (invDel(inv, RAW_FISH, 1).failure) return
            invAdd(inv, COOKED_FISH, 1)
            statAdvance(COOKING, COOK_XP)
            state().points += 10
            overlay()
        }
    }

    private suspend fun ProtectedAccess.loadCrate(npc: Npc) {
        faceEntitySquare(npc)
        if (!checkLevel()) return
        if (vulnerable) {
            mes("Tempoross is vulnerable! Attack it with your harpoon.")
            return
        }
        while (true) {
            val cooked = inv.count(COOKED_FISH) > 0
            val item = if (cooked) COOKED_FISH else if (inv.count(RAW_FISH) > 0) RAW_FISH else break
            delay(2)
            if (invDel(inv, item, 1).failure) break
            statAdvance(FISHING, xpBase(statBase(FISHING)) / 10.0)
            state().points += if (cooked) 65 else 20
            energy = (energy - if (cooked) 2 else 1).coerceAtLeast(0)
            overlay()
            if (energy == 0) {
                vulnerable = true
                mes("Tempoross's energy is depleted - it is vulnerable! Harpoon it while you can.")
                return
            }
        }
        mes("Tempoross energy: $energy%", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.harpoonBoss(npc: Npc) {
        faceEntitySquare(npc)
        if (!checkLevel()) return
        if (!vulnerable) {
            mes("Tempoross is too powerful to attack right now. Load the cannons first.")
            return
        }
        if (!hasHarpoon()) {
            mes("You need a harpoon to attack Tempoross.")
            return
        }
        repeat(VULNERABLE_ATTEMPTS) {
            anim(HARPOON_SEQ)
            delay(4)
            essence = (essence - Random.nextInt(2, 5)).coerceAtLeast(0)
            statAdvance(FISHING, xpBase(statBase(FISHING)) * 11 / 100.0)
            state().points += 55
            overlay()
            if (essence == 0) {
                victory()
                return
            }
        }
        vulnerable = false
        energy = MAX_ENERGY / 2
        mes("Tempoross recovers. Essence remaining: $essence%. Load the cannons again.")
    }

    private fun ProtectedAccess.victory() {
        val s = state()
        // Wiki: 1 permit at 2000 pts, +1 per further 700, remainder = linear chance of one more.
        val over = s.points - MIN_POINTS
        val permits = if (over < 0) 0 else 1 + over / PERMIT_STEP + if (Random.nextInt(PERMIT_STEP) < over % PERMIT_STEP) 1 else 0
        mes("Tempoross is subdued! You earned ${s.points} points.")
        if (permits > 0) {
            s.permits += permits
            mes("You receive $permits reward permit${if (permits > 1) "s" else ""}. Fish the reward pool to use them.")
        } else {
            mes("You needed at least $MIN_POINTS points for a reward.")
        }
        s.points = 0
        energy = MAX_ENERGY
        essence = MAX_ESSENCE
        vulnerable = false
    }

    private suspend fun ProtectedAccess.tether(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        if (inv.count(ROPE) == 0) {
            mes("You need a rope to tether yourself.")
            return
        }
        anim(TETHER_SEQ)
        state().tethered = true
        mes("You tether yourself to the mast.", ChatType.Spam)
    }

    private suspend fun ProtectedAccess.pump(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        var n = 0
        while (inv.count(BUCKET) > 0) {
            delay(2)
            if (invDel(inv, BUCKET, 1).failure) break
            invAdd(inv, BUCKET_WATER, 1)
            n++
        }
        if (n == 0) mes("You need an empty bucket to pump water.")
    }

    private suspend fun ProtectedAccess.fishRewardPool(loc: BoundLocInfo) {
        arriveDelay()
        faceLoc(loc)
        val s = state()
        if (s.permits <= 0) {
            mes("You have no reward permits.")
            return
        }
        while (s.permits > 0 && inv.freeSpace() > 0) {
            anim(HARPOON_SEQ)
            delay(3)
            s.permits--
            invAdd(inv, CASKET, 1)
        }
        mes("You have ${s.permits} reward permit${if (s.permits == 1) "" else "s"} remaining.")
    }

    private fun ProtectedAccess.openCasket() {
        if (inv.freeSpace() < 2) {
            mes("You need at least 2 free inventory spaces to open the casket.")
            return
        }
        if (invDel(inv, CASKET, 1).failure) return
        val roll = CASKET_TABLE.filter { exists(it.obj) }.random()
        invAdd(inv, roll.obj, Random.nextInt(roll.min, roll.max + 1))
        if (exists(PET) && Random.nextInt(PET_ONE_IN) == 0 && inv.freeSpace() > 0) {
            invAdd(inv, PET, 1)
            mes("You have a funny feeling like you're being followed.")
        }
        mes("You open the reward casket.")
    }

    /** OSRS wiki Tempoross XP base (per-action XP = base/100 catch, base/10 crate, base*11/100 spirit pool). */
    private fun xpBase(level: Int): Int =
        if (level < 70) 450 + (1000 - 450) * (level - 35) / (99 - 35) else 890 + (1000 - 890) * (level - 70) / (99 - 70)

    private fun catchChance(level: Int): Double = (0.30 + 0.006 * (level - 35)).coerceIn(0.30, 0.85)

    private data class Roll(val obj: String, val min: Int, val max: Int)

    private companion object {
        const val MAX_ENERGY = 100
        const val MAX_ESSENCE = 100
        const val VULNERABLE_ATTEMPTS = 8
        const val WAVE_ONE_IN = 25
        const val MIN_POINTS = 2000
        const val PERMIT_STEP = 700
        const val PET_ONE_IN = 8000

        // Cooking XP per harpoonfish: not given by the wiki Tempoross page; approximation.
        const val COOK_XP = 10.0

        const val FISHING = "stat.fishing"
        const val COOKING = "stat.cooking"

        const val RAW_FISH = "obj.tempoross_raw_harpoonfish"
        const val COOKED_FISH = "obj.tempoross_harpoonfish"
        const val CASKET = "obj.tempoross_reward_casket"
        const val PET = "obj.temporosspet"
        const val ROPE = "obj.rope"
        const val BUCKET = "obj.bucket_empty"
        const val BUCKET_WATER = "obj.bucket_water"

        const val SHRINE_FIRE = "loc.tempoross_shrine_fire"
        const val REWARD_POOL = "loc.tempoross_rewardpool"

        const val HARPOON_SEQ = "seq.human_harpoon"
        const val COOK_SEQ = "seq.human_cooking"
        const val TETHER_SEQ = "seq.tempoross_tether"

        val HARPOONS = listOf(
            "obj.harpoon", "obj.dragon_harpoon", "obj.infernal_harpoon", "obj.crystal_harpoon",
        ).filter { exists(it) }

        val CRATES = linkedMapOf(
            "loc.tempoross_crate_harpoon" to "obj.harpoon",
            "loc.tempoross_crate_rope" to "obj.rope",
            "loc.tempoross_crate_bucket" to "obj.bucket_empty",
            "loc.tempoross_crate_hammer" to "obj.hammer",
        )
        val SPOTS = listOf(
            "npc.tempoross_harpoonfish_fishingspot_north",
            "npc.tempoross_harpoonfish_fishingspot_south",
            "npc.tempoross_harpoonfish_fishingspot_special",
            "npc.tempoross_p2_fishingspot",
        )
        val AMMO_CRATE_NPCS = (1..4).map { "npc.tempoross_npc_crate_ammunition_$it" }
        val VULNERABLE_BOSS = listOf("npc.tempoross_boss_whirlpool")
        val TETHERS = listOf(
            "loc.tempoross_mast_bottom_west", "loc.tempoross_mast_bottom_east",
            "loc.tempoross_totem_north", "loc.tempoross_totem_south",
        )
        val PUMPS = listOf("loc.tempoross_water_pump", "loc.tempoross_water_pump_dock")

        /** Approximation of the reward casket table. */
        val CASKET_TABLE = listOf(
            Roll("obj.cert_raw_tuna", 10, 50), Roll("obj.cert_raw_lobster", 10, 50),
            Roll("obj.cert_raw_swordfish", 10, 40), Roll("obj.cert_raw_shark", 5, 25),
            Roll("obj.cert_seaweed", 10, 40), Roll("obj.spirit_flakes", 32, 64),
            Roll("obj.coins", 1000, 5000), Roll("obj.tempoross_damp_egg", 1, 1),
        )

        val REQUIRED: List<String> =
            listOf(
                RAW_FISH, COOKED_FISH, CASKET, ROPE, BUCKET, BUCKET_WATER, SHRINE_FIRE, REWARD_POOL,
                HARPOON_SEQ, COOK_SEQ, TETHER_SEQ, "obj.harpoon", "obj.hammer", "obj.coins",
            ) + CRATES.keys + SPOTS + AMMO_CRATE_NPCS + VULNERABLE_BOSS + TETHERS + PUMPS

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
