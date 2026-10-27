// Capri Signal — tiny local HTTP receiver.
// Binds ONLY to 127.0.0.1 with a raw TcpListener (no http.sys / URL ACL, no admin rights, unreachable from the network).
// Runs on a background thread; never touches the Avalonia UI thread. Never executes anything it receives.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CapriDesktop.Signal
{
    internal sealed class SignalReceiver : IDisposable
    {
        internal const int MaxHeader = 8 * 1024;
        internal const int MaxBody = 32 * 1024;
        private const int MaxPerWindow = 40;      // requests per 10 s, all clients together
        private const int MaxConnections = 8;

        private readonly SignalSettings settings;
        private readonly Func<SignalEvent, string> deliver;
        private readonly SignalLog log;
        private readonly Queue<DateTime> recent = new Queue<DateTime>();
        private readonly SemaphoreSlim slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        private TcpListener listener;
        private CancellationTokenSource stop;
        private Task loop;

        internal int Port { get; private set; }
        internal bool Running { get { return listener != null; } }
        internal string LastError { get; private set; }

        internal SignalReceiver(SignalSettings settings, Func<SignalEvent, string> deliver, SignalLog log)
        {
            this.settings = settings;
            this.deliver = deliver;
            this.log = log;
        }

        /// <summary>Tries the preferred port and the next 9. Returns false (and sets LastError) if none is free.</summary>
        internal bool Start()
        {
            if (listener != null) return true;
            for (int port = settings.Port; port < settings.Port + 10; port++)
            {
                TcpListener candidate = new TcpListener(IPAddress.Loopback, port);
                candidate.ExclusiveAddressUse = true;
                try { candidate.Start(16); }
                catch (SocketException error)
                {
                    log.Write("port " + port + " busy (" + error.SocketErrorCode + ")");
                    continue;
                }
                listener = candidate;
                Port = port;
                stop = new CancellationTokenSource();
                CancellationToken token = stop.Token;
                loop = Task.Run(() => AcceptLoop(token));
                if (settings.ActivePort != port) { settings.ActivePort = port; settings.Save(); }
                LastError = null;
                log.Write("receiver listening on 127.0.0.1:" + port);
                return true;
            }
            LastError = "No free port between " + settings.Port + " and " + (settings.Port + 9) + ".";
            log.Write("receiver NOT started: " + LastError);
            return false;
        }

        internal void Stop()
        {
            TcpListener current = listener;
            if (current == null) return;
            listener = null;
            try { stop.Cancel(); } catch (Exception) { }
            try { current.Stop(); } catch (Exception) { }
            try { if (loop != null) loop.Wait(1500); } catch (Exception) { }
            if (settings.ActivePort != 0) { settings.ActivePort = 0; settings.Save(); }
            log.Write("receiver stopped");
        }

        public void Dispose() { Stop(); }

        private async Task AcceptLoop(CancellationToken token)
        {
            TcpListener current = listener;
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await current.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch (Exception) { if (token.IsCancellationRequested) return; continue; }
                if (!await slots.WaitAsync(0).ConfigureAwait(false)) { client.Dispose(); continue; }
                _ = Task.Run(async () =>
                {
                    try { await Handle(client, token).ConfigureAwait(false); }
                    catch (Exception error) { log.Write("connection error: " + error.GetType().Name); }
                    finally { client.Dispose(); slots.Release(); }
                });
            }
        }

        private async Task Handle(TcpClient client, CancellationToken outer)
        {
            client.NoDelay = true;
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(outer))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                NetworkStream stream = client.GetStream();
                Request request;
                try { request = await Request.Read(stream, timeout.Token).ConfigureAwait(false); }
                catch (BadRequest error) { await Reply(stream, error.Status, Status(error.Message), timeout.Token); return; }
                catch (Exception) { return; }
                Response response = Process(request);
                log.Write(request.Method + " " + request.PathOnly + " -> " + response.Status + (response.Note != null ? " " + response.Note : ""));
                await Reply(stream, response.Status, response.Body, timeout.Token);
            }
        }

        internal sealed class Response
        {
            internal int Status;
            internal string Body;
            internal string Note; // goes to the log; metadata only
        }

        /// <summary>Pure request → response logic (also used by the self-test).</summary>
        internal Response Process(Request request)
        {
            // Browsers always send Origin on cross-site POSTs: refuse them (blocks web pages poking at localhost).
            if (request.Header("origin") != null) return new Response { Status = 403, Body = Status("browser requests are not allowed") };
            string host = (request.Header("host") ?? "").ToLowerInvariant();
            if (host != "127.0.0.1:" + Port && host != "localhost:" + Port) return new Response { Status = 403, Body = Status("bad host") };

            if (request.PathOnly == "/v1/health")
            {
                if (request.Method != "GET") return new Response { Status = 405, Body = Status("use GET") };
                return new Response { Status = 200, Body = "{\"ok\":true,\"app\":\"capri-signal\",\"version\":1,\"enabled\":" + (settings.Enabled ? "true" : "false") + "}" };
            }

            string tool = null;
            if (request.PathOnly == "/v1/event") tool = "generic";
            else if (request.PathOnly.StartsWith("/v1/hook/", StringComparison.Ordinal)) tool = request.PathOnly.Substring("/v1/hook/".Length);
            if (tool == null) return new Response { Status = 404, Body = Status("not found") };
            if (request.Method != "POST") return new Response { Status = 405, Body = Status("use POST") };
            if (!Authorized(request)) return new Response { Status = 401, Body = Status("missing or wrong token"), Note = "unauthorized" };
            if (!Allow()) return new Response { Status = 429, Body = Status("too many requests") };

            SignalEvent e;
            try { e = SignalRouter.Route(tool, request.Query("event"), request.Body); }
            catch (SignalRejected error) { return new Response { Status = 400, Body = Status(error.Message), Note = "rejected " + tool + ": " + error.Message }; }
            if (e == null) return new Response { Status = 202, Body = "{\"status\":\"ignored\"}", Note = "ignored " + tool };
            string outcome;
            try { outcome = deliver(e); }
            catch (Exception error) { return new Response { Status = 500, Body = Status("internal error"), Note = error.GetType().Name }; }
            return new Response { Status = 200, Body = "{\"status\":\"" + outcome + "\",\"type\":\"" + SignalEvent.TypeName(e.Kind) + "\"}", Note = e.Source + " " + SignalEvent.TypeName(e.Kind) + " " + outcome };
        }

        private bool Authorized(Request request)
        {
            string given = request.Header("x-capri-token");
            string bearer = request.Header("authorization");
            if (given == null && bearer != null && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = bearer.Substring(7).Trim();
            if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(settings.Token)) return false;
            byte[] a = Encoding.UTF8.GetBytes(given);
            byte[] b = Encoding.UTF8.GetBytes(settings.Token);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private bool Allow()
        {
            lock (recent)
            {
                DateTime now = DateTime.UtcNow;
                while (recent.Count > 0 && (now - recent.Peek()).TotalSeconds > 10) recent.Dequeue();
                if (recent.Count >= MaxPerWindow) return false;
                recent.Enqueue(now);
                return true;
            }
        }

        private static string Status(string message)
        {
            return "{\"error\":\"" + (message ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
        }

        private static async Task Reply(NetworkStream stream, int status, string body, CancellationToken token)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
            string head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            try
            {
                byte[] headBytes = Encoding.ASCII.GetBytes(head);
                await stream.WriteAsync(headBytes, 0, headBytes.Length, token).ConfigureAwait(false);
                await stream.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 429: return "Too Many Requests";
                default: return "Error";
            }
        }

        internal sealed class BadRequest : Exception
        {
            internal readonly int Status;
            internal BadRequest(int status, string message) : base(message) { Status = status; }
        }

        internal sealed class Request
        {
            internal string Method;
            internal string Target;
            internal string Body;
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal string PathOnly { get { int q = Target.IndexOf('?'); return q < 0 ? Target : Target.Substring(0, q); } }

            internal string Header(string name) { string value; return Headers.TryGetValue(name, out value) ? value : null; }

            internal string Query(string name)
            {
                int q = Target.IndexOf('?');
                if (q < 0) return null;
                foreach (string pair in Target.Substring(q + 1).Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    string key = eq < 0 ? pair : pair.Substring(0, eq);
                    if (key == name) return Uri.UnescapeDataString(eq < 0 ? "" : pair.Substring(eq + 1));
                }
                return null;
            }

            internal static async Task<Request> Read(Stream stream, CancellationToken token)
            {
                byte[] buffer = new byte[MaxHeader + MaxBody];
                int filled = 0;
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    if (filled >= MaxHeader) throw new BadRequest(400, "headers too large");
                    int read = await stream.ReadAsync(buffer, filled, MaxHeader - filled, token).ConfigureAwait(false);
                    if (read <= 0) throw new IOException("closed");
                    filled += read;
                    headerEnd = IndexOf(buffer, filled, new byte[] { 13, 10, 13, 10 });
                }
                Request request = Parse(Encoding.ASCII.GetString(buffer, 0, headerEnd));
                int bodyStart = headerEnd + 4;
                string lengthText = request.Header("content-length");
                if (request.Header("transfer-encoding") != null) throw new BadRequest(411, "chunked bodies are not supported");
                int length = 0;
                if (lengthText != null && (!int.TryParse(lengthText, out length) || length < 0)) throw new BadRequest(400, "bad content-length");
                if (lengthText == null && request.Method == "POST") throw new BadRequest(411, "content-length required");
                if (length > MaxBody) throw new BadRequest(413, "body larger than " + MaxBody + " bytes");
                while (filled - bodyStart < length)
                {
                    int read = await stream.ReadAsync(buffer, filled, bodyStart + length - filled, token).ConfigureAwait(false);
                    if (read <= 0) throw new IOException("closed");
                    filled += read;
                }
                try { request.Body = new UTF8Encoding(false, true).GetString(buffer, bodyStart, length); }
                catch (DecoderFallbackException) { throw new BadRequest(400, "body is not UTF-8"); }
                if (request.Body.Length > 0 && request.Body[0] == '﻿') request.Body = request.Body.Substring(1); // PowerShell 5 BOM
                return request;
            }

            internal static Request Parse(string head)
            {
                string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = lines[0].Split(' ');
                if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new BadRequest(400, "bad request line");
                Request request = new Request { Method = first[0].ToUpperInvariant(), Target = first[1] };
                if (!request.Target.StartsWith("/", StringComparison.Ordinal) || request.Target.Length > 512) throw new BadRequest(400, "bad target");
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) throw new BadRequest(400, "bad header");
                    request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
                return request;
            }

            private static int IndexOf(byte[] data, int count, byte[] pattern)
            {
                for (int i = 0; i + pattern.Length <= count; i++)
                {
                    bool match = true;
                    for (int j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
                    if (match) return i;
                }
                return -1;
            }
        }
    }

    /// <summary>Metadata-only log: never the token, never message text or paths.</summary>
    internal sealed class SignalLog
    {
        private readonly string path;
        private readonly object gate = new object();

        internal SignalLog(string path) { this.path = path; }

        internal void Write(string line)
        {
            if (path == null) return;
            lock (gate)
            {
                try
                {
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > 256 * 1024) File.Move(path, path + ".old", true);
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }
}
