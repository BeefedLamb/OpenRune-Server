package org.rsmod.content.osrsport.bots

import org.rsmod.game.entity.Npc
import org.rsmod.game.entity.Player
import org.rsmod.map.CoordGrid

/**
 * What a bot spends its day doing. Each role is a strategy in [BotBrain]: it looks at the bot's
 * situation (triggers) and picks one action per think.
 */
enum class BotRole(val label: String) {
    Wanderer("townsperson"),
    Woodcutter("woodcutter"),
    Fighter("adventurer"),
    Companion("companion"),
}

class Bot(val name: String, var role: BotRole, var home: CoordGrid) {
    var player: Player? = null
    val client = BotClient()
    var loggingIn = false
    var nextThinkTick = 0
    var target: Npc? = null
    var lastProgressTick = 0
    var lastSpokeTick = -1000
    var retreating = false
    var outfitted = false
    var lastLoc: CoordGrid? = null
    var cooldownUntil = 0
    var progressCount = 0
    val avoid = HashMap<CoordGrid, Int>()

    val online: Boolean
        get() = player?.let { !it.loggingOut } ?: false
}
