package com.example.penclient

import android.content.Context
import android.util.Log
import android.view.MotionEvent
import android.view.View
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors

class PenCaptureView(context: Context) : View(context) {

    companion object {
        const val MAGIC = 0x50454E31
        const val ACTION_DOWN = 0
        const val ACTION_MOVE = 1
        const val ACTION_UP = 2
        const val PORT = 8888
        const val TAG = "PenClient"
    }

    var serverIp: String = "192.168.1.107"

    private var socket: DatagramSocket? = null
    private var serverAddress: InetAddress? = null

    // 单线程池：所有 UDP 发送都在这个后台线程执行
    private val sendExecutor = Executors.newSingleThreadExecutor()

    fun connect() {
        try {
            socket?.close()
            socket = DatagramSocket()
            serverAddress = InetAddress.getByName(serverIp)
            Log.d(TAG, "connect ok -> $serverIp")
        } catch (e: Exception) {
            socket = null
            serverAddress = null
            Log.e(TAG, "connect failed: ${e.message}")
        }
    }

    fun disconnect() {
        socket?.close()
        socket = null
        serverAddress = null
        Log.d(TAG, "disconnected")
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        val toolType = event.getToolType(0)
        if (toolType != MotionEvent.TOOL_TYPE_STYLUS) return false

        val action = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN,
            MotionEvent.ACTION_POINTER_DOWN -> ACTION_DOWN
            MotionEvent.ACTION_MOVE -> ACTION_MOVE
            MotionEvent.ACTION_UP,
            MotionEvent.ACTION_POINTER_UP,
            MotionEvent.ACTION_CANCEL -> ACTION_UP
            else -> return true
        }

        val w = width.toFloat()
        val h = height.toFloat()
        if (w <= 0 || h <= 0) return true

        for (i in 0 until event.historySize) {
            sendPacket(
                action,
                event.getHistoricalX(0, i) / w,
                event.getHistoricalY(0, i) / h,
                event.getHistoricalPressure(0, i),
                event
            )
        }

        sendPacket(action, event.x / w, event.y / h, event.pressure, event)
        return true
    }

    private fun sendPacket(
        action: Int,
        x: Float,
        y: Float,
        pressure: Float,
        event: MotionEvent
    ) {
        val s = socket
        val addr = serverAddress
        if (s == null || addr == null) {
            Log.e(TAG, "send skipped: socket=$socket addr=$serverAddress")
            return
        }

        val tilt = event.getAxisValue(MotionEvent.AXIS_TILT)
        val orientation = event.getAxisValue(MotionEvent.AXIS_ORIENTATION)

        val buf = ByteBuffer.allocate(32).order(ByteOrder.LITTLE_ENDIAN)
        buf.putInt(MAGIC)
        buf.putInt(action)
        buf.putFloat(x)
        buf.putFloat(y)
        buf.putFloat(pressure)
        buf.putFloat(tilt)
        buf.putFloat(orientation)
        buf.putFloat(0f)

        val packet = buf.array()

        // 关键改动：把发送扔到后台线程，避免 NetworkOnMainThreadException
        sendExecutor.execute {
            try {
                s.send(DatagramPacket(packet, packet.size, addr, PORT))
            } catch (e: Exception) {
                Log.e(TAG, "send failed: ${e.message}")
            }
        }
    }

    override fun onDetachedFromWindow() {
        super.onDetachedFromWindow()
        socket?.close()
        socket = null
        sendExecutor.shutdown()
    }
}