package org.rsmod.content.skills.magic.utility

/**
 * Item mappings for the utility spells. Spell level / magic xp / rune costs are NOT defined here:
 * they come straight from the cache spell objs (spell_levelreq / spell_castxp / spell_runetype_*)
 * via [org.rsmod.api.spells.MagicSpellRegistry], exactly like Alchemy and the teleports.
 *
 * All obj names below were verified against the rev 240 gamevals.
 */
internal object UtilitySpellData {
    /** Enchant Lvl-1..7 spell objs -> (unenchanted -> enchanted) transforms. */
    enum class Enchant(val spellObj: String, val transforms: Map<String, String>) {
        Lvl1(
            "obj.07_enchant_amulet_lvl1",
            mapOf(
                "obj.sapphire_ring" to "obj.ring_of_recoil",
                "obj.sapphire_necklace" to "obj.necklace_of_minigames_8",
                "obj.strung_sapphire_amulet" to "obj.amulet_of_magic",
                "obj.jewl_sapphire_bracelet" to "obj.jewl_bracelet_of_clay",
                "obj.opal_ring" to "obj.ring_of_pursuit",
                "obj.opal_necklace" to "obj.dodgy_necklace",
                "obj.strung_opal_amulet" to "obj.amulet_of_bounty",
                "obj.opal_bracelet" to "obj.expeditious_bracelet",
            ),
        ),
        Lvl2(
            "obj.27_enchant_amulet_lvl2",
            mapOf(
                "obj.emerald_ring" to "obj.ring_of_dueling_8",
                "obj.emerald_necklace" to "obj.magic_emerald_necklace",
                "obj.strung_emerald_amulet" to "obj.amulet_of_defence",
                "obj.jewl_emerald_bracelet" to "obj.jewl_castlewars_bracelet3",
                "obj.jade_ring" to "obj.ring_of_returning_5",
                "obj.jade_necklace" to "obj.necklace_of_passage_5",
                "obj.strung_jade_amulet" to "obj.amulet_of_chemistry",
                "obj.jade_bracelet" to "obj.flamtaer_bracelet",
            ),
        ),
        Lvl3(
            "obj.49_enchant_amulet_lvl3",
            mapOf(
                "obj.ruby_ring" to "obj.ring_of_forging",
                "obj.ruby_necklace" to "obj.necklace_of_digsite_5",
                "obj.strung_ruby_amulet" to "obj.amulet_of_strength",
                "obj.jewl_ruby_bracelet" to "obj.jewl_bracelet_of_innoculation",
                "obj.topaz_ring" to "obj.vampyre_ring", // Efaritay's aid
                "obj.topaz_necklace" to "obj.necklace_of_faith",
                "obj.strung_topaz_amulet" to "obj.burning_amulet_5",
                "obj.topaz_bracelet" to "obj.bracelet_of_slaughter",
            ),
        ),
        Lvl4(
            "obj.57_enchant_amulet_lvl4",
            mapOf(
                "obj.diamond_ring" to "obj.ring_of_life",
                "obj.diamond_necklace" to "obj.jewl_necklace_of_phoenix",
                "obj.strung_diamond_amulet" to "obj.amulet_of_power",
                "obj.jewl_diamond_bracelet" to "obj.jewl_runerunning_bracelet_5",
            ),
        ),
        Lvl5(
            "obj.68_enchant_amulet_lvl5",
            mapOf(
                "obj.dragonstone_ring" to "obj.ring_of_wealth",
                "obj.dragonstone_necklace" to "obj.jewl_necklace_of_skills",
                "obj.strung_dragonstone_amulet" to "obj.amulet_of_glory",
                "obj.jewl_dragonstone_bracelet" to "obj.jewl_bracelet_of_combat",
            ),
        ),
        Lvl6(
            "obj.87_enchant_amulet_lvl6",
            mapOf(
                "obj.onyx_ring" to "obj.enchanted_onyx_ring", // Ring of stone
                "obj.onyx_necklace" to "obj.jewl_beserker_necklace",
                "obj.strung_onyx_amulet" to "obj.enchanted_onyx_amulet", // Amulet of fury
                "obj.jewl_onyx_bracelet" to "obj.jewl_bracelet_regen",
            ),
        ),
        Lvl7(
            "obj.93_enchant_amulet_lvl7",
            mapOf(
                "obj.zenyte_ring" to "obj.zenyte_ring_enchanted",
                "obj.zenyte_necklace" to "obj.zenyte_necklace_enchanted",
                "obj.zenyte_amulet" to "obj.zenyte_amulet_enchanted",
                "obj.zenyte_bracelet" to "obj.zenyte_bracelet_enchanted",
            ),
        ),
        ;

        val spellName: String get() = "Lvl-${ordinal + 1} Enchant"
    }

    /** Plank Make: logs -> plank, coin cost per plank (OSRS: 70 / 175 / 350 / 1,050). */
    data class Plank(val logs: String, val plank: String, val coins: Int)

    val planks =
        listOf(
            Plank("obj.logs", "obj.woodplank", 70),
            Plank("obj.oak_logs", "obj.plank_oak", 175),
            Plank("obj.teak_logs", "obj.plank_teak", 350),
            Plank("obj.mahogany_logs", "obj.plank_mahogany", 1050),
        )

    /** Humidify: empty water containers -> filled. */
    val humidify =
        listOf(
            "obj.vial_empty" to "obj.vial_water",
            "obj.bucket_empty" to "obj.bucket_water",
            "obj.jug_empty" to "obj.jug_water",
            "obj.bowl_empty" to "obj.bowl_water",
        )

    /** String Jewellery: unstrung amulet -> strung amulet (4 Crafting xp each). */
    val stringJewellery =
        listOf(
            "obj.unstrung_gold_amulet" to "obj.strung_gold_amulet",
            "obj.unstrung_sapphire_amulet" to "obj.strung_sapphire_amulet",
            "obj.unstrung_opal_amulet" to "obj.strung_opal_amulet",
            "obj.unstrung_jade_amulet" to "obj.strung_jade_amulet",
            "obj.unstrung_emerald_amulet" to "obj.strung_emerald_amulet",
            "obj.unstrung_topaz_amulet" to "obj.strung_topaz_amulet",
            "obj.unstrung_ruby_amulet" to "obj.strung_ruby_amulet",
            "obj.unstrung_diamond_amulet" to "obj.strung_diamond_amulet",
            "obj.unstrung_dragonstone_amulet" to "obj.strung_dragonstone_amulet",
            "obj.unstrung_onyx_amulet" to "obj.strung_onyx_amulet",
            "obj.unstrung_zenyte_amulet" to "obj.zenyte_amulet",
        )
    const val STRING_CRAFTING_XP = 4.0

    /** Superglass Make alkalis (1 alkali + 1 bucket of sand -> 1 molten glass, 10 Crafting xp). */
    val superglassAlkalis = listOf("obj.soda_ash", "obj.seaweed")
    const val SUPERGLASS_CRAFTING_XP = 10.0

    const val SPIN_FLAX_PER_CAST = 5
    const val SPIN_FLAX_CRAFTING_XP = 15.0
}
