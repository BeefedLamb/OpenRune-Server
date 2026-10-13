package org.rsmod.content.osrsport.bots

import com.github.michaelbull.logging.InlineLogger
import jakarta.inject.Inject
import kotlin.random.Random
import org.rsmod.api.game.process.GameLifecycle
import org.rsmod.api.player.output.mes
import org.rsmod.api.realm.Realm
import org.rsmod.api.script.onCommand
import org.rsmod.api.script.onEvent
import org.rsmod.content.osrsport.unitylink.HeadlessChat
import org.rsmod.content.osrsport.unitylink.HeadlessLogin
import org.rsmod.content.osrsport.unitylink.LinkPlayerSaid
import org.rsmod.events.EventBus
import org.rsmod.game.client.NoopClient
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.PlayerList
import org.rsmod.game.entity.util.PathingEntityCommon
import org.rsmod.map.CoordGrid
import org.rsmod.plugin.scripts.PluginScript
import org.rsmod.plugin.scripts.ScriptContext
import org.rsmod.routefinder.collision.CollisionFlagMap

/**
 * Populates the world with server-side bots: players with no network connection, logged in
 * through [HeadlessLogin] and driven by [BotBrain] on the game tick. Bots near a real player
 * think every few ticks; far-away bots think rarely. Talking near a bot gets an answer from a
 * local Ollama model when one is running, otherwise a canned line.
 *
 * Commands: `::bots` lists them, `::bot <name> <role>` adds one where you stand
 * (wanderer, woodcutter, fighter, companion), `::botsoff` logs them all out.
 */
