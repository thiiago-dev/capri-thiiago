// Capri Signal — normalized event model.
// Every integration (Claude Code, Codex, Cursor, Antigravity, manual tests) is translated into this one shape.
using System;
using System.Security.Cryptography;
using System.Text;

namespace CapriDesktop.Signal
{
    internal enum SignalKind
    {
        Started,            // task.started            — the agent began working on a prompt
        TurnEnded,          // task.turn_ended         — the agent finished its turn (NOT proof the work is done or correct)
        Completed,          // task.completed          — the tool explicitly reports the task/agent as completed
        NeedsInput,         // task.needs_input        — the agent is waiting for an answer from you
        PermissionRequired, // task.permission_required — the agent wants you to authorize an action
        Failed              // task.failed             — the run stopped because of an error
    }

    internal sealed class SignalEvent
    {
        internal const int MaxMessage = 280;
        internal const int MaxField = 120;
        internal const int MaxPath = 400;

        internal SignalKind Kind;
        internal string Source;      // tool id: claude-code, codex, cursor, antigravity, test, ...
        internal string EventId;     // optional, client-supplied id for idempotency
        internal string TaskId;      // optional, session / conversation id
        internal string TurnId;      // optional, turn / prompt / generation id
        internal string Project;     // optional, project display name
        internal string ProjectPath; // optional, local folder
        internal string Message;     // optional, short human text (truncated)
        internal string Detail;      // optional, sub-type from the tool (e.g. "permission_prompt", "rate_limit")
        internal DateTimeOffset Time = DateTimeOffset.Now;

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
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "task.started": kind = SignalKind.Started; return true;
                case "task.turn_ended": kind = SignalKind.TurnEnded; return true;
                case "task.completed": kind = SignalKind.Completed; return true;
                case "task.needs_input": kind = SignalKind.NeedsInput; return true;
                case "task.permission_required": kind = SignalKind.PermissionRequired; return true;
                case "task.failed": kind = SignalKind.Failed; return true;
                default: kind = SignalKind.Failed; return false;
            }
        }

        /// <summary>Higher = more urgent. Used to order the queue and decide whether an update re-rings.</summary>
        internal static int Priority(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.Failed: return 5;
                case SignalKind.PermissionRequired: return 4;
                case SignalKind.NeedsInput: return 3;
                case SignalKind.Completed: return 2;
                case SignalKind.TurnEnded: return 2;
                default: return 0;
            }
        }

        /// <summary>Key used to collapse several events of the same task into one card.</summary>
        internal string ThreadKey
        {
            get { return (Source ?? "?") + "|" + (TaskId ?? Project ?? ProjectPath ?? "-"); }
        }

        /// <summary>Key used to drop exact duplicates (retries, double-registered hooks).</summary>
        internal string DedupKey
        {
            get
            {
                if (!string.IsNullOrEmpty(EventId)) return "id:" + Source + "|" + EventId;
                string raw = ThreadKey + "|" + TypeName(Kind) + "|" + (TurnId ?? "") + "|" + (Detail ?? "") + "|" + (Message ?? "");
                using (SHA256 sha = SHA256.Create()) return "h:" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
            }
        }

        internal static string Clean(string value, int max)
        {
            if (value == null) return null;
            StringBuilder text = new StringBuilder(Math.Min(value.Length, max + 1));
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
}
