// Capri Signal — queue, priorities, de-duplication and sound throttling. UI-agnostic and thread-safe.
using System;
using System.Collections.Generic;
using System.Linq;

namespace CapriDesktop.Signal
{
    internal sealed class SignalNotice
    {
        internal int Id;
        internal string ThreadKey;
        internal SignalEvent Event;
        internal SignalKind PreviousKind;
        internal int Count = 1;
        internal DateTimeOffset Created;
        internal DateTimeOffset Updated;
        internal bool Expanded;

        /// <summary>Cards that just report a finished turn can fade on their own; anything that needs you stays.</summary>
        internal bool Sticky { get { return Event.Kind == SignalKind.NeedsInput || Event.Kind == SignalKind.PermissionRequired || Event.Kind == SignalKind.Failed; } }
    }

    internal enum SignalOutcome { Shown, Updated, Duplicate, Disabled, Logged }

    internal sealed class SignalDecision
    {
        internal SignalOutcome Outcome;
        internal SignalNotice Notice;
        internal bool Ring;
    }

    internal sealed class NotificationManager
    {
        private readonly object gate = new object();
        private readonly SignalSettings settings;
        private readonly Dictionary<string, DateTimeOffset> seen = new Dictionary<string, DateTimeOffset>();
        private readonly List<SignalNotice> active = new List<SignalNotice>();
        private DateTimeOffset lastRing = DateTimeOffset.MinValue;
        private int nextId;

        internal event Action Changed;

        internal NotificationManager(SignalSettings settings) { this.settings = settings; }

        internal SignalDecision Accept(SignalEvent e, DateTimeOffset now)
        {
            SignalDecision decision = new SignalDecision();
            lock (gate)
            {
                // 1. exact duplicates (retries, a hook registered twice, ...)
                foreach (string old in seen.Where(p => (now - p.Value).TotalSeconds > settings.DedupSeconds).Select(p => p.Key).ToList()) seen.Remove(old);
                string key = e.DedupKey;
                if (seen.ContainsKey(key)) { decision.Outcome = SignalOutcome.Duplicate; return decision; }
                seen[key] = now;
                if (seen.Count > 2000) seen.Clear();

                if (!settings.Enabled) { decision.Outcome = SignalOutcome.Disabled; return decision; }
                if (e.Kind == SignalKind.Started && !settings.ShowStarted) { decision.Outcome = SignalOutcome.Logged; return decision; }

                // 2. one card per task: later events of the same session update it instead of stacking
                SignalNotice notice = active.FirstOrDefault(n => n.ThreadKey == e.ThreadKey);
                bool escalated = false;
                if (notice != null)
                {
                    escalated = SignalEvent.Priority(e.Kind) > SignalEvent.Priority(notice.Event.Kind);
                    notice.PreviousKind = notice.Event.Kind;
                    notice.Event = e;
                    notice.Count++;
                    notice.Updated = now;
                    decision.Outcome = SignalOutcome.Updated;
                }
                else
                {
                    notice = new SignalNotice { Id = ++nextId, ThreadKey = e.ThreadKey, Event = e, PreviousKind = e.Kind, Created = now, Updated = now };
                    active.Add(notice);
                    decision.Outcome = SignalOutcome.Shown;
                }
                decision.Notice = notice;

                // 3. sound: never while silent, never for "started", and never twice within the cooldown
                //    unless the same task escalated to something that needs you.
                bool wantsSound = settings.Sound && !settings.Silent && e.Kind != SignalKind.Started;
                bool cooled = (now - lastRing).TotalSeconds >= settings.SoundCooldownSeconds;
                decision.Ring = wantsSound && (cooled || (escalated && SignalEvent.Priority(e.Kind) >= 3));
                if (decision.Ring) lastRing = now;
            }
            Raise();
            return decision;
        }

        /// <summary>Cards to draw, most urgent first, capped by MaxVisible. The rest wait in the queue.</summary>
        internal List<SignalNotice> Visible()
        {
            lock (gate) return Ordered().Take(settings.MaxVisible).ToList();
        }

        internal int Waiting { get { lock (gate) return Math.Max(0, active.Count - settings.MaxVisible); } }
        internal int Count { get { lock (gate) return active.Count; } }

        private IEnumerable<SignalNotice> Ordered()
        {
            return active.OrderByDescending(n => SignalEvent.Priority(n.Event.Kind)).ThenByDescending(n => n.Updated);
        }

        internal void Dismiss(int id)
        {
            lock (gate) active.RemoveAll(n => n.Id == id);
            Raise();
        }

        internal void DismissAll()
        {
            lock (gate) active.Clear();
            Raise();
        }

        /// <summary>Removes non-sticky cards older than AutoHideSeconds. Returns true if something changed.</summary>
        internal bool Expire(DateTimeOffset now)
        {
            if (settings.AutoHideSeconds <= 0) return false;
            int removed;
            lock (gate) removed = active.RemoveAll(n => !n.Sticky && (now - n.Updated).TotalSeconds >= settings.AutoHideSeconds);
            if (removed > 0) Raise();
            return removed > 0;
        }

        internal void Toggle(int id)
        {
            lock (gate) foreach (SignalNotice n in active) if (n.Id == id) n.Expanded = !n.Expanded;
            Raise();
        }

        private void Raise()
        {
            Action changed = Changed;
            if (changed != null) changed();
        }
    }
}
