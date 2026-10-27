// Capri Signal — adapters. Each one translates the raw hook payload of one tool into SignalEvent.
// Formats are taken from each tool's official hook documentation (see signal/README.md).
// Unknown or uninteresting events return null and are ignored (HTTP 202 "ignored").
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CapriDesktop.Signal
{
    internal interface ISignalAdapter
    {
        string Tool { get; }
        /// <param name="raw">JSON object received from the hook (stdin of the hook, forwarded as-is).</param>
        /// <param name="hookEvent">Event name passed by the hook script (needed for tools whose payload omits it).</param>
        SignalEvent Translate(JsonElement raw, string hookEvent);
    }

    internal static class Json
    {
        internal static string Str(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            JsonElement value;
            if (!obj.TryGetProperty(name, out value)) return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            return null;
        }

        internal static bool? Bool(JsonElement obj, string name)
        {
            JsonElement value;
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out value)) return null;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        internal static string FirstArrayString(JsonElement obj, string name)
        {
            JsonElement value;
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array) return null;
            foreach (JsonElement item in value.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) return item.GetString();
            return null;
        }

        internal static string ProjectName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string trimmed = path.TrimEnd('\\', '/');
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? trimmed : name;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Normalized format, for tests and for any tool you wire up yourself.</summary>
    internal sealed class GenericAdapter : ISignalAdapter
    {
        public string Tool { get { return "generic"; } }

        public SignalEvent Translate(JsonElement raw, string hookEvent)
        {
            SignalKind kind;
            if (!SignalEvent.TryParseType(Json.Str(raw, "type"), out kind)) throw new SignalRejected("unknown or missing \"type\"");
            string source = Json.Str(raw, "source");
            if (string.IsNullOrWhiteSpace(source)) throw new SignalRejected("missing \"source\"");
            string path = Json.Str(raw, "project_path");
            return new SignalEvent
            {
                Kind = kind,
                Source = source,
                EventId = Json.Str(raw, "event_id"),
                TaskId = Json.Str(raw, "task_id"),
                TurnId = Json.Str(raw, "turn_id"),
                Project = Json.Str(raw, "project") ?? Json.ProjectName(path),
                ProjectPath = path,
                Message = Json.Str(raw, "message"),
                Detail = Json.Str(raw, "detail"),
            };
        }
    }

    /// <summary>Claude Code hooks: UserPromptSubmit, Stop, StopFailure, Notification.</summary>
    internal sealed class ClaudeCodeAdapter : ISignalAdapter
    {
        public string Tool { get { return "claude-code"; } }

        public SignalEvent Translate(JsonElement raw, string hookEvent)
        {
            string name = Json.Str(raw, "hook_event_name") ?? hookEvent;
            string cwd = Json.Str(raw, "cwd");
            SignalEvent e = new SignalEvent
            {
                Source = Tool,
                TaskId = Json.Str(raw, "session_id"),
                TurnId = Json.Str(raw, "prompt_id"),
                ProjectPath = cwd,
                Project = Json.ProjectName(cwd),
            };
            switch (name)
            {
                case "UserPromptSubmit":
                    e.Kind = SignalKind.Started; e.Message = "Claude Code ha empezado a trabajar."; return e;
                case "Stop":
                    if (Json.Bool(raw, "stop_hook_active") == true) return null; // a Stop hook is already continuing the turn
                    e.Kind = SignalKind.TurnEnded; e.Message = Json.Str(raw, "last_assistant_message") ?? "Claude Code ha terminado su turno."; return e;
                case "StopFailure":
                    e.Kind = SignalKind.Failed;
                    e.Detail = Json.Str(raw, "error_type") ?? Json.Str(raw, "matcher") ?? Json.Str(raw, "reason");
                    e.Message = Json.Str(raw, "error") ?? Json.Str(raw, "message") ?? "El turno de Claude Code terminó por un error" + (e.Detail != null ? " (" + e.Detail + ")." : ".");
                    return e;
                case "Notification":
                    string type = Json.Str(raw, "notification_type");
                    e.Detail = type;
                    e.Message = Json.Str(raw, "message");
                    switch (type)
                    {
                        case "permission_prompt": e.Kind = SignalKind.PermissionRequired; return e;
                        case "idle_prompt":
                        case "agent_needs_input":
                        case "elicitation_dialog":
                        case "elicitation_url_dialog": e.Kind = SignalKind.NeedsInput; return e;
                        case "agent_completed": e.Kind = SignalKind.Completed; return e;
                        case null:
                            // Older versions send no type: guess from the text, never assume "completed".
                            string text = (e.Message ?? "").ToLowerInvariant();
                            if (text.Contains("permission")) { e.Kind = SignalKind.PermissionRequired; return e; }
                            if (text.Contains("waiting for your input") || text.Contains("needs your")) { e.Kind = SignalKind.NeedsInput; return e; }
                            return null;
                        default: return null; // auth_success, quota_*, elicitation_complete, ... are not task signals
                    }
                default: return null;
            }
        }
    }

    /// <summary>OpenAI Codex hooks (hooks.json): UserPromptSubmit, Stop, PermissionRequest.</summary>
    internal sealed class CodexAdapter : ISignalAdapter
    {
        public string Tool { get { return "codex"; } }

        public SignalEvent Translate(JsonElement raw, string hookEvent)
        {
            string name = Json.Str(raw, "hook_event_name") ?? hookEvent;
            // Legacy `notify` program: payload has "type": "agent-turn-complete".
            string legacy = Json.Str(raw, "type");
            string cwd = Json.Str(raw, "cwd");
            SignalEvent e = new SignalEvent
            {
                Source = Tool,
                TaskId = Json.Str(raw, "session_id") ?? Json.Str(raw, "thread-id"),
                TurnId = Json.Str(raw, "turn_id") ?? Json.Str(raw, "turn-id"),
                ProjectPath = cwd,
                Project = Json.ProjectName(cwd),
            };
            if (name == null && legacy == "agent-turn-complete")
            {
                e.Kind = SignalKind.TurnEnded; e.Message = Json.Str(raw, "last-assistant-message") ?? "Codex ha terminado su turno."; return e;
            }
            switch (name)
            {
                case "UserPromptSubmit": e.Kind = SignalKind.Started; e.Message = "Codex ha empezado a trabajar."; return e;
                case "Stop":
                    if (Json.Bool(raw, "stop_hook_active") == true) return null;
                    e.Kind = SignalKind.TurnEnded; e.Message = Json.Str(raw, "last_assistant_message") ?? "Codex ha terminado su turno."; return e;
                case "PermissionRequest":
                    e.Kind = SignalKind.PermissionRequired; e.Detail = Json.Str(raw, "tool_name");
                    e.Message = "Codex pide autorización" + (e.Detail != null ? " para " + e.Detail + "." : "."); return e;
                default: return null;
            }
        }
    }

    /// <summary>Cursor hooks (hooks.json v1): stop.</summary>
    internal sealed class CursorAdapter : ISignalAdapter
    {
        public string Tool { get { return "cursor"; } }

        public SignalEvent Translate(JsonElement raw, string hookEvent)
        {
            string name = Json.Str(raw, "hook_event_name") ?? hookEvent;
            string root = Json.FirstArrayString(raw, "workspace_roots");
            SignalEvent e = new SignalEvent
            {
                Source = Tool,
                TaskId = Json.Str(raw, "conversation_id"),
                TurnId = Json.Str(raw, "generation_id"),
                ProjectPath = root,
                Project = Json.ProjectName(root),
            };
            if (name != "stop") return null;
            string status = Json.Str(raw, "status");
            e.Detail = status;
            switch (status)
            {
                case "error": e.Kind = SignalKind.Failed; e.Message = "El agente de Cursor se detuvo por un error."; return e;
                case "aborted": e.Kind = SignalKind.TurnEnded; e.Message = "El agente de Cursor fue cancelado."; return e;
                default: e.Kind = SignalKind.TurnEnded; e.Message = "El agente de Cursor ha terminado su turno."; return e;
            }
        }
    }

    /// <summary>Google Antigravity hooks: Stop. Its payload has no event name, so the script passes it.</summary>
    internal sealed class AntigravityAdapter : ISignalAdapter
    {
        public string Tool { get { return "antigravity"; } }

        public SignalEvent Translate(JsonElement raw, string hookEvent)
        {
            string root = Json.FirstArrayString(raw, "workspacePaths");
            SignalEvent e = new SignalEvent
            {
                Source = Tool,
                TaskId = Json.Str(raw, "conversationId"),
                TurnId = Json.Str(raw, "executionNum"),
                ProjectPath = root,
                Project = Json.ProjectName(root),
            };
            if (hookEvent == "PreInvocation") { e.Kind = SignalKind.Started; e.Message = "Antigravity ha empezado a trabajar."; return e; }
            if (hookEvent != "Stop") return null;
            string reason = Json.Str(raw, "terminationReason");
            string error = Json.Str(raw, "error");
            e.Detail = reason;
            if (reason == "error" || !string.IsNullOrEmpty(error)) { e.Kind = SignalKind.Failed; e.Message = error ?? "Antigravity se detuvo por un error."; return e; }
            if (reason == "max_steps_exceeded") { e.Kind = SignalKind.NeedsInput; e.Message = "Antigravity alcanzó el máximo de pasos y espera instrucciones."; return e; }
            e.Kind = SignalKind.TurnEnded;
            e.Message = Json.Bool(raw, "fullyIdle") == false
                ? "Antigravity ha terminado su turno (aún hay tareas en segundo plano)."
                : "Antigravity ha terminado su turno.";
            return e;
        }
    }

    internal static class SignalAdapters
    {
        internal static readonly Dictionary<string, ISignalAdapter> All = Build();

        private static Dictionary<string, ISignalAdapter> Build()
        {
            Dictionary<string, ISignalAdapter> map = new Dictionary<string, ISignalAdapter>(StringComparer.OrdinalIgnoreCase);
            foreach (ISignalAdapter a in new ISignalAdapter[] { new GenericAdapter(), new ClaudeCodeAdapter(), new CodexAdapter(), new CursorAdapter(), new AntigravityAdapter() }) map[a.Tool] = a;
            map["claude"] = map["claude-code"];
            return map;
        }
    }
}
