package org.rsmod.content.skills.fletching

/*
 * Fletching data tables.
 *
 * The cache has no usable Fletching dbtables (dbtable.arrow_fletching exists but carries no
 * columns), so the level requirements and XP below are hardcoded from the Old School RuneScape
 * Wiki Fletching tables (oldschool.runescape.wiki/w/Fletching, rev ~240 era). XP is per *item*
 * produced for set-based recipes (arrows, darts, bolts, javelins), and per action otherwise.
 *
 * Item/seq names are the cache's internal gamevals names. Every recipe is validated against
 * the RSCM name tables at startup; any recipe referencing a name that doesn't resolve is
 * skipped and logged instead of crashing the server.
 */

/** One input consumed per action (or per produced item, for set recipes). */
data class FletchInput(val obj: String, val count: Int = 1)

data class FletchRecipe(
    /** Display/category key only. */
    val category: Category,
    val output: String,
    /** Amount produced per action (e.g. 15 arrow shafts from a log). */
    val outputCount: Int,
    val inputs: List<FletchInput>,
    /** Tool kept in the inventory (knife, chisel, hammer), or null. */
    val tool: String?,
    val level: Int,
    /** XP for a full action, or per item when [perItem] is true. */
    val xp: Double,
    /**
     * Set recipes (arrows/darts/bolts/javelins/headless arrows): each produced item consumes one
     * of every input, up to [outputCount] per action. If fewer are available, fewer are made.
     */
    val perItem: Boolean = false,
    val anim: String,
    val ticks: Int,
    val message: String,
) {
    enum class Category { CUT, STRING, ARROW, DART, BOLT, BOLT_TIP, GEM_TIP, CROSSBOW, JAVELIN }
}

private const val ANIM_KNIFE = "seq.human_fletching"
private const val KNIFE = "obj.knife"
private const val CHISEL = "obj.chisel"
private const val HAMMER = "obj.hammer"

object FletchingRecipes {

