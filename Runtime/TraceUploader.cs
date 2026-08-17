using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace VideoStream
{
    /// <summary>
    /// PIPETRACE tracing for the Unity controller.
    ///
    /// Emits `[PIPETRACE] dev=ctrl <key=value ...> ts=<nanoTime>` lines and uploads
    /// them to the log-server NEW API `POST /run/upload` (X-Device: ctrl, X-Append: 1),
    /// which groups controller + gateway logs into one run on the PC. The collected
    /// data (encode timing, frame sizes, timestamps) is analyzed afterwards.
    ///
    /// Frame events are sampled 1 in <see cref="EveryNFrames"/> by frameId; loss-cause
    /// events go through <see cref="Log"/> and are always emitted.
    /// </summary>
    public static class TraceUploader
    {
        public static bool Enabled = true;
        public static int EveryNFrames = 30;
        public static string ServerUrl = "http://192.168.1.33:9101";

        /// <summary>
        /// Test-session id. When set, uploads carry X-Run=&lt;sessionId&gt; and the
        /// log-server uses it as the run folder name, so one test session stays in a
        /// single runs/&lt;sessionId&gt;/ directory.
        /// </summary>
        public static string SessionId = "";

        /// <summary>
        /// Also mirror each PIPETRACE line to logcat (adb fallback). Default OFF:
        /// Debug.Log on Android runs on the main thread and can jitter the frame
        /// pacing, so tracing normally uploads without touching logcat.
        /// </summary>
        public static bool LogToLogcat = false;

        const int FlushIntervalMs = 5000;
        const int MaxBufferedLines = 4000;
        const int MaxDatagramPayload = 1400; // matches UdpFramer on the receiver

        static readonly object Lock = new object();
        static readonly List<string> Buffer = new List<string>();
        static float _lastFlushTime;

        /// <summary>Full-count event (always emitted).</summary>
        public static void Log(string line)
        {
            Append(line);
        }

        /// <summary>
        /// Cheap sampling check — call BEFORE building the trace string so frame
        /// events do no string work on the streaming path when not sampled.
        /// </summary>
        public static bool ShouldTraceFrame(int frameId)
        {
            return Enabled && EveryNFrames > 0 && frameId >= 0 && frameId % EveryNFrames == 0;
        }

        /// <summary>Frame event, sampled 1 in EveryNFrames by frameId.</summary>
        public static void TraceFrame(string line, int frameId)
        {
            if (!ShouldTraceFrame(frameId)) return;
            Append(line);
        }

        /// <summary>Drive from MonoBehaviour Update().</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            if (Time.unscaledTime - _lastFlushTime >= FlushIntervalMs / 1000f)
            {
                _lastFlushTime = Time.unscaledTime;
                Flush();
            }
        }

        /// <summary>Force a flush of pending lines (call on stream stop).</summary>
        public static void FlushNow()
        {
            _lastFlushTime = 0f;
            Tick();
        }

        /// <summary>Estimated datagram count for a frame, same as UdpFramer.MAX_DATAGRAM_PAYLOAD.</summary>
        public static int FragmentCount(int size)
        {
            return size <= MaxDatagramPayload ? 1 : (size + MaxDatagramPayload - 1) / MaxDatagramPayload;
        }

        static void Append(string line)
        {
            if (!Enabled) return;
            var entry = $"[PIPETRACE] dev=ctrl {line} ts={NowNs()}";
            if (LogToLogcat)
            {
                // Optional adb fallback; off by default to keep the main thread clean.
                UnityEngine.Debug.Log(entry);
            }
            lock (Lock)
            {
                Buffer.Add(entry);
                if (Buffer.Count > MaxBufferedLines)
                {
                    Buffer.RemoveRange(0, Buffer.Count - MaxBufferedLines);
                }
            }
        }

        static long NowNs()
        {
            // Monotonic high-res clock; double avoids long overflow, ms-grade precision is enough.
            return (long)((double)Stopwatch.GetTimestamp() * 1_000_000_000.0 / Stopwatch.Frequency);
        }

        static void Flush()
        {
            string body;
            lock (Lock)
            {
                if (Buffer.Count == 0) return;
                body = string.Join("\n", Buffer);
                Buffer.Clear();
            }

            var url = ServerUrl.TrimEnd('/') + "/run/upload";
            var req = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer()
            };
            req.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
            req.SetRequestHeader("X-Device", "ctrl");
            req.SetRequestHeader("X-Append", "1");
            if (!string.IsNullOrEmpty(SessionId))
            {
                req.SetRequestHeader("X-Run", SessionId);
            }
            req.timeout = 3;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                var ok = req.result == UnityWebRequest.Result.Success;
                if (!ok)
                {
                    // Re-queue on failure so the next flush retries instead of dropping.
                    lock (Lock)
                    {
                        Buffer.InsertRange(0, body.Split('\n'));
                        if (Buffer.Count > MaxBufferedLines * 2)
                        {
                            Buffer.RemoveRange(0, Buffer.Count - MaxBufferedLines * 2);
                        }
                    }
                }
                req.Dispose();
            };
        }
    }
}
