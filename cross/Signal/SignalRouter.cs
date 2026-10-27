// Capri Signal — validation, adapter selection and sanitizing. No UI, no I/O: easy to test.
using System;
using System.Text.Json;

namespace CapriDesktop.Signal
{
    internal sealed class SignalRejected : Exception
    {
        internal SignalRejected(string reason) : base(reason) { }
    }

    internal static class SignalRouter
    {
        private static readonly JsonDocumentOptions Parse = new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

        /// <summary>Returns null when the event is valid but not interesting (ignored). Throws SignalRejected when malformed.</summary>
        internal static SignalEvent Route(string tool, string hookEvent, string body)
        {
            if (string.IsNullOrWhiteSpace(tool)) tool = "generic";
            if (!IsIdentifier(tool, 40)) throw new SignalRejected("invalid tool name");
            if (hookEvent != null && !IsIdentifier(hookEvent, 60)) throw new SignalRejected("invalid event name");
            ISignalAdapter adapter;
            if (!SignalAdapters.All.TryGetValue(tool, out adapter)) throw new SignalRejected("unknown tool \"" + tool + "\"");
            if (string.IsNullOrWhiteSpace(body)) throw new SignalRejected("empty body");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body, Parse); }
            catch (JsonException) { throw new SignalRejected("body is not valid JSON"); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new SignalRejected("body must be a JSON object");
                SignalEvent e = adapter.Translate(doc.RootElement, hookEvent);
                if (e == null) return null;
                return Sanitize(e);
            }
        }

        internal static SignalEvent Sanitize(SignalEvent e)
        {
            e.Source = SignalEvent.Clean(e.Source, 40) ?? "unknown";
            if (!IsIdentifier(e.Source, 40)) throw new SignalRejected("invalid source");
            e.EventId = SignalEvent.Clean(e.EventId, SignalEvent.MaxField);
            e.TaskId = SignalEvent.Clean(e.TaskId, SignalEvent.MaxField);
            e.TurnId = SignalEvent.Clean(e.TurnId, SignalEvent.MaxField);
            e.Project = SignalEvent.Clean(e.Project, 80);
            e.Detail = SignalEvent.Clean(e.Detail, 60);
            e.Message = SignalEvent.Clean(e.Message, SignalEvent.MaxMessage);
            string path = (e.ProjectPath ?? "").Trim();
            bool pathOk = path.Length > 0 && path.Length <= SignalEvent.MaxPath;
            foreach (char c in path) if (char.IsControl(c)) pathOk = false;
            e.ProjectPath = pathOk ? path : null; // over-long or odd paths are dropped, never truncated
            return e;
        }

        internal static bool IsIdentifier(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length > max) return false;
            foreach (char c in value) if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;
            return true;
        }

        internal static string Display(string source)
        {
            switch ((source ?? "").ToLowerInvariant())
            {
                case "claude-code": return "Claude Code";
                case "codex": return "OpenAI Codex";
                case "cursor": return "Cursor";
                case "antigravity": return "Google Antigravity";
                case "test": return "Prueba";
                default: return source;
            }
        }
    }
}
