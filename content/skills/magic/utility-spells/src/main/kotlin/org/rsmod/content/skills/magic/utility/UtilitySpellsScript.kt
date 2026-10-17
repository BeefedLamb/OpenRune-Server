package org.rsmod.content.skills.magic.utility

import com.github.michaelbull.logging.InlineLogger
import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import dev.openrune.types.ItemServerType
import jakarta.inject.Inject
import org.rsmod.api.combat.commons.magic.MagicSpell
import org.rsmod.api.combat.manager.MagicRuneManager
import org.rsmod.api.combat.manager.MagicRuneManager.Companion.isFailure
import org.rsmod.api.player.output.UpdateInventory.resendSlot
import org.rsmod.api.player.protect.ProtectedAccess
import org.rsmod.api.player.protect.ProtectedAccessLauncher
import org.rsmod.api.player.protect.clearPendingAction
import org.rsmod.api.player.ui.IfOverlayButtonT
import org.rsmod.api.script.onEvent
import org.rsmod.api.script.onIfOverlayButton
import org.rsmod.api.spells.MagicSpellRegistry
import org.rsmod.api.stats.xpmod.XpModifiers
import org.rsmod.api.table.cooking.CookingFoodsRow
import org.rsmod.api.table.smithing.SmithingBarsRow
import org.rsmod.content.skills.smithing.smelting.SmithingSmeltXp
import org.rsmod.events.EventBus
import org.rsmod.game.entity.Player
import org.rsmod.game.inv.isType
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext

/**
 * Non-combat utility spells that were missing from the magic module:
 * - Standard: Lvl-1..7 Enchant (jewellery), Superheat Item, Bones to Bananas, Bones to Peaches.
 * - Lunar: Plank Make, Superglass Make, Spin Flax, String Jewellery, Humidify, Bake Pie, Hunter Kit.
 *
 * Level requirement, Magic xp and rune costs come from the cache spell objs (MagicSpellRegistry),
 * so they're 1:1 with the game data; this script only implements each spell's effect.
 */
