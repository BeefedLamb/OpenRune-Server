package org.rsmod.content.skills.smithing.blastfurnace

import jakarta.inject.Inject
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.player.stat.smithingLvl
import org.rsmod.api.player.vars.VarPlayerIntMapSetter
import org.rsmod.api.script.onOpLoc1
import org.rsmod.api.script.onOpLocU
import org.rsmod.api.stats.xpmod.XpModifiers
import org.rsmod.api.table.smithing.SmithingBarsRow
import org.rsmod.content.skills.smithing.smelting.SmithingSmeltXp
import org.rsmod.game.entity.Player
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext

/**
 * Simplified solo Blast Furnace (Keldagrim).
 *
 * - Put ore on the conveyor belt (op1 or use-ore-on-belt): all of that ore type in the inventory is
 *   moved into the furnace (per-player cache varbits `varbit.blast_furnace_<ore>`).
 * - The furnace immediately smelts whatever it can: BF rule = half the regular coal (steel 1,
 *   mithril 2, adamantite 3, runite 4); bronze/iron/gold/silver need no coal; iron never fails.
 *   Smithing XP is the cache `SmithingBarsRow.xp` (gold uses the goldsmith-gauntlet alternate).
 * - Bars wait in the dispenser (`varbit.blast_furnace_<bar>_bars`, max 28 each). Take them with
 *   ice gloves equipped, or a bucket of water (cools them; bucket is emptied).
 *
 * NOT modelled (deferred): foreman fee (<60 Smithing), coffer drain, stoking/pumping/belt/pipe
 * repairs, temperature, the bar-select interface, smithing-catalyst/lead/nickel bars.
 */
class BlastFurnaceScript @Inject constructor(private val xpMods: XpModifiers) : PluginScript() {

    private data class BfBar(
        val bar: String,
        val primary: String,
        val secondary: String?,
        val secondaryAmount: Int,
        val barVarbit: String,
    ) {
        lateinit var row: SmithingBarsRow
    }

    private val oreVarbits =
        linkedMapOf(
            "obj.copper_ore" to "varbit.blast_furnace_copper_ore",
            "obj.tin_ore" to "varbit.blast_furnace_tin_ore",
            "obj.iron_ore" to "varbit.blast_furnace_iron_ore",
            "obj.mithril_ore" to "varbit.blast_furnace_mithril_ore",
            "obj.adamantite_ore" to "varbit.blast_furnace_adamantite_ore",
            "obj.runite_ore" to "varbit.blast_furnace_runite_ore",
            "obj.gold_ore" to "varbit.blast_furnace_gold_ore",
            "obj.silver_ore" to "varbit.blast_furnace_silver_ore",
            "obj.coal" to "varbit.blast_furnace_coal",
        )

    // Highest tier first so coal goes to the most valuable bars.
    private val bars =
        listOf(
            BfBar("obj.runite_bar", "obj.runite_ore", COAL, 4, "varbit.blast_furnace_runite_bars"),
            BfBar("obj.adamantite_bar", "obj.adamantite_ore", COAL, 3, "varbit.blast_furnace_adamantite_bars"),
            BfBar("obj.mithril_bar", "obj.mithril_ore", COAL, 2, "varbit.blast_furnace_mithril_bars"),
            BfBar("obj.steel_bar", "obj.iron_ore", COAL, 1, "varbit.blast_furnace_steel_bars"),
            BfBar("obj.gold_bar", "obj.gold_ore", null, 0, "varbit.blast_furnace_gold_bars"),
            BfBar("obj.silver_bar", "obj.silver_ore", null, 0, "varbit.blast_furnace_silver_bars"),
            BfBar("obj.bronze_bar", "obj.copper_ore", "obj.tin_ore", 1, "varbit.blast_furnace_bronze_bars"),
            BfBar("obj.iron_bar", "obj.iron_ore", null, 0, "varbit.blast_furnace_iron_bars"),
        )

    override fun ScriptContext.startup() {
        val rows = SmithingBarsRow.all().associateBy { it.output.internalName }
        for (bar in bars) {
            bar.row = rows[bar.bar] ?: error("Blast Furnace: no smithing_bars row for ${bar.bar}")
        }
        onOpLoc1(BELT) { depositAll() }
        for (ore in oreVarbits.keys) {
            onOpLocU(BELT, ore) { depositAll() }
        }
        onOpLoc1(DISPENSER) { collectBars() }
        onOpLocU(DISPENSER, BUCKET_WATER) { collectBars() }
    }

