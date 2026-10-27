// Capri Signal for the Windows Capri (app/). C# 5 / .NET Framework 4.x — builds with the classic csc.exe.
// Coding agents (Claude Code, Codex, Cursor, Antigravity) call signal\capri-signal.ps1 from their hooks; it POSTs the
// hook JSON to this tiny receiver on 127.0.0.1. Capri then TELLS you, in her own bubble and voice.
// Never executes anything it receives. Never reachable from the network.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CapriDesktop
{
    internal enum SignalKind { Started, TurnEnded, Completed, NeedsInput, PermissionRequired, Failed }

    internal sealed class SignalEvent
    {
        internal SignalKind Kind;
        internal string Source;
        internal string EventId;
        internal string TaskId;
        internal string TurnId;
        internal string Project;
        internal string ProjectPath;
        internal string Message;
        internal string Detail;

        internal static string TypeName(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.Started: return "task.started";
                case SignalKind.TurnEnded: return "task.turn_ended";
                case SignalKind.Completed: return "task.completed";
                case SignalKind.NeedsInput: return "task.needs_input";
                case SignalKind.PermissionRequired: return "task.permission_required";
                default: return "task.failed";
            }
        }

        internal static bool TryParseType(string type, out SignalKind kind)
        {
            kind = SignalKind.Failed;
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "task.started": kind = SignalKind.Started; return true;
                case "task.turn_ended": kind = SignalKind.TurnEnded; return true;
                case "task.completed": kind = SignalKind.Completed; return true;
                case "task.needs_input": kind = SignalKind.NeedsInput; return true;
                case "task.permission_required": kind = SignalKind.PermissionRequired; return true;
                case "task.failed": kind = SignalKind.Failed; return true;
                default: return false;
            }
        }

        internal static int Priority(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.Failed: return 5;
                case SignalKind.PermissionRequired: return 4;
                case SignalKind.NeedsInput: return 3;
                case SignalKind.Started: return 0;
                default: return 2;
            }
        }

        internal bool Urgent { get { return Kind == SignalKind.Failed || Kind == SignalKind.PermissionRequired || Kind == SignalKind.NeedsInput; } }

        internal string ThreadKey { get { return (Source ?? "?") + "|" + (TaskId ?? Project ?? ProjectPath ?? "-"); } }

        internal string DedupKey
        {
            get
            {
                if (!string.IsNullOrEmpty(EventId)) return "id:" + Source + "|" + EventId;
                string raw = ThreadKey + "|" + TypeName(Kind) + "|" + (TurnId ?? "") + "|" + (Detail ?? "") + "|" + (Message ?? "");
                using (SHA256 sha = SHA256.Create()) return "h:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", "");
            }
        }

        internal static string Clean(string value, int max)
        {
            if (value == null) return null;
            StringBuilder text = new StringBuilder();
            bool space = false;
            foreach (char c in value)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
                if (space) { text.Append(' '); space = false; }
                text.Append(c);
                if (text.Length > max) break;
            }
            string done = text.ToString();
            if (done.Length > max) done = done.Substring(0, max - 1) + "…";
            return done.Length == 0 ? null : done;
        }
    }

    internal sealed class SignalRejected : Exception
    {
        internal SignalRejected(string reason) : base(reason) { }
    }

    // ---- minimal, strict JSON reader (objects → Dictionary, arrays → List, numbers → double) -------------------
    internal static class MiniJson
    {
        internal static object Parse(string text)
        {
            int at = 0;
            object value = Value(text, ref at, 0);
            Skip(text, ref at);
            if (at != text.Length) throw new SignalRejected("body is not valid JSON");
            return value;
        }

        private static void Skip(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++; }

        private static object Value(string s, ref int i, int depth)
        {
            if (depth > 32) throw new SignalRejected("JSON nested too deep");
            Skip(s, ref i);
            if (i >= s.Length) throw new SignalRejected("body is not valid JSON");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> obj = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new SignalRejected("body is not valid JSON");
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new SignalRejected("body is not valid JSON");
                    i++;
                    obj[key] = Value(s, ref i, depth + 1);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return obj; }
                    throw new SignalRejected("body is not valid JSON");
                }
            }
            if (c == '[')
            {
                List<object> list = new List<object>();
                i++; Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i, depth + 1));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new SignalRejected("body is not valid JSON");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double number;
            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new SignalRejected("body is not valid JSON");
            return number;
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            StringBuilder b = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new SignalRejected("body is not valid JSON");
                        b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new SignalRejected("body is not valid JSON");
                }
            }
            throw new SignalRejected("body is not valid JSON");
        }

        internal static string Text(Dictionary<string, object> obj, string name)
        {
            object value;
            if (obj == null || !obj.TryGetValue(name, out value) || value == null) return null;
            if (value is string) return (string)value;
            if (value is double) return ((double)value).ToString(CultureInfo.InvariantCulture);
            return null;
        }

        internal static bool? Flag(Dictionary<string, object> obj, string name)
        {
            object value;
            if (obj == null || !obj.TryGetValue(name, out value) || !(value is bool)) return null;
            return (bool)value;
        }

        internal static string First(Dictionary<string, object> obj, string name)
        {
            object value;
            if (obj == null || !obj.TryGetValue(name, out value)) return null;
            List<object> list = value as List<object>;
            if (list == null) return null;
            foreach (object item in list) if (item is string) return (string)item;
            return null;
        }
    }

    // ---- adapters: raw hook payload → SignalEvent (null = not interesting) ------------------------------------
    internal static class SignalAdapters
    {
        internal static bool Known(string tool)
        {
            switch (tool)
            {
                case "generic": case "claude-code": case "claude": case "codex": case "cursor": case "antigravity": return true;
                default: return false;
            }
        }

        internal static string ProjectName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { string t = path.TrimEnd('\\', '/'); string n = Path.GetFileName(t); return string.IsNullOrEmpty(n) ? t : n; }
            catch (Exception) { return null; }
        }

        internal static SignalEvent Translate(string tool, string hookEvent, Dictionary<string, object> raw)
        {
            switch (tool)
            {
                case "generic": return Generic(raw);
                case "claude":
                case "claude-code": return Claude(raw, hookEvent);
                case "codex": return Codex(raw, hookEvent);
                case "cursor": return Cursor(raw, hookEvent);
                default: return Antigravity(raw, hookEvent);
            }
        }

        private static SignalEvent Generic(Dictionary<string, object> raw)
        {
            SignalKind kind;
            if (!SignalEvent.TryParseType(MiniJson.Text(raw, "type"), out kind)) throw new SignalRejected("unknown or missing \"type\"");
            string source = MiniJson.Text(raw, "source");
            if (string.IsNullOrWhiteSpace(source)) throw new SignalRejected("missing \"source\"");
            string path = MiniJson.Text(raw, "project_path");
            SignalEvent e = new SignalEvent();
            e.Kind = kind; e.Source = source; e.EventId = MiniJson.Text(raw, "event_id"); e.TaskId = MiniJson.Text(raw, "task_id");
            e.TurnId = MiniJson.Text(raw, "turn_id"); e.Project = MiniJson.Text(raw, "project") ?? ProjectName(path); e.ProjectPath = path;
            e.Message = MiniJson.Text(raw, "message"); e.Detail = MiniJson.Text(raw, "detail");
            return e;
        }

        private static SignalEvent Claude(Dictionary<string, object> raw, string hookEvent)
        {
            string name = MiniJson.Text(raw, "hook_event_name") ?? hookEvent;
            string cwd = MiniJson.Text(raw, "cwd");
            SignalEvent e = new SignalEvent();
            e.Source = "claude-code"; e.TaskId = MiniJson.Text(raw, "session_id"); e.TurnId = MiniJson.Text(raw, "prompt_id"); e.ProjectPath = cwd; e.Project = ProjectName(cwd);
            switch (name)
            {
                case "UserPromptSubmit": e.Kind = SignalKind.Started; return e;
                case "Stop":
                    if (MiniJson.Flag(raw, "stop_hook_active") == true) return null;
                    e.Kind = SignalKind.TurnEnded; e.Message = MiniJson.Text(raw, "last_assistant_message"); return e;
                case "StopFailure":
                    e.Kind = SignalKind.Failed; e.Detail = MiniJson.Text(raw, "error_type") ?? MiniJson.Text(raw, "matcher");
                    e.Message = MiniJson.Text(raw, "error") ?? MiniJson.Text(raw, "message"); return e;
                case "Notification":
                    string type = MiniJson.Text(raw, "notification_type");
                    e.Detail = type; e.Message = MiniJson.Text(raw, "message");
                    if (type == "permission_prompt") { e.Kind = SignalKind.PermissionRequired; return e; }
                    if (type == "idle_prompt" || type == "agent_needs_input" || type == "elicitation_dialog" || type == "elicitation_url_dialog") { e.Kind = SignalKind.NeedsInput; return e; }
                    if (type == "agent_completed") { e.Kind = SignalKind.Completed; return e; }
                    if (type == null)
                    {
                        string text = (e.Message ?? "").ToLowerInvariant();
                        if (text.Contains("permission")) { e.Kind = SignalKind.PermissionRequired; return e; }
                        if (text.Contains("waiting for your input")) { e.Kind = SignalKind.NeedsInput; return e; }
                    }
                    return null;
                default: return null;
            }
        }

        private static SignalEvent Codex(Dictionary<string, object> raw, string hookEvent)
        {
            string name = MiniJson.Text(raw, "hook_event_name") ?? hookEvent;
            string cwd = MiniJson.Text(raw, "cwd");
            SignalEvent e = new SignalEvent();
            e.Source = "codex"; e.TaskId = MiniJson.Text(raw, "session_id") ?? MiniJson.Text(raw, "thread-id");
            e.TurnId = MiniJson.Text(raw, "turn_id") ?? MiniJson.Text(raw, "turn-id"); e.ProjectPath = cwd; e.Project = ProjectName(cwd);
            if (MiniJson.Text(raw, "hook_event_name") == null && MiniJson.Text(raw, "type") == "agent-turn-complete") { e.Kind = SignalKind.TurnEnded; e.Message = MiniJson.Text(raw, "last-assistant-message"); return e; }
            switch (name)
            {
                case "UserPromptSubmit": e.Kind = SignalKind.Started; return e;
                case "Stop":
                    if (MiniJson.Flag(raw, "stop_hook_active") == true) return null;
                    e.Kind = SignalKind.TurnEnded; e.Message = MiniJson.Text(raw, "last_assistant_message"); return e;
                case "PermissionRequest": e.Kind = SignalKind.PermissionRequired; e.Detail = MiniJson.Text(raw, "tool_name"); return e;
                default: return null;
            }
        }

        private static SignalEvent Cursor(Dictionary<string, object> raw, string hookEvent)
        {
            string name = MiniJson.Text(raw, "hook_event_name") ?? hookEvent;
            if (name != "stop") return null;
            string root = MiniJson.First(raw, "workspace_roots");
            SignalEvent e = new SignalEvent();
            e.Source = "cursor"; e.TaskId = MiniJson.Text(raw, "conversation_id"); e.TurnId = MiniJson.Text(raw, "generation_id"); e.ProjectPath = root; e.Project = ProjectName(root);
            string status = MiniJson.Text(raw, "status");
            e.Detail = status;
            e.Kind = status == "error" ? SignalKind.Failed : SignalKind.TurnEnded;
            return e;
        }

        private static SignalEvent Antigravity(Dictionary<string, object> raw, string hookEvent)
        {
            string root = MiniJson.First(raw, "workspacePaths");
            SignalEvent e = new SignalEvent();
            e.Source = "antigravity"; e.TaskId = MiniJson.Text(raw, "conversationId"); e.TurnId = MiniJson.Text(raw, "executionNum"); e.ProjectPath = root; e.Project = ProjectName(root);
            if (hookEvent == "PreInvocation") { e.Kind = SignalKind.Started; return e; }
            if (hookEvent != "Stop") return null;
            string reason = MiniJson.Text(raw, "terminationReason");
            string error = MiniJson.Text(raw, "error");
            e.Detail = reason;
            if (reason == "error" || !string.IsNullOrEmpty(error)) { e.Kind = SignalKind.Failed; e.Message = error; return e; }
            e.Kind = reason == "max_steps_exceeded" ? SignalKind.NeedsInput : SignalKind.TurnEnded;
            return e;
        }

        internal static SignalEvent Route(string tool, string hookEvent, string body)
        {
            if (string.IsNullOrWhiteSpace(tool)) tool = "generic";
            if (!IsIdentifier(tool, 40)) throw new SignalRejected("invalid tool name");
            if (hookEvent != null && !IsIdentifier(hookEvent, 60)) throw new SignalRejected("invalid event name");
            if (!Known(tool)) throw new SignalRejected("unknown tool \"" + tool + "\"");
            if (string.IsNullOrWhiteSpace(body)) throw new SignalRejected("empty body");
            Dictionary<string, object> raw = MiniJson.Parse(body) as Dictionary<string, object>;
            if (raw == null) throw new SignalRejected("body must be a JSON object");
            SignalEvent e = Translate(tool, hookEvent, raw);
            if (e == null) return null;
            e.Source = SignalEvent.Clean(e.Source, 40);
            if (!IsIdentifier(e.Source, 40)) throw new SignalRejected("invalid source");
            e.EventId = SignalEvent.Clean(e.EventId, 120); e.TaskId = SignalEvent.Clean(e.TaskId, 120); e.TurnId = SignalEvent.Clean(e.TurnId, 120);
            e.Project = SignalEvent.Clean(e.Project, 80); e.Detail = SignalEvent.Clean(e.Detail, 60); e.Message = SignalEvent.Clean(e.Message, 280);
            string path = (e.ProjectPath ?? "").Trim();
            bool ok = path.Length > 0 && path.Length <= 400;
            foreach (char c in path) if (char.IsControl(c)) ok = false;
            e.ProjectPath = ok ? path : null;
            return e;
        }

        internal static bool IsIdentifier(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length > max) return false;
            foreach (char c in value) if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
            return true;
        }

        internal static string Display(string source, bool spanish)
        {
            switch ((source ?? "").ToLowerInvariant())
            {
                case "claude-code": return "Claude Code";
                case "codex": return "Codex";
                case "cursor": return "Cursor";
                case "antigravity": return "Antigravity";
                case "test": return spanish ? "La prueba" : "The test agent";
                case "claude-chat": return "Claude";
                default: return source;
            }
        }
    }

    // ---- settings (%APPDATA%\Capri\signal.json — same file the scripts read) ---------------------------------
    [DataContract]
    internal sealed class SignalSettings
    {
        internal const int DefaultPort = 47615;
        [DataMember(Name = "enabled")] public bool Enabled;
        [DataMember(Name = "announceStarted")] public bool AnnounceStarted;
        [DataMember(Name = "dedupSeconds")] public double DedupSeconds;
        [DataMember(Name = "port")] public int Port;
        [DataMember(Name = "activePort")] public int ActivePort;
        [DataMember(Name = "token")] public string Token;
        [DataMember(Name = "logEvents")] public bool LogEvents;
        [DataMember(Name = "language")] public string Language;   // "en" (default) or "es"
        [DataMember(Name = "inbox")] public string Inbox;         // folder Capri watches for *.json alerts (chat sessions drop files here)

        internal string Folder;
        internal string FilePath { get { return Path.Combine(Folder, "signal.json"); } }
        internal string LogPath { get { return Path.Combine(Folder, "signal-log.txt"); } }

        public SignalSettings() { Defaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { Defaults(); }

        private void Defaults()
        {
            Enabled = true; Language = "en"; AnnounceStarted = false; DedupSeconds = 30; Port = DefaultPort; LogEvents = true;
        }

        internal static string DefaultFolder() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capri"); }

        internal static SignalSettings Load(string folder)
        {
            SignalSettings s = null;
            string file = Path.Combine(folder, "signal.json");
            try
            {
                if (File.Exists(file))
                    using (FileStream stream = File.OpenRead(file)) s = (SignalSettings)new DataContractJsonSerializer(typeof(SignalSettings)).ReadObject(stream);
            }
            catch (Exception) { s = null; }
            if (s == null) s = new SignalSettings();
            s.Folder = folder;
            if (string.IsNullOrEmpty(s.Token) || s.Token.Length < 32) s.Token = NewToken();
            if (s.Port < 1024 || s.Port > 65000) s.Port = DefaultPort;
            if (s.DedupSeconds < 1) s.DedupSeconds = 30;
            if (s.Language != "es") s.Language = "en";
            if (string.IsNullOrWhiteSpace(s.Inbox)) s.Inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "Capri", "inbox");
            s.Save();
            return s;
        }

        internal static string NewToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = FilePath + ".tmp";
                using (FileStream stream = File.Create(temp)) new DataContractJsonSerializer(typeof(SignalSettings)).WriteObject(stream, this);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(temp, FilePath);
            }
            catch (Exception) { }
        }
    }

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
                    if (info.Exists && info.Length > 256 * 1024) { string old = path + ".old"; if (File.Exists(old)) File.Delete(old); File.Move(path, old); }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }
    }

    // ---- receiver: raw TcpListener on 127.0.0.1 only ----------------------------------------------------------
    internal sealed class SignalReceiver
    {
        internal const int MaxHeader = 8 * 1024;
        internal const int MaxBody = 32 * 1024;
        private readonly SignalSettings settings;
        private readonly Func<SignalEvent, string> deliver;
        private readonly SignalLog log;
        private readonly Queue<DateTime> recent = new Queue<DateTime>();
        private TcpListener listener;
        private Thread loop;
        private int active;

        internal int Port;
        internal string LastError;
        internal bool Running { get { return listener != null; } }

        internal SignalReceiver(SignalSettings settings, Func<SignalEvent, string> deliver, SignalLog log)
        {
            this.settings = settings; this.deliver = deliver; this.log = log;
        }

        internal bool Start()
        {
            if (listener != null) return true;
            for (int port = settings.Port; port < settings.Port + 10; port++)
            {
                TcpListener candidate = new TcpListener(IPAddress.Loopback, port);
                candidate.ExclusiveAddressUse = true;
                try { candidate.Start(16); }
                catch (SocketException error) { log.Write("port " + port + " busy (" + error.SocketErrorCode + ")"); continue; }
                listener = candidate;
                Port = port;
                loop = new Thread(Accept);
                loop.IsBackground = true;
                loop.Name = "Capri Signal";
                loop.Start(candidate);
                settings.ActivePort = port;
                settings.Save();
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
            try { current.Stop(); } catch (Exception) { }
            try { if (loop != null) loop.Join(1500); } catch (Exception) { }
            settings.ActivePort = 0;
            settings.Save();
            log.Write("receiver stopped");
        }

        private void Accept(object state)
        {
            TcpListener current = (TcpListener)state;
            while (listener == current)
            {
                TcpClient client;
                try { client = current.AcceptTcpClient(); }
                catch (Exception) { if (listener != current) return; continue; }
                if (Interlocked.Increment(ref active) > 8) { Interlocked.Decrement(ref active); client.Close(); continue; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Handle(client); }
                    catch (Exception error) { log.Write("connection error: " + error.GetType().Name); }
                    finally { try { client.Close(); } catch (Exception) { } Interlocked.Decrement(ref active); }
                });
            }
        }

        private void Handle(TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            NetworkStream stream = client.GetStream();
            int status;
            string body;
            string note = null;
            string method = "?", path = "?";
            try
            {
                Dictionary<string, string> headers;
                string target, payload;
                Read(stream, out method, out target, out headers, out payload);
                path = target.Split('?')[0];
                Process(method, target, headers, payload, out status, out body, out note);
            }
            catch (BadRequest error) { status = error.Status; body = Error(error.Message); }
            catch (IOException) { return; }
            log.Write(method + " " + path + " -> " + status + (note != null ? " " + note : ""));
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            string head = "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : status == 202 ? "Accepted" : "Error") + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + data.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            try
            {
                byte[] headBytes = Encoding.ASCII.GetBytes(head);
                stream.Write(headBytes, 0, headBytes.Length);
                stream.Write(data, 0, data.Length);
            }
            catch (Exception) { }
        }

        internal void Process(string method, string target, Dictionary<string, string> headers, string payload, out int status, out string body, out string note)
        {
            note = null;
            string path = target.Split('?')[0];
            string value;
            if (headers.TryGetValue("origin", out value)) { status = 403; body = Error("browser requests are not allowed"); return; }
            string host = headers.TryGetValue("host", out value) ? value.ToLowerInvariant() : "";
            if (host != "127.0.0.1:" + Port && host != "localhost:" + Port) { status = 403; body = Error("bad host"); return; }
            if (path == "/v1/health")
            {
                status = method == "GET" ? 200 : 405;
                body = method == "GET" ? "{\"ok\":true,\"app\":\"capri-signal\",\"flavor\":\"windows\",\"version\":1,\"enabled\":" + (settings.Enabled ? "true" : "false") + "}" : Error("use GET");
                return;
            }
            string tool = null;
            if (path == "/v1/event") tool = "generic";
            else if (path.StartsWith("/v1/hook/", StringComparison.Ordinal)) tool = path.Substring(9);
            if (tool == null) { status = 404; body = Error("not found"); return; }
            if (method != "POST") { status = 405; body = Error("use POST"); return; }
            if (!Authorized(headers)) { status = 401; body = Error("missing or wrong token"); note = "unauthorized"; return; }
            if (!Allow()) { status = 429; body = Error("too many requests"); return; }
            SignalEvent e;
            try { e = SignalAdapters.Route(tool, Query(target, "event"), payload); }
            catch (SignalRejected error) { status = 400; body = Error(error.Message); note = "rejected " + tool + ": " + error.Message; return; }
            if (e == null) { status = 202; body = "{\"status\":\"ignored\"}"; note = "ignored " + tool; return; }
            string outcome = deliver(e);
            status = 200;
            body = "{\"status\":\"" + outcome + "\",\"type\":\"" + SignalEvent.TypeName(e.Kind) + "\"}";
            note = e.Source + " " + SignalEvent.TypeName(e.Kind) + " " + outcome;
        }

        private bool Authorized(Dictionary<string, string> headers)
        {
            string given;
            if (!headers.TryGetValue("x-capri-token", out given))
            {
                string bearer;
                if (headers.TryGetValue("authorization", out bearer) && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) given = bearer.Substring(7).Trim();
            }
            if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(settings.Token) || given.Length != settings.Token.Length) return false;
            int diff = 0;
            for (int i = 0; i < given.Length; i++) diff |= given[i] ^ settings.Token[i];
            return diff == 0;
        }

        private bool Allow()
        {
            lock (recent)
            {
                DateTime now = DateTime.UtcNow;
                while (recent.Count > 0 && (now - recent.Peek()).TotalSeconds > 10) recent.Dequeue();
                if (recent.Count >= 40) return false;
                recent.Enqueue(now);
                return true;
            }
        }

        private static string Query(string target, string name)
        {
            int q = target.IndexOf('?');
            if (q < 0) return null;
            foreach (string pair in target.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                if (key == name) return Uri.UnescapeDataString(eq < 0 ? "" : pair.Substring(eq + 1));
            }
            return null;
        }

        private static string Error(string message) { return "{\"error\":\"" + (message ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}"; }

        internal sealed class BadRequest : Exception
        {
            internal readonly int Status;
            internal BadRequest(int status, string message) : base(message) { Status = status; }
        }

        private static void Read(Stream stream, out string method, out string target, out Dictionary<string, string> headers, out string body)
        {
            byte[] buffer = new byte[MaxHeader + MaxBody];
            int filled = 0, end = -1;
            while (end < 0)
            {
                if (filled >= MaxHeader) throw new BadRequest(400, "headers too large");
                int read = stream.Read(buffer, filled, MaxHeader - filled);
                if (read <= 0) throw new IOException("closed");
                filled += read;
                for (int i = 0; i + 3 < filled; i++) if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10) { end = i; break; }
            }
            string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new BadRequest(400, "bad request line");
            method = first[0].ToUpperInvariant();
            target = first[1];
            if (!target.StartsWith("/", StringComparison.Ordinal) || target.Length > 512) throw new BadRequest(400, "bad target");
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) throw new BadRequest(400, "bad header");
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            string value;
            if (headers.ContainsKey("transfer-encoding")) throw new BadRequest(411, "chunked bodies are not supported");
            int length = 0;
            if (headers.TryGetValue("content-length", out value) && (!int.TryParse(value, out length) || length < 0)) throw new BadRequest(400, "bad content-length");
            if (!headers.ContainsKey("content-length") && method == "POST") throw new BadRequest(411, "content-length required");
            if (length > MaxBody) throw new BadRequest(413, "body larger than " + MaxBody + " bytes");
            int start = end + 4;
            while (filled - start < length)
            {
                int read = stream.Read(buffer, filled, start + length - filled);
                if (read <= 0) throw new IOException("closed");
                filled += read;
            }
            try { body = new UTF8Encoding(false, true).GetString(buffer, start, length); }
            catch (DecoderFallbackException) { throw new BadRequest(400, "body is not UTF-8"); }
            if (body.Length > 0 && body[0] == '﻿') body = body.Substring(1);
        }
    }

    // ---- what Capri says ------------------------------------------------------------------------------------
    internal sealed class SignalLine
    {
        internal string Shown;   // short header in her bubble ("> ALERT: ...")
        internal string Said;    // what she says out loud
        internal bool Alert;
        internal SignalEvent Event;
    }

    internal sealed class SignalHub
    {
        internal readonly SignalSettings Settings;
        internal readonly SignalLog Log;
        private readonly SignalReceiver receiver;
        private readonly object gate = new object();
        private readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        private readonly List<SignalLine> pending = new List<SignalLine>();
        private readonly Random random = new Random();

        internal SignalHub(string folder)
        {
            Settings = SignalSettings.Load(folder);
            Log = new SignalLog(Settings.LogEvents ? Settings.LogPath : null);
            receiver = new SignalReceiver(Settings, Deliver, Log);
        }

        internal bool Running { get { return receiver.Running; } }
        internal int Port { get { return receiver.Port; } }
        internal string LastError { get { return receiver.LastError; } }

        internal void Start() { if (Settings.Enabled) receiver.Start(); }
        internal void Stop() { receiver.Stop(); }

        internal void SetEnabled(bool on)
        {
            Settings.Enabled = on;
            Settings.Save();
            if (on) receiver.Start(); else { receiver.Stop(); lock (gate) pending.Clear(); }
        }

        /// <summary>Background thread. Dedups, collapses per task, queues one line for Capri.</summary>
        internal string Deliver(SignalEvent e)
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                List<string> old = new List<string>();
                foreach (KeyValuePair<string, DateTime> p in seen) if ((now - p.Value).TotalSeconds > Settings.DedupSeconds) old.Add(p.Key);
                foreach (string k in old) seen.Remove(k);
                string key = e.DedupKey;
                if (seen.ContainsKey(key)) return "duplicate";
                seen[key] = now;
                if (!Settings.Enabled) return "disabled";
                if (e.Kind == SignalKind.Started && !Settings.AnnounceStarted) return "logged";
                // one pending line per task: a newer, more urgent event replaces the older one
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].Event.ThreadKey != e.ThreadKey) continue;
                    if (SignalEvent.Priority(e.Kind) < SignalEvent.Priority(pending[i].Event.Kind)) return "merged";
                    pending.RemoveAt(i);
                    pending.Add(Compose(e));
                    return "updated";
                }
                if (pending.Count >= 6) pending.RemoveAt(0);
                pending.Add(Compose(e));
                return "queued";
            }
        }

        /// <summary>UI thread: next line to say (most urgent first), or null.</summary>
        internal SignalLine Next()
        {
            lock (gate)
            {
                if (pending.Count == 0) return null;
                int best = 0;
                for (int i = 1; i < pending.Count; i++) if (SignalEvent.Priority(pending[i].Event.Kind) > SignalEvent.Priority(pending[best].Event.Kind)) best = i;
                SignalLine line = pending[best];
                pending.RemoveAt(best);
                return line;
            }
        }

        internal bool Waiting { get { lock (gate) return pending.Count > 0; } }

        internal bool HasAlert { get { lock (gate) { foreach (SignalLine l in pending) if (l.Alert) return true; return false; } } }

        private string Pick(params string[] options) { return options[random.Next(options.Length)]; }

        internal bool Spanish { get { return Settings.Language == "es"; } }

        internal void SetSpanish(bool on)
        {
            Settings.Language = on ? "es" : "en";
            Settings.Save();
        }

        internal SignalLine Compose(SignalEvent e)
        {
            string tool = SignalAdapters.Display(e.Source, Spanish);
            bool es = Spanish;
            string where = e.Project != null ? (es ? " en " : " in ") + e.Project : "";
            SignalLine line = new SignalLine();
            line.Event = e;
            line.Alert = e.Urgent;
            switch (e.Kind)
            {
                case SignalKind.Started:
                    line.Shown = tool + (es ? " · en marcha" : " · started");
                    line.Said = es ? tool + " se ha puesto a trabajar" + where + ". Te aviso cuando acabe, choom."
                                   : tool + " just started working" + where + ". I'll ping you when it's done, choom.";
                    break;
                case SignalKind.TurnEnded:
                    line.Shown = tool + (es ? " · turno terminado" : " · turn finished");
                    line.Said = es
                        ? Pick("Choom, " + tool + " ha terminado" + where + ". Échale un ojo.",
                               "Eh, " + tool + " ya ha acabado su turno" + where + ". Revisa lo que ha hecho.",
                               "Ping: " + tool + " ha terminado" + where + ". Te toca, choom.")
                        : Pick("Choom, " + tool + " just finished" + where + ". Go take a look.",
                               "Hey, " + tool + " wrapped up its turn" + where + ". Check what it did.",
                               "Ping: " + tool + " is done" + where + ". Your move, choom.");
                    break;
                case SignalKind.Completed:
                    line.Shown = tool + (es ? " · tarea completada" : " · task complete");
                    line.Said = es ? tool + " dice que ha completado la tarea" + where + ". Compruébalo antes de darlo por bueno."
                                   : tool + " says the task is complete" + where + ". Double-check it before you trust it.";
                    break;
                case SignalKind.NeedsInput:
                    line.Shown = tool + (es ? " · espera tu respuesta" : " · waiting for you");
                    line.Said = es
                        ? Pick("Choom, " + tool + " te está esperando" + where + ". Necesita que le contestes.",
                               "Eh, " + tool + " se ha quedado esperando tu respuesta" + where + ".")
                        : Pick("Choom, " + tool + " is waiting on you" + where + ". It needs an answer.",
                               "Hey, " + tool + " is stuck waiting for your reply" + where + ".");
                    break;
                case SignalKind.PermissionRequired:
                    line.Shown = tool + (es ? " · pide permiso" : " · needs permission");
                    line.Said = es ? "Alerta: " + tool + " pide permiso para hacer algo" + where + ". Revísalo antes de aceptar."
                                   : "Heads up: " + tool + " wants permission to do something" + where + ". Check it before you approve.";
                    break;
                default:
                    line.Shown = tool + (es ? " · error" : " · error");
                    bool reason = e.Detail != null && e.Detail != "error";
                    line.Said = es
                        ? "Problemas, choom: " + tool + " ha fallado" + where + "." + (reason ? " Motivo: " + e.Detail.Replace('_', ' ') + "." : " Revisa el error.")
                        : "Trouble, choom: " + tool + " crashed out" + where + "." + (reason ? " Reason: " + e.Detail.Replace('_', ' ') + "." : " Check the error.");
                    break;
            }
            return line;
        }

        private DateTime nextInbox = DateTime.MinValue;

        /// <summary>UI thread, cheap: every 2 s reads up to 5 small *.json files from the inbox folder, then deletes them.
        /// Same format as POST /v1/event. Files are data only — nothing in them is ever executed.</summary>
        internal void PollInbox()
        {
            if (!Settings.Enabled || DateTime.UtcNow < nextInbox) return;
            nextInbox = DateTime.UtcNow.AddSeconds(2);
            string folder = Settings.Inbox;
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                int done = 0;
                foreach (string file in Directory.GetFiles(folder, "*.json"))
                {
                    if (done++ >= 5) break;
                    string outcome;
                    try
                    {
                        FileInfo info = new FileInfo(file);
                        if (info.Length > 4096) outcome = "too large";
                        else
                        {
                            SignalEvent e = SignalAdapters.Route("generic", null, File.ReadAllText(file, Encoding.UTF8));
                            outcome = e == null ? "ignored" : Deliver(e);
                        }
                    }
                    catch (SignalRejected error) { outcome = "rejected: " + error.Message; }
                    catch (IOException) { continue; } // still being written: next round
                    try { File.Delete(file); } catch (Exception) { }
                    Log.Write("inbox file -> " + outcome);
                }
            }
            catch (Exception error) { Log.Write("inbox error: " + error.GetType().Name); }
        }

        internal void Test(SignalKind kind)
        {
            SignalEvent e = new SignalEvent();
            e.Kind = kind; e.Source = "test"; e.EventId = Guid.NewGuid().ToString("N"); e.TaskId = "menu-test"; e.Project = "Capri";
            Deliver(e);
        }
    }
}

