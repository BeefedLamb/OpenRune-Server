package dev.openrune.tools

import dev.openrune.definition.codec.SequenceCodec
import dev.openrune.definition.type.SequenceType
import dev.openrune.filesystem.Cache
import java.io.File
import java.nio.file.Path

/**
 * Dumps every sequence (animation) definition for the Unity client, decoded with OpenRune's
 * rev-aware [SequenceCodec] (read-only on the cache). export_animations.py then decodes the
 * frame / framemap data these sequences reference and writes anims.bin.gz.
 *
 * Output: seqs.txt, tab-separated, one record per line:
 *  S id frameStep maxLoops forcedPriority priority replyMode precedenceAnimating skeletalId
 *    rangeBegin rangeEnd rightHand leftHand delays(csv) frameIds(csv) interleave(csv)
 *    sounds(csv of frame:synth:loops:location:retain)
 *
 * Usage: exportSequences [cacheDir] [outFile]
 */
fun main(args: Array<String>) {
    val cacheDir = Path.of(args.getOrElse(0) { ".data/cache/LIVE" })
    val out = File(args.getOrElse(1) { "osrs-port-tools/unity/Assets/StreamingAssets/world/seqs.txt" })
    out.parentFile.mkdirs()
    val cache = Cache.load(cacheDir)
    val codec = SequenceCodec(240)
    val lines = ArrayList<String>()
    var bad = 0
    var skeletal = 0
    var withSounds = 0
    for (id in cache.files(2, 12)) {
        val data = cache.data(2, 12, id, null) ?: continue
        val s = try { codec.loadData(id, data) } catch (e: Exception) { bad++; continue }
        if (s.skeletalId >= 0) skeletal++
        val sounds = frameSounds(s)
        if (sounds.isNotEmpty()) withSounds++
        lines += listOf(
            "S", id, s.frameStep, s.maxLoops, s.forcedPriority, s.priority, s.replyMode,
            s.precedenceAnimating, s.skeletalId, s.rangeBegin, s.rangeEnd, s.rightHandItem, s.leftHandItem,
            (s.frameDelays ?: emptyList()).joinToString(","),
            (s.frameIDs ?: emptyList()).joinToString(","),
            (s.interleaveLeave ?: emptyList()).joinToString(","),
            sounds,
        ).joinToString("\t")
    }
    out.writeText("# OSQ1\n" + lines.joinToString("\n") + "\n")
    cache.close()
    println("Wrote ${lines.size} sequences ($skeletal skeletal, $withSounds with frame sounds, $bad undecodable) to ${out.absolutePath}")
}

/** One entry per frame that has a sound: `frame:synth:loops:location:retain`. */
private fun frameSounds(s: SequenceType): String {
    val parts = ArrayList<String>()
    val seen = HashSet<Int>()
    s.soundEffects.forEachIndexed { frame, snd ->
        val sound = snd ?: return@forEachIndexed
        if (sound.id <= 0) return@forEachIndexed
        parts += "$frame:${sound.id}:${sound.loops}:${sound.location}:${sound.retain}"
        seen += frame
    }
    s.skeletalSounds.forEach { (frame, snd) ->
        if (frame in seen || snd.id <= 0) return@forEach
        parts += "$frame:${snd.id}:${snd.loops}:${snd.location}:${snd.retain}"
    }
    return parts.joinToString(",")
}