class BotScript
@Inject
constructor(
    private val headlessLogin: HeadlessLogin,
    private val brain: BotBrain,
    private val playerList: PlayerList,
    private val realm: Realm,
    private val eventBus: EventBus,
    private val collision: CollisionFlagMap,
) : PluginScript() {
    private val bots = LinkedHashMap<String, Bot>()
    private val chatter = BotChatter()
    private var tick = 0

    override fun ScriptContext.startup() {
        onEvent<GameLifecycle.StartCycle> { cycle() }
        onEvent<LinkPlayerSaid> { answer(player, text) }

        onCommand("bots") {
            desc = "Lists the server-side bots."
            cheat {
                if (bots.isEmpty()) {
                    player.mes("There are no bots.")
                }
                for (bot in bots.values) {
                    val where = bot.player?.coords?.let { "${it.x}, ${it.z}" } ?: "offline"
                    player.mes("${bot.name} (${bot.role.label}): $where")
                }
            }
        }
        onCommand("bot") {
            desc = "Adds a bot where you stand: ::bot name role"
            invalidArgs = "Use as ::bot name wanderer|woodcutter|fighter|companion"
            cheat {
                val name = args[0].replaceFirstChar { it.titlecase() }
                val role = BotRole.entries.first { it.name.equals(args[1], ignoreCase = true) }
                addBot(name, role, player.coords)
                player.mes("$name the ${role.label} is on the way.")
            }
        }
        onCommand("botsoff") {
            desc = "Logs every bot out."
            cheat {
                for (bot in bots.values) {
                    bot.player?.forceDisconnect = true
                }
                bots.clear()
                player.mes("All bots logged out.")
            }
        }
    }

    private fun cycle() {
        tick++
        if (tick == SPAWN_DEFAULTS_AT_TICK) {
            spawnDefaults()
        }
        drainReplies()
        val humans = playerList.filter { p -> !p.loggingOut && bots.values.none { it.player === p } }
        for (bot in bots.values) {
            val player = bot.player ?: continue
            if (player.loggingOut) {
                bot.player = null
                continue
            }
            if (tick < bot.nextThinkTick) {
                continue
            }
            val nearHuman = humans.any { BotBrain.chebyshev(it.coords, player.coords) <= NEAR_RANGE }
            bot.nextThinkTick = tick + if (nearHuman) THINK_NEAR else THINK_FAR + Random.nextInt(THINK_FAR)
            try {
                brain.think(bot, player, tick, humans)
            } catch (e: Exception) {
                logger.error(e) { "Bot ${bot.name} failed to think" }
            }
            if (nearHuman && tick - bot.lastSpokeTick > IDLE_CHAT_TICKS && Random.nextInt(IDLE_CHAT_CHANCE) == 0) {
                say(bot, player, IDLE_LINES.getValue(bot.role).random())
            }
        }
    }

    private fun spawnDefaults() {
        val centre = realm.config.spawnCoord
        for ((name, role) in DEFAULT_ROSTER) {
            val home = centre.translate(Random.nextInt(-HOME_SPREAD, HOME_SPREAD + 1), Random.nextInt(-HOME_SPREAD, HOME_SPREAD + 1))
            addBot(name, role, home)
        }
    }

    private fun addBot(name: String, role: BotRole, home: CoordGrid) {
        val existing = bots[name.lowercase()]
        if (existing != null) {
            existing.role = role
            existing.home = home
            existing.outfitted = false
            if (existing.online || existing.loggingIn) {
                return
            }
        }
        val bot = existing ?: Bot(name, role, home).also { bots[name.lowercase()] = it }
        bot.loggingIn = true
        val queued =
            headlessLogin.login(
                name = name,
                client = NoopClient,
                admin = false,
                failed = { reason ->
                    bot.loggingIn = false
                    logger.warn { "Bot $name could not log in: $reason" }
                },
                loggedIn = { player ->
                    bot.loggingIn = false
                    bot.player = player
                    if (BotBrain.chebyshev(player.coords, home) > HOME_LEASH || player.coords.level != home.level) {
                        PathingEntityCommon.telejump(player, collision, home)
                    }
                },
            )
        if (!queued) {
            bot.loggingIn = false
        }
    }

    private fun answer(speaker: Player, text: String) {
        val listeners =
            bots.values
                .filter { bot -> bot.player?.let { it.coords.level == speaker.coords.level && BotBrain.chebyshev(it.coords, speaker.coords) <= HEAR_RANGE } == true }
                .sortedBy { BotBrain.chebyshev(it.player!!.coords, speaker.coords) }
        val named = listeners.firstOrNull { text.contains(it.name, ignoreCase = true) }
        val bot = named ?: listeners.firstOrNull() ?: return
        val player = bot.player ?: return
        bot.lastSpokeTick = tick
        val asked = chatter.ask(bot.name, "a ${bot.role.label} near Lumbridge", text, speaker.displayName)
        if (!asked) {
            say(bot, player, REPLY_LINES.random())
        }
    }

    private fun drainReplies() {
        while (true) {
            val reply = chatter.replies.poll() ?: break
            val bot = bots[reply.botName.lowercase()] ?: continue
            val player = bot.player ?: continue
            say(bot, player, reply.text)
        }
    }

    private fun say(bot: Bot, player: Player, text: String) {
        bot.lastSpokeTick = tick
        player.say(text)
        eventBus.publish(HeadlessChat(player, text))
    }

    private companion object {
        private val logger = InlineLogger()

        const val SPAWN_DEFAULTS_AT_TICK = 20
        const val THINK_NEAR = 3
        const val THINK_FAR = 15
        const val NEAR_RANGE = 48
        const val HEAR_RANGE = 10
        const val HOME_SPREAD = 10
        const val HOME_LEASH = 30
        const val IDLE_CHAT_TICKS = 150
        const val IDLE_CHAT_CHANCE = 60

        val DEFAULT_ROSTER =
            listOf(
                "Aldric" to BotRole.Wanderer,
                "Brina" to BotRole.Woodcutter,
                "Corwin" to BotRole.Fighter,
                "Dessa" to BotRole.Woodcutter,
                "Edric" to BotRole.Fighter,
                "Fenna" to BotRole.Wanderer,
                "Garrick" to BotRole.Companion,
            )

        val IDLE_LINES =
            mapOf(
                BotRole.Wanderer to listOf("Lovely day in Lumbridge.", "Anyone seen the Duke today?", "Mind the goblins east of the river."),
                BotRole.Woodcutter to listOf("Timber!", "These axes dull so fast.", "Almost a full load of logs."),
                BotRole.Fighter to listOf("Goblins again...", "Need a better sword.", "Who wants to train combat?"),
                BotRole.Companion to listOf("Right behind you.", "Where are we headed?", "I've got your back."),
            )

        val REPLY_LINES =
            listOf("Hm? Oh, hello there.", "Can't talk long, busy day.", "Aye, I hear you.", "Ask the Lumbridge guide, they know more.")
    }
}