    val all: List<FletchRecipe> = buildList {
        // ---------------- Log cutting (knife) ----------------
        fun cut(log: String, out: String, n: Int, lvl: Int, xp: Double, logs: Int = 1, what: String) =
            add(FletchRecipe(FletchRecipe.Category.CUT, out, n, listOf(FletchInput(log, logs)), KNIFE, lvl, xp,
                anim = ANIM_KNIFE, ticks = 3, message = "You carefully cut the wood into $what."))

        cut("obj.logs", "obj.arrow_shaft", 15, 1, 5.0, what = "15 arrow shafts")
        cut("obj.logs", "obj.javelin_shaft", 15, 3, 5.0, what = "15 javelin shafts")
        cut("obj.logs", "obj.unstrung_shortbow", 1, 5, 5.0, what = "a shortbow")
        cut("obj.logs", "obj.xbows_crossbow_stock_wood", 1, 9, 6.0, what = "a crossbow stock")
        cut("obj.logs", "obj.unstrung_longbow", 1, 10, 10.0, what = "a longbow")

        cut("obj.oak_logs", "obj.arrow_shaft", 30, 15, 10.0, what = "30 arrow shafts")
        cut("obj.oak_logs", "obj.unstrung_oak_shortbow", 1, 20, 16.5, what = "an oak shortbow")
        cut("obj.oak_logs", "obj.xbows_crossbow_stock_oak", 1, 24, 16.0, what = "an oak crossbow stock")
        cut("obj.oak_logs", "obj.unstrung_oak_longbow", 1, 25, 25.0, what = "an oak longbow")
        cut("obj.oak_logs", "obj.oak_shield", 1, 27, 50.0, logs = 2, what = "an oak shield")

        cut("obj.willow_logs", "obj.arrow_shaft", 45, 30, 15.0, what = "45 arrow shafts")
        cut("obj.willow_logs", "obj.unstrung_willow_shortbow", 1, 35, 33.3, what = "a willow shortbow")
        cut("obj.willow_logs", "obj.xbows_crossbow_stock_willow", 1, 39, 22.0, what = "a willow crossbow stock")
        cut("obj.willow_logs", "obj.unstrung_willow_longbow", 1, 40, 41.5, what = "a willow longbow")
        cut("obj.willow_logs", "obj.willow_shield", 1, 42, 83.0, logs = 2, what = "a willow shield")

        cut("obj.teak_logs", "obj.xbows_crossbow_stock_teak", 1, 46, 27.0, what = "a teak crossbow stock")

        cut("obj.maple_logs", "obj.arrow_shaft", 60, 45, 20.0, what = "60 arrow shafts")
        cut("obj.maple_logs", "obj.unstrung_maple_shortbow", 1, 50, 50.0, what = "a maple shortbow")
        cut("obj.maple_logs", "obj.xbows_crossbow_stock_maple", 1, 54, 32.0, what = "a maple crossbow stock")
        cut("obj.maple_logs", "obj.unstrung_maple_longbow", 1, 55, 58.3, what = "a maple longbow")
        cut("obj.maple_logs", "obj.maple_shield", 1, 57, 100.0, logs = 2, what = "a maple shield")

        cut("obj.mahogany_logs", "obj.xbows_crossbow_stock_mahogany", 1, 61, 41.0, what = "a mahogany crossbow stock")

        cut("obj.yew_logs", "obj.arrow_shaft", 75, 60, 25.0, what = "75 arrow shafts")
        cut("obj.yew_logs", "obj.unstrung_yew_shortbow", 1, 65, 67.5, what = "a yew shortbow")
        cut("obj.yew_logs", "obj.xbows_crossbow_stock_yew", 1, 69, 50.0, what = "a yew crossbow stock")
        cut("obj.yew_logs", "obj.unstrung_yew_longbow", 1, 70, 75.0, what = "a yew longbow")
        cut("obj.yew_logs", "obj.yew_shield", 1, 72, 150.0, logs = 2, what = "a yew shield")

        cut("obj.magic_logs", "obj.arrow_shaft", 90, 75, 30.0, what = "90 arrow shafts")
        cut("obj.magic_logs", "obj.xbows_crossbow_stock_magic", 1, 78, 70.0, what = "a magic crossbow stock")
        cut("obj.magic_logs", "obj.unstrung_magic_shortbow", 1, 80, 83.3, what = "a magic shortbow")
        cut("obj.magic_logs", "obj.unstrung_magic_longbow", 1, 85, 91.5, what = "a magic longbow")
        cut("obj.magic_logs", "obj.magic_shield", 1, 87, 183.0, logs = 2, what = "a magic shield")

        cut("obj.redwood_logs", "obj.arrow_shaft", 105, 90, 35.0, what = "105 arrow shafts")
        cut("obj.redwood_logs", "obj.redwood_shield", 1, 92, 216.0, logs = 2, what = "a redwood shield")

        // ---------------- Bow stringing (bow string) ----------------
        fun string(u: String, bow: String, lvl: Int, xp: Double, seq: String) =
            add(FletchRecipe(FletchRecipe.Category.STRING, bow, 1,
                listOf(FletchInput(u), FletchInput("obj.bow_string")), null, lvl, xp,
                anim = seq, ticks = 2, message = "You add a string to the bow."))

        string("obj.unstrung_shortbow", "obj.shortbow", 5, 5.0, "seq.stringing_shortbow")
        string("obj.unstrung_longbow", "obj.longbow", 10, 10.0, "seq.stringing_longbow")
        string("obj.unstrung_oak_shortbow", "obj.oak_shortbow", 20, 16.5, "seq.stringing_oak_shortbow")
        string("obj.unstrung_oak_longbow", "obj.oak_longbow", 25, 25.0, "seq.stringing_oak_longbow")
        string("obj.unstrung_willow_shortbow", "obj.willow_shortbow", 35, 33.3, "seq.stringing_willow_shortbow")
        string("obj.unstrung_willow_longbow", "obj.willow_longbow", 40, 41.5, "seq.stringing_willow_longbow")
        string("obj.unstrung_maple_shortbow", "obj.maple_shortbow", 50, 50.0, "seq.stringing_maple_shortbow")
        string("obj.unstrung_maple_longbow", "obj.maple_longbow", 55, 58.3, "seq.stringing_maple_longbow")
        string("obj.unstrung_yew_shortbow", "obj.yew_shortbow", 65, 67.5, "seq.stringing_yew_shortbow")
        string("obj.unstrung_yew_longbow", "obj.yew_longbow", 70, 75.0, "seq.stringing_yew_longbow")
        string("obj.unstrung_magic_shortbow", "obj.magic_shortbow", 80, 83.3, "seq.stringing_magic_shortbow")
        string("obj.unstrung_magic_longbow", "obj.magic_longbow", 85, 91.5, "seq.stringing_magic_longbow")

        // ---------------- Arrows (15 per action) ----------------
        add(FletchRecipe(FletchRecipe.Category.ARROW, "obj.headless_arrow", 15,
            listOf(FletchInput("obj.arrow_shaft"), FletchInput("obj.feather")), null, 1, 1.0, perItem = true,
            anim = "seq.human_fletching_add_feather", ticks = 2, message = "You attach feathers to the arrow shafts."))
        fun arrow(tip: String, out: String, lvl: Int, xp: Double) =
            add(FletchRecipe(FletchRecipe.Category.ARROW, out, 15,
                listOf(FletchInput("obj.headless_arrow"), FletchInput(tip)), null, lvl, xp, perItem = true,
                anim = "seq.human_fletching_add_arrow_tips", ticks = 2, message = "You attach arrow heads to the arrows."))
        arrow("obj.bronze_arrowheads", "obj.bronze_arrow", 1, 1.3)
        arrow("obj.iron_arrowheads", "obj.iron_arrow", 15, 2.5)
        arrow("obj.steel_arrowheads", "obj.steel_arrow", 30, 5.0)
        arrow("obj.mithril_arrowheads", "obj.mithril_arrow", 45, 7.5)
        arrow("obj.adamant_arrowheads", "obj.adamant_arrow", 60, 10.0)
        arrow("obj.rune_arrowheads", "obj.rune_arrow", 75, 12.5)
        arrow("obj.amethyst_arrowheads", "obj.amethyst_arrow", 82, 13.5)
        arrow("obj.dragon_arrowheads", "obj.dragon_arrow", 90, 15.0)

        // ---------------- Darts (10 per action) ----------------
        fun dart(metal: String, lvl: Int, xp: Double) =
            add(FletchRecipe(FletchRecipe.Category.DART, "obj.${metal}_dart", 10,
                listOf(FletchInput("obj.${metal}_dart_tip"), FletchInput("obj.feather")), null, lvl, xp, perItem = true,
                anim = "seq.human_fletching_add_dart_feathers_$metal", ticks = 2, message = "You finish making darts."))
        dart("bronze", 10, 1.8)
        dart("iron", 22, 3.8)
        dart("steel", 37, 7.5)
        dart("mithril", 52, 11.2)
        dart("adamant", 67, 15.0)
        dart("rune", 81, 18.8)
        dart("amethyst", 90, 21.0)
        dart("dragon", 95, 25.0)

        // ---------------- Bolts: feathering unfinished bolts (10 per action) ----------------
        fun bolt(unf: String, out: String, lvl: Int, xp: Double, seqMetal: String) =
            add(FletchRecipe(FletchRecipe.Category.BOLT, out, 10,
                listOf(FletchInput(unf), FletchInput("obj.feather")), null, lvl, xp, perItem = true,
                anim = "seq.human_fletching_add_bolt_feathers_$seqMetal", ticks = 2, message = "You fletch some bolts."))
        bolt("obj.xbows_crossbow_bolts_bronze_unfeathered", "obj.bolt", 9, 0.5, "bronze")
        bolt("obj.xbows_crossbow_bolts_blurite_unfeathered", "obj.xbows_crossbow_bolts_blurite", 24, 1.0, "blurite")
        bolt("obj.xbows_crossbow_bolts_iron_unfeathered", "obj.xbows_crossbow_bolts_iron", 39, 1.5, "iron")
        bolt("obj.xbows_crossbow_bolts_silver_unfeathered", "obj.xbows_crossbow_bolts_silver", 43, 2.5, "silver")
        bolt("obj.xbows_crossbow_bolts_steel_unfeathered", "obj.xbows_crossbow_bolts_steel", 46, 3.5, "steel")
        bolt("obj.xbows_crossbow_bolts_mithril_unfeathered", "obj.xbows_crossbow_bolts_mithril", 54, 5.0, "mithril")
        bolt("obj.xbows_crossbow_bolts_adamantite_unfeathered", "obj.xbows_crossbow_bolts_adamantite", 61, 7.0, "adamant")
        bolt("obj.xbows_crossbow_bolts_runite_unfeathered", "obj.xbows_crossbow_bolts_runite", 69, 10.0, "rune")
        bolt("obj.dragon_bolts_unfeathered", "obj.dragon_bolts", 84, 12.0, "dragon")

        // ---------------- Gem bolt tips (chisel) ----------------
        fun gemTip(gem: String, tips: String, n: Int, lvl: Int, xp: Double) =
            add(FletchRecipe(FletchRecipe.Category.GEM_TIP, tips, n, listOf(FletchInput(gem)), CHISEL, lvl, xp,
                anim = "seq.human_fletching_huntingbolts_chisel", ticks = 2, message = "You cut the gem into bolt tips."))
        gemTip("obj.opal", "obj.opal_bolttips", 12, 11, 1.5)
        gemTip("obj.jade", "obj.xbows_bolt_tips_jade", 12, 26, 2.4)
        gemTip("obj.red_topaz", "obj.xbows_bolt_tips_redtopaz", 12, 48, 3.9)
        gemTip("obj.sapphire", "obj.xbows_bolt_tips_sapphire", 12, 56, 4.0)
        gemTip("obj.emerald", "obj.xbows_bolt_tips_emerald", 12, 58, 5.5)
        gemTip("obj.ruby", "obj.xbows_bolt_tips_ruby", 12, 63, 6.3)
        gemTip("obj.diamond", "obj.xbows_bolt_tips_diamond", 12, 65, 7.0)
        gemTip("obj.dragonstone", "obj.xbows_bolt_tips_dragonstone", 12, 71, 8.2)
        gemTip("obj.onyx", "obj.xbows_bolt_tips_onyx", 12, 73, 9.4)
        // Amethyst (chisel + amethyst) is owned by Crafting (bolt tips/arrowtips/javelin heads/dart tips); registering it here crashes startup.

        // ---------------- Tipping bolts (10 per action) ----------------
        fun tip(base: String, tips: String, out: String, lvl: Int, xp: Double, seqMetal: String) =
            add(FletchRecipe(FletchRecipe.Category.BOLT_TIP, out, 10,
                listOf(FletchInput(base), FletchInput(tips)), null, lvl, xp, perItem = true,
                anim = "seq.human_fletching_add_bolt_tips_$seqMetal", ticks = 2, message = "You fletch bolt tips onto the bolts."))
        tip("obj.bolt", "obj.opal_bolttips", "obj.opal_bolts", 11, 1.6, "bronze")
        tip("obj.xbows_crossbow_bolts_blurite", "obj.xbows_bolt_tips_jade", "obj.xbows_crossbow_bolts_blurite_tipped_jade", 26, 2.4, "blurite")
        tip("obj.xbows_crossbow_bolts_iron", "obj.pearl_bolttips", "obj.pearl_bolts", 41, 3.2, "iron")
        tip("obj.xbows_crossbow_bolts_steel", "obj.xbows_bolt_tips_redtopaz", "obj.xbows_crossbow_bolts_steel_tipped_redtopaz", 48, 3.9, "steel")
        tip("obj.xbows_crossbow_bolts_mithril", "obj.xbows_bolt_tips_sapphire", "obj.xbows_crossbow_bolts_mithril_tipped_sapphire", 56, 4.7, "mithril")
        tip("obj.xbows_crossbow_bolts_mithril", "obj.xbows_bolt_tips_emerald", "obj.xbows_crossbow_bolts_mithril_tipped_emerald", 58, 5.5, "mithril")
        tip("obj.xbows_crossbow_bolts_adamantite", "obj.xbows_bolt_tips_ruby", "obj.xbows_crossbow_bolts_adamantite_tipped_ruby", 63, 6.3, "adamant")
        tip("obj.xbows_crossbow_bolts_adamantite", "obj.xbows_bolt_tips_diamond", "obj.xbows_crossbow_bolts_adamantite_tipped_diamond", 65, 7.0, "adamant")
        tip("obj.xbows_crossbow_bolts_runite", "obj.xbows_bolt_tips_dragonstone", "obj.xbows_crossbow_bolts_runite_tipped_dragonstone", 71, 8.2, "rune")
        tip("obj.xbows_crossbow_bolts_runite", "obj.xbows_bolt_tips_onyx", "obj.xbows_crossbow_bolts_runite_tipped_onyx", 73, 9.4, "rune")
        // Dragon gem-tipped bolts: level 84, same XP as the matching gem tip.
        listOf(
            Triple("obj.opal_bolttips", "opal", 1.6), Triple("obj.xbows_bolt_tips_jade", "jade", 2.4),
            Triple("obj.pearl_bolttips", "pearl", 3.2), Triple("obj.xbows_bolt_tips_redtopaz", "topaz", 3.9),
            Triple("obj.xbows_bolt_tips_sapphire", "sapphire", 4.7), Triple("obj.xbows_bolt_tips_emerald", "emerald", 5.5),
            Triple("obj.xbows_bolt_tips_ruby", "ruby", 6.3), Triple("obj.xbows_bolt_tips_diamond", "diamond", 7.0),
            Triple("obj.xbows_bolt_tips_dragonstone", "dragonstone", 8.2), Triple("obj.xbows_bolt_tips_onyx", "onyx", 9.4),
        ).forEach { (tips, gem, xp) ->
            tip("obj.dragon_bolts", tips, "obj.dragon_bolts_unenchanted_$gem", 84, xp, "dragon")
        }

        // ---------------- Crossbows (limbs + stock w/ hammer, then crossbow string) ----------------
        fun xbow(stock: String, metal: String, lvl: Int, attachXp: Double, stringXp: Double) {
            val unstrung = "obj.xbows_crossbow_unstrung_$metal"
            add(FletchRecipe(FletchRecipe.Category.CROSSBOW, unstrung, 1,
                listOf(FletchInput(stock), FletchInput("obj.xbows_crossbow_limbs_$metal")), HAMMER, lvl, attachXp,
                anim = ANIM_KNIFE, ticks = 2, message = "You attach the limbs to the stock."))
            add(FletchRecipe(FletchRecipe.Category.CROSSBOW, "obj.xbows_crossbow_$metal", 1,
                listOf(FletchInput(unstrung), FletchInput("obj.xbows_crossbow_string")), null, lvl, stringXp,
                anim = "seq.xbows_stringing_crossbow_$metal", ticks = 2, message = "You add a string to the crossbow."))
        }
        xbow("obj.xbows_crossbow_stock_wood", "bronze", 9, 12.0, 6.0)
        xbow("obj.xbows_crossbow_stock_oak", "blurite", 24, 16.0, 16.0)
        xbow("obj.xbows_crossbow_stock_willow", "iron", 39, 22.0, 22.0)
        xbow("obj.xbows_crossbow_stock_teak", "steel", 46, 27.0, 27.0)
        xbow("obj.xbows_crossbow_stock_maple", "mithril", 54, 32.0, 32.0)
        xbow("obj.xbows_crossbow_stock_mahogany", "adamantite", 61, 41.0, 41.0)
        xbow("obj.xbows_crossbow_stock_yew", "runite", 69, 50.0, 50.0)
        xbow("obj.xbows_crossbow_stock_magic", "dragon", 78, 70.0, 70.0)

        // ---------------- Javelins (15 per action) ----------------
        fun javelin(metal: String, lvl: Int, xp: Double) =
            add(FletchRecipe(FletchRecipe.Category.JAVELIN, "obj.${metal}_javelin", 15,
                listOf(FletchInput("obj.javelin_shaft"), FletchInput("obj.${metal}_javelin_head")), null, lvl, xp,
                perItem = true, anim = ANIM_KNIFE, ticks = 2, message = "You attach javelin heads to the shafts."))
        javelin("bronze", 3, 1.0)
        javelin("iron", 17, 2.0)
        javelin("steel", 32, 5.0)
        javelin("mithril", 47, 8.0)
        javelin("adamant", 62, 10.0)
        javelin("rune", 77, 12.4)
        javelin("amethyst", 84, 13.5)
        javelin("dragon", 92, 15.0)
    }
}
