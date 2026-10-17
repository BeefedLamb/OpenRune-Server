package org.rsmod.content.osrsport.unitylink

import org.rsmod.api.attr.AttributeKey
import org.rsmod.api.table.FacialHairStylesRow
import org.rsmod.api.table.HairStylesRow
import org.rsmod.api.table.HandStylesRow
import org.rsmod.api.table.LeggingStylesRow
import org.rsmod.api.table.ShoeStylesRow
import org.rsmod.api.table.SleeveStylesRow
import org.rsmod.api.table.TorsoStylesRow
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.player.Appearance

/**
 * OSRS "Character Design" for the Unity link: validates a client-chosen look, applies it to the
 * player's [Appearance] and stores it in a persistent attribute (the engine itself does not save
 * appearance), so it is restored on every later login.
 *
 * Stored value: `[bodyType, hair, jaw, torso, arms, hands, legs, feet, colHair, colTorso,
 * colLegs, colFeet, colSkin]`.
 */
object CharacterDesign {
    val SAVED_LOOK: AttributeKey<List<Int>> = AttributeKey(persistenceKey = "osrsport_appearance")

    const val SLOT_COUNT = 7
    const val FIELD_COUNT = 1 + SLOT_COUNT + 5

    /** Exclusive upper bound for each colour slot (hair, torso, legs, feet, skin). */
    val COLOUR_COUNTS = intArrayOf(25, 29, 29, 6, 14)

    val SLOT_NAMES = listOf("hair", "jaw", "torso", "arms", "hands", "legs", "feet")

    /** Legal identkit ids per slot, as (bodyTypeA, bodyTypeB) pairs from the cache style tables. */
    val KIT_STYLES: List<List<Pair<Int, Int>>> by lazy {
        listOf(
            HairStylesRow.all().map { it.playerKitIdTypeA to it.playerKitIdTypeB },
            FacialHairStylesRow.all().map { it.playerKitIdTypeA to it.playerKitIdTypeB },
            TorsoStylesRow.all().map { it.playerKitIdTypeA to it.playerKitIdTypeB },
            SleeveStylesRow.all().map { it.playerKitIdTypeA to it.playerKitIdTypeB },
            HandStylesRow.all().map { it.playerKitIdTypeA to it.playerKitIdTypeB },
            LeggingStylesRow.all().map { it.playerKitIdTypeA to it.playerKitIdTypeB },
            ShoeStylesRow.all().map { it.playerKitIdTypeA to it.playerKitIdTypeB },
        )
    }

    fun kitsFor(slot: Int, bodyType: Int): List<Int> =
        KIT_STYLES[slot]
            .map { if (bodyType == Appearance.BODY_TYPE_B) it.second else it.first }
            .filter { it in 0..65535 }
            .distinct()

    fun hasDesigned(player: Player): Boolean = player.attr[SAVED_LOOK] != null

    /**
     * Validates [values] (FIELD_COUNT ints) and returns a sanitised copy: bad body type -> A, kit
     * ids not legal for the slot/body type -> that slot's first legal kit (or the current kit if
     * the slot has none), colours clamped into range. Returns null when the shape is wrong.
     */
    fun sanitise(player: Player, values: List<Int>): List<Int>? {
        if (values.size != FIELD_COUNT) {
            return null
        }
        val bodyType = if (values[0] == Appearance.BODY_TYPE_B) Appearance.BODY_TYPE_B else Appearance.BODY_TYPE_A
        val current = player.appearance.identKitSnapshot()
        val out = ArrayList<Int>(FIELD_COUNT)
        out += bodyType
        for (slot in 0 until SLOT_COUNT) {
            val legal = kitsFor(slot, bodyType)
            val wanted = values[1 + slot]
            out += when {
                wanted in legal -> wanted
                legal.isNotEmpty() -> legal.first()
                else -> current[slot].toInt() and 0xFFFF
            }
        }
        for (i in 0 until 5) {
            out += values[1 + SLOT_COUNT + i].coerceIn(0, COLOUR_COUNTS[i] - 1)
        }
        return out
    }

    fun apply(player: Player, look: List<Int>) {
        if (look.size != FIELD_COUNT) {
            return
        }
        val appearance = player.appearance
        appearance.bodyType = look[0]
        appearance.pronoun = if (look[0] == Appearance.BODY_TYPE_B) Appearance.PRONOUN_SHE else Appearance.PRONOUN_HE
        for (slot in 0 until SLOT_COUNT) {
            val kit = look[1 + slot]
            if (kit in 0..65535) appearance.setIdentKit(slot, kit)
        }
        for (i in 0 until 5) {
            val colour = look[1 + SLOT_COUNT + i]
            if (colour in 0..255) appearance.setColour(i, colour)
        }
    }

    /** Re-applies a saved look at login; returns false when the player has never designed one. */
    fun restore(player: Player): Boolean {
        val saved = player.attr[SAVED_LOOK] ?: return false
        apply(player, saved.map { (it as Number).toInt() })
        return true
    }

    fun save(player: Player, look: List<Int>) {
        player.attr[SAVED_LOOK] = look.toMutableList()
    }

    /** `LOOK bodyType hair jaw torso arms hands legs feet colHair colTorso colLegs colFeet colSkin`. */
    fun lookLine(player: Player): String {
        val a = player.appearance
        val kits = a.identKitSnapshot().map { it.toInt() and 0xFFFF }
        val cols = a.coloursSnapshot().map { it.toInt() and 0xFF }
        return "LOOK ${a.bodyType} ${kits.joinToString(" ")} ${cols.joinToString(" ")}"
    }

    /** One `KITS slot bodyTypeAIds bodyTypeBIds` line per slot (ids comma-separated). */
    fun kitLines(): List<String> =
        (0 until SLOT_COUNT).map { slot ->
            "KITS $slot ${kitsFor(slot, Appearance.BODY_TYPE_A).joinToString(",").ifEmpty { "-" }} " +
                kitsFor(slot, Appearance.BODY_TYPE_B).joinToString(",").ifEmpty { "-" }
        }
}
