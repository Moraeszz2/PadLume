using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Padlume
{
    public sealed class RemoteControllerInfo
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string BatteryText { get; set; } = "";
        public double BatteryPercent { get; set; }
        public bool IsSelected { get; set; }
        public bool IsBlocked { get; set; }
    }

    /// <summary>
    /// Minimal local HTTP server that lets a phone on the same Wi-Fi/LAN see the controller list and
    /// switch which one has priority — the same action as clicking a controller in the list inside the
    /// app itself. There's no authentication: anyone who can reach this PC on the local network can use
    /// it while it's running. That's an acceptable tradeoff for a LAN-only convenience feature, but it's
    /// exactly why this stays off by default and only starts when the user explicitly checks the box.
    /// </summary>
    public sealed class RemoteControlServer : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        private const string FirewallRuleName = "Padlume Phone Control";

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;

        public int Port { get; private set; }
        public bool IsRunning => _listener?.IsListening == true;

        /// <summary>Supplies a fresh snapshot of the controller list on demand — called from a background thread, so the implementation must marshal onto the UI thread itself.</summary>
        public Func<IReadOnlyList<RemoteControllerInfo>>? GetControllers { get; set; }

        /// <summary>Requests that the controller with this history key become the selected/prioritized one — called from a background thread.</summary>
        public Action<string>? SelectController { get; set; }

        public bool Start(int port)
        {
            Stop();

            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://+:{port}/");
                listener.Start();
                _listener = listener;
                Port = port;

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ListenLoop(listener, _cts.Token));

                // Windows blocks unsolicited inbound connections on any port by default, on every
                // network profile (Private, Public, whatever) — without an explicit firewall rule, the
                // phone's requests get silently dropped and the listener above never even sees them.
                // Best-effort: if this fails (e.g. netsh missing), the server still runs and may still
                // work if a rule already exists from a previous run.
                AddFirewallRule(port);
                return true;
            }
            catch (Exception ex)
            {
                // Most common cause: the port is already taken by something else. Since Padlume already
                // runs elevated (app.manifest), lack of URL ACL privilege isn't normally the problem.
                App.Log("RemoteControlServer", $"Start({port}) failed: {ex.Message}");
                _listener = null;
                return false;
            }
        }

        public void Stop()
        {
            // Nothing to tear down yet on the very first Start() of a session (Start() always calls
            // Stop() first) — skips the pointless "delete a rule that was never created" netsh call and
            // its harmless-but-noisy "no rule found" log entry.
            if (_listener == null)
                return;

            _cts?.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
            try { _listener.Close(); } catch { /* already closed */ }
            _listener = null;
            _cts?.Dispose();
            _cts = null;

            RemoveFirewallRule();
        }

        /// <summary>Adds an inbound-allow rule for this port, scoped to this exe, on all profiles. Doesn't
        /// need to delete a previous rule first — Start() always calls Stop() (which does that) before
        /// reaching this point, so by the time we get here any earlier rule is already gone.</summary>
        private static void AddFirewallRule(int port)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
                return;

            RunNetsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow " +
                     $"protocol=TCP localport={port} program=\"{exePath}\" profile=any");
        }

        private static void RemoveFirewallRule() =>
            RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");

        private static void RunNetsh(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return;

                // Same deadlock-avoidance pattern as ControllerDeviceLock.SetEnabled: read both streams
                // asynchronously before waiting, with a timeout and a kill fallback, so a hung netsh.exe
                // can't hang the caller.
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(5000))
                {
                    App.Log("RemoteControlServer", $"netsh {arguments} didn't respond within 5s, terminating.");
                    try { process.Kill(entireProcessTree: true); } catch { /* may have already exited on its own */ }
                    return;
                }

                if (process.ExitCode != 0)
                    App.Log("RemoteControlServer", $"netsh {arguments} returned {process.ExitCode}. {stdoutTask.Result}{stderrTask.Result}".Trim());
            }
            catch (Exception ex)
            {
                App.Log("RemoteControlServer", $"netsh {arguments} threw: {ex.Message}");
            }
        }

        private async Task ListenLoop(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch
                {
                    return; // listener was stopped
                }

                _ = Task.Run(() => HandleRequest(context), token);
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod;
            // HEAD must get the exact same headers a GET would, but never a body — the HTTP spec
            // requires it, and .NET's HttpListenerResponse enforces it internally: if you call
            // OutputStream.Write for a HEAD request, it treats the allowed body size as zero
            // regardless of Content-Length, throws "Bytes to be written to the stream exceed the
            // Content-Length bytes size specified", and the connection gets torn down mid-response —
            // which shows up client-side as a bare "connection closed" with no useful error. Confirmed
            // by reproducing it locally with `curl -I`. isHead here makes every branch below skip the
            // body write instead of special-casing each one.
            bool isHead = method == "HEAD";

            try
            {
                if ((method == "GET" || isHead) && path == "/")
                {
                    WriteResponse(response, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(IndexHtml), isHead);
                }
                else if ((method == "GET" || isHead) && path == "/api/controllers")
                {
                    var list = GetControllers?.Invoke() ?? Array.Empty<RemoteControllerInfo>();
                    WriteResponse(response, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list, JsonOptions), isHead);
                }
                else if (method == "POST" && path == "/api/select")
                {
                    using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                    var body = reader.ReadToEnd();
                    var payload = JsonSerializer.Deserialize<SelectPayload>(body, JsonOptions);
                    if (!string.IsNullOrEmpty(payload?.Key))
                        SelectController?.Invoke(payload.Key);
                    WriteResponse(response, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"), isHead: false);
                }
                else
                {
                    response.StatusCode = 404;
                    WriteResponse(response, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"), isHead);
                }

                App.Log("RemoteControlServer", $"{method} {path} from {request.RemoteEndPoint} -> {response.StatusCode}");
            }
            catch (Exception ex)
            {
                App.Log("RemoteControlServer", $"{method} {path} from {request.RemoteEndPoint} failed: {ex}");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { /* connection may already be gone */ }
            }
        }

        private static void WriteResponse(HttpListenerResponse response, string contentType, byte[] bytes, bool isHead)
        {
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            if (!isHead)
                response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        private sealed class SelectPayload
        {
            public string? Key { get; set; }
        }

        /// <summary>Best-effort guess at the LAN IPv4 address a phone on the same Wi-Fi could use to reach this PC — the first non-loopback address on an interface that's actually up.</summary>
        public static string? GetLocalIPAddress()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose() => Stop();

        private const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="apple-mobile-web-app-title" content="Padlume">
<meta name="theme-color" content="#0A0A0C">
<title>Padlume</title>
<link rel="apple-touch-icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Crect width='24' height='24' rx='5.3' fill='%232C4FD6'/%3E%3Cpath fill='white' transform='translate(2.2,2.2) scale(0.817)' d='M21,6H3C1.9,6,1,6.9,1,8v8c0,1.1,0.9,2,2,2h18c1.1,0,2-0.9,2-2V8C23,6.9,22.1,6,21,6z M11,13H8v3H6v-3H3v-2h3V8h2v3h3V13z M15.5,15c-0.83,0-1.5-0.67-1.5-1.5s0.67-1.5,1.5-1.5s1.5,0.67,1.5,1.5S16.33,15,15.5,15z M19.5,12c-0.83,0-1.5-0.67-1.5-1.5S18.67,9,19.5,9s1.5,0.67,1.5,1.5S20.33,12,19.5,12z'/%3E%3C/svg%3E">
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Crect width='24' height='24' rx='5.3' fill='%232C4FD6'/%3E%3Cpath fill='white' transform='translate(2.2,2.2) scale(0.817)' d='M21,6H3C1.9,6,1,6.9,1,8v8c0,1.1,0.9,2,2,2h18c1.1,0,2-0.9,2-2V8C23,6.9,22.1,6,21,6z M11,13H8v3H6v-3H3v-2h3V8h2v3h3V13z M15.5,15c-0.83,0-1.5-0.67-1.5-1.5s0.67-1.5,1.5-1.5s1.5,0.67,1.5,1.5S16.33,15,15.5,15z M19.5,12c-0.83,0-1.5-0.67-1.5-1.5S18.67,9,19.5,9s1.5,0.67,1.5,1.5S20.33,12,19.5,12z'/%3E%3C/svg%3E">
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; -webkit-touch-callout: none; user-select: none; }
  html, body { height: 100%; }
  body {
    margin: 0;
    padding: calc(env(safe-area-inset-top) + 18px) calc(env(safe-area-inset-right) + 14px)
             calc(env(safe-area-inset-bottom) + 24px) calc(env(safe-area-inset-left) + 14px);
    background:
      radial-gradient(600px 320px at 15% -8%, rgba(59,130,246,0.16), transparent 60%),
      radial-gradient(500px 260px at 100% 0%, rgba(91,134,255,0.10), transparent 55%),
      #08080A;
    color: #F8FAFC;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    -webkit-font-smoothing: antialiased;
    overscroll-behavior-y: contain;
    min-height: 100%;
  }
  .header { display: flex; align-items: center; gap: 12px; margin-bottom: 4px; }
  .logo {
    width: 38px; height: 38px; border-radius: 11px; flex: none;
    background: linear-gradient(160deg, #6C93FF, #2C4FD6);
    box-shadow: 0 0 0 1px rgba(255,255,255,0.08) inset, 0 6px 18px -4px rgba(59,130,246,0.65);
    display: flex; align-items: center; justify-content: center;
  }
  .logo svg { width: 21px; height: 21px; fill: white; }
  h1 {
    font-size: 18px; margin: 0; font-weight: 800; letter-spacing: -0.3px;
    background: linear-gradient(90deg, #FFFFFF, #C7D6FF);
    -webkit-background-clip: text; background-clip: text; -webkit-text-fill-color: transparent;
  }
  .live { display: flex; align-items: center; gap: 6px; font-size: 11px; color: #6EE7A8; margin-top: 3px; font-weight: 600; letter-spacing: 0.2px; }
  .live .radar { position: relative; width: 7px; height: 7px; }
  .live .radar::before, .live .radar::after {
    content: ''; position: absolute; inset: 0; border-radius: 50%; background: #22C55E;
  }
  .live .radar::before { animation: ping 1.8s cubic-bezier(0,0,0.2,1) infinite; }
  .live .radar::after { box-shadow: 0 0 6px 1px #22C55E; }
  @keyframes ping { 0% { transform: scale(1); opacity: 0.7; } 100% { transform: scale(3.2); opacity: 0; } }
  p.hint { color: #8A93A3; font-size: 13px; margin: 18px 2px 14px; letter-spacing: 0.1px; }
  .card {
    background: linear-gradient(180deg, rgba(255,255,255,0.03), rgba(255,255,255,0));
    background-color: #121215;
    border: 1.5px solid #26262C; border-radius: 18px;
    padding: 14px 15px; margin-bottom: 10px; display: flex; align-items: center; gap: 13px;
    transition: border-color .18s, background-color .18s, transform .1s, box-shadow .18s;
  }
  .card.rise { animation: rise .25s ease-out both; }
  @keyframes rise { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: translateY(0); } }
  .card.selected {
    border-color: #3B82F6; background-color: #121A2E;
    box-shadow: 0 0 0 1px rgba(59,130,246,0.35) inset, 0 8px 24px -8px rgba(59,130,246,0.55);
  }
  .card:active { transform: scale(0.982); }
  .battery { width: 30px; height: 16px; flex: none; position: relative; }
  .battery-outline {
    position: absolute; inset: 0; border: 1.6px solid; border-radius: 4.5px; opacity: 0.9;
  }
  .battery-nub {
    position: absolute; right: -4px; top: 4.5px; width: 3px; height: 7px;
    border-radius: 0 2px 2px 0; background: currentColor; opacity: 0.9;
  }
  .battery-track { position: absolute; inset: 3px; border-radius: 1.5px; background: rgba(255,255,255,0.05); overflow: hidden; }
  .battery-fill { position: absolute; inset: 0; border-radius: 1.5px; transition: width .3s ease; }
  .info { flex: 1; min-width: 0; }
  .name { font-weight: 650; font-size: 14.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .status { font-size: 12px; color: #8A93A3; margin-top: 3px; }
  .status.is-selected { color: #7DA6FF; font-weight: 600; }
  .pct { font-weight: 800; font-size: 16px; flex: none; font-variant-numeric: tabular-nums; letter-spacing: -0.2px; }
  .badge {
    font-size: 10px; font-weight: 700; color: #FF6B6B; border: 1px solid rgba(239,68,68,0.5); border-radius: 6px;
    background: rgba(239,68,68,0.08);
    padding: 3px 7px; flex: none; text-transform: uppercase; letter-spacing: 0.4px;
  }
  .empty {
    display: flex; flex-direction: column; align-items: center; justify-content: center;
    color: #7B8291; font-size: 13.5px; text-align: center; padding-top: 22vh; gap: 12px;
  }
  .empty .empty-icon {
    width: 56px; height: 56px; border-radius: 16px; display: flex; align-items: center; justify-content: center;
    background: linear-gradient(180deg, rgba(255,255,255,0.05), rgba(255,255,255,0.01));
    border: 1px solid #24242A;
  }
  .empty svg { width: 26px; height: 26px; opacity: 0.45; }
</style>
</head>
<body>
  <div class="header">
    <div class="logo"><svg viewBox="0 0 24 24"><path d="M21,6H3C1.9,6,1,6.9,1,8v8c0,1.1,0.9,2,2,2h18c1.1,0,2-0.9,2-2V8C23,6.9,22.1,6,21,6z M11,13H8v3H6v-3H3v-2h3V8h2v3h3V13z M15.5,15c-0.83,0-1.5-0.67-1.5-1.5s0.67-1.5,1.5-1.5s1.5,0.67,1.5,1.5S16.33,15,15.5,15z M19.5,12c-0.83,0-1.5-0.67-1.5-1.5S18.67,9,19.5,9s1.5,0.67,1.5,1.5S20.33,12,19.5,12z"/></svg></div>
    <div>
      <h1>Padlume</h1>
      <div class="live"><span class="radar"></span>Live</div>
    </div>
  </div>
  <p class="hint">Tap a controller to give it priority.</p>
  <div id="list"></div>
  <div id="empty" class="empty" style="display:none">
    <div class="empty-icon">
      <svg viewBox="0 0 24 24" fill="currentColor"><path d="M21,6H3C1.9,6,1,6.9,1,8v8c0,1.1,0.9,2,2,2h18c1.1,0,2-0.9,2-2V8C23,6.9,22.1,6,21,6z M11,13H8v3H6v-3H3v-2h3V8h2v3h3V13z M15.5,15c-0.83,0-1.5-0.67-1.5-1.5s0.67-1.5,1.5-1.5s1.5,0.67,1.5,1.5S16.33,15,15.5,15z M19.5,12c-0.83,0-1.5-0.67-1.5-1.5S18.67,9,19.5,9s1.5,0.67,1.5,1.5S20.33,12,19.5,12z"/></svg>
    </div>
    <div>No controllers detected.</div>
  </div>

<script>
function batteryColor(pct) {
  return pct >= 50 ? '#22C55E' : pct >= 20 ? '#F5A623' : '#EF4444';
}

let hasRenderedOnce = false;

async function refresh() {
  let data;
  try {
    const res = await fetch('/api/controllers', { cache: 'no-store' });
    data = await res.json();
  } catch {
    return;
  }

  const list = document.getElementById('list');
  const empty = document.getElementById('empty');
  empty.style.display = data.length === 0 ? 'flex' : 'none';
  list.innerHTML = '';

  for (const c of data) {
    const color = batteryColor(c.batteryPercent);
    const fillWidth = Math.max(0, Math.min(1, c.batteryPercent / 100)) * 100;

    const card = document.createElement('div');
    // Only plays the entrance animation on the very first render — refresh() rebuilds the whole
    // list every 2s to stay simple, and replaying a slide-in on every poll would look like
    // flickering instead of "modern", so subsequent polls update silently in place.
    card.className = 'card' + (c.isSelected ? ' selected' : '') + (hasRenderedOnce ? '' : ' rise');
    card.onclick = () => select(c.key);

    card.innerHTML = `
      <div class="battery" style="color:${color}">
        <div class="battery-outline" style="border-color:currentColor"></div>
        <div class="battery-nub"></div>
        <div class="battery-track">
          <div class="battery-fill" style="width:${fillWidth}%;background:${color}"></div>
        </div>
      </div>
      <div class="info">
        <div class="name"></div>
        <div class="status"></div>
      </div>
      ${c.isBlocked ? '<div class="badge">Blocked</div>' : ''}
      <div class="pct" style="color:${color}"></div>
    `;
    card.querySelector('.name').textContent = c.name;
    const status = card.querySelector('.status');
    status.textContent = c.isSelected ? 'Selected — receiving input' : (c.isBlocked ? 'Blocked' : 'Tap to select');
    if (c.isSelected) status.classList.add('is-selected');
    card.querySelector('.pct').textContent = c.batteryText;
    list.appendChild(card);
  }

  hasRenderedOnce = true;
}

async function select(key) {
  try {
    await fetch('/api/select', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key }),
    });
  } catch {}
  refresh();
}

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
    }
}
