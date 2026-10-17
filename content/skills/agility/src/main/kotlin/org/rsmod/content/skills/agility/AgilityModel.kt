package org.rsmod.content.skills.agility

import org.rsmod.map.CoordGrid

/** Where a movement step ends, resolved against the player's tile and the obstacle loc's tile. */
sealed interface Dest {
    fun resolve(player: CoordGrid, loc: CoordGrid, origin: CoordGrid): CoordGrid

    /** A fixed tile. */
    data class Abs(val x: Int, val z: Int, val level: Int) : Dest {
        override fun resolve(player: CoordGrid, loc: CoordGrid, origin: CoordGrid) = CoordGrid(x, z, level)
    }

    /** Offset from the player's current tile (optionally changing level). */
    data class Rel(val dx: Int, val dz: Int, val level: Int? = null) : Dest {
        override fun resolve(player: CoordGrid, loc: CoordGrid, origin: CoordGrid) =
            CoordGrid(player.x + dx, player.z + dz, level ?: player.level)
    }

    /** Keep the player's x (obstacles several tiles wide, e.g. nets), fixed z and level. */
    data class KeepX(val z: Int, val level: Int) : Dest {
        override fun resolve(player: CoordGrid, loc: CoordGrid, origin: CoordGrid) = CoordGrid(player.x, z, level)
    }

    /**
     * Straight through the obstacle along whichever axis the player approached from, moving
     * [distance] tiles from the player's current tile (walls, stepping stones, tunnels).
     */
    data class Across(val distance: Int) : Dest {
        override fun resolve(player: CoordGrid, loc: CoordGrid, origin: CoordGrid): CoordGrid {
            val dx = loc.x - player.x
            val dz = loc.z - player.z
            return if (kotlin.math.abs(dx) >= kotlin.math.abs(dz)) {
                CoordGrid(player.x + distance * sign(dx), player.z, player.level)
            } else {
                CoordGrid(player.x, player.z + distance * sign(dz), player.level)
            }
        }

        private fun sign(v: Int) = if (v >= 0) 1 else -1
    }
}

enum class MoveMode {
    /** Animated force-movement (exactmove) to the destination over [Step.ticks]. */
    Glide,
    /** Play the animation, wait [Step.ticks], then appear at the destination (climbs, drops). */
    Jump,
}

data class Step(
    val dest: Dest,
    val mode: MoveMode,
    val ticks: Int = 1,
    val anim: String? = null,
)

/**
 * Failure chance, linearly interpolated between ([lowLevel], [lowChance]) and ([highLevel],
 * [highChance]); a chance of 1.0 or more means the obstacle can no longer be failed.
 */
data class Fail(
    val lowLevel: Int,
    val lowChance: Double,
    val highLevel: Int,
    val highChance: Double,
    val landing: CoordGrid,
    val message: String = "You lose your footing and fall to the level below.",
) {
    fun successChance(level: Int): Double {
        if (highLevel == lowLevel) return highChance
        val t = (level - lowLevel).toDouble() / (highLevel - lowLevel)
        return lowChance + (highChance - lowChance) * t
    }
}

data class Obstacle(
    val loc: String,
    val xp: Double,
    val steps: List<Step>,
    /** Only this placed instance of [loc] (for loc types reused across a course / the world). */
    val at: CoordGrid? = null,
    /** Agility level required for this obstacle; 0 = use the course's level. */
    val level: Int = 0,
    /** Tile to step onto before the obstacle movement starts. */
    val approach: CoordGrid? = null,
    val fail: Fail? = null,
    val attempt: String? = null,
    val success: String? = null,
    /** Player must stand at x >= / <= loc.x (one-way obstacles). */
    val oneWay: OneWay? = null,
)

enum class OneWay { FromEast, FromWest, FromNorth, FromSouth }

data class Course(
    val name: String,
    val level: Int,
    /** Obstacles in lap order; the last one completes the lap. */
    val obstacles: List<Obstacle>,
    /** Extra XP for completing every obstacle of the lap in order (Gnome / Barbarian). */
    val lapBonus: Double = 0.0,
    val lapStrengthBonus: Double = 0.0,
    val markSpawns: List<CoordGrid> = emptyList(),
    /** Per-lap mark of grace chance at the course level. */
    val markChance: Double = 1.0 / 3.0,
    /** Whether the mark chance drops once 20+ levels above the course (all but Canifis). */
    val markFalloff: Boolean = true,
    val quest: String? = null,
    val questName: String? = null,
)

data class Shortcut(
    val name: String,
    val obstacle: Obstacle,
)