class UtilitySpellsScript
@Inject
constructor(
    private val eventBus: EventBus,
    private val protectedAccess: ProtectedAccessLauncher,
    private val spells: MagicSpellRegistry,
    private val runes: MagicRuneManager,
    private val xpMods: XpModifiers,
) : PluginScript() {
    private val smeltableBars by lazy {
        SmithingBarsRow.all().filter { it.output.internalName != "obj.lovakite_bar" }
    }

    private val pies by lazy {
        CookingFoodsRow.all().filter {
            it.input.internalName.startsWith("obj.uncooked_") &&
                it.input.internalName.endsWith("_pie")
        }
    }

    override fun ScriptContext.startup() {
        // --- Item-targeted spells (cast on an inventory item) ---
        for (enchant in UtilitySpellData.Enchant.entries) {
            // The enchant spell objs carry stale gamevals names on this rev, so match by the
            // cache spell_name param ("Lvl-1 Enchant" .. "Lvl-7 Enchant") instead.
            val spellType = ServerCacheManager.getItem(enchant.spellObj.asRSCM(RSCMType.OBJ))
            val spell =
                spellType?.let { spells.getOrLoadObjSpell(it) }
                    ?: spells.allSpells().firstOrNull { it.name.equals(enchant.spellName, ignoreCase = true) }
            if (spell == null) {
                logger.warn { "Enchant spell not found: ${enchant.spellName}" }
                continue
            }
            logger.info {
                "Enchant: ${spell.name} lvl=${spell.levelReq} xp=${spell.castXp} " +
                    "button=${spell.component.packed} runes=" +
                    spell.objReqs.joinToString { "${it.count}x${it.obj.internalName}" }
            }
            onInvTarget(spell) { slot, obj, s -> castEnchant(slot, obj, s, enchant) }
        }
        onInvTarget(SUPERHEAT) { slot, obj, spell -> castSuperheat(slot, obj, spell) }
        onInvTarget(PLANK_MAKE) { slot, obj, spell -> castPlankMake(slot, obj, spell) }

        // --- Self-cast spells ---
        onSelfCast(BONES_TO_BANANAS) { castBonesTo(it, "obj.banana") }
        onSelfCast(BONES_TO_PEACHES) { castBonesTo(it, "obj.peach") }
        onSelfCast(HUMIDIFY) { castHumidify(it) }
        onSelfCast(HUNTER_KIT) { castHunterKit(it) }
        onSelfCast(SUPERGLASS) { castSuperglass(it) }
        onSelfCast(SPIN_FLAX) { castSpinFlax(it) }
        onSelfCast(STRING_JEWELLERY) { castStringJewellery(it) }
        onSelfCast(BAKE_PIE) { castBakePie(it) }

        logger.info {
            "Utility spells: " +
                spells.allSpells()
                    .filter { it.name.contains("Enchant", true) || it.name.contains("Superheat", true) ||
                        it.name.contains("Plank", true) || it.name.contains("Bones to", true) }
                    .joinToString { "${it.name}(lvl ${it.levelReq}, ${it.castXp}xp)" }
        }
    }

    private fun resolveSpell(spellObj: String): MagicSpell? {
        val type = ServerCacheManager.getItem(spellObj.asRSCM(RSCMType.OBJ)) ?: return null
        return spells.getObjSpell(type)
    }

    private fun ScriptContext.onSelfCast(
        spellObj: String,
        action: suspend ProtectedAccess.(MagicSpell) -> Unit,
    ) {
        val spell = resolveSpell(spellObj)
        if (spell == null) {
            logger.warn { "Utility spell not found in spellbook enums, skipping: $spellObj" }
            return
        }
        onIfOverlayButton(spell.component) { action(spell) }
    }

    private fun ScriptContext.onInvTarget(
        spellObj: String,
        action: ProtectedAccess.(Int, ItemServerType, MagicSpell) -> Unit,
    ) {
        val spell = resolveSpell(spellObj)
        if (spell == null) {
            logger.warn { "Utility spell not found in spellbook enums, skipping: $spellObj" }
            return
        }
        onInvTarget(spell, action)
    }

    private fun ScriptContext.onInvTarget(
        spell: MagicSpell,
        action: ProtectedAccess.(Int, ItemServerType, MagicSpell) -> Unit,
    ) {
        val key = EventBus.composeLongKey(spell.component.packed, InventoryItemsComponent)
        onEvent<IfOverlayButtonT>(key) {
            val target = targetObj ?: return@onEvent resendSlot(player.inv, 0)
            player.launchTargeted(targetSlot, target, spell, action)
        }
    }

    private fun Player.launchTargeted(
        slot: Int,
        target: ItemServerType,
        spell: MagicSpell,
        action: ProtectedAccess.(Int, ItemServerType, MagicSpell) -> Unit,
    ) {
        if (isDelayed || isAccessProtected) {
            resendSlot(inv, 0)
            return
        }
        clearPendingAction(eventBus)
        resetFaceEntity()
        protectedAccess.launch(this) {
            if (actionDelay > mapClock) {
                return@launch
            }
            if (slot !in inv.indices || !inv[slot].isType(target)) {
                resendSlot(inv, 0)
                return@launch
            }
            action(slot, target, spell)
        }
    }

    private fun ProtectedAccess.tryFx(seq: String?, spot: String?, height: Int = 92) {
        runCatching { if (seq != null) anim(seq) }
        runCatching { if (spot != null) spotanim(spot, height = height) }
    }

    // ------------------------------------------------------------------ Enchanting

    private fun ProtectedAccess.castEnchant(
        slot: Int,
        target: ItemServerType,
        spell: MagicSpell,
        enchant: UtilitySpellData.Enchant,
    ) {
        val output = enchant.transforms[target.internalName]
        if (output == null) {
            mes("This spell cannot enchant that item.")
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        if (invDel(inv, target.internalName, 1, slot = slot).failure) {
            return
        }
        invAdd(inv, output, 1)
        actionDelay = mapClock + ENCHANT_DELAY
        val isRing = target.internalName.endsWith("_ring")
        val (seq, spot) =
            when {
                isRing -> "seq.enchant_ring" to "spotanim.enchant_ring"
                enchant.ordinal <= 2 -> "seq.enchant_amulet_lvl${enchant.ordinal + 1}" to
                    "spotanim.enchant_amulet_lvl${enchant.ordinal + 1}"
                enchant.ordinal <= 4 -> "seq.enchant_amulet2_lvl${enchant.ordinal + 1}" to
                    "spotanim.enchant_amulet2_lvl${enchant.ordinal + 1}"
                else -> "seq.enchant_amulet2_lvl6" to "spotanim.enchant_amulet2_lvl6"
            }
        tryFx(seq, spot)
        statAdvance("stat.magic", spell.castXp)
    }

    // ------------------------------------------------------------------ Superheat Item

    private fun ProtectedAccess.castSuperheat(slot: Int, target: ItemServerType, spell: MagicSpell) {
        val candidates = smeltableBars.filter { it.input.first().internalName == target.internalName }
        if (candidates.isEmpty()) {
            mes("You need to cast superheat item on ore.")
            return
        }
        val smithing = statBase("stat.smithing")
        // Prefer the bar that uses a secondary ore (e.g. iron + coal -> steel) when possible.
        val bar =
            candidates
                .sortedByDescending { it.input.size }
                .firstOrNull { bar ->
                    smithing >= bar.levelReq() &&
                        bar.input.indices.all { i ->
                            inv.count(bar.input[i].internalName) >= bar.inputAmount.getOrElse(i) { 1 }
                        }
                }
        if (bar == null) {
            val simplest = candidates.minBy { it.input.size }
            if (smithing < simplest.levelReq()) {
                mes("You need a Smithing level of at least ${simplest.levelReq()} to smelt ${target.name.lowercase()}.")
            } else {
                val secondary = simplest.input.getOrNull(1)?.name?.lowercase() ?: "materials"
                mes(
                    "You need ${simplest.inputAmount.getOrElse(1) { 1 }} ${secondary} to smelt " +
                        "${target.name.lowercase()}."
                )
            }
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        for (i in bar.input.indices) {
            val amount = bar.inputAmount.getOrElse(i) { 1 }
            if (amount <= 0) continue
            if (i == 0) {
                invDel(inv, bar.input[i].internalName, amount, slot = slot)
            } else {
                invDel(inv, bar.input[i].internalName, amount)
            }
        }
        invAdd(inv, bar.output.internalName, 1)
        actionDelay = mapClock + SUPERHEAT_DELAY
        tryFx("seq.human_castsuperheatitem", "spotanim.superheatitem_casting")
        statAdvance("stat.magic", spell.castXp)
        val smithXp = SmithingSmeltXp.resolve(player, inv, bar, isSuperHeat = true, xpMods = xpMods, regularFurnace = false)
        statAdvance("stat.smithing", smithXp)
    }

    private fun SmithingBarsRow.levelReq(): Int = statReq.firstOrNull()?.t1 ?: 1

    // ------------------------------------------------------------------ Plank Make (Lunar)

    private fun ProtectedAccess.castPlankMake(slot: Int, target: ItemServerType, spell: MagicSpell) {
        val plank = UtilitySpellData.planks.firstOrNull { it.logs == target.internalName }
        if (plank == null) {
            mes("You can only cast Plank Make on logs, oak logs, teak logs or mahogany logs.")
            return
        }
        if (inv.count(COINS) < plank.coins) {
            mes("You need ${plank.coins} coins to make a plank from these logs.")
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        if (invDel(inv, COINS, plank.coins).failure) {
            return
        }
        invDel(inv, plank.logs, 1, slot = slot)
        invAdd(inv, plank.plank, 1)
        actionDelay = mapClock + PLANK_MAKE_DELAY
        tryFx("seq.dream_player_humidify_spell", "spotanim.dream_plank_make_spell_spotanim")
        statAdvance("stat.magic", spell.castXp)
    }

    // ------------------------------------------------------------------ Self-cast spells

    private fun ProtectedAccess.castBonesTo(spell: MagicSpell, fruit: String) {
        val bones = inv.count(BONES)
        if (bones == 0) {
            mes("You aren't holding any bones!")
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        val count = inv.count(BONES)
        if (count == 0 || invDel(inv, BONES, count).failure) return
        invAdd(inv, fruit, count)
        tryFx("seq.human_castbones", "spotanim.bones_to_bananas")
        statAdvance("stat.magic", spell.castXp)
    }

    private fun ProtectedAccess.castHumidify(spell: MagicSpell) {
        val fillable = UtilitySpellData.humidify.filter { (empty, _) -> inv.count(empty) > 0 }
        if (fillable.isEmpty()) {
            mes("You have nothing in your inventory that this spell can humidify.")
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        for ((empty, full) in fillable) {
            val count = inv.count(empty)
            if (count > 0 && invDel(inv, empty, count).success) {
                invAdd(inv, full, count)
            }
        }
        tryFx("seq.dream_player_humidify_spell", "spotanim.dream_humidify_spell_spotanim")
        statAdvance("stat.magic", spell.castXp)
    }

    private fun ProtectedAccess.castHunterKit(spell: MagicSpell) {
        if (inv.freeSpace() < 1) {
            mes("You don't have enough inventory space.")
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        invAdd(inv, HUNTER_KIT_OBJ, 1)
        tryFx("seq.dream_player_humidify_spell", null)
        statAdvance("stat.magic", spell.castXp)
    }

    private fun ProtectedAccess.castSuperglass(spell: MagicSpell) {
        val sand = inv.count(SAND)
        val alkali = UtilitySpellData.superglassAlkalis.sumOf { inv.count(it) }
        if (sand == 0 || alkali == 0) {
            mes("You need buckets of sand and soda ash or seaweed to cast this spell.")
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        var made = 0
        for (alk in UtilitySpellData.superglassAlkalis) {
            while (inv.count(SAND) > 0 && inv.count(alk) > 0) {
                if (invDel(inv, alk, 1).failure) break
                if (invDel(inv, SAND, 1).failure) {
                    invAdd(inv, alk, 1)
                    break
                }
                // The empty bucket is consumed by the spell in OSRS (no empty buckets returned).
                invAdd(inv, MOLTEN_GLASS, 1)
                made++
            }
        }
        tryFx("seq.dream_player_humidify_spell", null)
        statAdvance("stat.magic", spell.castXp)
        if (made > 0) statAdvance("stat.crafting", UtilitySpellData.SUPERGLASS_CRAFTING_XP * made)
    }

    private fun ProtectedAccess.castSpinFlax(spell: MagicSpell) {
        if (inv.count(FLAX) == 0) {
            mes("You don't have any flax to spin.")
            return
        }
        if (runes.attemptCast(player, spell).isFailure()) {
            return
        }
        val count = minOf(inv.count(FLAX), UtilitySpellData.SPIN_FLAX_PER_CAST)
        if (invDel(inv, FLAX, count).failure) return
        invAdd(inv, BOW_STRING, count)
        tryFx("seq.dream_player_humidify_spell", null)
        statAdvance("stat.magic", spell.castXp)
        statAdvance("stat.crafting", UtilitySpellData.SPIN_FLAX_CRAFTING_XP * count)
    }

    /** Auto-repeats: each amulet strung is its own cast (runes + Magic xp), like OSRS. */
    private suspend fun ProtectedAccess.castStringJewellery(spell: MagicSpell) {
        if (UtilitySpellData.stringJewellery.none { (u, _) -> inv.count(u) > 0 }) {
            mes("You don't have any jewellery that this spell can string.")
            return
        }
        while (true) {
            val (unstrung, strung) =
                UtilitySpellData.stringJewellery.firstOrNull { (u, _) -> inv.count(u) > 0 } ?: break
            if (runes.attemptCast(player, spell).isFailure()) break
            if (invDel(inv, unstrung, 1).failure) break
            invAdd(inv, strung, 1)
            tryFx("seq.dream_player_humidify_spell", "spotanim.quest_lunar_spellbook_string_jewelery_spot_anim")
            statAdvance("stat.magic", spell.castXp)
            statAdvance("stat.crafting", UtilitySpellData.STRING_CRAFTING_XP)
            delay(AUTO_REPEAT_TICKS)
        }
    }

    /** Auto-repeats: each pie baked is its own cast; never burns; full Cooking xp. */
    private suspend fun ProtectedAccess.castBakePie(spell: MagicSpell) {
        val cooking = statBase("stat.cooking")
        val bakeable = { pies.filter { inv.count(it.input.internalName) > 0 } }
        if (bakeable().isEmpty()) {
            mes("You don't have any uncooked pies to bake.")
            return
        }
        while (true) {
            val pie = bakeable().firstOrNull { row -> row.statReq.all { cooking >= it.t1 } }
            if (pie == null) {
                if (bakeable().isNotEmpty()) mes("You need a higher Cooking level to bake that pie.")
                break
            }
            if (runes.attemptCast(player, spell).isFailure()) break
            if (invDel(inv, pie.input.internalName, 1).failure) break
            invAdd(inv, pie.output.internalName, 1)
            tryFx("seq.dream_player_humidify_spell", "spotanim.quest_lunar_spellbook_bake_pie_spot_anim")
            statAdvance("stat.magic", spell.castXp)
            statAdvance("stat.cooking", pie.xp.toDouble() * xpMods.get(player, "stat.cooking"))
            delay(AUTO_REPEAT_TICKS)
        }
    }

    private companion object {
        val logger = InlineLogger()
        val InventoryItemsComponent = "component.inventory:items".asRSCM(RSCMType.COMPONENT)

        const val SUPERHEAT = "obj.43_superheat_item"
        const val BONES_TO_BANANAS = "obj.15_bones_bananas"
        const val BONES_TO_PEACHES = "obj.60_bones_peaches"
        const val PLANK_MAKE = "obj.86_plank_make"
        const val HUMIDIFY = "obj.68_humidify"
        const val HUNTER_KIT = "obj.71_hunter_kit"
        const val SUPERGLASS = "obj.77_superglass"
        const val SPIN_FLAX = "obj.76_spin_flax"
        const val STRING_JEWELLERY = "obj.80_string_jewelry"
        const val BAKE_PIE = "obj.65_bake_pie"

        const val COINS = "obj.coins"
        const val BONES = "obj.bones"
        const val SAND = "obj.bucket_sand"
        const val MOLTEN_GLASS = "obj.molten_glass"
        const val FLAX = "obj.flax"
        const val BOW_STRING = "obj.bow_string"
        const val HUNTER_KIT_OBJ = "obj.dream_hunter_box"

        const val ENCHANT_DELAY = 3
        const val SUPERHEAT_DELAY = 3
        const val PLANK_MAKE_DELAY = 3
        const val AUTO_REPEAT_TICKS = 3
    }
}
