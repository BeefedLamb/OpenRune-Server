package org.rsmod.content.skills.agility

import org.rsmod.content.skills.agility.MoveMode.Glide
import org.rsmod.content.skills.agility.MoveMode.Jump
import org.rsmod.map.CoordGrid

/**
 * Agility course data.
 *
 * Sources, so the numbers can be audited:
 * - Loc names / ids and their placed tiles: read straight from this server's cache (gameval
 *   archive + map squares).
 * - Level requirements, per-obstacle XP, lap bonuses, which obstacles can fail and the level at
 *   which they stop failing: the OSRS Wiki course pages (current values, incl. the May 2024
 *   rooftop rework: Draynor is level 1 and cannot be failed).
 * - Landing / intermediate tiles: the open-source Kronos (rev 184) agility implementation, each
 *   tile then checked against this cache's collision (not blocked, no solid loc).
 * - Fail landing tiles: derived from the cache as the open ground tile beneath the obstacle.
 *   These are NOT verified against live OSRS; see the agility report.
 */
object AgilityCourses {
    private fun t(x: Int, z: Int, level: Int) = CoordGrid(x, z, level)

    private fun abs(x: Int, z: Int, level: Int) = Dest.Abs(x, z, level)

    private fun climb(x: Int, z: Int, level: Int, ticks: Int = 2, anim: String = Seqs.CLIMB) =
        Step(abs(x, z, level), Jump, ticks, anim)

    private fun leap(x: Int, z: Int, level: Int, ticks: Int = 2, anim: String = Seqs.JUMP_DOWN) =
        Step(abs(x, z, level), Jump, ticks, anim)

    private fun walk(x: Int, z: Int, level: Int, ticks: Int, anim: String? = Seqs.BALANCE) =
        Step(abs(x, z, level), Glide, ticks, anim)

    private fun fail(courseLevel: Int, untilLevel: Int, x: Int, z: Int) =
        // Wiki gives "fails until level N" but not the starting rate; 85% at the course level is
        // an assumption (Canifis' published curve starts at ~88%).
        Fail(courseLevel, 0.85, untilLevel, 1.0, t(x, z, 0))

    val gnome =
        Course(
            name = "Gnome Stronghold Agility Course",
            level = 1,
            lapBonus = 50.0,
            obstacles =
                listOf(
                    Obstacle("loc.gnome_log_balance1", 10.0, listOf(walk(2474, 3429, 0, 7, Seqs.LOG_BALANCE)),
                        attempt = "You walk carefully across the slippery log...",
                        success = "...You make it safely to the other side."),
                    Obstacle("loc.obstical_net2", 10.0, listOf(Step(Dest.KeepX(3424, 1), Jump, 2, Seqs.CLIMB)),
                        attempt = "You climb the netting..."),
                    Obstacle("loc.climbing_branch", 6.5, listOf(climb(2473, 3420, 2)),
                        attempt = "You climb the tree...", success = "...To the platform above."),
                    Obstacle("loc.balancing_rope", 10.0, listOf(walk(2483, 3420, 2, 6)),
                        attempt = "You carefully cross the tightrope."),
                    Obstacle("loc.climbing_tree", 6.5, listOf(climb(2485, 3419, 0)),
                        attempt = "You climb down the tree...", success = "You land on the ground."),
                    Obstacle("loc.obstical_net3", 10.0, listOf(Step(Dest.KeepX(3428, 0), Jump, 2, Seqs.CLIMB)),
                        oneWay = OneWay.FromSouth, attempt = "You climb the netting..."),
                    // Two pipes; each squeezes 7 tiles north (Kronos: obj.y-1 -> obj.y+6).
                    Obstacle("loc.obstical_pipe3_1", 7.5, pipeSteps(2484), at = t(2484, 3431, 0),
                        approach = t(2484, 3430, 0), attempt = "You pull yourself through the pipes."),
                ),
        )

    /** Second Gnome pipe: same lap slot as the first (either completes the lap). */
    val gnomePipe2 =
        Obstacle("loc.obstical_pipe3_2", 7.5, pipeSteps(2487), at = t(2487, 3431, 0),
            approach = t(2487, 3430, 0), attempt = "You pull yourself through the pipes.")

