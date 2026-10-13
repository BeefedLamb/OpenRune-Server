package org.rsmod.content.osrsport.bots

import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import jakarta.inject.Inject
import kotlin.math.abs
import kotlin.math.max
import kotlin.random.Random
import org.rsmod.api.invtx.invAdd
import org.rsmod.api.npc.isValidTarget
import org.rsmod.api.player.interact.HeldInteractions
import org.rsmod.api.player.interact.LocInteractions
import org.rsmod.api.player.interact.NpcInteractions
import org.rsmod.api.player.isInCombat
import org.rsmod.api.player.protect.ProtectedAccessLauncher
import org.rsmod.api.player.righthand
import org.rsmod.api.player.stat.baseHitpointsLvl
import org.rsmod.api.player.stat.hitpoints
import org.rsmod.api.registry.loc.LocRegistry
import org.rsmod.api.repo.npc.NpcRepository
import org.rsmod.content.osrsport.unitylink.HeadlessDialogue
import org.rsmod.game.entity.Npc
import org.rsmod.game.entity.Player
import org.rsmod.game.interact.InteractionNpcOp
import org.rsmod.game.interact.InteractionOp
import org.rsmod.game.loc.BoundLocInfo
import org.rsmod.game.movement.RouteRequestCoord
import org.rsmod.game.movement.RouteRequestLoc
import org.rsmod.map.CoordGrid
import org.rsmod.map.zone.ZoneKey

/**
 * Rule-based bot behaviour in the style of AzerothCore's playerbots: every think, a bot checks a
 * few triggers (a dialogue is open, inventory full, low health, a friend is fighting) and its
 * role's strategy picks one action. Bots act through the same interactions real players use, so
 * every skill and combat rule on the server applies to them unchanged.
 */
