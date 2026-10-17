package org.rsmod.api.player.events

import org.rsmod.events.UnboundEvent
import org.rsmod.game.entity.Player

/**
 * The farming patches near [player] and the loc variant each one currently shows (crop + growth
 * stage). Published by the Farming script whenever it syncs patch state; front-ends such as the
 * Unity link render [FarmingPatchView.variantLocId] in place of the patch's base multiloc.
 */
public data class FarmingPatchEvent(
    public val player: Player,
    public val patches: List<FarmingPatchView>,
) : UnboundEvent

/**
 * One patch: [baseLocId] is the map's multiloc (the id ops must target), [variantLocId] the
 * morphed loc for transmit-varbit [value]. [x]/[z]/[level] is the loc's south-west tile.
 */
public data class FarmingPatchView(
    public val baseLocId: Int,
    public val variantLocId: Int,
    public val value: Int,
    public val x: Int,
    public val z: Int,
    public val level: Int,
    public val shape: Int,
    public val angle: Int,
)
