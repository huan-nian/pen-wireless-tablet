package com.example.penclient

import androidx.annotation.StringRes

/** 笔迹在平板上保留多久。电脑上的笔迹不受这个设置影响。 */
enum class InkRetention(
    val timeoutMs: Long,
    @StringRes val labelRes: Int
) {
    /** 永不自动清除。 */
    NEVER(-1L, R.string.ink_mode_never),

    /** 停笔 5 秒后清除。 */
    SECONDS_5(5_000L, R.string.ink_mode_5s),

    /** 停笔 10 秒后清除。 */
    SECONDS_10(10_000L, R.string.ink_mode_10s),

    /** 停笔 1 分钟后清除。 */
    MINUTE_1(60_000L, R.string.ink_mode_1m);

    /** 是否会自动清除。 */
    val autoClears: Boolean get() = timeoutMs > 0
}
