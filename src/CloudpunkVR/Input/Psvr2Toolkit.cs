using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// Client for the PSVR2 Toolkit (github.com/BnuuySolutions/PSVR2Toolkit): adaptive triggers and eye tracking.
    /// TCP 127.0.0.1:3364, 8-byte header (uint16 command, 2 pad, int32 length, little endian) + data.
    /// </summary>
    internal static class Psvr2Toolkit
    {
        public const byte Left = 0, Right = 1;
        public const ushort CmdTriggerOff = 6, CmdTriggerFeedback = 7, CmdTriggerWeapon = 8, CmdTriggerVibration = 9;

        private const int Port = 3364;
        private const ushort IpcVersion = 1; // every Toolkit release accepts v1
        private const ushort CmdHello = 2, CmdHelloAnswer = 3, CmdAskGaze = 4, CmdGaze = 5;
        private const int GazeEveryMs = 33;

        /// <summary>Set every frame on the main thread.</summary>
        public static volatile bool WantTriggers, WantGaze;
        public static volatile bool Connected;

        private static Thread worker;
        private static volatile bool stopping;
        private static readonly object queueLock = new object();
        private static readonly Queue<byte[]> queue = new Queue<byte[]>();
        private static readonly object streamLock = new object();
        private static NetworkStream live;

        public static void Tick(bool onPsvr2)
        {
            WantTriggers = onPsvr2 && VRConfig.AdaptiveTriggers.Value;
            // gaze for menus (EyeSelect) and on-foot LookHighlight
            WantGaze = onPsvr2 && ((VRConfig.EyeSelect.Value && UI.VRUI.Interactive) || UI.LookHighlight.Active);
            if ((WantTriggers || WantGaze) && worker == null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "Cloudpunk VR - PSVR2 Toolkit" };
                worker.Start();
            }
        }

        public static byte[] Message(ushort command, params byte[] data)
        {
            var m = new byte[8 + data.Length];
            BitConverter.GetBytes(command).CopyTo(m, 0);
            BitConverter.GetBytes(data.Length).CopyTo(m, 4);
            data.CopyTo(m, 8);
            return m;
        }

        public static void Send(byte[] message)
        {
            lock (queueLock) { queue.Enqueue(message); Monitor.Pulse(queueLock); }
        }

        // ---- Eye gaze ----

        private static readonly object gazeLock = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static Vector3 gaze;
        private static long gazeTicks = long.MinValue;
        private static bool loggedGaze;

        /// <summary>Latest gaze direction in head space (Unity axes), if fresh.</summary>
        public static bool TryGetGaze(float maxAgeSeconds, out Vector3 direction)
        {
            lock (gazeLock)
            {
                direction = gaze;
                return gazeTicks != long.MinValue && clock.ElapsedTicks - gazeTicks < maxAgeSeconds * Stopwatch.Frequency;
            }
        }

        /// <summary>
        /// v1, 33 bytes per eye (left, right): origin valid, origin mm (3f), dir valid, dir (3f), pupil valid,
        /// pupil mm (f), blink valid, blinking. Toolkit x points left.
        /// </summary>
        private static void ReadGaze(byte[] d)
        {
            Vector3 sum = Vector3.zero;
            int eyes = 0;
            for (int eye = 0; eye < 2; eye++)
            {
                int o = eye * 33;
                bool directionValid = d[o + 13] != 0;
                bool blinking = d[o + 31] != 0 && d[o + 32] != 0;
                if (!directionValid || blinking) continue;
                var v = new Vector3(-BitConverter.ToSingle(d, o + 14), BitConverter.ToSingle(d, o + 18), BitConverter.ToSingle(d, o + 22));
                if (v.sqrMagnitude < 0.5f || v.z <= 0f) continue;
                sum += v.normalized;
                eyes++;
            }
            if (eyes == 0) return;
            var dir = (sum / eyes).normalized;
            lock (gazeLock) { gaze = dir; gazeTicks = clock.ElapsedTicks; }
            if (!loggedGaze)
            {
                loggedGaze = true;
                Plugin.L.LogInfo($"Eye tracking: gaze received from the PSVR2 Toolkit ({Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg:0}° right, {Mathf.Asin(dir.y) * Mathf.Rad2Deg:0}° up).");
            }
        }

        // ---- Connection (background thread) ----

        private static void Run()
        {
            bool loggedMissing = false;
            byte[] askGaze = Message(CmdAskGaze);
            while (!stopping)
            {
                if (!WantTriggers && !WantGaze) { Thread.Sleep(1000); continue; }
                try
                {
                    using (var client = new TcpClient())
                    {
                        client.NoDelay = true;
                        var attempt = client.BeginConnect("127.0.0.1", Port, null, null);
                        if (!attempt.AsyncWaitHandle.WaitOne(1000)) throw new IOException("no answer on port " + Port);
                        client.EndConnect(attempt);
                        var stream = client.GetStream();
                        stream.ReadTimeout = 2000;

                        var hello = new byte[8];
                        BitConverter.GetBytes(IpcVersion).CopyTo(hello, 0);
                        BitConverter.GetBytes((uint)Process.GetCurrentProcess().Id).CopyTo(hello, 4);
                        var h = Message(CmdHello, hello);
                        stream.Write(h, 0, h.Length);
                        var answer = ReadExactly(stream, 12); // header + result (1), padding (1), server version (2)
                        if (BitConverter.ToUInt16(answer, 0) != CmdHelloAnswer || answer[8] != 1)
                            throw new IOException($"hello refused (result {answer[8]}, server version {BitConverter.ToUInt16(answer, 10)})");

                        lock (queueLock) queue.Clear();
                        lock (streamLock) live = stream;
                        Connected = true;
                        loggedMissing = false;
                        Plugin.L.LogInfo($"PSVR2 Toolkit connected (protocol {BitConverter.ToUInt16(answer, 10)}): adaptive triggers {(WantTriggers ? "on" : "off")}, eye tracking {(WantGaze ? "on" : "when menus are open")}.");

                        long nextGaze = 0;
                        while (!stopping)
                        {
                            byte[] next = null;
                            lock (queueLock)
                            {
                                if (queue.Count == 0) Monitor.Wait(queueLock, WantGaze ? GazeEveryMs : 1000);
                                if (queue.Count > 0) next = queue.Dequeue();
                            }
                            if (next != null)
                            {
                                lock (streamLock) stream.Write(next, 0, next.Length);
                                Thread.Sleep(15); // the Toolkit reads one command per receive: keep them apart
                            }
                            if (WantGaze && clock.ElapsedMilliseconds >= nextGaze)
                            {
                                nextGaze = clock.ElapsedMilliseconds + GazeEveryMs;
                                byte[] head, data;
                                lock (streamLock)
                                {
                                    stream.Write(askGaze, 0, askGaze.Length);
                                    head = ReadExactly(stream, 8);
                                    int length = BitConverter.ToInt32(head, 4);
                                    if (length < 0 || length > 1024) throw new IOException("unexpected gaze answer length " + length);
                                    data = ReadExactly(stream, length);
                                }
                                if (BitConverter.ToUInt16(head, 0) == CmdGaze && data.Length >= 66) ReadGaze(data);
                            }
                            else if (next == null && !WantTriggers && !WantGaze) break;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (Connected) Plugin.L.LogInfo("PSVR2 Toolkit: connection lost (" + e.Message + ").");
                    else if (!loggedMissing) { loggedMissing = true; Plugin.L.LogInfo("PSVR2 Toolkit not found (" + e.Message + "); adaptive triggers and eye tracking stay off. Retrying every few seconds."); }
                }
                lock (streamLock) live = null;
                Connected = false;
                if (!stopping) Thread.Sleep(5000);
            }
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n <= 0) throw new IOException("closed by the Toolkit");
                got += n;
            }
            return buffer;
        }

        /// <summary>Writes from the calling thread, for exit when the worker may not run again.</summary>
        public static bool SendNow(params byte[][] messages)
        {
            lock (streamLock)
            {
                if (live == null) return false;
                try
                {
                    foreach (var m in messages) { live.Write(m, 0, m.Length); live.Flush(); Thread.Sleep(15); }
                    return true;
                }
                catch (Exception) { return false; }
            }
        }

        public static void Stop()
        {
            stopping = true;
            lock (queueLock) Monitor.Pulse(queueLock);
        }
    }
}
