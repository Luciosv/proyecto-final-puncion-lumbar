using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using NativeWebSocket;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Calibration WebSocket Client")]
    public class CalibrationWebSocketClient : MonoBehaviour
    {
        [Header("Connection")]
        [Tooltip("WebSocket URL of the CalibrationApp. Overwritten automatically when auto-discovery finds the server.")]
        public string wsUrl = "ws://localhost:8765";
        public bool autoConnect = true;
        public bool autoReconnect = true;

        [Header("Auto-Discovery (UDP)")]
        [Tooltip("Listen for UDP beacons from the CalibrationApp and connect automatically, " +
                 "even when it runs on a different machine.")]
        public bool autoDiscovery = true;
        [Tooltip("UDP port used for discovery (must match CalibrationApp — default 8766).")]
        public int discoveryPort = 8766;

        [Header("Reconnection")]
        [Min(0.5f)] public float reconnectDelayBase = 1f;
        [Min(1f)]   public float reconnectDelayMax  = 30f;

        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;

        public event Action          OnConnected;
        public event Action          OnDisconnected;
        public event Action<string>  OnCalibrationJsonReceived;

        // ── WebSocket state ───────────────────────────────────────────────────
        WebSocket  _ws;
        bool       _closeRequested;
        Coroutine  _reconnectRoutine;

        // ── UDP discovery ─────────────────────────────────────────────────────
        UdpClient  _udp;
        Thread     _discoveryThread;
        string     _pendingUrl;          // written by discovery thread, read in Update
        readonly object _urlLock = new();

        // ── Lifecycle ─────────────────────────────────────────────────────────

        void Start()
        {
            if (autoDiscovery) StartDiscovery();
            if (autoConnect)   Connect();
        }

        void Update()
        {
            // Apply a newly discovered server URL on the Unity main thread.
            string discovered;
            lock (_urlLock)
            {
                discovered  = _pendingUrl;
                _pendingUrl = null;
            }

            if (discovered != null && discovered != wsUrl)
            {
                Debug.Log($"[CalibrationWebSocketClient] Auto-discovered server at {discovered}");
                wsUrl = discovered;

                // Reconnect to the new address.
                if (IsConnected) Disconnect();
                Connect();
            }

            _ws?.DispatchMessageQueue();
        }

        void OnDestroy()
        {
            StopReconnect();
            StopDiscovery();

            if (_ws != null)
            {
                _closeRequested = true;
                try { _ = _ws.Close(); }
                catch { /* ignore */ }
                _ws = null;
            }
        }

        // ── UDP discovery ─────────────────────────────────────────────────────

        void StartDiscovery()
        {
            try
            {
                _udp = new UdpClient(discoveryPort) { EnableBroadcast = true };
                _discoveryThread = new Thread(DiscoveryLoop)
                {
                    IsBackground = true,
                    Name = "CalibrationDiscovery",
                };
                _discoveryThread.Start();
                Debug.Log($"[CalibrationWebSocketClient] Discovery listening on UDP:{discoveryPort}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CalibrationWebSocketClient] Could not start discovery: {ex.Message}");
            }
        }

        void StopDiscovery()
        {
            try { _udp?.Close(); } catch { /* ignore */ }
            _udp = null;
        }

        void DiscoveryLoop()
        {
            var endpoint = new IPEndPoint(IPAddress.Any, discoveryPort);
            while (true)
            {
                try
                {
                    byte[] data = _udp.Receive(ref endpoint);
                    string msg  = Encoding.UTF8.GetString(data);

                    if (!msg.Contains("CalibrationApp")) continue;

                    int    port = ExtractPort(msg);
                    string url  = $"ws://{endpoint.Address}:{port}";

                    lock (_urlLock) _pendingUrl = url;
                }
                catch
                {
                    break; // socket closed → exit thread cleanly
                }
            }
        }

        // Minimal port extraction from {"service":"CalibrationApp","port":8765}
        static int ExtractPort(string json)
        {
            int idx = json.IndexOf("\"port\"", StringComparison.Ordinal);
            if (idx < 0) return 8765;
            int colon = json.IndexOf(':', idx + 6);
            if (colon < 0) return 8765;
            int end = json.IndexOfAny(new[] { ',', '}' }, colon + 1);
            if (end < 0) end = json.Length;
            return int.TryParse(json.Substring(colon + 1, end - colon - 1).Trim(), out int p) ? p : 8765;
        }

        // ── WebSocket ─────────────────────────────────────────────────────────

        public async void Connect()
        {
            if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.Connecting))
                return;

            _closeRequested = false;
            StopReconnect();

            _ws = new WebSocket(wsUrl);

            _ws.OnOpen += () =>
            {
                Debug.Log($"[CalibrationWebSocketClient] Connected to {wsUrl}");
                OnConnected?.Invoke();
            };

            _ws.OnMessage += bytes =>
            {
                string json = Encoding.UTF8.GetString(bytes);
                OnCalibrationJsonReceived?.Invoke(json);
            };

            _ws.OnError += error =>
            {
                Debug.LogWarning($"[CalibrationWebSocketClient] Error: {error}");
            };

            _ws.OnClose += closeCode =>
            {
                Debug.Log($"[CalibrationWebSocketClient] Disconnected (code: {closeCode})");
                OnDisconnected?.Invoke();
                if (!_closeRequested && autoReconnect)
                    _reconnectRoutine = StartCoroutine(ReconnectRoutine());
            };

            try
            {
                await _ws.Connect();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CalibrationWebSocketClient] Connection failed: {ex.Message}");
                OnDisconnected?.Invoke();
                if (!_closeRequested && autoReconnect)
                    _reconnectRoutine = StartCoroutine(ReconnectRoutine());
            }
        }

        public async void Disconnect()
        {
            _closeRequested = true;
            StopReconnect();

            if (_ws != null)
            {
                try { await _ws.Close(); }
                catch { /* ignore */ }
                _ws = null;
            }
        }

        // ── Reconnection ──────────────────────────────────────────────────────

        void StopReconnect()
        {
            if (_reconnectRoutine != null)
            {
                StopCoroutine(_reconnectRoutine);
                _reconnectRoutine = null;
            }
        }

        IEnumerator ReconnectRoutine()
        {
            float delay = reconnectDelayBase;
            while (!_closeRequested)
            {
                Debug.Log($"[CalibrationWebSocketClient] Reconnecting in {delay:F1}s…");
                yield return new WaitForSecondsRealtime(delay);
                if (_closeRequested) break;

                Connect();
                yield return WaitForConnectionResult();

                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    _reconnectRoutine = null;
                    yield break;
                }

                delay = Mathf.Min(delay * 2f, reconnectDelayMax);
            }
            _reconnectRoutine = null;
        }

        IEnumerator WaitForConnectionResult()
        {
            float timeout = 10f;
            float elapsed = 0f;
            while (elapsed < timeout && !_closeRequested)
            {
                if (_ws == null)                          yield break;
                if (_ws.State == WebSocketState.Open)    yield break;
                if (_ws.State == WebSocketState.Closing) yield break;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }
    }
}
