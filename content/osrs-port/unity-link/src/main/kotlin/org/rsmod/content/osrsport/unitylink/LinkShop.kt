package org.rsmod.content.osrsport.unitylink

import dev.openrune.ServerCacheManager
import dev.openrune.rscm.RSCM.asRSCM
import dev.openrune.rscm.RSCMType
import dev.openrune.types.aconverted.interf.IfButtonOp
import org.rsmod.annotations.InternalApi
import org.rsmod.api.player.protect.ProtectedAccessLauncher
import org.rsmod.api.player.ui.IfModalButton
import org.rsmod.api.player.ui.ifClose
import org.rsmod.api.shops.cost.StandardGpCostCalculations
import org.rsmod.events.EventBus
import org.rsmod.game.entity.Player
import org.rsmod.game.inv.Inventory

/**
 * Shop bridge for the Unity link, built like [LinkBank]. Shops are the server's real ones: a
 * shopkeeper's "Trade" op (or the "trade" dialogue choice) calls `Shops.open`, which opens
 * `interface.shopmain` and sets `player.openedShop`. Every buy/sell is replayed as the matching
 * shop interface button ([IfModalButton]) so ShopScript / StandardCurrencyShopOperations apply all
 * their rules (stock, prices, currency, inventory space, sale restrictions) exactly as for a normal
 * client.
 *
 * Server -> client: `SHOPOPEN` / `SHOPCLOSE` when the shop modal opens/closes, and while open
 * `SHOP currency|slot|id|count|name|price;...` (first field is the currency name, e.g. `coins`;
 * price is the current single-item buy price), re-sent on change.
 * Client -> server: `BUY slot id count` (shop slot), `SELL slot id count` (backpack slot), count
 * 1, 5, 10 or 50; `SHOPVALUE 0|1 slot id` (0 = shop stock "Value", 1 = backpack sell value,
 * answered by a normal `MSG`); `SHOPCLOSE`.
 */
class LinkShop(private val eventBus: EventBus, private val protectedAccess: ProtectedAccessLauncher) {
    fun isOpen(player: Player): Boolean = player.ui.containsModal(SHOP_MAIN) && player.openedShop != null

    /** Per-tick sync: open/close transitions and shop stock while open. */
    fun sync(connection: LinkConnection, player: Player) {
        val open = isOpen(player)
        if (open != connection.shopOpen) {
            connection.shopOpen = open
            connection.lastShopLine = ""
            connection.send(if (open) "SHOPOPEN" else "SHOPCLOSE")
        }
        if (!open) {
            return
        }
        val line = stockLine(player)
        if (line != connection.lastShopLine) {
            connection.lastShopLine = line
            connection.send(line)
        }
    }

    private fun stockLine(player: Player): String {
        val shop = player.openedShop ?: return "SHOP "
        val currency = shop.currency.substringAfter('.').replace('|', ' ').replace(';', ' ')
        val sb = StringBuilder("SHOP ").append(currency).append(';')
        val inv = shop.inv
        for (slot in 0 until inv.size) {
            val obj = inv[slot] ?: continue
            val type = ServerCacheManager.getItem(obj.id)
            val name = (type?.name ?: "null").replace('|', '/').replace(';', ',')
            val price =
                StandardGpCostCalculations.calculateShopSellSingleValue(
                    initialStock = inv.initialStock(obj.id),
                    currentStock = obj.count,
                    baseCost = type?.cost ?: 0,
                    sellPercentage = shop.sellPercentage,
                    changePercentage = shop.changePercentage,
                )
            sb.append(slot).append('|').append(obj.id).append('|').append(obj.count).append('|')
                .append(name).append('|').append(price).append(';')
        }
        return sb.toString()
    }

    fun buy(connection: LinkConnection, player: Player, slot: Int, id: Int, count: Int) {
        if (!ready(connection, player)) return
        val inv = player.openedShop?.inv ?: return
        if (slot !in 0 until inv.size || inv[slot]?.id != id) {
            connection.lastShopLine = "" // client is stale; resend
            return
        }
        val op = quantityOp(count) ?: return
        press(player, "component.shopmain:items", slot + 1, op, id)
    }

    fun sell(connection: LinkConnection, player: Player, slot: Int, id: Int, count: Int) {
        if (!ready(connection, player)) return
        val inv = player.inv
        if (slot !in 0 until inv.size || inv[slot]?.id != id) {
            connection.lastInvLine = ""
            return
        }
        val op = quantityOp(count) ?: return
        press(player, "component.shopside:items", slot, op, id)
    }

    fun value(connection: LinkConnection, player: Player, backpack: Boolean, slot: Int, id: Int) {
        if (!ready(connection, player)) return
        if (backpack) {
            if (player.inv[slot]?.id != id) return
            press(player, "component.shopside:items", slot, IfButtonOp.Op1, id)
        } else {
            if (player.openedShop?.inv?.get(slot)?.id != id) return
            press(player, "component.shopmain:items", slot + 1, IfButtonOp.Op1, id)
        }
    }

    fun close(player: Player) {
        if (player.ui.containsModal(SHOP_MAIN)) {
            player.ifClose(eventBus)
        }
    }

    private fun quantityOp(count: Int): IfButtonOp? =
        when (count) {
            1 -> IfButtonOp.Op2
            5 -> IfButtonOp.Op3
            10 -> IfButtonOp.Op4
            50 -> IfButtonOp.Op5
            else -> null
        }

    @OptIn(InternalApi::class)
    private fun ready(connection: LinkConnection, player: Player): Boolean {
        if (!isOpen(player)) {
            connection.send("SHOPCLOSE")
            connection.shopOpen = false
            return false
        }
        return !player.isModalButtonProtected
    }

    /** Fires the shop interface button the way If3ButtonHandler does for a real client click. */
    @OptIn(InternalApi::class)
    private fun press(player: Player, component: String, comsub: Int, op: IfButtonOp, obj: Int) {
        val type = ServerCacheManager.fromComponent(component.asRSCM(RSCMType.COMPONENT))
        val event = IfModalButton(type, comsub, ServerCacheManager.getItem(obj), op)
        protectedAccess.launchLenient(player) { eventBus.publish(this, event) }
    }

    private fun Inventory.initialStock(obj: Int): Int {
        val stock = type.stock ?: return 0
        for (entry in stock) {
            if (entry.obj == obj) return entry.count
        }
        return 0
    }

    companion object {
        const val SHOP_MAIN = "interface.shopmain"
    }
}