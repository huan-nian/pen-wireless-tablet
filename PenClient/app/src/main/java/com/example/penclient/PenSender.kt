package com.example.penclient

import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/** 一次扫描发现到的接收端。 */
data class DiscoveredHost(val address: String, val port: Int)

/**
 * 负责把笔事件通过 UDP 可靠地、低延迟地送出去。
 *
 * 性能考虑：手写场景下最怕的不是丢包，而是**延迟堆积**。如果发送端用无界队列，
 * 一旦网络或接收端变慢，队列会越积越长，笔迹就会和手差出好几秒。这里用固定长度的
 * 有界队列，并且在入队失败时主动丢掉**最旧**的移动事件：手写时最新的位置永远比
 * 旧位置重要。
 */
class PenSender {

    companion object {
        private const val TAG = "PenSender"

        /** 待发送队列容量。约等于 8~10 帧的笔输入，超出即说明链路已经跟不上。 */
        private const val QUEUE_CAPACITY = 256

        /** 发现接收端时广播探测报文的间隔。 */
        private const val DISCOVER_INTERVAL_MS = 200L
    }

    private val queue = ArrayBlockingQueue<ByteArray>(QUEUE_CAPACITY)
    private val running = AtomicBoolean(false)

    @Volatile
    private var socket: DatagramSocket? = null

    @Volatile
    private var target: InetSocketAddress? = null

    @Volatile
    private var senderThread: Thread? = null

    private val seqCounter = AtomicInteger(0)
    private val sentCount = AtomicLong(0)
    private val droppedCount = AtomicLong(0)

    @Volatile
    var host: String = ""
        private set

    @Volatile
    var port: Int = PenProtocol.DEFAULT_PORT
        private set

    val isConnected: Boolean
        get() = target != null && socket?.isClosed == false

    val sentPackets: Long get() = sentCount.get()
    val droppedPackets: Long get() = droppedCount.get()

    /**
     * 绑定目标地址并启动发送线程。重复调用会先断开旧连接。
     * @return 是否成功（地址无法解析或端口非法时返回 false）
     */
    fun connect(host: String, port: Int): Boolean {
        disconnect()

        val trimmed = host.trim()
        if (trimmed.isEmpty() || port !in 1..65535) {
            Log.e(TAG, "connect rejected: host='$trimmed' port=$port")
            return false
        }

        return try {
            val address = InetAddress.getByName(trimmed)
            val sock = DatagramSocket().apply {
                soTimeout = 0
                // 小报文频繁发送，开大发送缓冲区可减少系统调用阻塞
                sendBufferSize = 256 * 1024
            }
            socket = sock
            target = InetSocketAddress(address, port)
            this.host = trimmed
            this.port = port
            queue.clear()
            running.set(true)
            senderThread = Thread({ sendLoop(sock) }, "pen-sender").apply {
                isDaemon = true
                start()
            }
            Log.d(TAG, "connect ok -> $trimmed:$port")
            true
        } catch (e: Exception) {
            Log.e(TAG, "connect failed: ${e.message}")
            socket = null
            target = null
            false
        }
    }

    fun disconnect() {
        running.set(false)
        senderThread?.interrupt()
        senderThread = null
        socket?.close()
        socket = null
        target = null
        queue.clear()
    }

    /**
     * 把一个笔事件报文交给发送线程。本方法非阻塞，可以在触摸回调里直接调用。
     * @return true 表示已入队，false 表示队列已满、为保住延迟丢弃了该事件
     */
    fun send(packet: ByteArray): Boolean {
        if (target == null) return false
        if (queue.offer(packet)) return true

        // 队列满：丢掉一个最旧的报文给新报文腾位置，优先保住最新位置
        queue.poll()
        droppedCount.incrementAndGet()
        return queue.offer(packet)
    }

    /** 下一个报文序号（单调递增，回绕由接收端按无符号差值处理）。 */
    fun nextSeq(): Int = seqCounter.getAndIncrement()