    private fun ProtectedAccess.depositAll() {
        var deposited = 0
        for ((ore, varbit) in oreVarbits) {
            val held = inv.count(ore)
            if (held == 0) continue
            val cap = if (ore == COAL) MAX_COAL else MAX_ORE
            val stored = player.vars[varbit]
            val amount = minOf(held, cap - stored)
            if (amount <= 0) {
                mes("The furnace can't hold any more of that.")
                continue
            }
            if (invDel(inv, ore, amount).success) {
                setBit(varbit, stored + amount)
                deposited += amount
            }
        }
        if (deposited == 0) {
            mes("You don't have any ore to put on the conveyor belt.")
            return
        }
        anim("seq.blast_furnace_human_conveyer_cycle")
        mes("You put the ore on the conveyor belt.")
        smelt()
    }

    private fun ProtectedAccess.smelt() {
        var made = 0
        for (bar in bars) {
            val levelReq = bar.row.statReq.first().t1
            if (player.smithingLvl < levelReq) continue
            while (true) {
                val barsHeld = player.vars[bar.barVarbit]
                if (barsHeld >= MAX_BARS) break
                if (stored(bar.primary) < 1) break
                if (bar.secondary != null && stored(bar.secondary) < bar.secondaryAmount) break
                // Iron bars need the iron ore not to be reserved for steel: steel goes first
                // (only when coal is present), so leftover iron becomes iron bars.
                take(bar.primary, 1)
                if (bar.secondary != null) take(bar.secondary, bar.secondaryAmount)
                setBit(bar.barVarbit, barsHeld + 1)
                val xp = SmithingSmeltXp.resolve(player, inv, bar.row, false, xpMods, regularFurnace = false)
                statAdvance("stat.smithing", xp)
                made++
            }
        }
        if (made > 0) {
            mes("The furnace smelts the ore into $made bar${if (made == 1) "" else "s"}.")
        }
    }

    private fun ProtectedAccess.collectBars() {
        val total = bars.sumOf { player.vars[it.barVarbit] }
        if (total == 0) {
            mes("There are no bars in the dispenser.")
            return
        }
        val hasGloves = ICE_GLOVES in player.worn
        if (!hasGloves && !inv.contains(BUCKET_WATER)) {
            mes("The bars are still molten! You need ice gloves or a bucket of water to cool them.")
            return
        }
        if (!hasGloves) {
            invDel(inv, BUCKET_WATER, 1)
            invAdd(inv, BUCKET_EMPTY, 1)
            mes("You pour water over the bars to cool them.")
        }
        var taken = 0
        for (bar in bars) {
            val count = player.vars[bar.barVarbit]
            if (count == 0) continue
            val space = inv.freeSpace()
            if (space == 0) break
            val amount = minOf(count, space)
            if (invAdd(inv, bar.bar, amount).success) {
                setBit(bar.barVarbit, count - amount)
                taken += amount
            }
        }
        if (taken == 0) {
            mes("You don't have enough inventory space.")
        } else {
            mes("You take $taken bar${if (taken == 1) "" else "s"} from the dispenser.")
        }
    }

    private fun ProtectedAccess.setBit(varbit: String, value: Int) {
        VarPlayerIntMapSetter.set(player, varbit, value)
    }

    private fun ProtectedAccess.stored(ore: String): Int = player.vars[oreVarbits.getValue(ore)]

    private fun ProtectedAccess.take(ore: String, amount: Int) {
        val varbit = oreVarbits.getValue(ore)
        setBit(varbit, player.vars[varbit] - amount)
    }

    private companion object {
        const val BELT = "loc.blast_furnace_conveyer_belt_clickable"
        const val DISPENSER = "loc.blast_furnace_dispenser"
        const val COAL = "obj.coal"
        const val ICE_GLOVES = "obj.ice_gloves"
        const val BUCKET_WATER = "obj.bucket_water"
        const val BUCKET_EMPTY = "obj.bucket_empty"
        const val MAX_ORE = 28
        const val MAX_COAL = 254
        const val MAX_BARS = 28
    }
}
