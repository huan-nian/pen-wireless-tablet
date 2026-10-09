package com.example.penclient

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.util.AttributeSet
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration

/**
 * 手写采集画布：整块区域等比映射到 Windows 主屏。
 *
 * 行为要点：
 *  - 只采集触控笔，手指触摸一律忽略并让事件继续传递，避免手掌误触干扰书写。
 *  - 支持悬停（Hover）：笔尖离屏约 1cm 内就会持续上报位置，Windows 上可以提前
 *    看到光标，落笔更准。
 *  - 支持笔尾橡皮（TOOL_TYPE_ERASER），自动带上橡皮标记。
 *  - 事件结束或视图销毁时一定会补发一次「抬起」，避免接收端笔尖卡在按下状态。
 */
class PenCanvasView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    /** 状态回调，用于把实时笔况显示到界面上。回调发生在主线程。 */
    interface Listener {
        fun onPenEvent(info: PenStatus)

        /** 长按被识别，即将在电脑上弹出右键菜单。用于给出震动/提示反馈。 */
        fun onLongPressRightClick(x: Float, y: Float)
    }

    /** 一次回调里携带的笔况快照。 */
    data class PenStatus(
        val pressure: Float,
        val tiltX: Float,
        val tiltY: Float,
        val eraser: Boolean,
        val hovering: Boolean,
        val inContact: Boolean,
        val sentPackets: Long,
        val droppedPackets: Long
    )

    companion object {
        /**
         * 长按判定的静止时长。Windows 的笔长按手势一般在 500~700ms，
         * 这里取 600ms，并配合「位移不超过系统触摸阈值」来判定。
         */
        private const val LONG_PRESS_MS = 600L
    }

    var listener: Listener? = null

    /** 上报坐标时使用的参照尺寸。默认取本视图尺寸。 */
    var sender: PenSender? = null

    /** 是否在本地把笔迹画出来，方便用户确认平板这一侧工作正常。 */
    var showInk: Boolean = true
        set(value) {
            field = value
            if (!value) clearInk()
            invalidate()
        }

    /**
     * 书写区背景色。默认暖白（与 ClientSettings 的默认值一致），比纯白柔和，
     * 长时间书写不刺眼；可在界面上换成护眼底色等。
     *
     * 不能叫 backgroundColor：View 已经有 setBackgroundColor(int)，
     * Kotlin 属性生成的 setter 会与它 JVM 签名冲突，编译直接报 Accidental override。
     */
    var inkBackgroundColor: Int = Color.parseColor("#F7F5F0")
        set(value) {
            if (field == value) return
            field = value
            // 背景色变了要把已有笔迹一起重绘到底色上，否则旧笔迹会浮在新底色之上
            repaintInkBackground()
            invalidate()
        }

    private var inkBitmap: Bitmap? = null
    private var inkCanvas: Canvas? = null

    private val strokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
        color = Color.parseColor("#1B1B1F")
    }

    private val cursorPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 2f
        color = Color.parseColor("#0B6BCB")
    }

    private val cursorDotPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = Color.parseColor("#0B6BCB")
    }

    private val backgroundPaint = Paint().apply { color = Color.WHITE }

    private val path = Path()

    // 当前正在书写的指针 id。多指场景下只跟这一支笔。
    private var activePointerId = MotionEvent.INVALID_POINTER_ID
    private var inContact = false
    private var eraserActive = false

    private var cursorX = -1f
    private var cursorY = -1f
    private var cursorVisible = false

    private var lastTiltX = 0f
    private var lastTiltY = 0f
    private var lastPressure = 0f

    // ---- 长按识别（长按 = 右键）----
    // 平板端必须自己判定长按：实测注入到 Windows 的笔输入不会触发系统的长按手势。
    private val longPressSlop = ViewConfiguration.get(context).scaledTouchSlop * 2f
    private var downX = 0f
    private var downY = 0f
    private var movedBeyondSlop = false
    private var longPressFired = false
    private val longPressRunnable = Runnable {
        if (!inContact || movedBeyondSlop || longPressFired) return@Runnable
        longPressFired = true

        // 轻震一下告诉用户「这一笔被判定为长按」，松开后电脑上会弹右键菜单
        performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
        listener?.onLongPressRightClick(cursorX, cursorY)
        invalidate()
    }

    private var lastNotifyAt = 0L
    private var lastInContact = false
    private var lastHovering = false

    init {
        // 需要接收悬停事件，否则笔尖离屏时不产生任何回调
        isHovered = true
        isFocusable = false
        // 画笔笔迹交给 Windows 呈现，这里只做本地预览，不参与焦点/滚动
        isClickable = false
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        if (w <= 0 || h <= 0) return
        inkBitmap?.recycle()
        inkBitmap = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        inkCanvas = Canvas(inkBitmap!!).apply { drawColor(inkBackgroundColor) }
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)

        val bitmap = inkBitmap
        if (showInk && bitmap != null) {
            canvas.drawBitmap(bitmap, 0f, 0f, null)
        } else {
            canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), backgroundPaint)
        }

        if (cursorVisible && cursorX >= 0f) {
            val radius = if (eraserActive) 18f else 9f
            canvas.drawCircle(cursorX, cursorY, radius, cursorPaint)
            canvas.drawCircle(cursorX, cursorY, 2f, cursorDotPaint)
        }
    }

    // ---------------------------------------------------------------- 触摸事件

    override fun onTouchEvent(event: MotionEvent): Boolean {
        val index = event.actionIndex.coerceIn(0, event.pointerCount - 1)
        val toolType = event.getToolType(index)

        val isPen = toolType == MotionEvent.TOOL_TYPE_STYLUS ||
                toolType == MotionEvent.TOOL_TYPE_ERASER
        if (!isPen) {
            // 手指：完全不管，交还给父容器/系统处理
            return false
        }

        val pointerId = event.getPointerId(index)

        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                activePointerId = pointerId
                beginStroke(pointerId, index, event)
                return true
            }
            MotionEvent.ACTION_MOVE -> {
                if (activePointerId == MotionEvent.INVALID_POINTER_ID) {
                    // 漏了 DOWN（例如中途接管）：补一次起始
                    activePointerId = pointerId
                    beginStroke(pointerId, index, event)
                    return true
                }
                emitMove(event)
                return true
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                if (activePointerId != MotionEvent.INVALID_POINTER_ID && pointerId != activePointerId) {
                    return true
                }
                emitMove(event)
                endStroke()
                activePointerId = MotionEvent.INVALID_POINTER_ID
                return true
            }
            MotionEvent.ACTION_CANCEL -> {
                endStroke()
                activePointerId = MotionEvent.INVALID_POINTER_ID
                return true
            }
        }
        return true
    }

    /**
     * 悬停事件走 onGenericMotionEvent（ACTION_HOVER_MOVE / ACTION_HOVER_ENTER / EXIT）。
     * 只上报位置不产生笔迹，让 Windows 侧提前显示光标。
     */
    override fun onGenericMotionEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_HOVER_MOVE, MotionEvent.ACTION_HOVER_ENTER -> {
                val index = event.actionIndex.coerceIn(0, event.pointerCount - 1)
                val toolType = event.getToolType(index)
                if (toolType != MotionEvent.TOOL_TYPE_STYLUS &&
                    toolType != MotionEvent.TOOL_TYPE_ERASER
                ) {
                    return false
                }
                eraserActive = toolType == MotionEvent.TOOL_TYPE_ERASER

                val sx = event.getX(index)
                val sy = event.getY(index)
                updateCursor(sx, sy)
                lastPressure = 0f
                updateTilt(event)
                dispatch(sx, sy, PenProtocol.Action.HOVER, 0f)

                notifyListener(hovering = true, inContact = false)
                invalidate()
                return true
            }
            MotionEvent.ACTION_HOVER_EXIT -> {
                // 笔离开感应范围：确保接收端抬起，否则光标会赖在屏幕上
                if (inContact) {
                    endStroke()
                    activePointerId = MotionEvent.INVALID_POINTER_ID
                }
                cursorVisible = false
                invalidate()
                return true
            }
        }
        return super.onGenericMotionEvent(event)
    }

    // ---------------------------------------------------------------- 内部实现

    private fun beginStroke(pointerId: Int, index: Int, event: MotionEvent) {
        eraserActive = event.getToolType(index) == MotionEvent.TOOL_TYPE_ERASER
        inContact = true
        updateTilt(event)
        path.reset()

        val sx = event.getX(index)
        val sy = event.getY(index)
        lastPressure = event.getPressure(index).coerceIn(0f, 1f)
        path.moveTo(sx, sy)
        updateCursor(sx, sy)

        // 启动长按计时：600ms 内不移动就判定为长按（= 右键）
        downX = sx
        downY = sy
        movedBeyondSlop = false
        longPressFired = false
        removeCallbacks(longPressRunnable)
        postDelayed(longPressRunnable, LONG_PRESS_MS)

        dispatch(sx, sy, PenProtocol.Action.DOWN, lastPressure)
        notifyListener(hovering = false, inContact = true)
        invalidate()
    }

    private fun emitMove(event: MotionEvent) {
        val pointerIndex = event.findPointerIndex(activePointerId)
        if (pointerIndex < 0) return

        // 先把系统缓存的中间采样点补发出去，高速书写时能显著提升笔迹圆滑度
        for (i in 0 until event.historySize) {
            val hx = event.getHistoricalX(pointerIndex, i)
            val hy = event.getHistoricalY(pointerIndex, i)
            val hp = event.getHistoricalPressure(pointerIndex, i).coerceIn(0f, 1f)
            cancelLongPressIfMoved(hx, hy)
            drawSegment(hx, hy, hp)
            dispatch(hx, hy, PenProtocol.Action.MOVE, hp)
        }

        val x = event.getX(pointerIndex)
        val y = event.getY(pointerIndex)
        lastPressure = event.getPressure(pointerIndex).coerceIn(0f, 1f)
        updateTilt(event)
        cancelLongPressIfMoved(x, y)
        drawSegment(x, y, lastPressure)
        updateCursor(x, y)
        dispatch(x, y, PenProtocol.Action.MOVE, lastPressure)

        notifyListener(hovering = false, inContact = true)
        invalidate()
    }

    private fun endStroke() {
        removeCallbacks(longPressRunnable)

        if (!inContact) {
            cursorVisible = false
            invalidate()
            return
        }
        inContact = false

        val x = cursorX.coerceAtLeast(0f)
        val y = cursorY.coerceAtLeast(0f)

        // 长按这一笔：抬起报文带上右键标记，接收端会把整笔笔迹丢掉、只弹右键菜单。
        // 本地也不该留下墨点，所以把这一笔的预览擦掉。
        if (longPressFired) {
            clearInk()
        }
        dispatch(x, y, PenProtocol.Action.UP, 0f, rightClick = longPressFired)

        longPressFired = false
        cursorVisible = false
        notifyListener(hovering = false, inContact = false)
        invalidate()
    }

    /** 笔一旦移动超过阈值就不再算长按。 */
    private fun cancelLongPressIfMoved(x: Float, y: Float) {
        if (movedBeyondSlop || longPressFired) return
        val dx = x - downX
        val dy = y - downY
        if (dx * dx + dy * dy > longPressSlop * longPressSlop) {
            movedBeyondSlop = true
            removeCallbacks(longPressRunnable)
        }
    }

    private fun drawSegment(x: Float, y: Float, pressure: Float) {
        val canvas = inkCanvas ?: return
        if (!showInk) return

        // 有压感时线宽随压力变化；很多平板只在 0/1 两档上报，做一次下限兜底
        val width = if (pressure > 0.02f) 1.5f + pressure * 5.5f else 3.2f
        strokePaint.strokeWidth = width

        path.lineTo(x, y)
        canvas.drawPath(path, strokePaint)
        // 重新起一段，避免把整条历史路径反复重画
        path.reset()
        path.moveTo(x, y)
    }

    private fun updateCursor(x: Float, y: Float) {
        cursorX = x
        cursorY = y
        cursorVisible = true
    }

    private fun updateTilt(event: MotionEvent) {
        val tiltRad = event.getAxisValue(MotionEvent.AXIS_TILT)
        val orientationRad = event.getAxisValue(MotionEvent.AXIS_ORIENTATION)
        val (tx, ty) = PenProtocol.tiltToWindows(tiltRad, orientationRad)
        lastTiltX = tx
        lastTiltY = ty
    }

    /** 统一从这里打包发送：两种事件路径（触摸/悬停）共用，避免字段拼装不一致。 */
    private fun dispatch(x: Float, y: Float, action: Int, pressure: Float, rightClick: Boolean = false) {
        val s = sender ?: return
        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0f || h <= 0f) return

        val packet = PenProtocol.encodePen(
            seq = s.nextSeq(),
            action = action,
            x = (x / w).coerceIn(0f, 1f),
            y = (y / h).coerceIn(0f, 1f),
            pressure = pressure.coerceIn(0f, 1f),
            tiltX = lastTiltX,
            tiltY = lastTiltY,
            eraser = eraserActive,
            rightClick = rightClick
        )
        s.send(packet)
    }

    private fun notifyListener(hovering: Boolean, inContact: Boolean) {
        val l = listener ?: return
        val now = System.currentTimeMillis()
        // 界面刷新限流；但「状态变化」必须立刻反映，否则按下/抬起的提示会滞后
        val stateChanged = (inContact != lastInContact) || (hovering != lastHovering)
        if (!stateChanged && now - lastNotifyAt < 100) return
        lastNotifyAt = now
        lastInContact = inContact
        lastHovering = hovering

        val s = sender
        l.onPenEvent(
            PenStatus(
                pressure = lastPressure,
                tiltX = lastTiltX,
                tiltY = lastTiltY,
                eraser = eraserActive,
                hovering = hovering,
                inContact = inContact,
                sentPackets = s?.sentPackets ?: 0L,
                droppedPackets = s?.droppedPackets ?: 0L
            )
        )
    }

    /** 清空本地预览笔迹。 */
    fun clearInk() {
        inkCanvas?.drawColor(inkBackgroundColor)
        path.reset()
        invalidate()
    }

    /**
     * 换底色时保留已有笔迹。
     *
     * 不能直接 drawColor 重铺——那样会把笔迹一起擦掉。做法是先把笔迹层复制一份，
     * 用新底色重铺原层，再把复制回来的笔迹画上去。中间不能按颜色匹配逐个替换像素：
     * 笔迹有抗锯齿边缘，颜色替换会留下毛边。
     */
    private fun repaintInkBackground() {
        val bitmap = inkBitmap ?: return
        val canvas = inkCanvas ?: return
        if (bitmap.width <= 0 || bitmap.height <= 0) return

        val preserved = try {
            bitmap.copy(Bitmap.Config.ARGB_8888, false)
        } catch (e: OutOfMemoryError) {
            // 内存紧张时退回「清空」而不是崩溃
            null
        }

        canvas.drawColor(inkBackgroundColor)

        if (preserved != null) {
            canvas.drawBitmap(preserved, 0f, 0f, null)
            preserved.recycle()
        }

        path.reset()
        invalidate()
    }

    /** 视图被移除时兜底抬笔，防止接收端笔尖卡在按下状态。 */
    override fun onDetachedFromWindow() {
        removeCallbacks(longPressRunnable)
        if (inContact) {
            endStroke()
        }
        activePointerId = MotionEvent.INVALID_POINTER_ID
        super.onDetachedFromWindow()
    }
}
