// Capri Signal — headless self-test:  Capri.exe --signal-selftest [outputFolder]
// Starts a real receiver on 127.0.0.1 with a throw-away settings folder, sends real HTTP requests and checks the results.
// Writes signal-selftest.txt (PASS/FAIL per check) and returns 0 only if everything passed. No UI, no user settings touched.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CapriDesktop.Signal
{
    internal static class SignalSelfTest
    {
        private sealed class Result { internal int Status; internal string Body; }

        internal static int Run(string output)
        {
            output = Path.GetFullPath(output ?? "signal-selftest");
            Directory.CreateDirectory(output);
            List<string> lines = new List<string>();
            int failed = 0;
            Action<string, bool, string> check = (name, ok, info) =>
            {
                lines.Add((ok ? "PASS " : "FAIL ") + name + (ok || info == null ? "" : "  -> " + info));
                if (!ok) failed++;
            };

            string folder = Path.Combine(Path.GetTempPath(), "capri-signal-test-" + Guid.NewGuid().ToString("N"));
            SignalReceiver receiver = null, second = null;
            try
            {
                SignalSettings settings = SignalSettings.Load(folder);
                settings.Port = 47690 + new Random().Next(0, 200);
                settings.Save();
                check("settings file created with random token", File.Exists(settings.FilePath) && settings.Token.Length == 64, null);

                SignalLog log = new SignalLog(settings.LogPath);
                NotificationManager manager = new NotificationManager(settings);
                List<SignalDecision> decisions = new List<SignalDecision>();
                receiver = new SignalReceiver(settings, e => { SignalDecision d = manager.Accept(e, DateTimeOffset.Now); lock (decisions) decisions.Add(d); return d.Outcome.ToString().ToLowerInvariant(); }, log);
                check("receiver starts", receiver.Start(), receiver.LastError);
                int port = receiver.Port;
                check("active port persisted for scripts", SignalSettings.Load(folder).ActivePort == port, null);

                using (HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    string url = "http://127.0.0.1:" + port;
                    Func<string, string, string, Result> post = (path, body, token) =>
                    {
                        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                        if (token != null) req.Headers.Add("X-Capri-Token", token);
                        try
                        {
                            HttpResponseMessage res = http.Send(req);
                            return new Result { Status = (int)res.StatusCode, Body = res.Content.ReadAsStringAsync().Result };
                        }
                        catch (HttpRequestException) { return new Result { Status = -1, Body = "connection closed by receiver" }; }
                    };
                    string t = settings.Token;

                    Result health = new Result();
                    HttpResponseMessage h = http.GetAsync(url + "/v1/health").Result;
                    health.Status = (int)h.StatusCode; health.Body = h.Content.ReadAsStringAsync().Result;
                    check("health endpoint", health.Status == 200 && health.Body.Contains("capri-signal") && !health.Body.Contains(t), health.Body);

                    Result r = post("/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\",\"task_id\":\"A\",\"project\":\"Capri\",\"message\":\"Tests passing\"}", t);
                    check("completed event accepted", r.Status == 200 && r.Body.Contains("shown") && r.Body.Contains("task.completed"), r.Status + " " + r.Body);

                    r = post("/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\",\"task_id\":\"A\",\"project\":\"Capri\",\"message\":\"Tests passing\"}", t);
                    check("exact duplicate dropped", r.Status == 200 && r.Body.Contains("duplicate"), r.Body);

                    r = post("/v1/event", "{\"type\":\"task.failed\",\"source\":\"codex\",\"task_id\":\"B\",\"message\":\"Build failed\"}", t);
                    check("failed event accepted", r.Status == 200 && r.Body.Contains("task.failed"), r.Body);

                    r = post("/v1/event", "{\"type\":\"task.needs_input\",\"source\":\"claude-code\",\"task_id\":\"C\"}", t);
                    check("needs_input event accepted (optional fields omitted)", r.Status == 200 && r.Body.Contains("task.needs_input"), r.Body);

                    r = post("/v1/event", "{\"type\":\"task.permission_required\",\"source\":\"claude-code\",\"task_id\":\"C\"}", t);
                    check("same task updates its card instead of stacking", r.Status == 200 && r.Body.Contains("updated"), r.Body);

                    r = post("/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\"}", null);
                    check("missing token rejected (401)", r.Status == 401, r.Status.ToString());
                    r = post("/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\"}", SignalSettings.NewToken());
                    check("wrong token rejected (401)", r.Status == 401, r.Status.ToString());

                    r = post("/v1/event", "{\"type\": \"task.completed\", ", t);
                    check("malformed JSON rejected (400)", r.Status == 400, r.Status + " " + r.Body);
                    r = post("/v1/event", "{\"type\":\"task.exploded\",\"source\":\"codex\"}", t);
                    check("unknown type rejected (400)", r.Status == 400, r.Body);
                    r = post("/v1/event", "[1,2,3]", t);
                    check("non-object body rejected (400)", r.Status == 400, r.Body);
                    r = post("/v1/hook/notepad", "{}", t);
                    check("unknown tool rejected (400)", r.Status == 400, r.Body);
                    r = post("/v1/event", "{\"type\":\"task.completed\",\"source\":\"x\",\"message\":\"" + new string('a', 40000) + "\"}", t);
                    check("oversized body rejected (413 or connection closed)", r.Status == 413 || r.Status == -1, r.Status.ToString());
                    r = post("/v1/event", "{\"type\":\"task.completed\",\"source\":\"rm -rf /\"}", t);
                    check("source with shell characters rejected", r.Status == 400, r.Body);

                    HttpRequestMessage browser = new HttpRequestMessage(HttpMethod.Post, url + "/v1/event") { Content = new StringContent("{\"type\":\"task.completed\",\"source\":\"codex\"}", Encoding.UTF8, "application/json") };
                    browser.Headers.Add("X-Capri-Token", t);
                    browser.Headers.Add("Origin", "https://evil.example");
                    check("browser (Origin) requests refused (403)", (int)http.Send(browser).StatusCode == 403, null);

                    HttpRequestMessage rebinding = new HttpRequestMessage(HttpMethod.Post, url + "/v1/event") { Content = new StringContent("{\"type\":\"task.completed\",\"source\":\"codex\"}", Encoding.UTF8, "application/json") };
                    rebinding.Headers.Add("X-Capri-Token", t);
                    rebinding.Headers.Host = "attacker.example:" + port;
                    check("foreign Host header refused (403)", (int)http.Send(rebinding).StatusCode == 403, null);

                    int burstOk = 0;
                    for (int i = 0; i < 5; i++)
                    {
                        r = post("/v1/event", "{\"type\":\"task.turn_ended\",\"source\":\"cursor\",\"task_id\":\"burst-" + i + "\"}", t);
                        if (r.Status == 200 && r.Body.Contains("shown")) burstOk++;
                    }
                    check("5 consecutive tasks each get their own card", burstOk == 5, burstOk + "/5");
                    check("queue caps visible cards at maxVisible", manager.Visible().Count == settings.MaxVisible && manager.Waiting > 0, manager.Visible().Count + " visible, " + manager.Waiting + " waiting");
                    check("most urgent card first", manager.Visible()[0].Event.Kind == SignalKind.Failed, manager.Visible()[0].Event.Kind.ToString());
                    int rings = 0; lock (decisions) foreach (SignalDecision d in decisions) if (d.Ring) rings++;
                    check("sound throttled during the burst", rings < decisions.Count, rings + " rings for " + decisions.Count + " events");

                    // adapters, through real HTTP
                    r = post("/v1/hook/claude-code?event=Stop", "{\"session_id\":\"s1\",\"cwd\":\"C:\\\\dev\\\\capri\",\"hook_event_name\":\"Stop\",\"stop_hook_active\":false,\"last_assistant_message\":\"Done.\"}", t);
                    check("Claude Code Stop -> task.turn_ended", r.Body.Contains("task.turn_ended"), r.Body);
                    r = post("/v1/hook/claude-code?event=Notification", "{\"session_id\":\"s1\",\"hook_event_name\":\"Notification\",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission to use Bash\"}", t);
                    check("Claude Code permission_prompt -> task.permission_required", r.Body.Contains("task.permission_required"), r.Body);
                    r = post("/v1/hook/claude-code?event=Notification", "{\"session_id\":\"s1\",\"hook_event_name\":\"Notification\",\"notification_type\":\"idle_prompt\",\"message\":\"Claude is waiting for your input\"}", t);
                    check("Claude Code idle_prompt -> task.needs_input", r.Body.Contains("task.needs_input"), r.Body);
                    r = post("/v1/hook/claude-code?event=Notification", "{\"session_id\":\"s1\",\"hook_event_name\":\"Notification\",\"notification_type\":\"auth_success\"}", t);
                    check("Claude Code auth_success ignored (202)", r.Status == 202, r.Status.ToString());
                    r = post("/v1/hook/claude-code?event=StopFailure", "{\"session_id\":\"s2\",\"hook_event_name\":\"StopFailure\"}", t);
                    check("Claude Code StopFailure -> task.failed", r.Body.Contains("task.failed"), r.Body);
                    r = post("/v1/hook/claude-code?event=Stop", "{\"session_id\":\"s3\",\"hook_event_name\":\"Stop\",\"stop_hook_active\":true}", t);
                    check("Claude Code Stop while stop_hook_active ignored", r.Status == 202, r.Status.ToString());
                    r = post("/v1/hook/codex?event=Stop", "{\"session_id\":\"x1\",\"turn_id\":\"t1\",\"cwd\":\"/tmp\",\"hook_event_name\":\"Stop\",\"stop_hook_active\":false,\"last_assistant_message\":null}", t);
                    check("Codex Stop -> task.turn_ended", r.Body.Contains("task.turn_ended"), r.Body);
                    r = post("/v1/hook/codex?event=PermissionRequest", "{\"session_id\":\"x1\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\"}", t);
                    check("Codex PermissionRequest -> task.permission_required", r.Body.Contains("task.permission_required"), r.Body);
                    r = post("/v1/hook/codex", "{\"type\":\"agent-turn-complete\",\"thread-id\":\"x2\",\"turn-id\":\"t9\",\"cwd\":\"/tmp\",\"last-assistant-message\":\"ok\"}", t);
                    check("Codex legacy notify payload -> task.turn_ended", r.Body.Contains("task.turn_ended"), r.Body);
                    r = post("/v1/hook/cursor?event=stop", "{\"conversation_id\":\"c1\",\"generation_id\":\"g1\",\"hook_event_name\":\"stop\",\"status\":\"error\",\"loop_count\":0,\"workspace_roots\":[\"/tmp\"]}", t);
                    check("Cursor stop status=error -> task.failed", r.Body.Contains("task.failed"), r.Body);
                    r = post("/v1/hook/cursor?event=stop", "{\"conversation_id\":\"c2\",\"generation_id\":\"g1\",\"hook_event_name\":\"stop\",\"status\":\"completed\",\"loop_count\":0}", t);
                    check("Cursor stop status=completed -> task.turn_ended", r.Body.Contains("task.turn_ended"), r.Body);
                    r = post("/v1/hook/antigravity?event=Stop", "{\"conversationId\":\"a1\",\"executionNum\":3,\"terminationReason\":\"model_stop\",\"fullyIdle\":true,\"workspacePaths\":[\"/tmp\"]}", t);
                    check("Antigravity Stop -> task.turn_ended", r.Body.Contains("task.turn_ended"), r.Body);
                    r = post("/v1/hook/antigravity?event=Stop", "{\"conversationId\":\"a2\",\"executionNum\":1,\"terminationReason\":\"error\",\"error\":\"quota\",\"fullyIdle\":true}", t);
                    check("Antigravity Stop with error -> task.failed", r.Body.Contains("task.failed"), r.Body);

                    // port conflict: a second receiver with the same preferred port must move to the next one
                    SignalSettings other = SignalSettings.Load(folder + "-b");
                    other.Port = port;
                    second = new SignalReceiver(other, e => "shown", new SignalLog(null));
                    check("port conflict detected, next free port used", second.Start() && second.Port != port, second.LastError);
                    second.Stop();

                    // shutdown and restart
                    receiver.Stop();
                    bool refused = false;
                    try { using (TcpClient c = new TcpClient()) c.Connect("127.0.0.1", port); }
                    catch (SocketException) { refused = true; }
                    check("receiver stops cleanly (port released)", refused, null);
                    check("active port cleared on stop", SignalSettings.Load(folder).ActivePort == 0, null);
                    check("receiver restarts", receiver.Start(), receiver.LastError);
                    r = post("/v1/event", "{\"type\":\"task.completed\",\"source\":\"codex\",\"task_id\":\"after-restart\"}", t);
                    check("events accepted after restart", r.Status == 200, r.Status.ToString());
                }

                string logText = File.Exists(settings.LogPath) ? File.ReadAllText(settings.LogPath) : "";
                check("log contains no token and no message text", logText.Length > 0 && !logText.Contains(settings.Token) && !logText.Contains("Tests passing") && !logText.Contains("Done."), null);

                byte[] wav = SignalSound.Call();
                check("provisional ring WAV generated", wav.Length > 44 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF", null);

                // preferences persist
                settings.Sound = false; settings.Volume = 33; settings.Silent = true; settings.Save();
                SignalSettings reloaded = SignalSettings.Load(folder);
                check("preferences persist", !reloaded.Sound && reloaded.Volume == 33 && reloaded.Silent, null);
                NotificationManager quiet = new NotificationManager(reloaded);
                SignalDecision silent = quiet.Accept(new SignalEvent { Kind = SignalKind.Failed, Source = "test", TaskId = "q" }, DateTimeOffset.Now);
                check("silent mode: card yes, sound no", silent.Outcome == SignalOutcome.Shown && !silent.Ring, null);
                reloaded.Enabled = false;
                check("disabled: nothing shown", quiet.Accept(new SignalEvent { Kind = SignalKind.Failed, Source = "test", TaskId = "q2" }, DateTimeOffset.Now).Outcome == SignalOutcome.Disabled, null);
                reloaded.Enabled = true; reloaded.Silent = false; reloaded.Sound = true; reloaded.AutoHideSeconds = 1;
                NotificationManager expiring = new NotificationManager(reloaded);
                expiring.Accept(new SignalEvent { Kind = SignalKind.TurnEnded, Source = "test", TaskId = "e1" }, DateTimeOffset.Now.AddSeconds(-5));
                expiring.Accept(new SignalEvent { Kind = SignalKind.NeedsInput, Source = "test", TaskId = "e2" }, DateTimeOffset.Now.AddSeconds(-5));
                expiring.Expire(DateTimeOffset.Now);
                check("turn-ended cards auto-hide, needs-input cards stay", expiring.Count == 1 && expiring.Visible()[0].Event.Kind == SignalKind.NeedsInput, expiring.Count.ToString());
            }
            catch (Exception error)
            {
                check("unexpected exception", false, error.GetType().Name + ": " + error.Message);
            }
            finally
            {
                try { if (receiver != null) receiver.Stop(); } catch (Exception) { }
                try { if (second != null) second.Stop(); } catch (Exception) { }
                try { Directory.Delete(folder, true); Directory.Delete(folder + "-b", true); } catch (Exception) { }
            }

            lines.Insert(0, "Capri Signal self-test  " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  —  " + (failed == 0 ? "ALL PASSED" : failed + " FAILED"));
            File.WriteAllLines(Path.Combine(output, "signal-selftest.txt"), lines);
            return failed == 0 ? 0 : 1;
        }
    }
}