    private fun pipeSteps(x: Int) =
        listOf(
            Step(abs(x, 3433, 0), Glide, 3, Seqs.PIPE_SQUEEZE),
            Step(abs(x, 3437, 0), Glide, 3, Seqs.PIPE_SQUEEZE),
        )

    /**
     * Barbarian Outpost. Wiki: level 35 (only the entry pipe actually checks it), needs Alfred
     * Grimhand's Barcrawl; lap = 153.3 XP incl. 46.3 bonus (+41.3 Strength).
     * Ropeswing / log / ledge failures are deferred (no verified fail tiles or rates).
     */
    val barbarian =
        Course(
            name = "Barbarian Outpost Agility Course",
            level = 35,
            lapBonus = 46.3,
            lapStrengthBonus = 41.3,
            quest = "miniquest_barcrawl",
            questName = "Alfred Grimhand's Barcrawl",
            obstacles =
                listOf(
                    Obstacle("loc.obstical_ropeswing1", 22.0, listOf(Step(Dest.Rel(0, -5), Glide, 2, Seqs.ROPE_SWING)),
                        approach = t(2551, 3554, 0), attempt = "You skillfully swing across."),
                    Obstacle("loc.barbarian_log_balance1", 13.7, listOf(walk(2541, 3546, 0, 9, Seqs.LOG_BALANCE)),
                        approach = t(2551, 3546, 0), attempt = "You walk carefully across the slippery log...",
                        success = "...You make it safely to the other side."),
                    Obstacle("loc.agility_obstical_net_barbarian", 8.2,
                        listOf(Step(Dest.Rel(-2, 0, level = 1), Jump, 2, Seqs.CLIMB)),
                        oneWay = OneWay.FromEast, attempt = "You climb the netting..."),
                    Obstacle("loc.balancing_ledge1", 22.0,
                        listOf(walk(2532, 3547, 1, 3, Seqs.SIDESTEP_IN), walk(2532, 3546, 1, 1, Seqs.SIDESTEP_OUT)),
                        attempt = "You put your foot on the ledge and try to edge across...",
                        success = "You skillfully edge across the gap."),
                    // The ladder (0 XP) between ledge and walls is the generic ladder handler.
                    crumbling(2536, 1),
                    crumbling(2539, 2),
                    crumbling(2542, 3),
                ),
        )

    private fun crumbling(x: Int, n: Int) =
        Obstacle("loc.castlecrumbly1", 13.7, listOf(Step(Dest.Rel(2, 0), Glide, 2, Seqs.WALL_CLIMB_OVER)),
            at = t(x, 3553, 0), approach = t(x - 1, 3553, 0), oneWay = OneWay.FromWest,
            attempt = "You climb the low wall...")

    /** Barbarian entry pipe (level 35 gate, 0 XP, not part of the lap). */
    val barbarianPipe =
        Obstacle("loc.agility_obstical_pipe_barbarian", 0.0,
            listOf(Step(Dest.Across(3), Glide, 3, Seqs.PIPE_SQUEEZE)), level = 35,
            attempt = "You squeeze through the pipe.")