namespace CapriDesktop
{
    /// <summary>Capri.exe --signal-selftest [folder] : real receiver on 127.0.0.1, real HTTP, no UI, no user settings touched.</summary>
    internal static class SignalSelfTest
    {
        private static int Post(int port, string path, string body, string token, string origin, out string reply)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Method = "POST";
            req.Proxy = null;
            req.Timeout = 5000;
            req.ContentType = "application/json";
            if (token != null) req.Headers["X-Capri-Token"] = token;
            if (origin != null) req.Headers["Origin"] = origin;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            try
            {
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                {
                    int status = (int)res.StatusCode;
                    using (StreamReader r = new StreamReader(res.GetResponseStream())) reply = r.ReadToEnd();
                    return status;
                }
            }
            catch (WebException error)
            {
                HttpWebResponse res = error.Response as HttpWebResponse;
                reply = error.Message;
                if (res == null) return -1;
                int status = (int)res.StatusCode;
                try { using (StreamReader r = new StreamReader(res.GetResponseStream())) reply = r.ReadToEnd(); }
                catch (Exception) { }
                finally { res.Close(); }
                return status;
            }
        }

        internal static int Run(string output)
        {
            output = Path.GetFullPath(string.IsNullOrEmpty(output) ? "signal-selftest" : output);
            Directory.CreateDirectory(output);
            List<string> lines = new List<string>();
            int failed = 0;
            Action<string, bool, string> check = delegate(string name, bool ok, string info)
            {
                lines.Add((ok ? "PASS " : "FAIL ") + name + (ok || info == null ? "" : "  -> " + info));
                if (!ok) failed++;
            };
            string folder = Path.Combine(Path.GetTempPath(), "capri-signal-test-" + Guid.NewGuid().ToString("N"));
            SignalHub hub = null;
            try
            {
                SignalSettings first = SignalSettings.Load(folder);
                first.Port = 47690 + new Random().Next(0, 200);
                first.Save();
                hub = new SignalHub(folder);
                hub.Start();
                check("receiver starts", hub.Running, hub.LastError);
                int port = hub.Port;
                string t = hub.Settings.Token;
                check("token is random and 64 chars", t.Length == 64, null);
                check("active port written for the scripts", SignalSettings.Load(folder).ActivePort == port, null);
                string r;
                int code;

                code = Post(port, "/v1/event", "{\"type\":\"task.turn_ended\",\"source\":\"codex\",\"task_id\":\"A\",\"project\":\"Capri\"}", t, null, out r);
                check("turn_ended accepted and queued", code == 200 && r.Contains("queued"), code + " " + r);
                code = Post(port, "/v1/event", "{\"type\":\"task.turn_ended\",\"source\":\"codex\",\"task_id\":\"A\",\"project\":\"Capri\"}", t, null, out r);
                check("duplicate dropped", r.Contains("duplicate"), r);
                code = Post(port, "/v1/event", "{\"type\":\"task.permission_required\",\"source\":\"codex\",\"task_id\":\"A\",\"project\":\"Capri\"}", t, null, out r);
                check("same task escalates instead of stacking", r.Contains("updated"), r);
                code = Post(port, "/v1/event", "{\"type\":\"task.failed\",\"source\":\"claude-code\",\"task_id\":\"B\",\"detail\":\"rate_limit\"}", t, null, out r);
                check("failed accepted (optional fields omitted)", code == 200, code + " " + r);
                code = Post(port, "/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\"}", null, null, out r);
                check("missing token -> 401", code == 401, code.ToString());
                code = Post(port, "/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\"}", SignalSettings.NewToken(), null, out r);
                check("wrong token -> 401", code == 401, code.ToString());
                code = Post(port, "/v1/event", "{\"type\": \"task.completed\",", t, null, out r);
                check("malformed JSON -> 400", code == 400, code + " " + r);
                code = Post(port, "/v1/event", "[1,2]", t, null, out r);
                check("non-object -> 400", code == 400, code.ToString());
                code = Post(port, "/v1/event", "{\"type\":\"task.completed\",\"source\":\"rm -rf\"}", t, null, out r);
                check("source with shell characters -> 400", code == 400, code.ToString());
                code = Post(port, "/v1/hook/notepad", "{}", t, null, out r);
                check("unknown tool -> 400", code == 400, code.ToString());
                code = Post(port, "/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\"}", t, "https://evil.example", out r);
                check("browser Origin -> 403", code == 403, code.ToString());

                code = Post(port, "/v1/hook/claude-code?event=Stop", "{\"session_id\":\"s1\",\"cwd\":\"C:\\\\dev\\\\capri\",\"hook_event_name\":\"Stop\",\"stop_hook_active\":false,\"last_assistant_message\":\"Hecho \\u00e9xito\"}", t, null, out r);
                check("Claude Code Stop -> turn_ended", r.Contains("task.turn_ended"), r);
                code = Post(port, "/v1/hook/claude-code?event=Notification", "{\"session_id\":\"s2\",\"hook_event_name\":\"Notification\",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission to use Bash\"}", t, null, out r);
                check("Claude Code permission_prompt -> permission_required", r.Contains("task.permission_required"), r);
                code = Post(port, "/v1/hook/claude-code?event=Notification", "{\"session_id\":\"s3\",\"hook_event_name\":\"Notification\",\"notification_type\":\"idle_prompt\"}", t, null, out r);
                check("Claude Code idle_prompt -> needs_input", r.Contains("task.needs_input"), r);
                code = Post(port, "/v1/hook/claude-code?event=Notification", "{\"session_id\":\"s4\",\"hook_event_name\":\"Notification\",\"notification_type\":\"auth_success\"}", t, null, out r);
                check("Claude Code auth_success ignored (202)", code == 202, code.ToString());
                code = Post(port, "/v1/hook/codex?event=Stop", "{\"session_id\":\"x1\",\"turn_id\":\"t1\",\"cwd\":\"/tmp\",\"hook_event_name\":\"Stop\",\"stop_hook_active\":false,\"last_assistant_message\":null}", t, null, out r);
                check("Codex Stop -> turn_ended", r.Contains("task.turn_ended"), r);
                code = Post(port, "/v1/hook/cursor?event=stop", "{\"conversation_id\":\"c1\",\"hook_event_name\":\"stop\",\"status\":\"error\",\"workspace_roots\":[\"/tmp\"]}", t, null, out r);
                check("Cursor stop error -> failed", r.Contains("task.failed"), r);
                code = Post(port, "/v1/hook/antigravity?event=Stop", "{\"conversationId\":\"a1\",\"executionNum\":3,\"terminationReason\":\"model_stop\",\"fullyIdle\":true}", t, null, out r);
                check("Antigravity Stop -> turn_ended", r.Contains("task.turn_ended"), r);

                SignalLine top = hub.Next();
                check("most urgent line comes first", top != null && top.Event.Kind == SignalKind.Failed && top.Alert, top == null ? "none" : top.Event.Kind.ToString());
                check("failed line names the tool", top != null && top.Said.Contains("crashed out") && (top.Said.Contains("Claude Code") || top.Said.Contains("Cursor")), top == null ? "" : top.Said);
                SignalLine reason = hub.Compose(new SignalEvent { Kind = SignalKind.Failed, Source = "claude-code", Detail = "rate_limit" });
                check("failed line gives the reason", reason.Said.Contains("rate limit"), reason.Said);
                SignalLine bare = hub.Compose(new SignalEvent { Kind = SignalKind.Failed, Source = "cursor", Detail = "error" });
                check("no pointless 'Reason: error'", !bare.Said.Contains("Reason"), bare.Said);
                SignalLine perm = hub.Next();
                check("permission line is an alert, in English by default", perm != null && perm.Alert && perm.Said.Contains("permission"), perm == null ? "" : perm.Said);
                hub.SetSpanish(true);
                SignalLine spanish = hub.Compose(new SignalEvent { Kind = SignalKind.PermissionRequired, Source = "codex" });
                check("Spanish when asked", spanish.Said.Contains("permiso"), spanish.Said);
                hub.SetSpanish(false);
                int left = 0;
                while (hub.Next() != null) left++;
                check("queue drains", !hub.Waiting, left.ToString());
                SignalLine plain = hub.Compose(new SignalEvent { Kind = SignalKind.TurnEnded, Source = "claude-code", Project = "capri-desktop" });
                check("turn-ended line is calm and names project", !plain.Alert && plain.Said.Contains("Claude Code") && plain.Said.Contains("capri-desktop"), plain.Said);

                string inbox = Path.Combine(folder, "inbox");
                Directory.CreateDirectory(inbox);
                hub.Settings.Inbox = inbox;
                File.WriteAllText(Path.Combine(inbox, "a.json"), "{\"type\":\"task.turn_ended\",\"source\":\"claude-chat\",\"task_id\":\"chat1\",\"project\":\"Capri\"}");
                File.WriteAllText(Path.Combine(inbox, "b.json"), "not json");
                hub.PollInbox();
                SignalLine chat = hub.Next();
                check("inbox file becomes a spoken line", chat != null && chat.Said.Contains("Claude") && chat.Event.Source == "claude-chat", chat == null ? "none" : chat.Said);
                check("inbox files are deleted (good and bad)", Directory.GetFiles(inbox).Length == 0, Directory.GetFiles(inbox).Length.ToString());

                hub.SetEnabled(false);
                check("disabling stops the receiver", !hub.Running, null);
                hub.SetEnabled(true);
                check("re-enabling restarts it", hub.Running, hub.LastError);
                code = Post(hub.Port, "/v1/event", "{\"type\":\"task.turn_ended\",\"source\":\"codex\",\"task_id\":\"after\"}", t, null, out r);
                check("events accepted after restart", code == 200, code + " " + r);
                string log = File.Exists(hub.Settings.LogPath) ? File.ReadAllText(hub.Settings.LogPath) : "";
                check("log has no token and no message text", log.Length > 0 && !log.Contains(t) && !log.Contains("Hecho"), null);
            }
            catch (Exception error) { check("unexpected exception", false, error.GetType().Name + ": " + error.Message); }
            finally
            {
                try { if (hub != null) hub.Stop(); } catch (Exception) { }
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
            lines.Insert(0, "Capri Signal (Windows Capri) self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  -  " + (failed == 0 ? "ALL PASSED" : failed + " FAILED"));
            File.WriteAllLines(Path.Combine(output, "signal-selftest.txt"), lines.ToArray());
            return failed == 0 ? 0 : 1;
        }
    }
}
