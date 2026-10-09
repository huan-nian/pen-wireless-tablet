package com.example.penclient

import android.content.Context
import android.graphics.Color

/**
 * 平板上需要记住的设置。
 *
 * 单独抽出来是因为连接页和书写页都要读写这些值：连上之后连接页会销毁、
 * 书写页接管，如果两边各写一份 SharedPreferences 逻辑很容易漏字段。
 */
class ClientSettings(context: Context) {

    private val prefs = context.applicationContext
        .getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    var host: String
        get() = prefs.getString(KEY_HOST, "").orEmpty()
        set(value) = prefs.edit().putString(KEY_HOST, value).apply()

    var port: Int
        get() = prefs.getInt(KEY_PORT, PenProtocol.DEFAULT_PORT)
        set(value) = prefs.edit().putInt(KEY_PORT, value).apply()

    var keepScreenOn: Boolean
        get() = prefs.getBoolean(KEY_KEEP_SCREEN_ON, true)
        set(value) = prefs.edit().putBoolean(KEY_KEEP_SCREEN_ON, value).apply()

    /** 书写区背景色。默认暖白，比纯白柔和，长时间书写不刺眼。 */
    var canvasColor: Int
        get() = prefs.getInt(KEY_CANVAS_COLOR, DEFAULT_CANVAS_COLOR)
        set(value) = prefs.edit().putInt(KEY_CANVAS_COLOR, value).apply()

    /** 笔迹保留时长。存名字而不是序号，这样以后插入新选项也不会串位。 */
    var inkRetention: InkRetention
        get() {
            val name = prefs.getString(KEY_INK_RETENTION, null) ?: return InkRetention.NEVER
            return runCatching { InkRetention.valueOf(name) }.getOrDefault(InkRetention.NEVER)
        }
        set(value) = prefs.edit().putString(KEY_INK_RETENTION, value.name).apply()

    private companion object {
        const val PREFS = "pen_client"
        const val KEY_HOST = "host"
        const val KEY_PORT = "port"
        const val KEY_KEEP_SCREEN_ON = "keep_screen_on"
        const val KEY_CANVAS_COLOR = "canvas_color"
        const val KEY_INK_RETENTION = "ink_retention"

        /** 与 colors.xml 里的 canvas_default 保持一致（暖白）。 */
        val DEFAULT_CANVAS_COLOR = Color.parseColor("#F7F5F0")
    }
}
