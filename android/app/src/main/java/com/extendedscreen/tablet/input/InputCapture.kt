package com.extendedscreen.tablet.input

import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.MotionEvent
import android.view.View
import com.extendedscreen.tablet.proto.Protocol
import kotlin.math.atan
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.tan

/**
 * Converts touch and stylus MotionEvents into POINTER messages. Coordinates are normalized to the
 * video area ([videoView]); fingers become the mouse on the PC, the stylus becomes a Windows pen.
 */
class InputCapture(
    private val videoView: View,
    private val send: (ByteArray) -> Unit,
) {
    private val handler = Handler(Looper.getMainLooper())
    private var penActiveUntil = 0L
    private var penHovering = false

    /**
     * Android sends HOVER_EXIT right before the stylus touches down. Delay it so a tap does not make the
     * Windows pen leave and re-enter range.
     */
    private var pendingHoverExit: ByteArray? = null
    private val hoverExitRunnable = Runnable {
        pendingHoverExit?.let(send)
        pendingHoverExit = null
        penHovering = false
    }

    fun onTouch(view: View, event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN ->
                emit(view, event, event.actionIndex, Protocol.ACTION_DOWN)
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP ->
                emit(view, event, event.actionIndex, Protocol.ACTION_UP)
            MotionEvent.ACTION_CANCEL ->
                for (i in 0 until event.pointerCount) emit(view, event, i, Protocol.ACTION_CANCEL)
            MotionEvent.ACTION_MOVE -> {
                // Historical samples make pen strokes smooth at the full digitizer rate.
                for (h in 0 until event.historySize) {
                    for (i in 0 until event.pointerCount) emit(view, event, i, Protocol.ACTION_MOVE, h)
                }
                for (i in 0 until event.pointerCount) emit(view, event, i, Protocol.ACTION_MOVE)
            }
        }
        return true
    }

    fun onGenericMotion(view: View, event: MotionEvent): Boolean {
        if (!isStylus(event, 0)) return false
        when (event.actionMasked) {
            MotionEvent.ACTION_HOVER_ENTER, MotionEvent.ACTION_HOVER_MOVE -> {
                handler.removeCallbacks(hoverExitRunnable)
                pendingHoverExit = null
                penHovering = true
                for (h in 0 until event.historySize) emit(view, event, 0, Protocol.ACTION_HOVER, h)
                emit(view, event, 0, Protocol.ACTION_HOVER)
            }
            MotionEvent.ACTION_HOVER_EXIT -> {
                pendingHoverExit = message(view, event, 0, Protocol.ACTION_HOVER_EXIT, -1)
                handler.removeCallbacks(hoverExitRunnable)
                handler.postDelayed(hoverExitRunnable, HOVER_EXIT_DELAY_MS)
            }
            else -> return false
        }
        return true
    }

    private fun emit(view: View, event: MotionEvent, index: Int, action: Byte, history: Int = -1) {
        val stylus = isStylus(event, index)
        val now = SystemClock.uptimeMillis()
        if (stylus) {
            handler.removeCallbacks(hoverExitRunnable)
            pendingHoverExit = null
            penActiveUntil = now + PALM_REJECTION_MS
        } else if (penHovering || now < penActiveUntil) {
            // Palm rejection: ignore fingers while the pen is near the screen.
            if (action != Protocol.ACTION_UP && action != Protocol.ACTION_CANCEL) return
        }
        send(message(view, event, index, action, history))
    }

    private fun message(view: View, event: MotionEvent, index: Int, action: Byte, history: Int): ByteArray {
        val rawX: Float
        val rawY: Float
        val pressure: Float
        val tilt: Float
        val orientation: Float
        if (history >= 0) {
            rawX = event.getHistoricalX(index, history)
            rawY = event.getHistoricalY(index, history)
            pressure = event.getHistoricalPressure(index, history)
            tilt = event.getHistoricalAxisValue(MotionEvent.AXIS_TILT, index, history)
            orientation = event.getHistoricalOrientation(index, history)
        } else {
            rawX = event.getX(index)
            rawY = event.getY(index)
            pressure = event.getPressure(index)
            tilt = event.getAxisValue(MotionEvent.AXIS_TILT, index)
            orientation = event.getOrientation(index)
        }

        // Events arrive in the coordinates of the view that received them; map onto the video view.
        val x = (rawX + view.left - videoView.left) / videoView.width.coerceAtLeast(1)
        val y = (rawY + view.top - videoView.top) / videoView.height.coerceAtLeast(1)

        val tool = when (event.getToolType(index)) {
            MotionEvent.TOOL_TYPE_ERASER -> Protocol.TOOL_ERASER
            MotionEvent.TOOL_TYPE_STYLUS -> Protocol.TOOL_PEN
            else -> Protocol.TOOL_FINGER
        }

        // AXIS_TILT is the angle from vertical, AXIS_ORIENTATION the direction it leans (0 = up, clockwise).
        val t = tan(tilt.toDouble().coerceIn(0.0, 1.5))
        val tiltX = Math.toDegrees(atan(t * sin(orientation.toDouble()))).toFloat()
        val tiltY = Math.toDegrees(atan(-t * cos(orientation.toDouble()))).toFloat()

        val barrel = (event.buttonState and
            (MotionEvent.BUTTON_STYLUS_PRIMARY or MotionEvent.BUTTON_SECONDARY)) != 0

        return Protocol.pointer(
            tool, action, event.getPointerId(index),
            x.coerceIn(0f, 1f), y.coerceIn(0f, 1f),
            pressure.coerceIn(0f, 1f), tiltX, tiltY,
            if (barrel) 1 else 0,
        )
    }

    private fun isStylus(event: MotionEvent, index: Int): Boolean =
        event.getToolType(index).let { it == MotionEvent.TOOL_TYPE_STYLUS || it == MotionEvent.TOOL_TYPE_ERASER }

    companion object {
        private const val HOVER_EXIT_DELAY_MS = 150L
        private const val PALM_REJECTION_MS = 400L
    }
}
