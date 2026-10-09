package com.example.penclient

/**
 * 全局唯一的发送器。
 *
 * 为什么要做成单例：连接动作发生在连接页，而书写发生在**另一个 Activity** 里。
 * 如果各建一个 PenSender，连上之后再打开书写页就会换成一个没连接的实例，
 * 结果「显示已连接但写不出东西」。UDP socket 也必须在两页之间保持同一个，
 * 否则接收端看到的来源端口会在切换页面时跳变。
 *
 * Activity 只负责设置 [PenSender.disconnect] 的时机，不负责创建实例。
 */
object PenServiceHolder {
    val sender: PenSender by lazy { PenSender() }
}
