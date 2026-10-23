package com.extendedscreen.tablet.net

import android.util.Log
import com.extendedscreen.tablet.proto.Frame
import com.extendedscreen.tablet.proto.FrameReader
import com.extendedscreen.tablet.proto.FrameWriter
import com.extendedscreen.tablet.proto.Protocol
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Connects to the PC through `adb reverse` (localhost:27183 on the tablet is tunneled over USB)
 * and keeps reconnecting until stopped.
 */
class StreamClient(
    private val hello: () -> ByteArray,
    private val listener: Listener,
) {
    interface Listener {
        fun onConnected()
        fun onDisconnected()
        fun onConfig(config: Protocol.Config)
        fun onCodecConfig(spsPps: ByteArray)
        fun onVideoFrame(data: ByteArray, offset: Int, length: Int, ptsUs: Long, keyframe: Boolean)
    }

    private class Outgoing(val type: Byte, val payload: ByteArray)

    @Volatile private var running = false
    @Volatile private var socket: Socket? = null
    private var thread: Thread? = null
    private val outgoing = LinkedBlockingQueue<Outgoing>(1024)

    fun start() {
        if (running) return
        running = true
        thread = Thread(::connectLoop, "stream-client").also { it.start() }
    }

    fun stop() {
        running = false
        closeSocket()
        thread?.interrupt()
        thread = null
    }

    /** Drops the current connection; the loop reconnects and sends a fresh HELLO. */
    fun reconnect() = closeSocket()

    /** Non-blocking; safe from the UI thread. Dropped when not connected. */
    fun send(type: Byte, payload: ByteArray) {
        if (socket != null) outgoing.offer(Outgoing(type, payload))
    }

    private fun connectLoop() {
        while (running) {
            try {
                Socket().use { s ->
                    s.tcpNoDelay = true
                    s.receiveBufferSize = 1 shl 20
                    s.connect(InetSocketAddress("127.0.0.1", Protocol.PORT), 1000)
                    session(s)
                }
            } catch (e: IOException) {
                Log.d(TAG, "connection ended: ${e.message}")
            } finally {
                if (socket != null) {
                    socket = null
                    listener.onDisconnected()
                }
            }
            if (running) {
                try {
                    Thread.sleep(1000)
                } catch (_: InterruptedException) {
                }
            }
        }
    }

    private fun session(s: Socket) {
        val writer = FrameWriter(s.getOutputStream().buffered(64 * 1024))
        val reader = FrameReader(s.getInputStream().buffered(256 * 1024))

        writer.write(Protocol.HELLO, hello())
        outgoing.clear()
        socket = s

        val sender = Thread({
            try {
                while (!s.isClosed) {
                    val msg = outgoing.poll(500, TimeUnit.MILLISECONDS) ?: continue
                    writer.write(msg.type, msg.payload)
                }
            } catch (_: Exception) {
                closeQuietly(s)
            }
        }, "stream-sender")
        sender.start()

        // With adb reverse the tunnel accepts even when the PC app is not running; the first read then hits EOF.
        var connectedReported = false
        try {
            while (running) {
                val frame = reader.read() ?: break
                if (!connectedReported) {
                    connectedReported = true
                    listener.onConnected()
                }
                dispatch(frame)
            }
        } finally {
            closeQuietly(s)
            sender.interrupt()
        }
    }

    private fun dispatch(frame: Frame) {
        when (frame.type) {
            Protocol.CONFIG -> listener.onConfig(Protocol.parseConfig(frame.payload))
            Protocol.VIDEO_CSD -> listener.onCodecConfig(frame.payload)
            Protocol.VIDEO_FRAME -> {
                val header = Protocol.le(Protocol.VIDEO_FRAME_HEADER).put(frame.payload, 0, Protocol.VIDEO_FRAME_HEADER)
                header.flip()
                val pts = header.long
                val key = (header.get().toInt() and 1) != 0
                listener.onVideoFrame(
                    frame.payload, Protocol.VIDEO_FRAME_HEADER,
                    frame.payload.size - Protocol.VIDEO_FRAME_HEADER, pts, key,
                )
            }
            Protocol.PING -> send(Protocol.PONG, frame.payload)
        }
    }

    private fun closeSocket() {
        socket?.let { closeQuietly(it) }
    }

    private fun closeQuietly(s: Socket) {
        try {
            s.close()
        } catch (_: IOException) {
        }
    }

    companion object {
        private const val TAG = "StreamClient"
    }
}
