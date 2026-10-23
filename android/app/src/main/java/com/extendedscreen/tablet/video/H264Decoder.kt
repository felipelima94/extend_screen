package com.extendedscreen.tablet.video

import android.media.MediaCodec
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface
import java.nio.ByteBuffer

/**
 * Hardware H.264 decoder rendering straight to a Surface. Frames are rendered as soon as they are
 * decoded (no presentation-time scheduling) to keep latency minimal.
 */
class H264Decoder(
    private val surface: Surface,
    private val onError: () -> Unit,
    private val onKeyframeNeeded: () -> Unit,
    private val onFrameRendered: () -> Unit,
) {
    private var codec: MediaCodec? = null
    private var outputThread: Thread? = null
    @Volatile private var running = false
    private var waitingForKeyframe = true

    val isConfigured get() = codec != null

    /** (Re)creates the codec for the given SPS/PPS (Annex-B). */
    @Synchronized
    fun configure(spsPps: ByteArray, width: Int, height: Int) {
        release()
        val (sps, pps) = splitParameterSets(spsPps)
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setByteBuffer("csd-0", ByteBuffer.wrap(sps))
            setByteBuffer("csd-1", ByteBuffer.wrap(pps))
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            setInteger(MediaFormat.KEY_PRIORITY, 0) // realtime
            setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, width * height)
        }
        val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        try {
            c.configure(format, surface, null, 0)
        } catch (e: Exception) {
            // Some vendors reject the tuning keys; retry with the bare format.
            Log.w(TAG, "configure with low-latency keys failed, retrying", e)
            format.setInteger(MediaFormat.KEY_PRIORITY, 1)
            format.setInteger(MediaFormat.KEY_OPERATING_RATE, 60)
            c.reset()
            c.configure(format, surface, null, 0)
        }
        c.start()
        codec = c
        waitingForKeyframe = true
        running = true
        outputThread = Thread({ drainOutput(c) }, "decoder-output").also { it.start() }
        Log.i(TAG, "decoder ${c.name} ${width}x$height")
    }

    /** Queues one access unit. Delta frames are skipped until the first keyframe after (re)configure. */
    fun decode(data: ByteArray, offset: Int, length: Int, ptsUs: Long, keyframe: Boolean) {
        val c = synchronized(this) { codec } ?: return
        if (waitingForKeyframe && !keyframe) return
        waitingForKeyframe = false
        try {
            val index = c.dequeueInputBuffer(50_000)
            if (index < 0) {
                // Decoder is backed up. Dropping breaks the reference chain, so resync on the next keyframe.
                waitingForKeyframe = true
                onKeyframeNeeded()
                return
            }
            val buffer = c.getInputBuffer(index)!!
            buffer.clear()
            buffer.put(data, offset, length)
            c.queueInputBuffer(index, 0, length, ptsUs, 0)
        } catch (e: Exception) {
            Log.e(TAG, "decode failed", e)
            onError()
        }
    }

    private fun drainOutput(c: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        while (running) {
            try {
                val index = c.dequeueOutputBuffer(info, 20_000)
                if (index >= 0) {
                    c.releaseOutputBuffer(index, true)
                    onFrameRendered()
                }
            } catch (e: IllegalStateException) {
                if (running) {
                    Log.e(TAG, "output failed", e)
                    onError()
                }
                return
            }
        }
    }

    @Synchronized
    fun release() {
        running = false
        val c = codec ?: return
        codec = null
        try {
            outputThread?.join(500)
        } catch (_: InterruptedException) {
        }
        try {
            c.stop()
        } catch (_: Exception) {
        }
        c.release()
    }

    companion object {
        private const val TAG = "H264Decoder"

        /** True when the device has a decoder that advertises the low-latency feature. */
        fun supportsLowLatency(): Boolean {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return false
            return MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { info ->
                !info.isEncoder && info.supportedTypes.contains(MediaFormat.MIMETYPE_VIDEO_AVC) &&
                    info.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC)
                        .isFeatureSupported(android.media.MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)
            }
        }

        /** Splits an Annex-B blob into (SPS, PPS), each with its 4-byte start code. */
        fun splitParameterSets(data: ByteArray): Pair<ByteArray, ByteArray> {
            var sps: ByteArray? = null
            var pps: ByteArray? = null
            for ((start, end) in nalUnits(data)) {
                if (end <= start) continue
                val nal = byteArrayOf(0, 0, 0, 1) + data.copyOfRange(start, end)
                when (data[start].toInt() and 0x1F) {
                    7 -> sps = nal
                    8 -> pps = nal
                }
            }
            return Pair(sps ?: error("SPS ausente"), pps ?: error("PPS ausente"))
        }

        /** (start, end) of each NAL payload, start codes excluded. */
        private fun nalUnits(d: ByteArray): List<Pair<Int, Int>> {
            val result = mutableListOf<Pair<Int, Int>>()
            var start = -1
            var i = 0
            while (i + 2 < d.size) {
                if (d[i].toInt() == 0 && d[i + 1].toInt() == 0 && d[i + 2].toInt() == 1) {
                    val end = if (i > 0 && d[i - 1].toInt() == 0) i - 1 else i
                    if (start >= 0) result += start to end
                    i += 3
                    start = i
                } else {
                    i++
                }
            }
            if (start in 0 until d.size) result += start to d.size
            return result
        }
    }
}
