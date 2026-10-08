package com.example.penclient

import android.annotation.SuppressLint
import android.content.res.ColorStateList
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowManager
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.example.penclient.databinding.ActivityMainBinding
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 主界面：配置接收端地址、开始/停止书写、显示实时笔况。
 *
 * 交互上尽量少点：连上之后点标题栏就能收起配置面板，让画布铺满整个屏幕，
 * 平板此时就是一块纯粹的无线手写板。
 */
class MainActivity : AppCompatActivity(), PenCanvasView.Listener {

    private lateinit var binding: ActivityMainBinding
    private val sender = PenSender()

    private val ui = Handler(Looper.getMainLooper())
    private val scanExecutor = Executors.newSingleThreadExecutor()
    private val scanning = AtomicBoolean(false)

    private var lastRateAt = 0L
    private var lastRateSent = 0L
    private var configCollapsed = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        binding.canvas.sender = sender
        binding.canvas.listener = this

        restoreSettings()
        wireActions()
        renderIdleStatus()

        // 允许通过 adb / 其它应用带上地址直接启动，方便自动化验证：
        //   adb shell am start -n com.example.penclient/.MainActivity --es host 192.168.1.5
        intent?.getStringExtra(EXTRA_HOST)?.let { host ->
            binding.hostInput.setText(host)
            binding.portInput.setText(
                intent.getIntExtra(EXTRA_PORT, PenProtocol.DEFAULT_PORT).toString()
            )
            connect()
        }
    }

    // ------------------------------------------------------------------ 配置

    private fun restoreSettings() {
        val prefs = getSharedPreferences(PREFS, MODE_PRIVATE)
        binding.hostInput.setText(prefs.getString(KEY_HOST, ""))
        binding.portInput.setText(
            prefs.getInt(KEY_PORT, PenProtocol.DEFAULT_PORT).toString()
        )
        binding.showInkSwitch.isChecked = prefs.getBoolean(KEY_SHOW_INK, true)
        binding.keepScreenOnSwitch.isChecked = prefs.getBoolean(KEY_KEEP_SCREEN_ON, true)

        binding.canvas.showInk = binding.showInkSwitch.isChecked
        applyKeepScreenOn(binding.keepScreenOnSwitch.isChecked)
    }

    private fun saveSettings(host: String, port: Int) {
        getSharedPreferences(PREFS, MODE_PRIVATE).edit()
            .putString(KEY_HOST, host)
            .putInt(KEY_PORT, port)
            .putBoolean(KEY_SHOW_INK, binding.showInkSwitch.isChecked)
            .putBoolean(KEY_KEEP_SCREEN_ON, binding.keepScreenOnSwitch.isChecked)
            .apply()
    }

    private fun applyKeepScreenOn(enabled: Boolean) {
        if (enabled) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    private fun wireActions() {
        binding.connectButton.setOnClickListener {
            if (sender.isConnected) disconnect() else connect()
        }

        binding.scanButton.setOnClickListener { startScan() }

        binding.showInkSwitch.setOnCheckedChangeListener { _, checked ->
            binding.canvas.showInk = checked
            persist()
        }

        binding.keepScreenOnSwitch.setOnCheckedChangeListener { _, checked ->
            applyKeepScreenOn(checked)
            persist()
        }

        // 点标题栏收起/展开配置面板，把屏幕让给画布
        binding.toolbar.setOnClickListener { toggleConfig() }
    }

    private fun toggleConfig() {
        configCollapsed = !configCollapsed
        binding.configScroll.visibility = if (configCollapsed) View.GONE else View.VISIBLE
        Toast.makeText(
            this,
            if (configCollapsed) "配置已收起，点标题栏可展开" else "配置已展开",
            Toast.LENGTH_SHORT
        ).show()
    }

    // ------------------------------------------------------------------ 连接

    private fun connect() {
        val host = binding.hostInput.text?.toString()?.trim().orEmpty()
        val port = binding.portInput.text?.toString()?.trim()?.toIntOrNull()
            ?: PenProtocol.DEFAULT_PORT

        if (host.isEmpty()) {
            binding.hostLayout.error = getString(R.string.hint_host)
            return
        }
        binding.hostLayout.error = null

        renderStatus(getString(R.string.status_connecting, host), R.color.dot_busy)

        if (!sender.connect(host, port)) {
            renderStatus(getString(R.string.status_failed), R.color.dot_idle)
            binding.hostLayout.error = getString(R.string.status_failed)
            return
        }

        saveSettings(host, port)
        lastRateSent = sender.sentPackets
        lastRateAt = System.currentTimeMillis()

        binding.connectButton.setText(R.string.action_disconnect)
        binding.canvasOverlay.text = getString(R.string.canvas_live_hint)
        startRateUpdater()
    }

    private fun disconnect() {
        sender.disconnect()
        binding.connectButton.setText(R.string.action_connect)
        binding.canvasOverlay.text = getString(R.string.canvas_idle_hint)
        renderStatus(getString(R.string.status_disconnected), R.color.dot_idle)
        binding.penText.setText(R.string.pen_idle)
    }

    private fun persist() {
        val host = binding.hostInput.text?.toString()?.trim().orEmpty()
        val port = binding.portInput.text?.toString()?.trim()?.toIntOrNull()
            ?: PenProtocol.DEFAULT_PORT
        saveSettings(host, port)
    }

    /** 每秒刷新一次速率与累计包数，避免每条笔事件都刷界面。 */
    private val rateUpdater = object : Runnable {
        override fun run() {
            if (!sender.isConnected) return
            val now = System.currentTimeMillis()
            val sent = sender.sentPackets
            val elapsed = (now - lastRateAt).coerceAtLeast(1)
            val rate = ((sent - lastRateSent) * 1000 / elapsed).toInt()
            lastRateSent = sent
            lastRateAt = now

            binding.statusText.text = getString(
                R.string.status_connected,
                "${sender.host}:${sender.port}",
                rate
            )
            val dropped = sender.droppedPackets
            if (dropped > 0) {
                binding.statusText.append(" · 丢弃 $dropped")
            }
            ui.postDelayed(this, 1000)
        }
    }

    private fun startRateUpdater() {
        ui.removeCallbacks(rateUpdater)
        ui.postDelayed(rateUpdater, 1000)
    }

    // ------------------------------------------------------------------ 扫描

    @SuppressLint("SetTextI18n")
    private fun startScan() {
        if (!scanning.compareAndSet(false, true)) return

        renderStatus(getString(R.string.status_scanning), R.color.dot_busy)
        binding.scanButton.isEnabled = false

        scanExecutor.execute {
            val results = try {
                sender.discover(timeoutMs = 2500)
            } catch (e: Exception) {
                emptyList()
            }

            ui.post {
                scanning.set(false)
                binding.scanButton.isEnabled = true

                if (results.isEmpty()) {
                    renderStatus(getString(R.string.status_scan_none), R.color.dot_idle)
                    return@post
                }

                val first = results.first()
                binding.hostInput.setText(first.address)
                binding.portInput.setText(first.port.toString())
                renderStatus(
                    getString(R.string.status_scan_found, results.size, first.address),
                    R.color.dot_ok
                )

                binding.discoveredText.visibility = View.VISIBLE
                binding.discoveredText.text = results.joinToString("\n") {
                    String.format(Locale.US, "%s : %d", it.address, it.port)
                }
            }
        }
    }

    // ------------------------------------------------------------ 笔况回调

    override fun onPenEvent(info: PenCanvasView.PenStatus) {
        if (info.inContact || info.hovering) {
            binding.penText.text = getString(
                R.string.pen_active,
                (info.pressure * 100).toInt(),
                info.tiltX.toInt(),
                info.tiltY.toInt(),
                if (info.eraser) getString(R.string.pen_eraser) else ""
            )
        } else {
            binding.penText.setText(R.string.pen_idle)
        }

        if (!sender.isConnected) return
        setDot(if (info.inContact) R.color.dot_busy else R.color.dot_ok)
    }

    /**
     * 长按被识别。平板端自己判定长按是因为：实测注入到 Windows 的笔输入不会触发
     * 系统的长按右键手势，所以必须在抬起时显式要求接收端注入鼠标右键。
     */
    override fun onLongPressRightClick(x: Float, y: Float) {
        binding.penText.setText(R.string.pen_long_press)
        Toast.makeText(this, R.string.toast_long_press, Toast.LENGTH_SHORT).show()
    }

    // ------------------------------------------------------------------ 状态

    private fun renderIdleStatus() {
        renderStatus(getString(R.string.status_idle), R.color.dot_idle)
        binding.penText.setText(R.string.pen_idle)
    }

    private fun renderStatus(text: String, @androidx.annotation.ColorRes colorRes: Int) {
        binding.statusText.text = text
        setDot(colorRes)
    }

    private fun setDot(@androidx.annotation.ColorRes colorRes: Int) {
        binding.statusDot.backgroundTintList =
            ColorStateList.valueOf(ContextCompat.getColor(this, colorRes))
    }

    // -------------------------------------------------------------- 生命周期

    override fun onPause() {
        super.onPause()
        // 退到后台就不再往电脑注入输入，避免误写
        if (sender.isConnected) {
            sender.disconnect()
            binding.connectButton.setText(R.string.action_connect)
            renderStatus(getString(R.string.status_disconnected), R.color.dot_idle)
        }
        ui.removeCallbacks(rateUpdater)
    }

    override fun onDestroy() {
        super.onDestroy()
        ui.removeCallbacks(rateUpdater)
        sender.disconnect()
        scanExecutor.shutdown()
    }

    companion object {
        private const val PREFS = "pen_client"
        private const val KEY_HOST = "host"
        private const val KEY_PORT = "port"
        private const val KEY_SHOW_INK = "show_ink"
        private const val KEY_KEEP_SCREEN_ON = "keep_screen_on"

        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
    }
}
