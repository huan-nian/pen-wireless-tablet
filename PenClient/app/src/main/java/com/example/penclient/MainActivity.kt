package com.example.penclient

import android.os.Bundle
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat

class MainActivity : AppCompatActivity() {

    private lateinit var penView: PenCaptureView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        WindowCompat.setDecorFitsSystemWindows(window, false)

        penView = PenCaptureView(this)

        val btnConnect = Button(this).apply {
            text = "连接 192.168.1.107"
            setOnClickListener {
                penView.serverIp = "192.168.1.107"
                penView.connect()
                Toast.makeText(this@MainActivity, "已连接 192.168.1.107", Toast.LENGTH_SHORT).show()
            }
        }

        val btnDisconnect = Button(this).apply {
            text = "断开"
            setOnClickListener {
                penView.disconnect()
                Toast.makeText(this@MainActivity, "已断开", Toast.LENGTH_SHORT).show()
            }
        }

        val buttonBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            addView(btnConnect)
            addView(btnDisconnect)
        }

        val root = FrameLayout(this)
        root.addView(
            penView,
            FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT
            )
        )
        root.addView(
            buttonBar,
            FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT
            ).apply {
                leftMargin = 40
                topMargin = 40
            }
        )

        setContentView(root)
    }
}