    private fun sendLoop(sock: DatagramSocket) {
        while (running.get()) {
            try {
                val packet = queue.poll(100, java.util.concurrent.TimeUnit.MILLISECONDS) ?: continue
                val dest = target ?: continue
                sock.send(DatagramPacket(packet, packet.size, dest))
                sentCount.incrementAndGet()
            } catch (e: InterruptedException) {
                Thread.currentThread().interrupt()
                return
            } catch (e: Exception) {
                if (running.get()) {
                    Log.e(TAG, "send failed: ${e.message}")
                }
            }
        }
    }

    /**
     * 在局域网内扫描 PenReceiver 接收端。
     *
     * 做法是向本机所有网段的广播地址反复发探测报文，同时在同一端口上收回复。
     * 需要在后台线程调用。
     *
     * @param timeoutMs 总扫描时长
     * @param onFound 每发现一个接收端回调一次（可能在后台线程触发）
     * @return 扫描结束后去重的结果
     */
    fun discover(timeoutMs: Long = 2000, onFound: (DiscoveredHost) -> Unit = {}): List<DiscoveredHost> {
        val found = LinkedHashMap<String, DiscoveredHost>()

        val sock = try {
            DatagramSocket().apply {
                broadcast = true
                soTimeout = 120
                reuseAddress = true
            }
        } catch (e: Exception) {
            Log.e(TAG, "discover socket failed: ${e.message}")
            return emptyList()
        }

        try {
            val probe = PenProtocol.encodeHello()
            val targets = broadcastTargets()
            Log.d(TAG, "discover on ${targets.size} subnet(s): $targets")

            val deadline = System.currentTimeMillis() + timeoutMs
            while (System.currentTimeMillis() < deadline) {
                for (addr in targets) {
                    try {
                        sock.send(DatagramPacket(probe, probe.size, addr, PenProtocol.DEFAULT_PORT))
                    } catch (e: Exception) {
                        Log.w(TAG, "probe -> $addr failed: ${e.message}")
                    }
                }

                // 收集这一轮内到达的回复
                val recvBuffer = ByteArray(64)
                while (true) {
                    try {
                        val reply = DatagramPacket(recvBuffer, recvBuffer.size)
                        sock.receive(reply)
                        val parsed = parseReply(reply.data, reply.length, reply.address?.hostAddress)
                            ?: continue
                        val key = "${parsed.address}:${parsed.port}"
                        if (found.put(key, parsed) == null) {
                            Log.d(TAG, "found receiver $key")
                            onFound(parsed)
                        }
                    } catch (_: java.net.SocketTimeoutException) {
                        break
                    }
                }

                Thread.sleep(DISCOVER_INTERVAL_MS)
            }
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        } catch (e: Exception) {
            Log.e(TAG, "discover failed: ${e.message}")
        } finally {
            sock.close()
        }

        return found.values.toList()
    }

    private fun parseReply(data: ByteArray, length: Int, fromAddress: String?): DiscoveredHost? {
        if (length < PenProtocol.REPLY_PACKET_SIZE) return null
        val buf = java.nio.ByteBuffer.wrap(data, 0, length).order(java.nio.ByteOrder.LITTLE_ENDIAN)
        if (buf.int != PenProtocol.MAGIC_REPLY) return null
        buf.int // 预留字段
        val port = buf.int
        if (port !in 1..65535) return null
        // 用报文来源地址，而不是回复里自称的地址：多网卡机器上回复里可能是不通的网段
        val from = fromAddress ?: return null
        return DiscoveredHost(from, port)
    }
    /** 计算本机所有活动网段的广播地址。 */
    private fun broadcastTargets(): List<InetAddress> {
        val result = LinkedHashSet<InetAddress>()
        try {
            for (nif in NetworkInterface.getNetworkInterfaces()) {
                if (!nif.isUp || nif.isLoopback) continue
                for (ia in nif.interfaceAddresses) {
                    val address = ia.address
                    if (address is java.net.Inet4Address && !address.isLoopbackAddress) {
                        ia.broadcast?.let { result.add(it) }
                    }
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "enumerate interfaces failed: ${e.message}")
        }
        if (result.isEmpty()) {
            try {
                result.add(InetAddress.getByName("255.255.255.255"))
            } catch (_: Exception) {
            }
        }
        return result.toList()
    }
}
