package org.rsmod.content.skills.farming

/**
 * Maps a patch's logical state to the transmit-varbit value the OSRS client uses to morph the
 * patch loc. Data: resources/farming/patch_states.tsv, derived from RuneLite's
 * PatchImplementation (which documents every value -> loc variant for each patch family).
 */
object FarmingPatchStates {
    private data class Row(val kind: String, val value: Int, val produce: String, val state: String, val stage: Int, val ops: String)

    private val rows: List<Row> by lazy {
        val stream = FarmingPatchStates::class.java.getResourceAsStream("/farming/patch_states.tsv")
            ?: error("farming/patch_states.tsv missing")
        stream.bufferedReader().readLines()
            .filter { it.isNotBlank() && !it.startsWith("#") }
            .map { l ->
                val p = l.split('\t')
                Row(p[0], p[1].toInt(), p[2], p[3], p[4].toInt(), p.getOrElse(5) { "" })
            }
    }

    val rowCount: Int get() = rows.size

    private fun of(kind: PatchKind) = rows.filter { it.kind == kind.tableKey }

    /** Weeds 0..3 (3 = fully overgrown). */
    fun weeds(kind: PatchKind, weeds: Int): Int = (3 - weeds.coerceIn(0, 3))

    /** Growing stage (0 = just planted). Watered variant is the second growing block, if any. */
    fun growing(kind: PatchKind, produce: String, stage: Int, watered: Boolean): Int? {
        val candidates = of(kind).filter {
            it.produce == produce && it.state == "GROWING" && it.stage == stage && !it.ops.contains("Check-health")
        }
        if (candidates.isEmpty()) return null
        return if (watered && candidates.size > 1) candidates[1].value else candidates[0].value
    }

    fun checkHealth(kind: PatchKind, produce: String): Int? =
        of(kind).firstOrNull { it.produce == produce && it.ops.contains("Check-health") }?.value

    /** Harvestable. For bushes/fruit trees, [count] is berries/fruit left (table stage = count). */
    fun harvestable(kind: PatchKind, produce: String, count: Int): Int? {
        val h = of(kind).filter { it.produce == produce && it.state == "HARVESTABLE" }
        val pick = when (kind) {
            PatchKind.BUSH -> h.filter { it.ops.contains("Pick") }
            PatchKind.FRUIT_TREE -> h.filter { it.ops.contains("Pick") }
            PatchKind.TREE -> h.filter { it.ops.contains("Chop") }
            else -> h
        }
        if (pick.isEmpty()) return null
        if (kind == PatchKind.BUSH || kind == PatchKind.FRUIT_TREE) {
            return (pick.firstOrNull { it.stage == count } ?: pick.first()).value
        }
        return pick.first().value
    }

    /** Tree / fruit tree stump ("Clear" while harvestable-state). */
    fun stump(kind: PatchKind, produce: String): Int? =
        of(kind).firstOrNull { it.produce == produce && it.state == "HARVESTABLE" && it.ops.startsWith("Clear") && !it.ops.contains("Pick") }?.value

    fun diseased(kind: PatchKind, produce: String, stage: Int): Int? = staged(kind, produce, "DISEASED", stage)

    fun dead(kind: PatchKind, produce: String, stage: Int): Int? =
        staged(kind, produce, "DEAD", stage) ?: if (kind == PatchKind.HERB) staged(kind, "ANYHERB", "DEAD", stage) else null

    private fun staged(kind: PatchKind, produce: String, state: String, stage: Int): Int? {
        val r = of(kind).filter { it.produce == produce && it.state == state }
        if (r.isEmpty()) return null
        return (r.firstOrNull { it.stage == stage } ?: r.minByOrNull { kotlin.math.abs(it.stage - stage) })!!.value
    }
}
