package org.rsmod.content.osrsport.bots

import kotlin.random.Random
import org.rsmod.content.osrsport.unitylink.CharacterDesign
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.player.Appearance

/**
 * Picks a body for a bot and keeps it. The seed is the bot's login name, which does not change
 * across a restart. The result is saved on the account, and a later login restores that save.
 */
object BotLook {
    fun ensure(player: Player, name: String) {
        if (CharacterDesign.restore(player)) {
            return
        }
        val look = CharacterDesign.sanitise(player, roll(name)) ?: return
        CharacterDesign.apply(player, look)
        CharacterDesign.save(player, look)
    }

    private fun roll(name: String): List<Int> {
        val rng = Random(name.lowercase().hashCode().toLong())
        val body = if (rng.nextBoolean()) Appearance.BODY_TYPE_A else Appearance.BODY_TYPE_B
        val look = ArrayList<Int>(CharacterDesign.FIELD_COUNT)
        look += body
        for (slot in 0 until CharacterDesign.SLOT_COUNT) {
            val legal = CharacterDesign.kitsFor(slot, body)
            look += if (legal.isEmpty()) -1 else legal[rng.nextInt(legal.size)]
        }
        for (count in CharacterDesign.COLOUR_COUNTS) {
            look += rng.nextInt(count)
        }
        return look
    }
}
