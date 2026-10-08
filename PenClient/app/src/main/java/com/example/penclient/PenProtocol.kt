package com.example.penclient

import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.sin

/**
 * 与 Windows 接收端共用的线协议定义。
 *
 * 定长报文，全部小端序（Little Endian）。两种报文用 magic 区分：
 *
 *   PEN 报文  magic = 0x504E4232 ("PNB2")，36 字节
 *     0  uint32  magic
 *     4  uint32  seq        单调递增序号，接收端据此统计丢包
 *     8  int32   action     见 Action
 *    12  float   x          归一化 [0,1]
 *    16  float   y          归一化 [0,1]
 *    20  float   pressure   [0,1]
 *    24  float   tiltX      相对屏幕 X 轴倾角，度，[-90,90]（右倾为正）
 *    28  float   tiltY      相对屏幕 Y 轴倾角，度，[-90,90]（向自己倾为负）
 *    32  int32   flags      位 0 = 橡皮端（笔尾）
 *
 *   HELLO 报文 magic = 0x504E4230 ("PNB0")，8 字节，用于自动发现接收端
 *     0  uint32  magic
 *     4  uint32  reserved
 *
 * 兼容性：旧版 32 字节报文（magic "PEN1"）与新版语义不同，接收端只认新 magic，
 * 不会误把旧包当新包解析。
 */
object PenProtocol {

    /** PNB2：笔事件报文 */
    const val MAGIC_PEN: Int = 0x504E4232

    /** PNB0：发现用探测报文 */
    const val MAGIC_HELLO: Int = 0x504E4230

    /** PNB1：接收端对探测报文的回复 */
    const val MAGIC_REPLY: Int = 0x504E4231

    const val DEFAULT_PORT: Int = 8888

    const val PEN_PACKET_SIZE: Int = 36
    const val HELLO_PACKET_SIZE: Int = 8
    const val REPLY_PACKET_SIZE: Int = 12

    /** 笔动作。悬停（Hover）用于让 Windows 在落笔前就显示出笔尖位置。 */
    object Action {
        const val DOWN = 0
        const val MOVE = 1
        const val UP = 2
        const val HOVER = 3
    }

    const val FLAG_ERASER: Int = 1

    /**
     * 平板端检测到「笔尖静止长按」，要求在这一笔抬起时于该位置弹出右键菜单。
     *
     * 为什么由平板端识别：实测发现注入到 Windows 的笔输入**不会**触发系统的
     * 「笔静止长按 → 右键」手势（笔按下静止 1.1 秒也不产生右键事件），
     * 所以长按右键必须由平板端判定、并显式要求接收端注入鼠标右键。
     */
    const val FLAG_RIGHT_CLICK: Int = 2

    /**
     * 把 Android 的「倾斜角 + 方位角」换算成 Windows 的 TiltX / TiltY（单位为度）。
     *
     * Android 的 AXIS_TILT 是笔身相对屏幕法线的夹角（弧度，0 表示笔垂直于屏幕），
     * AXIS_ORIENTATION 是该倾斜的方向（弧度，-π..π）。Windows 则分别给出笔相对
     * X 轴与 Y 轴的倾角，最大 ±90 度，所以需要做一次球面分解。
     *
     * @return Pair(tiltX, tiltY)，单位为度
     */
    fun tiltToWindows(tiltRad: Float, orientationRad: Float): Pair<Float, Float> {
        val magnitude = (abs(tiltRad) * 180.0 / PI).coerceIn(0.0, 90.0)
        if (magnitude < 0.01f) return 0f to 0f
        val x = magnitude * sin(orientationRad.toDouble())
        val y = -magnitude * cos(orientationRad.toDouble())
        return x.toFloat().coerceIn(-90f, 90f) to y.toFloat().coerceIn(-90f, 90f)
    }

    /** 打包一个笔事件报文。 */
    fun encodePen(
        seq: Int,
        action: Int,
        x: Float,
        y: Float,
        pressure: Float,
        tiltX: Float,
        tiltY: Float,
        eraser: Boolean,
        rightClick: Boolean = false
    ): ByteArray {
        val buf = ByteBuffer.allocate(PEN_PACKET_SIZE).order(ByteOrder.LITTLE_ENDIAN)
        buf.putInt(MAGIC_PEN)
        buf.putInt(seq)
        buf.putInt(action)
        buf.putFloat(x)
        buf.putFloat(y)
        buf.putFloat(pressure)
        buf.putFloat(tiltX)
        buf.putFloat(tiltY)
        var flags = 0
        if (eraser) flags = flags or FLAG_ERASER
        if (rightClick) flags = flags or FLAG_RIGHT_CLICK
        buf.putInt(flags)
        return buf.array()
    }

    /** 打包发现探测报文。 */
    fun encodeHello(reserved: Int = 0): ByteArray {
        val buf = ByteBuffer.allocate(HELLO_PACKET_SIZE).order(ByteOrder.LITTLE_ENDIAN)
        buf.putInt(MAGIC_HELLO)
        buf.putInt(reserved)
        return buf.array()
    }

    /** 校验一段字节是否为合法的笔事件报文。 */
    fun isPenPacket(data: ByteArray, length: Int): Boolean {
        if (length < PEN_PACKET_SIZE) return false
        return ByteBuffer.wrap(data, 0, 4).order(ByteOrder.LITTLE_ENDIAN).int == MAGIC_PEN
    }
}
