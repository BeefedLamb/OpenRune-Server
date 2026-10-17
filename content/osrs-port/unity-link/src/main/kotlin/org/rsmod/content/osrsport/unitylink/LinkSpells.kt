package org.rsmod.content.osrsport.unitylink

import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import dev.openrune.types.aconverted.interf.IfButtonOp
import org.rsmod.annotations.InternalApi
import org.rsmod.api.combat.commons.magic.MagicSpell
import org.rsmod.api.combat.commons.magic.MagicSpellType
import org.rsmod.api.combat.commons.magic.Spellbook
import org.rsmod.api.combat.manager.MagicRuneManager
import org.rsmod.api.player.output.mes
import org.rsmod.api.player.protect.ProtectedAccessLauncher
import org.rsmod.api.player.protect.clearPendingAction
import org.rsmod.api.player.stat.statBase
import org.rsmod.api.player.ui.IfOverlayButton
import org.rsmod.api.player.ui.IfOverlayButtonT
import org.rsmod.api.player.vars.enumVarBit
import org.rsmod.api.spells.MagicSpellRegistry
import org.rsmod.api.spells.runes.MagicRunes
import org.rsmod.events.EventBus
import org.rsmod.game.entity.Player
import org.rsmod.game.ui.Component

/**
 * Spellbook bridge for the Unity link, built like [LinkShop]/[LinkBank]. The spell list comes from
 * the server's own [MagicSpellRegistry] (cache spell objs: name, level, runes, xp) filtered to the
 * player's current spellbook (`varbit.spellbook`, so `::spellbook` and the altars switch it), and
 * every cast is replayed as the exact interface event a real client produces, so the existing spell
 * scripts (UtilitySpellsScript, AlchemyScript, SpellTeleportScript, Arceuus scripts, ...) do all the
 * validation, rune removal, xp and effects:
 * - self-cast spells: `IfOverlayButton(spell.component)` (a spellbook button click);
 * - item-target spells: `IfOverlayButtonT(spell.component -> inventory:items, slot)` (spell used on
 *   a backpack item).
 *
 * Server -> client: `SPELLS book;objId|name|level|type|target|levelOk|runesOk|runes;...` re-sent on
 * change. book = standard|ancients|lunars|arceuus. type = combat|utility|teleport|other. target:
 * `s` self/click, `i` backpack item, `n` npc (combat spell: not castable from the panel yet),
 * `x` no handler on this server. runes = `objId:count,objId:count` (`-` when none). The spell
 * objId doubles as the icon id (spell objs are in the exported item icon set).
 * Client -> server: `CAST objId` (self-cast) or `CAST objId slot` (item-target, backpack slot).
 */