    val draynor =
        Course(
            name = "Draynor Village Rooftop Course",
            level = 1,
            markChance = 1.0 / 3.0,
            markSpawns = listOf(t(3099, 3280, 3), t(3089, 3274, 3), t(3094, 3266, 3), t(3088, 3259, 3),
                t(3092, 3255, 3), t(3099, 3257, 3), t(3098, 3259, 3)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_draynor_wallclimb", 5.0, listOf(climb(3102, 3279, 3))),
                    Obstacle("loc.rooftops_draynor_tightrope_1", 8.0,
                        listOf(walk(3090, 3277, 3, 8), walk(3090, 3276, 3, 1, null))),
                    Obstacle("loc.rooftops_draynor_tightrope_2", 7.0,
                        listOf(walk(3092, 3276, 3, 1, null), walk(3092, 3266, 3, 10))),
                    Obstacle("loc.rooftops_draynor_wallcrossing", 7.0,
                        listOf(walk(3089, 3262, 3, 2, Seqs.SIDESTEP_IN), walk(3088, 3261, 3, 1, Seqs.SIDESTEP_OUT))),
                    Obstacle("loc.rooftops_draynor_wallscramble", 10.0,
                        listOf(walk(3088, 3256, 3, 1, null), Step(abs(3088, 3255, 3), Glide, 1, Seqs.WALL_JUMP))),
                    Obstacle("loc.rooftops_draynor_leapdown", 4.0, listOf(leap(3096, 3256, 3, 1))),
                    Obstacle("loc.rooftops_draynor_crate", 79.0, listOf(leap(3103, 3261, 0, 3))),
                ),
        )

    val alKharid =
        Course(
            name = "Al Kharid Rooftop Course",
            level = 20,
            markSpawns = listOf(t(3276, 3188, 3), t(3271, 3191, 3), t(3273, 3182, 3), t(3267, 3171, 3),
                t(3271, 3170, 3), t(3268, 3163, 3), t(3266, 3166, 3), t(3291, 3163, 3), t(3297, 3168, 3),
                t(3301, 3164, 3), t(3316, 3161, 1), t(3318, 3163, 1), t(3315, 3176, 2), t(3317, 3178, 2),
                t(3315, 3183, 3), t(3313, 3181, 3), t(3302, 3189, 3), t(3300, 3190, 3)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_kharid_wallclimb", 12.0, listOf(climb(3273, 3192, 3))),
                    Obstacle("loc.rooftops_kharid_tightrope_1", 36.0, listOf(walk(3272, 3172, 3, 10)),
                        fail = fail(20, 40, 3272, 3177)),
                    Obstacle("loc.rooftops_kharid_rope_swing", 48.0,
                        listOf(Step(abs(3284, 3166, 3), Glide, 3, Seqs.ROPE_SWING))),
                    Obstacle("loc.rooftops_kharid_slide_side", 48.0,
                        listOf(Step(abs(3303, 3163, 1), Jump, 1, Seqs.ZIPLINE_BITE),
                            Step(abs(3315, 3163, 1), Glide, 4, Seqs.ZIPLINE_SLIDE)),
                        fail = fail(20, 40, 3309, 3163)),
                    Obstacle("loc.rooftops_kharid_bamboo_tree_top", 12.0,
                        listOf(Step(abs(3317, 3174, 2), Jump, 5, Seqs.HANDHOLDS)),
                        approach = t(3318, 3165, 1)),
                    Obstacle("loc.rooftops_kharid_wallclimb_2", 6.0, listOf(climb(3316, 3180, 3))),
                    Obstacle("loc.rooftops_kharid_tightrope_4", 18.0,
                        listOf(walk(3302, 3186, 3, 12), walk(3302, 3187, 3, 1, null)),
                        approach = t(3314, 3186, 3)),
                    Obstacle("loc.rooftops_kharid_leapdown", 36.0, listOf(leap(3299, 3194, 0, 2))),
                ),
        )

