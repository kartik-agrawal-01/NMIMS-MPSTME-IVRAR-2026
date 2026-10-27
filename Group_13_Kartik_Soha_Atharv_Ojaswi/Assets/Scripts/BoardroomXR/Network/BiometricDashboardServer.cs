using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Unity.Netcode;
using UnityEngine;
using BoardroomXR.Auth;
using BoardroomXR.Biometrics;
using BoardroomXR.Identity;
using BoardroomXR.Logging;

namespace BoardroomXR.Network
{
    /// <summary>
    /// A tiny HTTP server inside the headset so a laptop can show the live trust
    /// state to spectators who are not wearing it:
    ///
    ///   GET /       one self-contained HTML page (inline CSS and JS, no CDN,
    ///               because expo Wi-Fi cannot be relied on)
    ///   GET /state  a JSON snapshot, rebuilt on the main thread every 0.2 s
    ///
    /// Reached from the laptop with `adb forward tcp:8080 tcp:8080` - see
    /// LIVE_DASHBOARD.bat. The listener binds to loopback by default, so nothing is
    /// exposed on the venue network.
    ///
    /// Host only. Every socket operation is wrapped: a networking failure here must
    /// never affect the demo, so the worst case is a dashboard that does not load.
    /// </summary>
    public class BiometricDashboardServer : MonoBehaviour
    {
        public int port = 8080;

        [Tooltip("Off by default: the dashboard is reached through adb forward, so the " +
                 "listener does not need to be visible on the venue network.")]
        public bool bindToAllInterfaces = false;

        public float snapshotIntervalSeconds = 0.2f;

        public static BiometricDashboardServer Instance { get; private set; }

        /// <summary>True once the listener is accepting connections.</summary>
        public bool IsListening { get; private set; }

        /// <summary>Requests served since start (shown in the log; useful when debugging adb).</summary>
        public int RequestsServed { get; private set; }

        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private volatile string _snapshot = "{}";
        private float _nextSnapshot;
        private float _nextStartAttempt;
        private AvatarIdentity _identity;
        private MFACoordinator _mfa;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            StopServer("component destroyed");
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit() => StopServer("application quit");

        private void OnApplicationPause(bool paused)
        {
            // Taking the headset off should not leave a socket thread alive.
            if (paused) StopServer("application paused");
            else _nextStartAttempt = 0f;
        }

        private void Update()
        {
            if (!IsListening && Time.unscaledTime >= _nextStartAttempt)
            {
                _nextStartAttempt = Time.unscaledTime + 2f;
                if (IsServerSide) StartServer();
            }

            if (Time.unscaledTime < _nextSnapshot) return;
            _nextSnapshot = Time.unscaledTime + Mathf.Max(0.05f, snapshotIntervalSeconds);
            _snapshot = BuildSnapshotJson();
        }

        private bool IsServerSide =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && NetworkManager.Singleton.IsServer;

        // ---------------- server lifecycle ----------------

        private void StartServer()
        {
            try
            {
                _listener = new TcpListener(bindToAllInterfaces ? IPAddress.Any : IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                IsListening = true;

                _thread = new Thread(ServeLoop) { IsBackground = true, Name = "BoardroomXR-Dashboard" };
                _thread.Start();

                SessionLogger.LogGlobal($"[Dashboard] Listening on " +
                    $"{(bindToAllInterfaces ? "0.0.0.0" : "127.0.0.1")}:{port}. " +
                    "On the laptop run LIVE_DASHBOARD.bat (adb forward tcp:8080 tcp:8080).");
                BioEventLog.Emit("dashboard_up", "-", $"port {port}");
            }
            catch (Exception e)
            {
                IsListening = false;
                _running = false;
                Debug.LogWarning($"[Dashboard] Could not start on port {port}: {e.Message}");
                try { _listener?.Stop(); } catch { }
                _listener = null;
            }
        }

        private void StopServer(string reason)
        {
            if (!_running && _listener == null) return;
            _running = false;
            IsListening = false;

            try { _listener?.Stop(); } catch { }
            _listener = null;

            try
            {
                if (_thread != null && _thread.IsAlive && !_thread.Join(500)) { /* background thread; it will exit */ }
            }
            catch { }
            _thread = null;

            SessionLogger.LogGlobal($"[Dashboard] Stopped ({reason}). Requests served: {RequestsServed}.");
        }

        /// <summary>
        /// Accept loop on a background thread. It only ever reads the volatile snapshot
        /// string, so it never touches the Unity API off the main thread.
        /// </summary>
        private void ServeLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    client.ReceiveTimeout = 2000;
                    client.SendTimeout = 2000;
                    HandleClient(client);
                }
                catch (SocketException)
                {
                    if (!_running) return;   // Stop() was called
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Dashboard] Request failed: {e.Message}");
                }
                finally
                {
                    try { client?.Close(); } catch { }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using var stream = client.GetStream();
            var buffer = new byte[2048];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return;

            string request = Encoding.ASCII.GetString(buffer, 0, read);
            string path = "/";
            int sp = request.IndexOf(' ');
            if (sp > 0)
            {
                int sp2 = request.IndexOf(' ', sp + 1);
                if (sp2 > sp) path = request.Substring(sp + 1, sp2 - sp - 1);
            }
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);

