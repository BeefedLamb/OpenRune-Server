package org.rsmod.content.osrsport.bots

import com.fasterxml.jackson.databind.ObjectMapper
import com.github.michaelbull.logging.InlineLogger
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.time.Duration
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicInteger

/**
 * Asks a local Ollama model (http://127.0.0.1:11434) what a bot says. Requests run on the HTTP
 * client's own threads; finished replies wait in [replies] until the game thread drains them, so
 * the tick never blocks. When Ollama isn't running, [ask] returns false and the caller falls
 * back to a canned line.
 */
class BotChatter(
    private val endpoint: String = System.getenv("OSRS_PORT_OLLAMA_URL") ?: "http://127.0.0.1:11434",
    private val model: String = System.getenv("OSRS_PORT_OLLAMA_MODEL") ?: "llama3.2:3b",
) {
    data class Reply(val botName: String, val text: String)

    val replies: ConcurrentLinkedQueue<Reply> = ConcurrentLinkedQueue()

    private val http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(2)).build()
    private val json = ObjectMapper()
    private val inFlight = AtomicInteger()

    @Volatile private var offlineUntilMillis = 0L

    fun ask(botName: String, persona: String, heard: String, speaker: String): Boolean {
        if (System.currentTimeMillis() < offlineUntilMillis || inFlight.get() >= MAX_IN_FLIGHT) {
            return false
        }
        val prompt =
            "You are $botName, $persona in the medieval fantasy world of Gielinor (Old School " +
                "RuneScape). $speaker says to you: \"$heard\". Answer in character in one short " +
                "sentence of at most 20 words, plain text, no quotes, no emotes."
        val body =
            json.writeValueAsString(
                mapOf(
                    "model" to model,
                    "prompt" to prompt,
                    "stream" to false,
                    "options" to mapOf("num_predict" to 60, "temperature" to 0.8),
                )
            )
        val request =
            HttpRequest.newBuilder(URI.create("$endpoint/api/generate"))
                .timeout(Duration.ofSeconds(REQUEST_TIMEOUT_SECONDS))
                .header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(body))
                .build()
        inFlight.incrementAndGet()
        http.sendAsync(request, HttpResponse.BodyHandlers.ofString()).whenComplete { response, error ->
            inFlight.decrementAndGet()
            if (error != null || response == null || response.statusCode() != 200) {
                offlineUntilMillis = System.currentTimeMillis() + OFFLINE_BACKOFF_MILLIS
                logger.debug { "Ollama unavailable: ${error?.message ?: response?.statusCode()}" }
                return@whenComplete
            }
            val text = runCatching { json.readTree(response.body()).path("response").asText() }.getOrNull()
            val line = text?.let(::clean)
            if (!line.isNullOrBlank()) {
                replies.add(Reply(botName, line))
            }
        }
        return true
    }

    private fun clean(raw: String): String =
        raw.replace(Regex("\\s+"), " ").trim().trim('"').take(MAX_LINE_LENGTH)

    private companion object {
        private val logger = InlineLogger()

        const val MAX_IN_FLIGHT = 2
        const val REQUEST_TIMEOUT_SECONDS = 30L
        const val OFFLINE_BACKOFF_MILLIS = 60_000L
        const val MAX_LINE_LENGTH = 80
    }
}