    val varrock =
        Course(
            name = "Varrock Rooftop Course",
            level = 30,
            markSpawns = listOf(t(3214, 3417, 3), t(3202, 3417, 3), t(3194, 3416, 1), t(3194, 3404, 3),
                t(3196, 3394, 3), t(3205, 3395, 3), t(3226, 3402, 3), t(3236, 3407, 3)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_varrock_wallclimb", 13.5,
                        listOf(climb(3220, 3414, 3), walk(3219, 3414, 3, 1, Seqs.WALL_JUMP2))),
                    Obstacle("loc.rooftops_varrock_clothesline", 23.0,
                        listOf(Step(abs(3212, 3414, 3), Glide, 2, Seqs.SPOT_JUMP),
                            Step(abs(3210, 3414, 3), Glide, 2, Seqs.SPOT_JUMP),
                            Step(abs(3208, 3414, 3), Glide, 2, Seqs.SPOT_JUMP)),
                        fail = fail(30, 50, 3211, 3414)),
                    Obstacle("loc.rooftops_varrock_leaptoruins", 19.0, listOf(leap(3197, 3416, 1, 2))),
                    Obstacle("loc.rooftops_varrock_wallswing", 28.0,
                        listOf(walk(3193, 3416, 1, 1, Seqs.RUN), Step(abs(3190, 3414, 1), Glide, 1, Seqs.WALL_JUMP),
                            Step(abs(3190, 3409, 1), Glide, 10, Seqs.HANDHOLDS_REVERSE),
                            walk(3190, 3407, 1, 2, Seqs.SIDESTEP_IN),
                            Step(abs(3192, 3406, 3), Jump, 1, Seqs.SPOT_JUMP)),
                        approach = t(3194, 3416, 1), fail = fail(30, 50, 3190, 3410)),
                    Obstacle("loc.rooftops_varrock_wallscramble", 10.0,
                        listOf(Step(Dest.KeepX(3399, 3), Glide, 1, Seqs.WALL_JUMP),
                            Step(Dest.KeepX(3398, 3), Glide, 1, Seqs.WALL_JUMP2))),
                    Obstacle("loc.rooftops_varrock_leaptobalcony", 24.5,
                        listOf(Step(abs(3215, 3399, 3), Glide, 2, Seqs.LEAP),
                            Step(abs(3217, 3399, 3), Glide, 1, Seqs.WALL_JUMP),
                            Step(abs(3218, 3399, 3), Glide, 1, Seqs.WALL_JUMP2))),
                    Obstacle("loc.rooftops_varrock_leapdown", 4.5, listOf(leap(3236, 3403, 3, 1))),
                    Obstacle("loc.rooftops_varrock_stepuproof", 3.5,
                        listOf(Step(abs(3236, 3410, 3), Glide, 1, Seqs.HURDLE))),
                    Obstacle("loc.rooftops_varrock_finish", 143.7,
                        listOf(Step(abs(3236, 3416, 2), Glide, 1, Seqs.SPOT_JUMP), leap(3236, 3417, 0, 1))),
                ),
        )

    /** Canifis. Wiki: needs Priest in Peril. Gap 3 success: 69% @1 -> 118% @99 (Mod Ash). */
    val canifis =
        Course(
            name = "Canifis Rooftop Course",
            level = 40,
            markChance = 2.0 / 3.0,
            markFalloff = false,
            quest = "quest_priestinperil",
            questName = "Priest in Peril",
            markSpawns = listOf(t(3508, 3494, 2), t(3502, 3506, 2), t(3499, 3505, 2), t(3489, 3500, 2),
                t(3492, 3499, 2), t(3476, 3496, 3), t(3475, 3493, 3), t(3482, 3486, 2), t(3478, 3484, 2),
                t(3493, 3476, 3), t(3495, 3472, 3), t(3491, 3472, 3), t(3513, 3479, 2), t(3512, 3481, 2),
                t(3510, 3476, 2)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_canifis_start_tree", 10.0,
                        listOf(Step(abs(3506, 3492, 2), Jump, 3, Seqs.TREE_CLIMB)), approach = t(3507, 3488, 0)),
                    Obstacle("loc.rooftops_canifis_jump", 8.0, listOf(leap(3502, 3504, 2, 2))),
                    Obstacle("loc.rooftops_canifis_jump_2", 8.0, listOf(leap(3493, 3504, 2, 2)),
                        approach = t(3498, 3504, 2)),
                    Obstacle("loc.rooftops_canifis_jump_5", 10.0,
                        listOf(Step(abs(3481, 3499, 2), Glide, 1, Seqs.WALL_JUMP),
                            Step(abs(3479, 3499, 3), Jump, 1, Seqs.WALL_JUMP2)),
                        approach = t(3487, 3499, 2),
                        fail = Fail(1, 0.69, 99, 1.18, t(3483, 3497, 0))),
                    Obstacle("loc.rooftops_canifis_jump_3", 8.0, listOf(leap(3478, 3486, 2, 2))),
                    Obstacle("loc.rooftops_canifis_polevault", 10.0,
                        listOf(Step(abs(3489, 3476, 3), Jump, 6, Seqs.POLE_VAULT))),
                    Obstacle("loc.rooftops_canifis_jump_4", 11.0, listOf(leap(3510, 3476, 2, 2)),
                        approach = t(3502, 3476, 3)),
                    Obstacle("loc.rooftops_canifis_leapdown", 175.0, listOf(leap(3510, 3485, 0, 2))),
                ),
        )

    val falador =
        Course(
            name = "Falador Rooftop Course",
            level = 50,
            markSpawns = listOf(t(3038, 3343, 3), t(3049, 3348, 3), t(3049, 3357, 3), t(3045, 3365, 3),
                t(3035, 3362, 3), t(3028, 3353, 3), t(3017, 3345, 3), t(3011, 3339, 3), t(3016, 3333, 3)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_falador_wallclimb", 11.0, listOf(climb(3036, 3342, 3))),
                    Obstacle("loc.rooftops_falador_tightrope_1", 22.0, listOf(walk(3047, 3343, 3, 7))),
                    Obstacle("loc.rooftops_falador_handholds_start", 61.0,
                        listOf(Step(abs(3050, 3351, 2), Jump, 1, Seqs.WALL_JUMP),
                            Step(abs(3051, 3355, 2), Glide, 7, Seqs.HANDHOLDS),
                            Step(abs(3050, 3357, 3), Jump, 2, Seqs.HANDHOLDS_LAST)),
                        fail = fail(50, 66, 3050, 3354)),
                    Obstacle("loc.rooftops_falador_gap_1", 27.0,
                        listOf(Step(abs(3048, 3361, 3), Glide, 1, Seqs.SPOT_JUMP)), approach = t(3048, 3358, 3)),
                    Obstacle("loc.rooftops_falador_gap_2", 26.0,
                        listOf(Step(abs(3041, 3361, 3), Glide, 1, Seqs.SPOT_JUMP)), approach = t(3045, 3361, 3)),
                    Obstacle("loc.rooftops_falador_tightrope_2", 61.0,
                        listOf(walk(3033, 3361, 3, 8), walk(3028, 3356, 3, 5), walk(3028, 3354, 3, 2))),
                    Obstacle("loc.rooftops_falador_tightrope_3", 53.0,
                        listOf(walk(3026, 3353, 3, 2, null), walk(3021, 3353, 3, 3, Seqs.LOG_BALANCE_LOOP),
                            walk(3020, 3353, 3, 1, null))),
                    Obstacle("loc.rooftops_falador_gap_3", 30.0,
                        listOf(Step(Dest.Rel(0, -4), Glide, 1, Seqs.HURDLE)), approach = t(3016, 3353, 3)),
                    Obstacle("loc.rooftops_falador_ledge_1", 14.0, listOf(Step(Dest.Rel(-2, 0), Glide, 1, Seqs.HURDLE))),
                    Obstacle("loc.rooftops_falador_ledge_2", 13.0, listOf(Step(Dest.Rel(0, -2), Glide, 1, Seqs.HURDLE))),
                    Obstacle("loc.rooftops_falador_ledge_3a", 13.0, listOf(Step(Dest.Rel(0, -2), Glide, 1, Seqs.HURDLE))),
                    Obstacle("loc.rooftops_falador_ledge_4", 14.0, listOf(Step(Dest.Rel(2, 0), Glide, 1, Seqs.HURDLE))),
                    Obstacle("loc.rooftops_falador_edge", 241.0,
                        listOf(Step(Dest.Rel(3, 0), Glide, 1, Seqs.HURDLE), leap(3029, 3333, 0, 2))),
                ),
        )

    val seers =
        Course(
            name = "Seers' Village Rooftop Course",
            level = 60,
            markSpawns = listOf(t(2726, 3492, 3), t(2728, 3495, 3), t(2707, 3493, 2), t(2708, 3489, 2),
                t(2712, 3481, 2), t(2710, 3478, 2), t(2710, 3472, 3), t(2702, 3474, 3), t(2698, 3462, 2)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_seers_wallclimb", 45.0,
                        listOf(Step(abs(2729, 3488, 1), Jump, 1, Seqs.CLIMBING), Step(abs(2729, 3491, 3), Jump, 1, Seqs.HANDHOLDS))),
                    Obstacle("loc.rooftops_seers_jump", 20.0, listOf(leap(2719, 3495, 2, 1), leap(2713, 3494, 2, 2)),
                        fail = fail(60, 79, 2716, 3494)),
                    Obstacle("loc.rooftops_seers_tightrope", 20.0, listOf(walk(2710, 3481, 2, 8)),
                        fail = fail(60, 79, 2710, 3485)),
                    Obstacle("loc.rooftops_seers_jump_1", 35.0,
                        listOf(Step(Dest.KeepX(3474, 3), Glide, 1, Seqs.WALL_JUMP),
                            Step(Dest.KeepX(3472, 3), Glide, 1, Seqs.WALL_JUMP2))),
                    Obstacle("loc.rooftops_seers_jump_2", 15.0, listOf(leap(2702, 3465, 2, 2))),
                    Obstacle("loc.rooftops_seers_leapdown", 435.0, listOf(leap(2704, 3464, 0, 2))),
                ),
        )

    val rellekka =
        Course(
            name = "Rellekka Rooftop Course",
            level = 80,
            markSpawns = listOf(t(2622, 3676, 3), t(2617, 3664, 3), t(2618, 3660, 3), t(2628, 3652, 3),
                t(2628, 3655, 3), t(2641, 3649, 3), t(2643, 3651, 3), t(2649, 3659, 3), t(2644, 3662, 3),
                t(2658, 3674, 3), t(2656, 3681, 3)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_rellekka_wallclimb", 20.0, listOf(climb(2626, 3676, 3))),
                    Obstacle("loc.rooftops_rellekka_gap_1", 30.0, listOf(Step(Dest.Rel(-1, -4), Glide, 2, Seqs.HURDLE))),
                    Obstacle("loc.rooftops_rellekka_tightrope_1", 40.0,
                        listOf(walk(2626, 3654, 3, 6), walk(2627, 3654, 3, 1, null)),
                        fail = fail(80, 85, 2626, 3656)),
                    Obstacle("loc.rooftops_rellekka_gap_2", 85.0,
                        listOf(walk(2635, 3658, 3, 6, Seqs.SIDESTEP), walk(2639, 3653, 3, 6, Seqs.SIDESTEP))),
                    Obstacle("loc.rooftops_rellekka_gap_3", 25.0, listOf(Step(Dest.Rel(0, 4), Glide, 2, Seqs.HURDLE))),
                    Obstacle("loc.rooftops_rellekka_tightrope_3", 105.0,
                        listOf(walk(2647, 3663, 3, 2, null), walk(2654, 3670, 3, 7), walk(2655, 3670, 3, 1, null))),
                    // Kronos lands on 2653,3676 then steps west; that tile holds a solid loc in
                    // this cache, so land straight on 2652,3676.
                    Obstacle("loc.rooftops_rellekka_dropoff", 475.0, listOf(leap(2652, 3676, 0, 2))),
                ),
        )

    val ardougne =
        Course(
            name = "Ardougne Rooftop Course",
            level = 90,
            markSpawns = listOf(t(2671, 3304, 3), t(2663, 3318, 3), t(2654, 3318, 3), t(2653, 3313, 3), t(2653, 3302, 3)),
            obstacles =
                listOf(
                    Obstacle("loc.rooftops_ardy_wallclimb", 43.0,
                        listOf(Step(abs(2673, 3298, 1), Jump, 1, Seqs.CLIMBING), Step(abs(2673, 3298, 2), Jump, 1, Seqs.CLIMBING),
                            Step(abs(2671, 3299, 3), Jump, 1, Seqs.CLIMBING))),
                    Obstacle("loc.rooftops_ardy_jump", 65.0,
                        listOf(leap(2667, 3311, 1, 1), leap(2665, 3315, 1, 1), Step(abs(2665, 3318, 3), Jump, 1, Seqs.WALL_JUMP)),
                        fail = fail(90, 95, 2667, 3313)),
                    Obstacle("loc.rooftops_ardy_plank", 50.0, listOf(walk(2656, 3318, 3, 5, Seqs.LOG_BALANCE))),
                    Obstacle("loc.rooftops_ardy_jump_2", 21.0, listOf(leap(2653, 3314, 3, 2)),
                        fail = fail(90, 95, 2653, 3316)),
                    Obstacle("loc.rooftops_ardy_jump_3", 28.0,
                        listOf(Step(abs(2651, 3309, 3), Jump, 2, Seqs.JUMP_DOWN_NOREACH))),
                    Obstacle("loc.rooftops_ardy_wallcrossing", 57.0,
                        listOf(walk(2654, 3299, 3, 3, Seqs.SIDESTEP_IN), walk(2656, 3297, 3, 2, Seqs.SIDESTEP_OUT))),
                    Obstacle("loc.rooftops_ardy_jump_4", 625.0,
                        listOf(leap(2658, 3298, 1, 2), walk(2661, 3298, 1, 3, null),
                            Step(abs(2663, 3297, 1), Glide, 1, Seqs.SPOT_JUMP), walk(2666, 3297, 1, 3, null),
                            Step(abs(2667, 3297, 1), Glide, 1, Seqs.SPOT_JUMP), leap(2668, 3297, 0, 2))),
                ),
        )

    val all = listOf(gnome, barbarian, draynor, alKharid, varrock, canifis, falador, seers, rellekka, ardougne)
}