class LinkSpells(
    private val eventBus: EventBus,
    private val protectedAccess: ProtectedAccessLauncher,
    private val registry: MagicSpellRegistry,
    private val runes: MagicRuneManager,
) {
    private val inventoryItems by lazy { Component("component.inventory:items".asRSCM(RSCMType.COMPONENT)) }

    /** Enchant spells live in the jewellery sub-menu, not in the spellbook enums. */
    private val extraSpells: List<MagicSpell> by lazy {
        ENCHANT_OBJS.mapNotNull { name ->
            runCatching { ServerCacheManager.getItem(name.asRSCM(RSCMType.OBJ)) }.getOrNull()
                ?.let { registry.getOrLoadObjSpell(it) }
        }
    }

    private fun spellsFor(book: Spellbook): List<MagicSpell> =
        (registry.allSpells() + extraSpells)
            .filter { bookOf(it) == book }
            // "Jewellery/Crossbow Bolt Enchantments" are sub-menu openers; the real enchants are listed.
            .filterNot { it.name.endsWith("Enchantments", ignoreCase = true) }
            .distinctBy { it.obj.id }
            .sortedWith(compareBy({ it.levelReq }, { it.name }))

    /** Re-sends the spell list every few ticks when anything (book, levels, runes) changed. */
    fun sync(connection: LinkConnection, player: Player, tick: Int) {
        if (tick % SYNC_INTERVAL != 0 && connection.lastSpellsLine.isNotEmpty()) return
        val line = runCatching { spellsLine(player) }.getOrNull() ?: return
        if (line != connection.lastSpellsLine) {
            connection.lastSpellsLine = line
            connection.send(line)
        }
    }

    private fun spellsLine(player: Player): String {
        val book = player.spellbook
        val magic = player.statBase("stat.magic")
        val sb = StringBuilder("SPELLS ").append(book.name.lowercase()).append(';')
        for (spell in spellsFor(book)) {
            val name = spell.name.replace('|', '/').replace(';', ',')
            val runesOk = hasRunesSilently(player, spell)
            val reqs =
                if (spell.objReqs.isEmpty()) "-"
                else spell.objReqs.joinToString(",") { "${it.obj.id}:${it.count}" }
            sb.append(spell.obj.id).append('|').append(name).append('|').append(spell.levelReq).append('|')
                .append(spell.type.name.lowercase()).append('|').append(targetKind(spell)).append('|')
                .append(if (magic >= spell.levelReq) 1 else 0).append('|').append(if (runesOk) 1 else 0)
                .append('|').append(reqs).append(';')
        }
        return sb.toString()
    }

    /** Same check as [MagicRuneManager.hasRunes] (staffs, rune pouch, ...) without its chat message. */
    private fun hasRunesSilently(player: Player, spell: MagicSpell): Boolean =
        runCatching { runes.validateSpell(player, spell).none { it is MagicRunes.Validation.Invalid } }
            .getOrDefault(false)

    private fun targetKind(spell: MagicSpell): Char =
        when {
            spell.type == MagicSpellType.Combat -> 'n'
            ITEM_TARGET.containsMatchIn(spell.name) -> 'i'
            eventBus.contains(IfOverlayButton::class.java, spell.component.packed.toLong()) -> 's'
            else -> 'x'
        }

    private fun resolve(player: Player, objId: Int): MagicSpell? {
        val spell =
            registry.allSpells().firstOrNull { it.obj.id == objId }
                ?: extraSpells.firstOrNull { it.obj.id == objId }
                ?: return null
        return spell.takeIf { bookOf(it) == player.spellbook }
    }

    /** Sub-menu spells (enchants) may carry no spellbook param; they belong to the standard book. */
    private fun bookOf(spell: MagicSpell): Spellbook? =
        spell.spellbook ?: Spellbook.Standard.takeIf { extraSpells.any { it.obj.id == spell.obj.id } }

    @OptIn(InternalApi::class)
    fun cast(connection: LinkConnection, player: Player, objId: Int, slot: Int?) {
        val spell = resolve(player, objId)
        if (spell == null) {
            player.mes("You can't cast that spell on your current spellbook.")
            connection.lastSpellsLine = ""
            return
        }
        if (player.isDelayed || player.isAccessProtected || player.isModalButtonProtected) return
        when (targetKind(spell)) {
            'i' -> {
                if (slot == null || slot !in player.inv.indices) {
                    player.mes("Choose an item to cast ${spell.name} on.")
                    return
                }
                val obj = player.inv[slot] ?: return
                val target = ServerCacheManager.getItem(obj.id) ?: return
                eventBus.publish(
                    IfOverlayButtonT(
                        player = player,
                        selectedSlot = -1,
                        selectedObj = null,
                        targetSlot = slot,
                        targetObj = target,
                        selectedComponent = Component(spell.component.packed),
                        targetComponent = inventoryItems,
                    )
                )
            }
            's' -> {
                player.clearPendingAction(eventBus)
                val event = IfOverlayButton(spell.component, -1, null, IfButtonOp.Op1)
                protectedAccess.launchLenient(player) { eventBus.publish(this, event) }
            }
            'n' -> player.mes("Select a target and attack it to cast combat spells (autocast).")
            else -> player.mes("${spell.name} isn't available yet.")
        }
    }

    private companion object {
        const val SYNC_INTERVAL = 3
        val ITEM_TARGET = Regex("Enchant|Superheat|Alchemy|Plank Make|Reanimation", RegexOption.IGNORE_CASE)
        val ENCHANT_OBJS =
            listOf(
                "obj.07_enchant_amulet_lvl1",
                "obj.27_enchant_amulet_lvl2",
                "obj.49_enchant_amulet_lvl3",
                "obj.57_enchant_amulet_lvl4",
                "obj.68_enchant_amulet_lvl5",
                "obj.87_enchant_amulet_lvl6",
                "obj.93_enchant_amulet_lvl7",
            )
    }
}

private val Player.spellbook by enumVarBit<Spellbook>("varbit.spellbook")
