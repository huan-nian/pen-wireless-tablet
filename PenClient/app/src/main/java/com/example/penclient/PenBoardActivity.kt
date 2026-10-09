package com.example.penclient

import android.content.res.ColorStateList
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowManager
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import com.example.penclient.databinding.ActivityFullscreenBinding

/**
 * 书写页：左侧快捷按钮 + 整块书写区。
 *
 * 这一页只做三件事：把笔事件送出去、把左侧按钮翻译成系统命令、在本地管理
 * 背景色与笔迹保留时长。所有真正影响电脑的操作都由接收端执行。
 */
class PenBoardActivity : AppCompatActivity(), PenCanvasView.Listener {

    private lateinit var binding: ActivityFullscreenBinding
    private lateinit var settings: ClientSettings

    private val sender: PenSender get() = PenServiceHolder.sender

    private val ui = Handler(Looper.getMainLooper())

    /** 当前笔迹消退设置。 */
    private var inkRetention = InkRetention.NEVER

    /** 停笔后清空平板笔迹的定时任务。 */
    private val clearInkRunnable = Runnable {
        // 定时器到点时如果笔还按着，说明用户正在写，不该把笔迹抽走
        if (!penInContact) {
            binding.canvas.clearInk()
        }
    }

    private var penInContact = false
    private var lastRateAt = 0L
    private var lastRateSent = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityFullscreenBinding.inflate(layoutInflater)
        setContentView(binding.root)

        settings = ClientSettings(this)
        inkRetention = settings.inkRetention

        binding.canvas.sender = sender
        binding.canvas.listener = this
        binding.canvas.inkBackgroundColor = settings.canvasColor

        applyKeepScreenOn()
        wireToolbar()
        wireBottomBar()
        refreshInkModeLabel()
        startStatusUpdater()