class BotBrain
@Inject
constructor(
    private val locRegistry: LocRegistry,
    private val npcRepo: NpcRepository,
    private val locInteractions: LocInteractions,
    private val npcInteractions: NpcInteractions,
    private val heldInteractions: HeldInteractions,
    private val protectedAccess: ProtectedAccessLauncher,
) {
    fun think(bot: Bot, player: Player, tick: Int, humans: List<Player>) {
        if (HeadlessDialogue.isOpen(player)) {
            HeadlessDialogue.resume(player, 1)
            return
        }
        if (player.isDelayed || player.isAccessProtected) {
            return
        }
        if (!bot.outfitted) {
            outfit(bot, player)
            return
        }
        when (bot.role) {
            BotRole.Wanderer -> wander(bot, player)
            BotRole.Woodcutter -> woodcut(bot, player, tick)
            BotRole.Fighter -> fight(bot, player, tick)
            BotRole.Companion -> accompany(bot, player, humans)
        }
    }

    private fun outfit(bot: Bot, player: Player) {
        bot.outfitted = true
        val tool = STARTER_TOOLS[bot.role] ?: return
        val hasTool = player.inv.any { it?.id == objId(tool) } || player.righthand?.id == objId(tool)
        if (!hasTool) {
            player.invAdd(player.inv, tool)
        }
        if (bot.role == BotRole.Fighter || bot.role == BotRole.Companion) {
            val slot = player.inv.indexOfFirst { it?.id == objId(tool) }
            if (slot >= 0) {
                protectedAccess.launch(player) { heldInteractions.equip(this, player.inv, slot) }
            }
        }
    }

    private fun wander(bot: Bot, player: Player) {
        if (player.isBusy2 || Random.nextInt(3) != 0) {
            return
        }
        val dx = Random.nextInt(-WANDER_RADIUS, WANDER_RADIUS + 1)
        val dz = Random.nextInt(-WANDER_RADIUS, WANDER_RADIUS + 1)
        walkTo(player, bot.home.translate(dx, dz))
    }

    private fun woodcut(bot: Bot, player: Player, tick: Int) {
        if (player.inv.isFull()) {
            val logSlot = player.inv.indexOfFirst { obj -> obj != null && itemName(obj.id).endsWith("logs", ignoreCase = true) }
            if (logSlot >= 0) {
                protectedAccess.launch(player) { heldInteractions.drop(this, player.inv, logSlot) }
            }
            return
        }
        if (player.isBusy2) {
            if (tick - bot.lastProgressTick > STUCK_TICKS) {
                player.clearInteraction()
                bot.lastProgressTick = tick
                bot.lastLoc?.let { bot.avoid[it] = tick + AVOID_TICKS }
            }
            return
        }
        if (tick < bot.cooldownUntil) {
            return
        }
        bot.avoid.entries.removeIf { it.value < tick }
        val tree =
            nearestLoc(player.coords, "Chop down") { coords, name ->
                coords !in bot.avoid && (name == "Tree" || name == "Oak tree" || name == "Dead tree")
            }
        if (tree == null) {
            wander(bot, player)
            return
        }
        bot.lastProgressTick = tick
        bot.cooldownUntil = tick + ACTION_COOLDOWN
        bot.lastLoc = tree.first.coords
        locInteractions.interact(player, tree.first, tree.second)
        val loc = tree.first
        player.routeRequest =
            RouteRequestLoc(
                destination = loc.coords,
                width = loc.width,
                length = loc.length,
                shape = loc.shapeId,
                angle = loc.angleId,
                forceApproachFlags = loc.forceApproachFlags,
            )
    }

    private fun fight(bot: Bot, player: Player, tick: Int) {
        val hpPercent = player.hitpoints * 100 / max(1, player.baseHitpointsLvl)
        if (hpPercent < RETREAT_HP_PERCENT) {
            bot.retreating = true
        }
        if (bot.retreating) {
            if (hpPercent >= RECOVERED_HP_PERCENT) {
                bot.retreating = false
            } else {
                if (player.interaction != null) {
                    player.clearInteraction()
                }
                if (!player.isBusy2 && chebyshev(player.coords, bot.home) > 3) {
                    walkTo(player, bot.home)
                }
                return
            }
        }
        if (player.isBusy2 || player.isInCombat()) {
            if (player.interaction == null && tick - bot.lastProgressTick > STUCK_TICKS) {
                bot.lastProgressTick = tick
            }
            return
        }
        val prey = nearestNpc(player) { npc -> npc.visType.combatLevel in 1..(player.combatLevel + 3) }
        if (prey == null) {
            wander(bot, player)
            return
        }
        bot.lastProgressTick = tick
        attack(player, prey)
    }

    private fun accompany(bot: Bot, player: Player, humans: List<Player>) {
        val leader = humans.minByOrNull { chebyshev(it.coords, player.coords) }
        if (leader == null || leader.coords.level != player.coords.level || chebyshev(leader.coords, player.coords) > FOLLOW_GIVE_UP) {
            wander(bot, player)
            return
        }
        bot.home = leader.coords
        val leaderTarget = (leader.interaction as? InteractionNpcOp)?.target
        if (leaderTarget != null && attackOp(leaderTarget) != null && leaderTarget.isValidTarget()) {
            val mine = (player.interaction as? InteractionNpcOp)?.target
            if (mine !== leaderTarget) {
                attack(player, leaderTarget)
            }
            return
        }
        if (player.isInCombat() && player.interaction != null) {
            return
        }
        if (chebyshev(leader.coords, player.coords) > FOLLOW_DISTANCE) {
            walkTo(player, leader.coords.translate(Random.nextInt(-1, 2), Random.nextInt(-1, 2)))
        }
    }

    private fun attack(player: Player, npc: Npc) {
        val op = attackOp(npc) ?: return
        npcInteractions.interact(player, npc, op)
    }

    private fun walkTo(player: Player, dest: CoordGrid) {
        player.routeRequest = RouteRequestCoord(dest)
    }

    private fun nearestNpc(player: Player, accept: (Npc) -> Boolean): Npc? =
        npcRepo
            .findAll(ZoneKey.from(player.coords), SEARCH_ZONE_RADIUS)
            .filter { it.coords.level == player.coords.level && it.isValidTarget() && attackOp(it) != null && accept(it) }
            .minByOrNull { chebyshev(it.coords, player.coords) }

    private fun nearestLoc(
        from: CoordGrid,
        opName: String,
        accept: (CoordGrid, String) -> Boolean,
    ): Pair<BoundLocInfo, InteractionOp>? {
        var best: Pair<BoundLocInfo, InteractionOp>? = null
        var bestDistance = Int.MAX_VALUE
        val centre = ZoneKey.from(from)
        for (dx in -SEARCH_ZONE_RADIUS..SEARCH_ZONE_RADIUS) {
            for (dz in -SEARCH_ZONE_RADIUS..SEARCH_ZONE_RADIUS) {
                for (loc in locRegistry.findAll(centre.translate(dx, dz))) {
                    if (loc.coords.level != from.level) {
                        continue
                    }
                    val type = ServerCacheManager.getObject(loc.id) ?: continue
                    if (!accept(loc.coords, type.name)) {
                        continue
                    }
                    val op = opNamed(opName) { slot -> type.actions.getOpOrNull(slot) } ?: continue
                    val distance = chebyshev(loc.coords, from)
                    if (distance < bestDistance) {
                        bestDistance = distance
                        best = BoundLocInfo(loc, type) to op
                    }
                }
            }
        }
        return best
    }

    private fun attackOp(npc: Npc): InteractionOp? = opNamed("Attack") { slot -> npc.visType.actions.getOpOrNull(slot) }

    private fun opNamed(name: String, opAt: (Int) -> String?): InteractionOp? {
        for (slot in 0 until 5) {
            if (opAt(slot).equals(name, ignoreCase = true)) {
                return INTERACTION_OPS[slot]
            }
        }
        return null
    }

    private fun itemName(id: Int): String = ServerCacheManager.getItem(id)?.name ?: ""

    private fun objId(internal: String): Int = internal.asRSCM(RSCMType.OBJ)

    companion object {
        const val WANDER_RADIUS = 8
        const val SEARCH_ZONE_RADIUS = 2
        const val STUCK_TICKS = 40
        const val AVOID_TICKS = 500

        /** Ticks to let a skilling script loop on its own before the bot re-issues the op. */
        const val ACTION_COOLDOWN = 8
        const val RETREAT_HP_PERCENT = 40
        const val RECOVERED_HP_PERCENT = 90
        const val FOLLOW_DISTANCE = 3
        const val FOLLOW_GIVE_UP = 40

        private val INTERACTION_OPS =
            arrayOf(InteractionOp.Op1, InteractionOp.Op2, InteractionOp.Op3, InteractionOp.Op4, InteractionOp.Op5)

        private val STARTER_TOOLS =
            mapOf(
                BotRole.Woodcutter to "obj.bronze_axe",
                BotRole.Fighter to "obj.bronze_sword",
                BotRole.Companion to "obj.iron_sword",
            )

        fun chebyshev(a: CoordGrid, b: CoordGrid): Int = max(abs(a.x - b.x), abs(a.z - b.z))
    }
}
