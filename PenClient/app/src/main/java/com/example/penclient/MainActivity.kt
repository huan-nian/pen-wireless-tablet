package com.example.penclient

import android.annotation.SuppressLint
import android.content.Intent
import android.content.res.ColorStateList
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowManager
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.example.penclient.databinding.ActivityMainBinding
import java.util.Locale
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 连接页：填接收端地址、扫描局域网、建立连接，然后把用户送进书写页。
 *
 * 这一页**不再承载书写**。之前把画布挤在配置卡片下面，只剩半屏甚至更小，
 * 写起来别扭且容易误触；现在连接与书写分成两个页面，各自专注一件事。
 */
class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var settings: ClientSettings

    /** 与书写页共用同一个发送器，页面切换不会断流。 */
    private val sender: PenSender get() = PenServiceHolder.sender

    private val ui = Handler(Looper.getMainLooper())
    private val scanExecutor = Executors.newSingleThreadExecutor()
    private val scanning = AtomicBoolean(false)

    private var lastRateAt = 0L
    private var lastRateSent = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        settings = ClientSettings(this)
        restoreSettings()
        wireActions()
        refreshConnectionUi()

        // 允许通过 adb / 其它应用带上地址直接启动，方便自动化验证：
        //   adb shell am start -n com.example.penclient/.MainActivity \
        //       --es host 192.168.1.5 --ez openBoard true
        // openBoard 只是给自动化测试用的钩子，正常使用不会带这个参数。
        intent?.getStringExtra(EXTRA_HOST)?.let { host ->
            binding.hostInput.setText(host)
            binding.portInput.setText(
                intent.getIntExtra(EXTRA_PORT, PenProtocol.DEFAULT_PORT).toString()
            )
            connect()
            if (intent.getBooleanExtra(EXTRA_OPEN_BOARD, false)) {
                openWritingPage()
            }
        }
    }

    // ------------------------------------------------------------------ 配置

    private fun restoreSettings() {
        binding.hostInput.setText(settings.host)
        binding.portInput.setText(settings.port.toString())
        binding.keepScreenOnSwitch.isChecked = settings.keepScreenOn
        applyKeepScreenOn(settings.keepScreenOn)
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

        binding.keepScreenOnSwitch.setOnCheckedChangeListener { _, checked ->
            settings.keepScreenOn = checked
            applyKeepScreenOn(checked)
        }

        binding.writeButton.setOnClickListener { openWritingPage() }
    }

    /** 进入书写页。只有连接成功后才可点。 */
    private fun openWritingPage() {
        if (!sender.isConnected) return
        startActivity(Intent(this, PenBoardActivity::class.java))
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

        settings.host = host
        settings.port = port
        lastRateSent = sender.sentPackets
        lastRateAt = System.currentTimeMillis()

        refreshConnectionUi()
        startRateUpdater()
    }

    private fun disconnect() {
        sender.disconnect()
        refreshConnectionUi()
    }

    private fun refreshConnectionUi() {
        val connected = sender.isConnected
        binding.connectButton.setText(
            if (connected) R.string.action_disconnect else R.string.action_connect
        )
        binding.writeButton.isEnabled = connected
        binding.writeButton.setText(
            if (connected) R.string.action_enter_fullscreen
            else R.string.action_enter_fullscreen_disabled
        )

        if (!connected) {
            renderStatus(getString(R.string.status_idle), R.color.dot_idle)
        }
    }

    /** 每秒刷新一次速率，避免每条笔事件都刷界面。 */
    private val rateUpdater = object : Runnable {
        override fun run() {
            if (!sender.isConnected) {
                refreshConnectionUi()
                return
            }
            val now = System.currentTimeMillis()
            val sent = sender.sentPackets
            val elapsed = (now - lastRateAt).coerceAtLeast(1)
            val rate = ((sent - lastRateSent) * 1000 / elapsed).toInt()
            lastRateSent = sent
            lastRateAt = now

            val text = getString(
                R.string.status_connected,
                "${sender.host}:${sender.port}",
                rate
            )
            val dropped = sender.droppedPackets
            renderStatus(
                if (dropped > 0) "$text · 丢弃 $dropped" else text,
                R.color.dot_ok
            )
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

    // ------------------------------------------------------------------ 状态

    private fun renderStatus(text: String, @androidx.annotation.ColorRes colorRes: Int) {
        binding.statusText.text = text
        binding.statusDot.backgroundTintList =
            ColorStateList.valueOf(ContextCompat.getColor(this, colorRes))
    }

    // -------------------------------------------------------------- 生命周期

    override fun onResume() {
        super.onResume()
        // 从书写页返回时状态可能已经变了（比如那边关闭了连接）
        refreshConnectionUi()
        if (sender.isConnected) startRateUpdater()
    }

    override fun onPause() {
        super.onPause()
        ui.removeCallbacks(rateUpdater)
        // 这里刻意不断开连接：跳转到书写页时本页也会进入 pause，
        // 断开就等于刚连上就断线。真正的断开在退出应用时处理。
    }

    override fun onDestroy() {
        super.onDestroy()
        ui.removeCallbacks(rateUpdater)
        scanExecutor.shutdown()
        // 正常退出应用才断开；配置变更导致的重建不算退出
        if (isFinishing) {
            sender.disconnect()
        }
    }

    companion object {
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"

        /** 仅用于自动化测试：连上后直接进入书写页。 */
        const val EXTRA_OPEN_BOARD = "openBoard"
    }
}
