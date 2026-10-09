package com.example.penclient

import android.content.res.ColorStateList
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowManager
import android.widget.Toast
import androidx.annotation.ColorRes
import androidx.annotation.StringRes
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
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

        applyWindowInsets()

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

    /**
     * 把状态栏与导航栏占用的空间作为内边距加到根布局上。
     *
     * 为什么需要：这一页没有用 fitsSystemWindows，内容会从屏幕最顶端开始画，
     * 最上面一排按钮会被状态栏压住。
     *
     * 更关键的是底部：手势导航的屏幕底部有一条系统手势区（上滑回桌面），
     * 落在里面的点击会被系统吃掉。而系统**不一定**把这条手势区作为导航栏 inset
     * 报告出来（实测这台平板 `mHasBottomNavigationBar=false`，底部 inset 是 0），
     * 所以除了 inset 还要额外预留一段高度，否则底部按钮会紧贴屏幕边缘而点不动。
     */
    private fun applyWindowInsets() {
        // 手势区预留高度：24dp（≈ 系统手势条高度），系统报了更大的 inset 就用系统的
        val gestureReserve = (24 * resources.displayMetrics.density).toInt()

        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.updatePadding(
                left = bars.left,
                top = bars.top,
                right = bars.right,
                bottom = maxOf(bars.bottom, gestureReserve),
            )
            insets
        }
    }

    private fun applyKeepScreenOn() {
        if (settings.keepScreenOn) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    // ------------------------------------------------------------ 左侧按钮

    private fun wireToolbar() {
        binding.btnHome.setOnClickListener { send(BoardCommand.HOME) }
        binding.btnTask.setOnClickListener { send(BoardCommand.TASK) }
        binding.btnSave.setOnClickListener { send(BoardCommand.SAVE) }
        binding.btnUndo.setOnClickListener { send(BoardCommand.UNDO) }
        binding.btnRedo.setOnClickListener { send(BoardCommand.REDO) }
        binding.btnScrollUp.setOnClickListener { send(BoardCommand.SCROLL_UP) }
        binding.btnScrollDown.setOnClickListener { send(BoardCommand.SCROLL_DOWN) }
        binding.btnDetail.setOnClickListener { finish() }
        binding.btnClose.setOnClickListener { exitApp() }
    }

    /**
     * 每个按钮对应的命令。本地操作（详情 / 退出）不在这里，直接绑到 finish / exitApp。
     */
    private enum class BoardCommand(
        val code: Int,
        @StringRes val labelRes: Int
    ) {
        HOME(PenProtocol.Command.SHOW_DESKTOP, R.string.tool_home),
        TASK(PenProtocol.Command.TASK_VIEW, R.string.tool_task),
        SAVE(PenProtocol.Command.SAVE, R.string.tool_save),
        UNDO(PenProtocol.Command.UNDO, R.string.tool_undo),
        REDO(PenProtocol.Command.REDO, R.string.tool_redo),
        SCROLL_UP(PenProtocol.Command.SCROLL_UP, R.string.tool_scroll_up),
        SCROLL_DOWN(PenProtocol.Command.SCROLL_DOWN, R.string.tool_scroll_down),
    }

    private fun send(command: BoardCommand) {
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

    /** 一个预设底色。 */
    private data class ColorPreset(@StringRes val nameRes: Int, val color: Int)

    /**
     * 背景色选择。
     *
     * 用一组预设色而不是完整的取色器：书写底色常用就那么几种，预设更省事也更容易点准。
     * 底色整体偏柔和（暖白、米黄、浅灰…），避免与深色底形成强烈对比。
     */
    private fun showBackgroundPicker() {
        val presets = listOf(
            ColorPreset(R.string.color_default, Color.parseColor("#F7F5F0")),
            ColorPreset(R.string.color_pure_white, Color.WHITE),
            ColorPreset(R.string.color_cream, Color.parseColor("#FAF3E3")),
            ColorPreset(R.string.color_light_gray, Color.parseColor("#EDEDEA")),
            ColorPreset(R.string.color_bean_green, Color.parseColor("#DCE8D8")),
            ColorPreset(R.string.color_light_blue, Color.parseColor("#E2EAF2")),
            ColorPreset(R.string.color_lilac, Color.parseColor("#EAE4F2")),
            ColorPreset(R.string.color_slate, Color.parseColor("#4A4E55")),
            ColorPreset(R.string.color_ink, Color.parseColor("#2B2E33")),
        )

        val names = presets.map { getString(it.nameRes) }.toTypedArray()
        val current = presets.indexOfFirst { it.color == binding.canvas.inkBackgroundColor }

        AlertDialog.Builder(this)
            .setTitle(R.string.dialog_background_title)
            .setSingleChoiceItems(names, current) { dialog, which ->
                val preset = presets[which]
                applyCanvasColor(preset.color)
                toast(getString(R.string.toast_background_changed, getString(preset.nameRes)))
                dialog.dismiss()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /**
     * 应用新的画布底色。
     *
     * 换底色时把画布清空：底色变了如果笔迹还留着，视觉上会像是「笔迹浮在新底色之上」，
     * 反而让人以为没换成功（这一条来自实际反馈）。清掉后新底色一目了然。
     */
    private fun applyCanvasColor(color: Int) {
        if (binding.canvas.inkBackgroundColor == color) return
        binding.canvas.inkBackgroundColor = color
        settings.canvasColor = color
        binding.canvas.clearInk()
    }

    /**
     * 笔迹消退时间。
     *
     * 注意这里**不能**用 setMessage 放说明文字：实测 Material 的 AlertDialog 在
     * 同时设置 message 与 singleChoiceItems 时，会只渲染 message，把选项列表整块吞掉，
     * 表现为「打开对话框只看到一句说明和取消按钮、选不了任何东西」。
     * 所以说明文字放在标题里，保证选项一定可见。
     */
    private fun showInkModePicker() {
        val modes = InkRetention.entries
        val names = modes.map { getString(it.labelRes) }.toTypedArray()
        val current = modes.indexOf(inkRetention)

        AlertDialog.Builder(this)
            .setTitle(R.string.dialog_ink_mode_title_with_hint)
            .setSingleChoiceItems(names, current) { dialog, which ->
                inkRetention = modes[which]
                settings.inkRetention = inkRetention
                refreshInkModeLabel()
                scheduleInkClear()
                toast(getString(R.string.toast_ink_mode_changed, getString(inkRetention.labelRes)))
                dialog.dismiss()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun refreshInkModeLabel() {
        binding.btnInkMode.setText(inkRetention.labelRes)
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

        // 只要还有输入就不断把清除时间往后推；
        // 抬笔后同样排一次，这样「写完 N 秒后消失」是从停笔开始算的
        if (info.inContact || info.hovering || info.sentPackets > 0) {
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

    private fun renderStatus(text: String, @ColorRes colorRes: Int) {
        binding.statusText.text = text
        setDot(colorRes)
    }

    private fun setDot(@ColorRes colorRes: Int) {
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