            if (path == "/state")
                Respond(stream, "200 OK", "application/json", _snapshot);
            else if (path == "/" || path == "/index.html")
                Respond(stream, "200 OK", "text/html; charset=utf-8", Page);
            else
                Respond(stream, "404 Not Found", "text/plain", "not found");

            RequestsServed++;
        }

        private static void Respond(System.IO.Stream stream, string status, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
            var header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            header.Append("Content-Type: ").Append(contentType).Append("\r\n");
            header.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("Access-Control-Allow-Origin: *\r\n");
            header.Append("Connection: close\r\n\r\n");

            byte[] head = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        // ---------------- snapshot (main thread) ----------------

        private string BuildSnapshotJson()
        {
            if (_identity == null) _identity = FindAnyObjectByType<AvatarIdentity>(FindObjectsInactive.Include);
            if (_mfa == null) _mfa = FindAnyObjectByType<MFACoordinator>(FindObjectsInactive.Include);

            var auth = ContinuousBiometricAuthenticator.Instance;
            var e = auth?.Engine;
            var cfg = e?.Config;

            string id = auth != null ? auth.CurrentUserId : "-";
            var badge = BadgeDirectory.Find(id);
            bool verified = _identity != null && _identity.IsSpawned && _identity.IsVerified.Value;

            var sb = new StringBuilder(4096);
            sb.Append('{');
            Str(sb, "identity", id); sb.Append(',');
            Str(sb, "name", badge != null ? badge.name : id); sb.Append(',');
            Str(sb, "role", badge != null ? badge.role : "-"); sb.Append(',');
            Bool(sb, "verified", verified); sb.Append(',');
            Str(sb, "state", e != null ? e.State.ToString() : "Idle"); sb.Append(',');
            Str(sb, "mfaStage", _mfa != null ? _mfa.CurrentStage.ToString() : "-"); sb.Append(',');
            Str(sb, "preset", cfg != null ? cfg.preset.ToString() : "-"); sb.Append(',');
            Num(sb, "threshold", cfg != null ? cfg.matchThreshold : 0f); sb.Append(',');
            Num(sb, "k", cfg != null ? cfg.violationsForLockout : 0); sb.Append(',');
            Num(sb, "violations", e != null ? e.Violations : 0); sb.Append(',');
            Num(sb, "trust", e != null ? Mathf.Clamp01(e.SmoothedScore) : 0f); sb.Append(',');
            Num(sb, "raw", e != null ? Mathf.Clamp01(e.RawScore) : 0f); sb.Append(',');
            Num(sb, "enrolProgress", e != null ? e.EnrolProgress : 0f); sb.Append(',');
            Num(sb, "enrolSeconds", cfg != null ? cfg.enrolSeconds : 0f); sb.Append(',');
            Bool(sb, "adaptive", cfg != null && cfg.adaptiveTemplate); sb.Append(',');
            Num(sb, "adaptationsSession", e != null ? e.AdaptationsThisSession : 0); sb.Append(',');
            Num(sb, "adaptationsTotal", e?.Template != null ? e.Template.adaptationCount : 0); sb.Append(',');
            Num(sb, "templateWindows", e?.Template != null ? e.Template.windowCount : 0); sb.Append(',');
            Num(sb, "lockouts", auth != null ? auth.LockoutCount : 0); sb.Append(',');
            Num(sb, "sessions", auth != null ? auth.SessionCount : 0); sb.Append(',');
            Bool(sb, "mimicry", BoardroomXR.UI.MimicryAttackGhost.Instance != null &&
                                BoardroomXR.UI.MimicryAttackGhost.Instance.IsRunning); sb.Append(',');

            // last 120 scored windows
            sb.Append("\"scores\":[");
            var recent = BiometricHistory.Recent(120);
            for (int i = 0; i < recent.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(recent[i].smoothed.ToString("F4", CultureInfo.InvariantCulture));
            }
            sb.Append("],");

            // 16 z-values with names and live values
            sb.Append("\"features\":[");
            bool hasZ = e != null && e.HasScoredWindow && e.Template != null;
            for (int i = 0; i < KinematicFeatures.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Str(sb, "name", FeatureExplainer.Table[i].label); sb.Append(',');
                Str(sb, "short", FeatureExplainer.Table[i].shortLabel); sb.Append(',');
                Str(sb, "unit", FeatureExplainer.Table[i].unit); sb.Append(',');
                Num(sb, "z", hasZ ? e.LastZ[i] : 0f); sb.Append(',');
                Num(sb, "value", hasZ ? e.LastFeatures[i] : 0f); sb.Append(',');
                Num(sb, "mean", hasZ ? e.Template.mean[i] : 0f);
                sb.Append('}');
            }
            sb.Append("],");

            // plain-English reasons
            sb.Append("\"reasons\":[");
            if (hasZ)
            {
                var reasons = FeatureExplainer.TopReasons(e.LastFeatures, e.LastZ, e.Template,
                    badge != null ? badge.name : id, 3);
                for (int i = 0; i < reasons.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    Str(sb, "text", reasons[i].text); sb.Append(',');
                    Num(sb, "z", reasons[i].absZ);
                    sb.Append('}');
                }
            }
            sb.Append("],");

            // event log
            sb.Append("\"events\":[");
            var events = BioEventLog.Recent(14);
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (i < events.Count - 1) sb.Append(',');
                sb.Append('{');
                Str(sb, "clock", events[i].clock); sb.Append(',');
                Str(sb, "type", events[i].type); sb.Append(',');
                Str(sb, "user", events[i].user); sb.Append(',');
                Str(sb, "detail", events[i].detail);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void Str(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":\"").Append(Escape(value)).Append('"');
        }

        private static void Num(StringBuilder sb, string key, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            sb.Append('"').Append(key).Append("\":").Append(value.ToString("F4", CultureInfo.InvariantCulture));
        }

        private static void Num(StringBuilder sb, string key, int value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Bool(StringBuilder sb, string key, bool value)
        {
            sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---------------- the page ----------------

        /// <summary>
        /// One self-contained page: inline CSS and JS, no external requests, so it
        /// loads over an adb forward with no internet at all. Single quotes are used
        /// throughout so this stays readable as a C# verbatim string.
        /// </summary>
        private const string Page = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>BoardroomXR - Live Behavioural Trust</title>
<style>
 :root{--bg:#07090d;--panel:#0d1118;--line:#1b2330;--ink:#e8eef6;--dim:#8b9aad;
       --ok:#4ade80;--warn:#fbbf24;--bad:#f87171;--info:#60c5f7}
 *{box-sizing:border-box}
 body{margin:0;background:var(--bg);color:var(--ink);
      font-family:ui-monospace,'Cascadia Mono','Consolas',monospace}
 header{display:flex;align-items:baseline;gap:18px;flex-wrap:wrap;
        padding:14px 22px;border-bottom:1px solid var(--line);background:#0a0d13}
 header h1{font-size:17px;margin:0;letter-spacing:.14em;color:var(--info)}
 header .sub{color:var(--dim);font-size:12px}
 header .dot{margin-left:auto;font-size:12px;color:var(--dim)}
 main{display:grid;grid-template-columns:1.15fr .85fr;gap:14px;padding:14px 22px 26px}
 .card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
 .card h2{margin:0 0 10px;font-size:11px;letter-spacing:.18em;color:var(--dim);font-weight:600}
 .big{display:flex;align-items:flex-end;gap:26px;flex-wrap:wrap}
 .trust{font-size:68px;line-height:1;font-weight:700}
 .state{font-size:19px;letter-spacing:.06em}
 .meta{color:var(--dim);font-size:12.5px;line-height:1.7}
 .meta b{color:var(--ink);font-weight:600}
 canvas{width:100%;height:190px;display:block}
 .bars{display:grid;grid-template-columns:118px 1fr 42px;gap:4px 10px;align-items:center;font-size:11.5px}
 .track{height:11px;background:#121823;border-radius:3px;overflow:hidden}
 .fill{height:100%;width:0;border-radius:3px;transition:width .18s linear}
 .zval{text-align:right;color:var(--dim)}
 ol.reasons{margin:0;padding-left:20px;font-size:14px;line-height:1.75}
 ol.reasons li{margin-bottom:3px}
 table{width:100%;border-collapse:collapse;font-size:12px}
 td{padding:3px 6px;border-bottom:1px solid #141b26;vertical-align:top}
 td.t{color:var(--dim);white-space:nowrap;width:62px}
 td.k{white-space:nowrap;width:150px}
 .pill{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11px;
       border:1px solid var(--line);color:var(--dim)}
 .pill.on{color:#07090d;background:var(--ok);border-color:var(--ok)}
 .pill.hot{color:#07090d;background:var(--bad);border-color:var(--bad)}
 .span2{grid-column:1 / span 2}
 @media(max-width:1000px){main{grid-template-columns:1fr}.span2{grid-column:1}}
</style>
</head>
<body>
<header>
  <h1>BOARDROOM XR</h1>
  <span class='sub'>continuous behavioural authentication &middot; live from the headset</span>
  <span class='dot' id='conn'>connecting...</span>
</header>

<main>
  <section class='card'>
    <h2>TRUST</h2>
    <div class='big'>
      <div class='trust' id='trust'>--</div>
      <div>
        <div class='state' id='state'>IDLE</div>
        <div class='meta' id='who'>no identity</div>
      </div>
    </div>
    <div class='meta' id='opmeta' style='margin-top:12px'></div>
  </section>

  <section class='card'>
    <h2>SESSION</h2>
    <div class='meta' id='session'></div>
    <div style='margin-top:10px'>
      <span class='pill' id='pAdaptive'>adaptive</span>
      <span class='pill' id='pMimicry'>mimicry</span>
      <span class='pill' id='pVerified'>verified</span>
    </div>
  </section>

  <section class='card span2'>
    <h2>TRUST SCORE &middot; LAST 120 WINDOWS (0.5 s HOP)</h2>
    <canvas id='chart' width='1200' height='380'></canvas>
  </section>

  <section class='card'>
    <h2>FEATURE DEVIATION |z|</h2>
    <div class='bars' id='bars'></div>
  </section>

  <section class='card'>
    <h2>WHY &middot; LARGEST DIFFERENCES FROM THE ENROLLED PROFILE</h2>
    <ol class='reasons' id='reasons'><li class='meta'>waiting for a scored window...</li></ol>
    <h2 style='margin-top:18px'>SECURITY EVENTS</h2>
    <table><tbody id='events'></tbody></table>
  </section>
</main>

<script>
var COLORS={Verified:'#4ade80',Warning:'#fbbf24',LockedOut:'#f87171',Enrolling:'#60c5f7',Idle:'#8b9aad'};
var last=null, fails=0;

function zColor(z){z=Math.abs(z);return z<1?'#4ade80':(z<2.5?'#fbbf24':'#f87171');}

function buildBars(features){
  var host=document.getElementById('bars');
  host.innerHTML='';
  for(var i=0;i<features.length;i++){
    var f=features[i];
    var label=document.createElement('div');
    label.textContent=f['short'];
    label.title=f.name+' ('+f.unit+')';
    var track=document.createElement('div'); track.className='track';
    var fill=document.createElement('div'); fill.className='fill'; fill.id='fill'+i;
    track.appendChild(fill);
    var val=document.createElement('div'); val.className='zval'; val.id='z'+i;
    host.appendChild(label); host.appendChild(track); host.appendChild(val);
  }
}

function drawChart(scores,threshold,state){
  var c=document.getElementById('chart'), g=c.getContext('2d');
  var w=c.width, h=c.height;
  g.clearRect(0,0,w,h);

  // threshold band
  var ty=h-threshold*h;
  g.fillStyle='rgba(251,191,36,0.10)'; g.fillRect(0,ty,w,h-ty);
  g.strokeStyle='#f87171'; g.setLineDash([7,6]); g.lineWidth=2;
  g.beginPath(); g.moveTo(0,ty); g.lineTo(w,ty); g.stroke(); g.setLineDash([]);

  // gridlines
  g.strokeStyle='#161d28'; g.lineWidth=1;
  for(var k=1;k<5;k++){var y=h*k/5;g.beginPath();g.moveTo(0,y);g.lineTo(w,y);g.stroke();}

  g.fillStyle='#8b9aad'; g.font='18px monospace';
  g.fillText('1.0',8,22); g.fillText('0.0',8,h-8);
  g.fillText('threshold '+threshold.toFixed(2),w-230,Math.max(20,ty-10));

  if(!scores||scores.length<2) return;
  g.strokeStyle=COLORS[state]||'#60c5f7'; g.lineWidth=3; g.beginPath();
  for(var i=0;i<scores.length;i++){
    var x=w*i/(scores.length-1), y=h-Math.max(0,Math.min(1,scores[i]))*h;
    if(i===0) g.moveTo(x,y); else g.lineTo(x,y);
  }
  g.stroke();
}

function pill(el,on,text,hot){
  el.textContent=text;
  el.className='pill'+(on?(hot?' hot':' on'):'');
}

function render(s){
  var col=COLORS[s.state]||'#8b9aad';
  var t=document.getElementById('trust');
  if(s.state==='Enrolling'){t.textContent=Math.round(s.enrolProgress*100)+'%';}
  else if(s.state==='Idle'){t.textContent='--';}
  else {t.textContent=Math.round(s.trust*100)+'%';}
  t.style.color=col;

  var st=document.getElementById('state');
  st.textContent=s.state==='Enrolling'?('ENROLLING - learning this person ('+Math.round(s.enrolSeconds)+' s)'):s.state.toUpperCase();
  st.style.color=col;

  document.getElementById('who').innerHTML='<b>'+s.name+'</b> &middot; '+s.role+' &middot; '+s.identity;
  document.getElementById('opmeta').innerHTML=
    'operating point <b>'+s.preset+'</b> &middot; threshold <b>'+s.threshold.toFixed(2)+
    '</b> &middot; lockout after <b>'+s.k+'</b> windows &middot; violations <b>'+s.violations+'/'+s.k+
    '</b><br>raw score <b>'+s.raw.toFixed(3)+'</b> &middot; smoothed <b>'+s.trust.toFixed(3)+'</b>';

  document.getElementById('session').innerHTML=
    'MFA stage <b>'+s.mfaStage+'</b><br>'+
    'template <b>'+s.templateWindows+'</b> enrolment windows, adapted <b>'+s.adaptationsTotal+'</b>x total, <b>'+
    s.adaptationsSession+'</b>x this session<br>'+
    'monitoring sessions <b>'+s.sessions+'</b> &middot; lockouts this run <b>'+s.lockouts+'</b>';

  pill(document.getElementById('pAdaptive'),s.adaptive,s.adaptive?'adaptive template ON':'adaptive template OFF',false);
  pill(document.getElementById('pMimicry'),s.mimicry,s.mimicry?'MIMICRY ATTACK RUNNING':'no attack running',true);
  pill(document.getElementById('pVerified'),s.verified,s.verified?'server: verified':'server: not verified',false);

  if(!last||last.features.length!==s.features.length) buildBars(s.features);
  for(var i=0;i<s.features.length;i++){
    var z=Math.abs(s.features[i].z);
    var fill=document.getElementById('fill'+i);
    if(fill){fill.style.width=Math.min(100,z/4*100)+'%';fill.style.background=zColor(z);}
    var zv=document.getElementById('z'+i);
    if(zv) zv.textContent=z.toFixed(1);
  }

  var ol=document.getElementById('reasons');
  ol.innerHTML='';
  if(s.reasons.length===0){
    var li=document.createElement('li'); li.className='meta';
    li.textContent=s.state==='Enrolling'?'learning this person\'s movement profile...':'movement matches the enrolled profile';
    ol.appendChild(li);
  } else {
    for(var r=0;r<s.reasons.length;r++){
      var li2=document.createElement('li');
      li2.textContent=s.reasons[r].text+'  ('+s.reasons[r].z.toFixed(1)+' sigma)';
      li2.style.color=zColor(s.reasons[r].z);
      ol.appendChild(li2);
    }
  }

  var tb=document.getElementById('events');
  tb.innerHTML='';
  for(var ev=0;ev<s.events.length;ev++){
    var e=s.events[ev], tr=document.createElement('tr');
    var c1=document.createElement('td'); c1.className='t'; c1.textContent=e.clock;
    var c2=document.createElement('td'); c2.className='k'; c2.textContent=e.type;
    c2.style.color=(e.type==='lockout'||e.type==='forced_lockout')?'#f87171':
                   (e.type==='mfa_pass'||e.type==='enrol_done')?'#4ade80':
                   (e.type.indexOf('gesture')===0)?'#fbbf24':'#60c5f7';
    var c3=document.createElement('td'); c3.textContent=e.detail;
    tr.appendChild(c1); tr.appendChild(c2); tr.appendChild(c3);
    tb.appendChild(tr);
  }

  drawChart(s.scores,s.threshold,s.state);
  last=s;
}

function poll(){
  fetch('/state',{cache:'no-store'}).then(function(r){return r.json();}).then(function(s){
    fails=0;
    document.getElementById('conn').innerHTML='live &middot; '+new Date().toLocaleTimeString();
    render(s);
  }).catch(function(err){
    fails++;
    document.getElementById('conn').textContent='no data from headset ('+fails+') - check adb forward';
  });
}
setInterval(poll,250);
poll();
</script>
</body>
</html>
";
    }
}
