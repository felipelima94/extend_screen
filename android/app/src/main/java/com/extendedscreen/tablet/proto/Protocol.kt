package com.extendedscreen.tablet.proto

import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Mirror of ExtendedScreen.Core.Protocol (see docs/protocol.md). Everything is little-endian. */
object Protocol {
    const val PORT = 27183
    const val VERSION: Short = 1
    const val HEADER_SIZE = 5
    const val MAX_PAYLOAD = 16 * 1024 * 1024

    const val HELLO: Byte = 1
    const val CONFIG: Byte = 2
    const val VIDEO_CSD: Byte = 3
    const val VIDEO_FRAME: Byte = 4
    const val REQUEST_KEYFRAME: Byte = 5
    const val POINTER: Byte = 6
    const val PING: Byte = 7
    const val PONG: Byte = 8

    const val TOOL_FINGER: Byte = 0
    const val TOOL_PEN: Byte = 1
    const val TOOL_ERASER: Byte = 2

    const val ACTION_DOWN: Byte = 0
    const val ACTION_MOVE: Byte = 1
    const val ACTION_UP: Byte = 2
    const val ACTION_HOVER: Byte = 3
    const val ACTION_HOVER_EXIT: Byte = 4
    const val ACTION_CANCEL: Byte = 5

    const val POINTER_SIZE = 27
    const val VIDEO_FRAME_HEADER = 9

    fun le(size: Int): ByteBuffer = ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN)

    fun hello(width: Int, height: Int, densityDpi: Int, lowLatency: Boolean, name: String): ByteArray {
        val nameBytes = name.toByteArray(Charsets.UTF_8)
        return le(17 + nameBytes.size)
            .putShort(VERSION)
            .putInt(width)
            .putInt(height)
            .putInt(densityDpi)
            .put(if (lowLatency) 1 else 0)
            .putShort(nameBytes.size.toShort())
            .put(nameBytes)
            .array()
    }

    fun pointer(
        tool: Byte, action: Byte, pointerId: Int, x: Float, y: Float,
        pressure: Float, tiltX: Float, tiltY: Float, buttons: Int,
    ): ByteArray = le(POINTER_SIZE)
        .put(tool)
        .put(action)
        .putInt(pointerId)
        .putFloat(x)
        .putFloat(y)
        .putFloat(pressure)
        .putFloat(tiltX)
        .putFloat(tiltY)
        .put(buttons.toByte())
        .array()

    data class Config(val width: Int, val height: Int, val fps: Int, val bitrate: Int)

    fun parseConfig(p: ByteArray): Config {
        val b = ByteBuffer.wrap(p).order(ByteOrder.LITTLE_ENDIAN)
        return Config(b.int, b.int, b.int, b.int)
    }
}

class Frame(val type: Byte, val payload: ByteArray)

class FrameReader(input: InputStream) {
    private val input = DataInputStream(input)
    private val header = ByteArray(Protocol.HEADER_SIZE)

    /** Returns null when the stream ends cleanly between frames. */
    fun read(): Frame? {
        val first = input.read()
        if (first < 0) return null
        header[0] = first.toByte()
        input.readFully(header, 1, Protocol.HEADER_SIZE - 1)
        val length = ByteBuffer.wrap(header, 1, 4).order(ByteOrder.LITTLE_ENDIAN).int
        if (length < 0 || length > Protocol.MAX_PAYLOAD) throw IOException("Frame too large: $length")
        val payload = ByteArray(length)
        try {
            input.readFully(payload)
        } catch (e: EOFException) {
            throw IOException("Stream ended in the middle of a frame", e)
        }
        return Frame(header[0], payload)
    }
}

/** Thread-safe frame writer. */
class FrameWriter(private val output: OutputStream) {
    @Synchronized
    fun write(type: Byte, payload: ByteArray) {
        val header = Protocol.le(Protocol.HEADER_SIZE).put(type).putInt(payload.size).array()
        output.write(header)
        output.write(payload)
        output.flush()
    }
}