        // 没连上就别让人以为能写：直接提示并退回连接页
        if (!sender.isConnected) {
            toast(getString(R.string.status_disconnected))
            finish()
        }
    }

    private fun applyKeepScreenOn() {
        if (settings.keepScreenOn) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    // ------------------------------------------------------------ 左侧按钮

    private fun wireToolbar() {
        binding.btnHome.setOnClickListener { send(Command.HOME) }
        binding.btnTask.setOnClickListener { send(Command.TASK) }
        binding.btnSave.setOnClickListener { send(Command.SAVE) }
        binding.btnUndo.setOnClickListener { send(Command.UNDO) }
        binding.btnRedo.setOnClickListener { send(Command.REDO) }
        binding.btnScrollUp.setOnClickListener { send(Command.SCROLL_UP) }
        binding.btnScrollDown.setOnClickListener { send(Command.SCROLL_DOWN) }
        binding.btnDetail.setOnClickListener { finish() }
        binding.btnClose.setOnClickListener { exitApp() }
    }

    /**
     * 每个按钮对应的命令。本地操作（详情 / 退出）不在这里，直接绑到 finish / exitApp。
     */
    private enum class Command(
        val code: Int,
        @androidx.annotation.StringRes val labelRes: Int
    ) {
        HOME(PenProtocol.Command.SHOW_DESKTOP, R.string.tool_home),
        TASK(PenProtocol.Command.TASK_VIEW, R.string.tool_task),
        SAVE(PenProtocol.Command.SAVE, R.string.tool_save),
        UNDO(PenProtocol.Command.UNDO, R.string.tool_undo),
        REDO(PenProtocol.Command.REDO, R.string.tool_redo),
        SCROLL_UP(PenProtocol.Command.SCROLL_UP, R.string.tool_scroll_up),
        SCROLL_DOWN(PenProtocol.Command.SCROLL_DOWN, R.string.tool_scroll_down),
    }

    private fun send(command: Command) {
        if (!sender.isConnected) {
            toast(getString(R.string.toast_command_failed))
            return
        }
        sender.send(PenProtocol.encodeCommand(command.code))
        // 轻震一下，让用户知道手指确实按到了（这些按钮没有视觉焦点反馈）
        binding.root.performHapticFeedback(android.view.HapticFeedbackConstants.VIRTUAL_KEY)
        toast(getString(R.string.toast_sent, getString(command.labelRes)))
    }

    /** 退出软件：断开连接后结束整个任务。 */
    private fun exitApp() {
        toast(getString(R.string.toast_exit))
        sender.disconnect()
        finishAffinity()
    }

    // ------------------------------------------------------------ 底部控件

    private fun wireBottomBar() {
        binding.btnBackground.setOnClickListener { showBackgroundPicker() }
        binding.btnInkMode.setOnClickListener { showInkModePicker() }
    }

    /**
     * 背景色选择。
     *
     * 用一列预设色而不是完整的取色器：书写底色在实用范围内就那么几种
     * （白色、护眼米黄、深色），预设更省事也更容易点准。
     */
    private fun showBackgroundPicker() {
        val presets = listOf(
            "纯白" to Color.WHITE,
            "护眼米黄" to Color.parseColor("#FAF3E3"),
            "浅灰" to Color.parseColor("#EFEFEF"),
            "豆沙绿" to Color.parseColor("#CCE8CF"),
            "浅蓝" to Color.parseColor("#E3F2FD"),
            "淡紫" to Color.parseColor("#F3E5F5"),
            "深灰" to Color.parseColor("#3C3F41"),
            "纯黑" to Color.BLACK,
        )

        val names = presets.map { it.first }.toTypedArray()
        val current = presets.indexOfFirst { it.second == binding.canvas.inkBackgroundColor }

        AlertDialog.Builder(this)
            .setTitle(R.string.dialog_background_title)
            .setSingleChoiceItems(names, current) { dialog, which ->
                val color = presets[which].second
                binding.canvas.inkBackgroundColor = color
                settings.canvasColor = color
                dialog.dismiss()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /**
     * 笔迹消退时间。只影响平板上的预览笔迹，电脑上的笔迹不受影响——
     * 这一点必须写清楚，否则用户会以为电脑上的画也会被擦掉。
     */
    private fun showInkModePicker() {
        val modes = InkRetention.entries
        val names = modes.map { getString(inkModeLabel(it)) }.toTypedArray()
        val current = modes.indexOf(inkRetention)

        AlertDialog.Builder(this)
            .setTitle(R.string.dialog_ink_mode_title)
            .setSingleChoiceItems(names, current) { dialog, which ->
                inkRetention = modes[which]
                settings.inkRetention = inkRetention
                refreshInkModeLabel()
                scheduleInkClear()
                dialog.dismiss()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .setMessage(R.string.ink_mode_hint)
            .show()
    }

    @androidx.annotation.StringRes
    private fun inkModeLabel(mode: InkRetention): Int = when (mode) {
        InkRetention.NEVER -> R.string.ink_mode_never
        InkRetention.SECONDS_5 -> R.string.ink_mode_5s
        InkRetention.SECONDS_10 -> R.string.ink_mode_10s
        InkRetention.MINUTE_1 -> R.string.ink_mode_1m
    }

    private fun refreshInkModeLabel() {
        binding.btnInkMode.setText(inkModeLabel(inkRetention))
    }

    /**
     * 重排「停笔后清空」的定时。
     *
     * 每次笔事件都会调用它，所以计时基准是「最后一次输入之后」，
     * 与「无输入操作时自动消失」的预期一致。
     */
    private fun scheduleInkClear() {
        ui.removeCallbacks(clearInkRunnable)
        if (!inkRetention.autoClears) return
        ui.postDelayed(clearInkRunnable, inkRetention.timeoutMs)
    }

    // ------------------------------------------------------------ 笔况回调

    override fun onPenEvent(info: PenCanvasView.PenStatus) {
        penInContact = info.inContact

        // 只要还有输入就不断把清除时间往后推
        if (info.inContact || info.hovering) {
            scheduleInkClear()
        } else if (info.sentPackets > 0) {
            // 抬笔：从现在开始计时，这样「写完 5 秒后消失」是写完才开始算
            scheduleInkClear()
        }

        if (sender.isConnected) {
            setDot(if (info.inContact) R.color.dot_busy else R.color.dot_ok)
        }
    }

    override fun onLongPressRightClick(x: Float, y: Float) {
        Toast.makeText(this, R.string.toast_long_press, Toast.LENGTH_SHORT).show()
    }

    // ------------------------------------------------------------------ 状态

    private val statusUpdater = object : Runnable {
        override fun run() {
            if (!sender.isConnected) {
                renderStatus(getString(R.string.status_disconnected), R.color.dot_idle)
                return
            }

            val now = System.currentTimeMillis()
            val sent = sender.sentPackets
            val elapsed = (now - lastRateAt).coerceAtLeast(1)
            val rate = ((sent - lastRateSent) * 1000 / elapsed).toInt()
            lastRateSent = sent
            lastRateAt = now

            renderStatus(
                getString(R.string.status_connected, "${sender.host}:${sender.port}", rate),
                R.color.dot_ok
            )
            ui.postDelayed(this, 1000)
        }
    }

    private fun startStatusUpdater() {
        lastRateSent = sender.sentPackets
        lastRateAt = System.currentTimeMillis()
        ui.removeCallbacks(statusUpdater)
        ui.postDelayed(statusUpdater, 1000)
    }

    private fun renderStatus(text: String, @androidx.annotation.ColorRes colorRes: Int) {
        binding.statusText.text = text
        binding.statusDot.backgroundTintList =
            ColorStateList.valueOf(ContextCompat.getColor(this, colorRes))
        binding.canvasOverlay.visibility = View.GONE
    }

    private fun setDot(@androidx.annotation.ColorRes colorRes: Int) {
        binding.statusDot.backgroundTintList =
            ColorStateList.valueOf(ContextCompat.getColor(this, colorRes))
    }

    private fun toast(text: String) {
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
    }

    // -------------------------------------------------------------- 生命周期

    override fun onPause() {
        super.onPause()
        ui.removeCallbacks(statusUpdater)
        ui.removeCallbacks(clearInkRunnable)
        // 离开书写页就不要再往电脑注入输入了，避免误写
        if (sender.isConnected) {
            sender.disconnect()
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        ui.removeCallbacks(statusUpdater)
        ui.removeCallbacks(clearInkRunnable)
    }
}
