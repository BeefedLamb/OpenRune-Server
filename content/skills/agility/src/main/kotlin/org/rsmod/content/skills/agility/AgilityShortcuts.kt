package org.rsmod.content.skills.agility

import org.rsmod.content.skills.agility.MoveMode.Glide
import org.rsmod.map.CoordGrid

/**
 * Agility shortcuts not already implemented elsewhere in content/ (stiles, the Varrock fence,
 * Champions' Guild / Lumbridge stepping stones, Falador & Port Sarim tunnels, Dwarven Mine
 * crevice, Motherlode tunnel, Lumbridge broken raft are all handled by their area modules, so
 * they are deliberately NOT repeated here).
 *
 * Levels / XP: OSRS Wiki "Shortcuts" table. Loc ids + tiles: this cache. Movement: Kronos.
 * Add an entry to extend.
 */
object AgilityShortcuts {
    val all =
        listOf(
            Shortcut(
                "Falador crumbling wall",
                Obstacle("loc.fai_falador_castle_crumble_mid", 0.5,
                    listOf(Step(Dest.Across(2), Glide, 2, Seqs.WALL_CLIMB_OVER)), level = 5,
                    attempt = "You climb over the crumbling wall."),
            ),
            Shortcut(
                "Grand Exchange underwall tunnel (west entrance)",
                Obstacle("loc.varrock_sc_tunnel_west", 0.0, tunnel(5, -3), level = 21,
                    attempt = "You climb into the tunnel..."),
            ),
            Shortcut(
                "Grand Exchange underwall tunnel (east entrance)",
                Obstacle("loc.varrock_sc_tunnel_east", 0.0, tunnel(-5, 3), level = 21,
                    attempt = "You climb into the tunnel..."),
            ),
            Shortcut(
                "Brimhaven rope swing to Moss Giant Island",
                Obstacle("loc.tree_ropeswing1", 3.0,
                    listOf(Step(Dest.Abs(2704, 3209, 0), Glide, 2, Seqs.ROPE_SWING)), level = 10,
                    approach = CoordGrid(2709, 3209, 0), attempt = "You skillfully swing across."),
            ),
            Shortcut(
                "Brimhaven rope swing back from Moss Giant Island",
                // Wiki: no Agility requirement when leaving the island.
                Obstacle("loc.tree_ropeswing2", 3.0,
                    listOf(Step(Dest.Abs(2709, 3205, 0), Glide, 2, Seqs.ROPE_SWING)), level = 1,
                    approach = CoordGrid(2705, 3205, 0), attempt = "You skillfully swing across."),
            ),
            Shortcut(
                "Mort Myre stepping stone (Nature Grotto)",
                Obstacle("loc.fairy_island_nature_grotto_shortcut", 0.0,
                    listOf(Step(Dest.Across(3), Glide, 2, Seqs.SPOT_JUMP),
                        Step(Dest.Across(2), Glide, 2, Seqs.SPOT_JUMP)), level = 50),
            ),
        )

    private fun tunnel(dx: Int, dz: Int) =
        listOf(
            Step(Dest.Rel(Integer.signum(dx), 0), Glide, 2, Seqs.TUNNEL_ENTER),
            Step(Dest.Rel(dx - 2 * Integer.signum(dx), dz), Glide, 3, Seqs.TUNNEL_WALK),
            Step(Dest.Rel(Integer.signum(dx), 0), Glide, 1, Seqs.TUNNEL_EXIT),
        )
}
