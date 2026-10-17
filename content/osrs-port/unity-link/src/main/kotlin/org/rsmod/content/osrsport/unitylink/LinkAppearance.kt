package org.rsmod.content.osrsport.unitylink

import dev.openrune.ServerCacheManager
import org.rsmod.game.entity.Player

/**
 * Resolves a player's visible body for the Unity client, the same way the OSRS player appearance
 * block does: 12 equipment slots (wearpos 0 head .. 11 jaw), each either a worn item, an identkit
 * body part, or nothing. Worn items replace the body part in their own slot and hide the slots in
 * their wearpos2/wearpos3 (a platebody hides arms, a full helm hides hair and jaw).
 *
 * Line: `PLOOK key bodyType colHair colTorso colLegs colFeet colSkin s0 .. s11`
 * key = `me` (the link player) or another player's slot id (as in `PLAYERS`); each slot value is
 * -1 (nothing), 256 + identkit id, or 512 + item id (the OSRS client's own encoding).
 */
object LinkAppearance {
    const val SLOT_COUNT = 12

    /** Identkit slot (hair, jaw, torso, arms, hands, legs, feet) -> the wearpos it is drawn in. */
    private val KIT_WEARPOS = intArrayOf(8, 11, 4, 6, 9, 7, 10)

    fun slots(player: Player): IntArray {
        val slots = IntArray(SLOT_COUNT) { -1 }
        val hidden = BooleanArray(SLOT_COUNT)
        val worn = player.worn
        for (wearpos in 0 until minOf(SLOT_COUNT, worn.size)) {
            val obj = worn[wearpos] ?: continue
            slots[wearpos] = 0x200 + obj.id
            val type = ServerCacheManager.getItem(obj.id) ?: continue
            if (type.wearpos2 in 0 until SLOT_COUNT) hidden[type.wearpos2] = true
            if (type.wearpos3 in 0 until SLOT_COUNT) hidden[type.wearpos3] = true
        }
        val kits = player.appearance.identKitSnapshot()
        for (i in KIT_WEARPOS.indices) {
            val wearpos = KIT_WEARPOS[i]
            if (slots[wearpos] != -1 || hidden[wearpos]) continue
            val kit = kits.getOrNull(i)?.toInt()?.and(0xFFFF) ?: continue
            if (kit != 0xFFFF) slots[wearpos] = 0x100 + kit
        }
        return slots
    }

    fun line(key: String, player: Player): String {
        val a = player.appearance
        val cols = a.coloursSnapshot().joinToString(" ") { (it.toInt() and 0xFF).toString() }
        return "PLOOK $key ${a.bodyType} $cols ${slots(player).joinToString(" ")}"
    }
}
