package com.extendedscreen.tablet

import android.annotation.SuppressLint
import android.graphics.Point
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import com.extendedscreen.tablet.input.InputCapture
import com.extendedscreen.tablet.net.StreamClient
import com.extendedscreen.tablet.proto.Protocol
import com.extendedscreen.tablet.video.H264Decoder

class MainActivity : AppCompatActivity(), StreamClient.Listener, SurfaceHolder.Callback {

    private lateinit var root: FrameLayout
    private lateinit var surfaceView: SurfaceView
    private lateinit var status: TextView
    private lateinit var input: InputCapture

    private var client: StreamClient? = null
    private var decoder: H264Decoder? = null
    private var config: Protocol.Config? = null
    private var lastCsd: ByteArray? = null
    @Volatile private var firstFrameShown = false
    private val lowLatency by lazy { H264Decoder.supportsLowLatency() }

    @SuppressLint("ClickableViewAccessibility")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        root = findViewById(R.id.root)
        surfaceView = findViewById(R.id.surface)
        status = findViewById(R.id.status)

        input = InputCapture(surfaceView) { client?.send(Protocol.POINTER, it) }
        root.setOnTouchListener(input::onTouch)
        root.setOnGenericMotionListener(input::onGenericMotion)
        surfaceView.holder.addCallback(this)
    }

    override fun onResume() {
        super.onResume()
        enterImmersive()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) enterImmersive()
    }

    private fun enterImmersive() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, root).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    // ---- Surface lifecycle drives the connection: no surface, nothing to decode into. ----

    override fun surfaceCreated(holder: SurfaceHolder) {
        decoder = H264Decoder(
            holder.surface,
            onError = { runOnUiThread(::recoverDecoder) },
            onKeyframeNeeded = { client?.send(Protocol.REQUEST_KEYFRAME, ByteArray(0)) },
            onFrameRendered = ::onFrameRendered,
        )
        client = StreamClient(::buildHello, this).also { it.start() }
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        client?.stop()
        client = null
        decoder?.release()
        decoder = null
        lastCsd = null
    }

    /** Native resolution in the current orientation, so the PC can match the virtual monitor to it. */
    private fun buildHello(): ByteArray {
        val size = Point()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val bounds = windowManager.maximumWindowMetrics.bounds
            size.set(bounds.width(), bounds.height())
        } else {
            @Suppress("DEPRECATION")
            windowManager.defaultDisplay.getRealSize(size)
        }
        val name = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
        return Protocol.hello(size.x, size.y, resources.displayMetrics.densityDpi, lowLatency, name)
    }

    // ---- StreamClient.Listener (network thread) ----

    override fun onConnected() {
        client?.send(Protocol.REQUEST_KEYFRAME, ByteArray(0))
        runOnUiThread { status.setText(R.string.status_connected) }
    }

    override fun onDisconnected() {
        firstFrameShown = false
        lastCsd = null
        decoder?.release()
        runOnUiThread {
            status.setText(R.string.status_waiting)
            status.visibility = View.VISIBLE
        }
    }

    override fun onConfig(config: Protocol.Config) {
        this.config = config
        runOnUiThread { fitVideo(config.width, config.height) }
    }

    override fun onCodecConfig(spsPps: ByteArray) {
        lastCsd = spsPps
        val c = config ?: return
        decoder?.configure(spsPps, c.width, c.height)
    }

    override fun onVideoFrame(data: ByteArray, offset: Int, length: Int, ptsUs: Long, keyframe: Boolean) {
        decoder?.decode(data, offset, length, ptsUs, keyframe)
    }

    private fun onFrameRendered() {
        if (firstFrameShown) return
        firstFrameShown = true
        runOnUiThread { status.visibility = View.GONE }
    }

    private fun recoverDecoder() {
        val csd = lastCsd
        val c = config
        if (csd == null || c == null) return
        Thread {
            try {
                decoder?.configure(csd, c.width, c.height)
                client?.send(Protocol.REQUEST_KEYFRAME, ByteArray(0))
            } catch (e: Exception) {
                client?.reconnect()
            }
        }.start()
    }

    /** Letterboxes the SurfaceView to the video aspect ratio; touches are normalized to this area. */
    private fun fitVideo(videoW: Int, videoH: Int) {
        if (root.width == 0) {
            root.post { fitVideo(videoW, videoH) }
            return
        }
        val rootW = root.width
        val rootH = root.height
        val scale = minOf(rootW.toFloat() / videoW, rootH.toFloat() / videoH)
        val w = (videoW * scale).toInt()
        val h = (videoH * scale).toInt()
        surfaceView.layoutParams = FrameLayout.LayoutParams(w, h, Gravity.CENTER)
    }

    override fun onDestroy() {
        client?.stop()
        decoder?.release()
        super.onDestroy()
    }
}
