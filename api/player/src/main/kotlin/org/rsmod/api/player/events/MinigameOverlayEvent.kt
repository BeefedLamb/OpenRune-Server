package org.rsmod.api.player.events

import org.rsmod.events.UnboundEvent
import org.rsmod.game.entity.Player

/**
 * Live minigame status for [player] (e.g. Wintertodt energy/points). Minigame scripts publish it
 * whenever their state changes; front-ends such as the Unity link render it as a HUD. An empty
 * [title] means "no minigame status" (hide the HUD).
 */
public data class MinigameOverlayEvent(
    public val player: Player,
    public val title: String,
    public val values: List<Pair<String, String>>,
) : UnboundEvent
