package org.rsmod.content.osrsport.unitylink

import com.github.michaelbull.logging.InlineLogger
import dev.or2.central.account.Rights
import jakarta.inject.Inject
import org.rsmod.api.account.AccountManager
import org.rsmod.api.account.loader.request.AccountLoadAuth
import org.rsmod.api.account.loader.request.AccountLoadResponse
import org.rsmod.api.account.loader.request.isNewAccount
import org.rsmod.api.player.vars.boolVarBit
import org.rsmod.api.realm.Realm
import org.rsmod.api.registry.account.AccountRegistry
import org.rsmod.api.registry.player.PlayerRegistry
import org.rsmod.api.registry.player.PlayerRegistryResult
import org.rsmod.events.EventBus
import org.rsmod.game.client.Client
import org.rsmod.game.entity.Player
import org.rsmod.game.entity.player.SessionStateEvent

/**
 * Logs a player in without the OSRS network protocol: the Unity client and server-side bots use
 * it. [failed] is called on any thread; [loggedIn] runs on the game thread once the player is in
 * the world.
 */
class HeadlessLogin
@Inject
constructor(
    private val accountManager: AccountManager,
    private val accountRegistry: AccountRegistry,
    private val playerRegistry: PlayerRegistry,
    private val eventBus: EventBus,
    private val realm: Realm,
) {
    fun login(
        name: String,
        client: Client<Any, Any>,
        admin: Boolean,
        failed: (String) -> Unit,
        loggedIn: (Player) -> Unit,
    ): Boolean =
        accountManager.loadOrCreate(AccountLoadAuth.UnknownDevice, name, { PASSWORD_HASH }) { response ->
            if (response !is AccountLoadResponse.Ok) {
                if (response is AccountLoadResponse.Err.Exception) {
                    logger.error(response.reason) { "Headless login: account load failed for '$name'" }
                } else {
                    logger.warn { "Headless login: account load failed for '$name': $response" }
                }
                failed("could not load account: ${response::class.simpleName}")
                return@loadOrCreate
            }
            val player = createPlayer(client, admin, response)
            accountRegistry.queueLogin(player, response) { p, _ -> finishLogin(p, failed, loggedIn) }
        }

    private fun createPlayer(client: Client<Any, Any>, admin: Boolean, response: AccountLoadResponse.Ok): Player {
        val player = Player(client = client)
        for (transform in response.transforms) {
            transform.apply(player)
        }
        player.ui.setWindowStatus(width = 765, height = 503, resizable = false)
        if (admin) {
            player.modLevel = Rights.ADMINISTRATOR
        }
        player.newAccount = response.isNewAccount()
        player.worldType = response.worldType

        val config = realm.config
        if (player.newAccount || response.firstVisitToWorldType) {
            player.coords = config.spawnCoord
            player.xpRate = config.baseXpRate
            if (response.firstVisitToWorldType) {
                player.newAccount = true
            }
            if (config.autoAssignDisplayNames && player.displayName.isBlank()) {
                player.displayName = player.username.replaceFirstChar { it.titlecase() }
            }
        }
        return player
    }

    private fun finishLogin(player: Player, failed: (String) -> Unit, loggedIn: (Player) -> Unit) {
        if (playerRegistry.isOnline(player.userId)) {
            failed("that account is already logged in")
            return
        }
        val slot = playerRegistry.nextFreeSlot()
        if (slot == null) {
            failed("server is full")
            return
        }
        eventBus.publish(SessionStateEvent.PrepareLogin(player))
        player.slotId = slot
        val result = playerRegistry.add(player)
        if (result != PlayerRegistryResult.Add.Success) {
            failed("could not register player: $result")
            logger.warn { "Headless login failed to register player: $result ($player)" }
            return
        }
        eventBus.publish(SessionStateEvent.Login(player))
        eventBus.publish(SessionStateEvent.EngineLogin(player))
        loggedIn(player)
    }

    private companion object {
        private val logger = InlineLogger()

        /** Headless accounts are local-only; this marks them and is never checked. */
        const val PASSWORD_HASH = "unity-link-local"

        private var Player.newAccount by boolVarBit("varbit.new_player_account")
    }
}