/** Animations, by the cache's gameval names (verified against this cache). */
object Seqs {
    const val CLIMB = "seq.human_reachforladder"
    const val CLIMBING = "seq.human_climbing"
    const val LOG_BALANCE = "seq.human_walk_logbalance"
    const val LOG_BALANCE_LOOP = "seq.human_walk_logbalance_loop"
    const val BALANCE = "seq.human_walk_logbalance"
    const val PIPE_SQUEEZE = "seq.human_doublepipesqueeze"
    const val ROPE_SWING = "seq.human_ropeswing_long"
    const val SIDESTEP_IN = "seq.human_into_sidestepl"
    const val SIDESTEP_OUT = "seq.human_outof_sidestepl"
    const val SIDESTEP = "seq.human_walk_sidestep"
    const val WALL_JUMP = "seq.agility_shortcut_wall_jump"
    const val WALL_JUMP2 = "seq.agility_shortcut_wall_jump2"
    const val JUMP_DOWN = "seq.agility_shortcut_wall_jumpdown"
    const val JUMP_DOWN_NOREACH = "seq.agility_shortcut_wall_jumpdown_noreachforward"
    const val SPOT_JUMP = "seq.human_spot_jump"
    const val RUN = "seq.ic_running"
    const val HURDLE = "seq.human_jump_hurdle"
    const val LEAP = "seq.seaslug_ken_leap"
    const val HANDHOLDS = "seq.agilityarena_handholds_middle"
    const val HANDHOLDS_LAST = "seq.agilityarena_handholds_last"
    const val HANDHOLDS_REVERSE = "seq.agilityarena_handholds_middlereverse"
    const val ZIPLINE_BITE = "seq.zipline_bite"
    const val ZIPLINE_SLIDE = "seq.zipline_slide"
    const val POLE_VAULT = "seq.rooftops_pole_vault"
    const val TREE_CLIMB = "seq.mdaughter_tree_climb_combi"
    const val WALL_CLIMB_OVER = "seq.human_walk_style"
    const val TUNNEL_ENTER = "seq.agilty_shortcut_enter_hole"
    const val TUNNEL_WALK = "seq.agilty_shortcut_tunnel_walk"
    const val TUNNEL_EXIT = "seq.agilty_shortcut_exit_hole"
    const val FALL = "seq.human_wobbleandfall_l"
}
