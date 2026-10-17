package org.rsmod.content.skills.fletching

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.script.onOpHeldU
import org.rsmod.api.script.onPlayerQueueWithArgs
import org.rsmod.content.skills.Material
import org.rsmod.content.skills.SkillMultiConfig
import org.rsmod.content.skills.SkillMultiEntry
import org.rsmod.content.skills.SkillingActionType
import org.rsmod.content.skills.openSkillMulti
import org.rsmod.game.inv.Inventory
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext

private const val QUEUE = "queue.fletching_make"
private const val STAT = "stat.fletching"
private const val FALLBACK_ANIM = "seq.human_fletching"

/**
 * Fletching: knife on logs, bow string on unstrung bows, feathers/tips on arrows, darts, bolts,
 * chisel on gems for bolt tips, hammer for crossbow limbs, crossbow string, javelins.
 *
 * Every use-item-on-item pair that appears in [FletchingRecipes] is registered here. Using one
 * item on the other opens the standard skillmulti make-X chatbox (same one Herblore/Crafting
 * use), then a weak queue repeats the action until the chosen amount is made, materials run
 * out, or the player does something else (weak queues are cleared by movement/interaction).
 */
class Fletching : PluginScript() {

    private val logger = InlineLogger()

    /** Recipes that survived name validation. */
    private lateinit var recipes: List<FletchRecipe>

    override fun ScriptContext.startup() {
        val (valid, invalid) = FletchingRecipes.all.partition { it.isResolvable() }
        recipes = valid.map { if (seqExists(it.anim)) it else it.copy(anim = FALLBACK_ANIM) }
        invalid.forEach { logger.warn { "Fletching: skipping recipe with unknown item name -> ${it.output}" } }
        logger.info { "Fletching: registered ${recipes.size} recipes (${invalid.size} skipped)." }

        // Group recipes by the unordered pair of items that triggers them.
        val byPair = linkedMapOf<Pair<String, String>, MutableList<FletchRecipe>>()
        for (r in recipes) {
            val a = r.tool ?: r.inputs[0].obj
            val b = if (r.tool != null) r.inputs[0].obj else r.inputs[1].obj
            val key = if (a <= b) a to b else b to a
            byPair.getOrPut(key) { mutableListOf() } += r
        }
        for ((pair, group) in byPair) {
            onOpHeldU(pair.first, pair.second) { openMenu(group) }
        }

        onPlayerQueueWithArgs<FletchTask>(QUEUE) { processTick(it.args) }
    }

    private suspend fun ProtectedAccess.openMenu(group: List<FletchRecipe>) {
        val level = statBase(STAT)
        val unlocked = group.filter { level >= it.level }
        if (unlocked.isEmpty()) {
            val lowest = group.minBy { it.level }
            mes("You need a Fletching level of at least ${lowest.level} to do that.")
            return
        }
        val makeable = unlocked.filter { it.actionsAvailable(inv) > 0 }
        if (makeable.isEmpty()) {
            mes("You don't have the materials needed to do that.")
            return
        }
        // A single set-recipe with no choice to make (e.g. feathers on shafts) starts directly,
        // like OSRS's "Make sets" prompt does when only one option exists.
        val action = when (makeable.first().category) {
            FletchRecipe.Category.CUT -> SkillingActionType.CUT
            FletchRecipe.Category.STRING, FletchRecipe.Category.CROSSBOW -> SkillingActionType.STRING
            FletchRecipe.Category.GEM_TIP -> SkillingActionType.CUT
            else -> SkillingActionType.MAKE_SETS
        }
        openSkillMulti(
            SkillMultiConfig(
                actionType = action,
                verb = "make",
                entries = makeable.map { r ->
                    SkillMultiEntry(r.output, r.inputs.map { Material(it.obj, it.count) })
                },
                maxCountProvider = { inventory, entry ->
                    makeable.firstOrNull { it.output == entry.internal }?.actionsAvailable(inventory) ?: 0
                },
            ),
        ) { selection ->
            val recipe = makeable.firstOrNull { it.output == selection.entry.internal } ?: return@openSkillMulti
            weakQueue(QUEUE, 1, FletchTask(recipe, selection.amount, 0))
        }
    }

    private fun ProtectedAccess.processTick(task: FletchTask) {
        val r = task.recipe
        if (statBase(STAT) < r.level) {
            mes("You need a Fletching level of at least ${r.level} to do that.")
            resetAnim()
            return
        }
        if (r.tool != null && !hasTool(r.tool)) {
            mes("You need a ${toolName(r.tool)} to do that.")
            resetAnim()
            return
        }
        val produce = r.itemsThisAction(inv)
        if (produce <= 0) {
            resetAnim()
            return
        }

        // Consume inputs.
        val removed = mutableListOf<Pair<String, Int>>()
        for (input in r.inputs) {
            val amt = if (r.perItem) produce * input.count else input.count
            if (invDel(inv, input.obj, amt).failure) {
                removed.forEach { (obj, n) -> invAdd(inv, obj, n) }
                resetAnim()
                return
            }
            removed += input.obj to amt
        }
        val outAmt = if (r.perItem) produce else r.outputCount
        if (invAdd(inv, r.output, outAmt).failure) {
            removed.forEach { (obj, n) -> invAdd(inv, obj, n) }
            mes("You don't have enough inventory space.")
            resetAnim()
            return
        }

        anim(r.anim)
        statAdvance(STAT, if (r.perItem) r.xp * produce else r.xp)
        mes(r.message)

        val done = task.completed + 1
        if (done < task.amount && r.itemsThisAction(inv) > 0) {
            weakQueue(QUEUE, r.ticks, task.copy(completed = done))
        }
    }

    private fun ProtectedAccess.hasTool(tool: String): Boolean =
        inv.contains(tool) ||
            (tool == "obj.knife" && FLETCHING_KNIFE_EXISTS && inv.contains("obj.fletching_knife"))

    private fun toolName(tool: String) = tool.removePrefix("obj.")

    private data class FletchTask(val recipe: FletchRecipe, val amount: Int, val completed: Int)
}

/** How many items a single action produces right now (0 = can't act). */
private fun FletchRecipe.itemsThisAction(inv: Inventory): Int {
    if (!perItem) return if (inputs.all { inv.count(it.obj) >= it.count }) outputCount else 0
    val avail = inputs.minOf { inv.count(it.obj) / it.count }
    return minOf(outputCount, avail)
}

/** How many full/partial actions the inventory supports (what the make-X menu shows). */
private fun FletchRecipe.actionsAvailable(inv: Inventory): Int {
    if (!perItem) return inputs.minOf { inv.count(it.obj) / it.count }
    val avail = inputs.minOf { inv.count(it.obj) / it.count }
    return (avail + outputCount - 1) / outputCount
}

private fun nameExists(name: String, type: RSCMType): Boolean =
    runCatching { name.asRSCM(type) }.isSuccess

private val FLETCHING_KNIFE_EXISTS: Boolean by lazy { nameExists("obj.fletching_knife", RSCMType.OBJ) }

private fun seqExists(name: String) = nameExists(name, RSCMType.SEQ)

private fun FletchRecipe.isResolvable(): Boolean =
    nameExists(output, RSCMType.OBJ) &&
        inputs.all { nameExists(it.obj, RSCMType.OBJ) } &&
        (tool == null || nameExists(tool, RSCMType.OBJ))
