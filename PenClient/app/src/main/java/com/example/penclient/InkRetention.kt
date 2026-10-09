package com.example.penclient

/** 笔迹在平板上保留多久。电脑上的笔迹不受这个设置影响。 */
enum class InkRetention(val timeoutMs: Long) {
    /** 永不自动清除。 */
    NEVER(-1L),

    /** 停笔 5 秒后清除。 */
    SECONDS_5(5_000L),

    /** 停笔 10 秒后清除。 */
    SECONDS_10(10_000L),

    /** 停笔 1 分钟后清除。 */
    MINUTE_1(60_000L);

    /** 是否会自动清除。 */
    val autoClears: Boolean get() = timeoutMs > 0
}
