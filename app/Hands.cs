using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
#if !CROSS
using System.Windows.Automation;
#endif

namespace CapriDesktop
{
    [DataContract]
    internal sealed class ToolArguments
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "site")] public string Site;
        [DataMember(Name = "search")] public string Search;
        [DataMember(Name = "action")] public string Action;
        [DataMember(Name = "focus")] public string Focus;
        [DataMember(Name = "song")] public string Song;
        [DataMember(Name = "app")] public string App;
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "task")] public string Task;
        [DataMember(Name = "target")] public string Target;
        [DataMember(Name = "text")] public string Text;
        [DataMember(Name = "keys")] public string Keys;
        [DataMember(Name = "button")] public string Button;
        [DataMember(Name = "enter")] public string Enter;
        [DataMember(Name = "direction")] public string Direction;
        [DataMember(Name = "seconds")] public string Seconds;
        [DataMember(Name = "path")] public string Path;
        [DataMember(Name = "to")] public string To;
        [DataMember(Name = "x")] public string X;
        [DataMember(Name = "y")] public string Y;
        [DataMember(Name = "command")] public string Command;
        [DataMember(Name = "folder")] public string Folder;
    }

    internal sealed class InstalledApp
    {
        internal readonly string Name;
        internal readonly string Id;
        internal InstalledApp(string name, string id) { Name = name; Id = id; }
    }

    internal sealed class OpenWindow
    {
        internal readonly IntPtr Handle;
        internal readonly string Title;
        internal readonly string Program;
        internal readonly bool Minimized;
        internal OpenWindow(IntPtr handle, string title, string program, bool minimized = false) { Handle = handle; Title = title; Program = program; Minimized = minimized; }
    }

    internal static class Hands
    {
        internal const string Note =
            "\nYou have tools for their PC: open (launch, play) their apps and games, switch to, minimize or close apps that are open, play songs or videos they name, open websites or search them, control their music and volume, look at their screen, and use their mouse and keyboard for jobs inside apps and sites. " +
            "Use a tool whenever they ask you to do something like that, and never say you did or are doing it without one. Look at their screen when they ask what you see or what you think of something on it. Only close an app when they ask you to. You can't touch their files. " +
            "Your own website, where people download you, is " + Home + ". " +
            "You have a web browser: when they ask something you don't know or that changes, like the weather, news, scores, prices or times, search Google for it instead of guessing or saying you can't. " +
            "Most messages are just chat, not jobs: answer those as yourself, with your own opinions and tastes, and don't offer to open, play or look at anything unless they ask.";

        internal const string Prompt =
            "You are the hands of Capri, a desktop companion on the user's Windows PC. You don't chat.\n" +
            "Act only on their latest request. Earlier messages are context for names, not commands to repeat. Call one tool: open an app, game or website, search a site, play music, control media or volume, check the current song, switch to, minimize or close an app, or look at their screen when asked about it. Use the names they gave.\n" +
            "If the job needs clicking, typing or more than one step, like opening an app and then searching, going somewhere or making something in it, call use_computer once with the whole job in their words.\n" +
            "Also call use_computer for jobs on files and code: write or edit code, add something to the file they have open, create or organise files and folders, or run a project and check it.\n" +
            "When they name an app, never guess a website for it: use open_app, or use_computer if there's more to do.\n" +
            "If they want music but name none, pick a popular song that fits.\n" +
            "Your own website is " + Home + ": when they ask for your website or page, call open_website with that address.\n" +
            "If they ask something you'd have to look up, like the weather, news, scores, prices, times or facts you're unsure of, call open_website with site google and their question as the search.\n" +
            "Questions about their current screen or song need a tool too. For chat, negated or hypothetical requests, or an unclear target, call no tool and reply with only the word: chat";

        internal const string Chat =
            "\nNo tools ran for this message. Answer as yourself, with your own opinions and tastes. Don't claim you opened, played or looked at anything. You can search Google for them: if they ask something you don't know or that changes, like the weather, news, scores or prices, say you'll look it up instead of guessing or saying you can't.";

        private static readonly string[,] Specs =
        {
            { "open_app", "Open, launch or play an app or game installed on their PC, like Spotify, Discord, Steam or Roblox.",
              "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"The app's name\"}},\"required\":[\"name\"]}" },
            { "open_website", "Open a website in their browser, or search a site like YouTube or Google.",
              "{\"type\":\"object\",\"properties\":{\"site\":{\"type\":\"string\",\"description\":\"The site's name or address, like youtube.com\"},\"search\":{\"type\":\"string\",\"description\":\"Words to search for on that site, if any\"}},\"required\":[\"site\"]}" },
            { "media", "Control the music or video playing on their PC, or the volume, or check what song is playing.",
              "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"What to do\",\"enum\":[\"play_pause\",\"next\",\"previous\",\"volume_up\",\"volume_down\",\"mute\",\"now_playing\"]}},\"required\":[\"action\"]}" },
            { "look_at_screen", "Look at their screen right now to see what they're doing, playing or showing you.",
              "{\"type\":\"object\",\"properties\":{\"focus\":{\"type\":\"string\",\"description\":\"What to look at, if they said\"}}}" },
            { "play_music", "Play a song, playlist, album or artist they name, on Spotify or YouTube. For their liked songs, use kind playlist and song Liked Songs.",
              "{\"type\":\"object\",\"properties\":{\"song\":{\"type\":\"string\",\"description\":\"What to play, like a song and its artist or a playlist's name\"},\"kind\":{\"type\":\"string\",\"description\":\"What it is\",\"enum\":[\"song\",\"playlist\",\"album\",\"artist\"]},\"app\":{\"type\":\"string\",\"description\":\"Where to play it, if they said\",\"enum\":[\"spotify\",\"youtube\"]}},\"required\":[\"song\"]}" },
            { "app_window", "Switch to, minimize or close an app or game that's open on their PC. Naming a site closes that browser tab.",
              "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"The app's name, or the site in a browser tab\"},\"action\":{\"type\":\"string\",\"description\":\"What to do\",\"enum\":[\"switch_to\",\"minimize\",\"close\"]}},\"required\":[\"name\",\"action\"]}" },
            { "use_computer", "Do a job on their PC: clicking, typing or several steps inside an app or website, and also creating, editing, reading or organising files and code, adding code to the file open in their editor, or running and checking a project.",
              "{\"type\":\"object\",\"properties\":{\"task\":{\"type\":\"string\",\"description\":\"The whole job, in their words\"}},\"required\":[\"task\"]}" }
        };

        internal static readonly string Tools = Render(false);
        internal static readonly string ClaudeTools = Render(true);
        internal static readonly string Listing = Written();
        private const string LookupWords = @"^(what('?s| is| are)|how('?s| is)|is it|will it|when('?s| is| does| do)|who won|did|check|get|tell me|show me|find)\b.*\b(weather|forecast|temperature|raining|snowing|news|headlines|scores?|stock price|price of|exchange rate|time (is it )?in|opening hours|open (today|now|tonight)|release date|showtimes|rain|snow|sunny|hot out|cold out)\b|^(weather|forecast|news|headlines)\b|^who (won|is winning)\b";

        private static readonly Regex Asks = new Regex(
            @"^(open|launch|start|run|load up|boot up|fire up|play|shuffle|put on|blast|queue|close|quit|exit|kill|shut down|switch (back |over )?to|back to|go to|bring up|pull up|show me|minimi[sz]e|pause|unpause|resume|stop the music|skip|next|previous|go back|volume|louder|quieter|mute|unmute|search|google|look up|mind (opening|launching|playing|closing))\b" +
            @"|^(turn|make) (it|this|that|the volume|the music|the sound) (up|down|louder|quieter)\b|^(turn (up|down)|lower|raise) (the )?(volume|music|sound)\b|^put (some |a )?(music|song|tunes) on\b|^(get|bring) .+ (going|up)$" +
            @"|^(look at|take a (look|peek)( at)?|check out|check|rate|watch) (this|that|it|my|the|what)\b" +
            @"|^(click|tap|double click|right click|type|press|scroll|go on|get on|hop on|sign in|log in|fill (in|out)|use (my|the) (mouse|keyboard|computer|pc))\b|^(use|make|create|write|draw|search|find) .+ (in|on|with|using|into) (the |my )?\w+" +
            @"|^(see|tell me|show me) (what|which)\b|^see (this|that|my screen|the screen)\b|^what('?s| is) (on my screen|on the screen|playing|this song|this track|the song|on spotify)\b" +
            @"|^what ((song|track)( is|'s)? (this|playing|on)|am i (doing|playing|watching|listening)|are we (doing|playing|watching|listening)|do you see|can you see)\b|^what do you think (of|about) (this|that|it|my (build|skin|setup))\b|^do you (see|like) (this|that|my (build|skin|setup))\b" +
            @"|^(i (want|need|wanna|would like) (you to |to )?(open|launch|play|listen|watch|search)|let's (play|listen|watch)|find (me )?.+ (on|online)|can (you|u) see)\b" +
            "|" + LookupWords +
            @"|^(ouvre|ouvrir|lancer?|d[ée]marrer?|fermer?|quitter?|r[ée]duis|r[ée]duire|minimiser?|chercher?|rechercher?|mets|mettre|jouer?|passer?|couper?|augmenter?|baisser?|regarder?|afficher?|montre-moi|va sur|aller sur|retourne sur)\b" +
            @"|^(tu vois|qu'est-ce que (tu vois|je fais)|c'est quoi (cette|la) (chanson|musique)|quelle (chanson|musique)|qu'est-ce qu'il y a sur mon [ée]cran)\b" +
            @"|^([áa]bre(me)?|abrir|iniciar?|ejecutar?|pon(me|er)?|reproduc(ir|e)|cierra|cerrar|minimiza(r)?|busca(r|me)?|pausa(r)?|sube|baja|silencia(r)?|mira(r)?|ves|f[íi]jate|echa(le)? un vistazo|qu[ée] (ves|hay|tengo|estoy|te parece)|puedes (ver|mirar))\b", RegexOptions.IgnoreCase);

        private static readonly Regex ScreenQuery = new Regex(
            @"\b(my screen|the screen|on screen|what do you see|can you see|look at (this|that|my|the|what)|check (this|that) out|what am i (doing|playing|watching|listening)|mira(r)? (mi |la |esto|lo que|aqu[íi]|en mi)|en (mi |la )?pantalla|qu[ée] (ves|hay en mi pantalla|estoy (haciendo|viendo|jugando|escuchando)|tengo abierto|te parece esto)|puedes (ver|mirar)|ves (esto|mi pantalla|lo que))\b" +
            @"|^(mira|mirar|look|see this|check this|f[íi]jate|echa(le)? un vistazo)\b", RegexOptions.IgnoreCase);

        internal static bool AskingToLook(string said)
        {
            return !string.IsNullOrWhiteSpace(said) && ScreenQuery.IsMatch(said);
        }

        internal static Func<List<InstalledApp>> Catalog = ReadCatalog;
        internal static Func<string, bool> Exists = File.Exists;
        internal static Action<string> Run = Start;
        internal static Action<byte> Press = Tap;
        internal static Func<string, string> Look = delegate { return "You can't see their screen right now."; };
        internal static Func<string, string> Fetch = Download;
        internal static Func<List<OpenWindow>> Windows = ReadWindows;
        internal static Func<OpenWindow, string, bool> Steer = Apply;
        internal static Func<string, bool> PressPlay = ClickPlay;
        internal static Func<string> NowPlaying = SpotifyTitle;
        internal static Func<string, string> Work = delegate { return "Using the mouse and keyboard isn't ready on this PC yet."; };
        internal static Func<string, string> Resume = delegate { return null; };
        private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
        private static readonly string[] Shell = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
        private delegate bool Visit(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr window, out int process);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(int from, int to, bool attach);
        [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
        private static readonly Regex Junk = new Regex(@"\b(uninstall\w*|desinstall\w*|readme|read me|lisez moi|help|aide|manual|manuel|documentation|release notes|notes de version|licen[cs]e|website|site web|support|assistance|changelog)\b", RegexOptions.IgnoreCase);
        private static readonly string[,] Searches =
        {
            { "youtube", "https://www.youtube.com/results?search_query=" },
            { "google", "https://www.google.com/search?q=" },
            { "twitch", "https://www.twitch.tv/search?term=" },
            { "reddit", "https://www.reddit.com/search/?q=" },
            { "wikipedia", "https://en.wikipedia.org/w/index.php?search=" },
            { "spotify", "https://open.spotify.com/search/" },
            { "roblox", "https://www.roblox.com/discover/?Keyword=" },
            { "github", "https://github.com/search?q=" }
        };
        private static readonly object gate = new object();
        private static List<InstalledApp> cached;
        private static DateTime cachedAt;

        internal static string Do(string tool, ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            try
            {
                if (tool == "open_app") return OpenApp(args.Name);
                if (tool == "open_website") return OpenSite(args.Site, args.Search);
                if (tool == "media") return Media(args.Action);
                if (tool == "look_at_screen") return Look(args.Focus);
                if (tool == "play_music") return PlayMusic(args.Song, args.App, args.Kind);
                if (tool == "app_window") return Window(args.Name, args.Action);
                if (tool == "discord_call") return Computer.DiscordCall(args.Name, args.Kind == "video");
                if (tool == "use_computer") return string.IsNullOrWhiteSpace(args.Task) ? "Which job, choom?" : Safety.Screen(args.Task) != null ? "Not doing that, choom." : Work(args.Task.Trim());
                return "There's no tool called " + tool + ".";
            }
            catch (Exception error) { return "That didn't work: " + error.Message; }
        }

        internal static void Forget() { lock (gate) cached = null; }

        internal static int Count { get { return Specs.GetLength(0); } }

        internal static string Name(int i) { return Specs[i, 0]; }

        internal static string Description(int i) { return Specs[i, 1]; }

        internal static string ElevenLabsTool(int i) { return "{\"tool_config\":{\"type\":\"client\",\"name\":\"" + Specs[i, 0] + "\",\"description\":" + Voice.Quote(Specs[i, 1]) + ",\"parameters\":" + Specs[i, 2] + ",\"expects_response\":true,\"response_timeout_secs\":" + (Specs[i, 0] == "use_computer" ? 60 : 20) + "}}"; }

        private static string Render(bool claude)
        {
            List<string> tools = new List<string>();
            for (int i = 0; i < Specs.GetLength(0); i++)
                tools.Add(claude
                    ? "{\"name\":\"" + Specs[i, 0] + "\",\"description\":" + Voice.Quote(Specs[i, 1]) + ",\"input_schema\":" + Specs[i, 2] + "}"
                    : "{\"type\":\"function\",\"function\":{\"name\":\"" + Specs[i, 0] + "\",\"description\":" + Voice.Quote(Specs[i, 1]) + ",\"parameters\":" + Specs[i, 2] + "}}");
            return "[" + string.Join(",", tools.ToArray()) + "]";
        }

        private static string Written()
        {
            StringBuilder text = new StringBuilder("\nYour tools:");
            for (int i = 0; i < Specs.GetLength(0); i++)
            {
                List<string> keys = new List<string>();
                foreach (Match key in Regex.Matches(Specs[i, 2], @"""(\w+)"":\{""type"":""string"",""description"":""[^""]*""(?:,""enum"":\[([^\]]*)\])?"))
                    keys.Add("\"" + key.Groups[1].Value + "\": \"" + (key.Groups[2].Success ? key.Groups[2].Value.Replace("\"", "").Replace(",", "|") : "...") + "\"");
                text.Append("\n- " + Specs[i, 0] + "{" + string.Join(", ", keys.ToArray()) + "}: " + Specs[i, 1]);
            }
            return text.Append("\nChoose one value from each | choice and omit unused fields.\nTo use one, reply with only the call, like: open_app{\"name\": \"Spotify\"}").ToString();
        }

        internal static bool Wants(string said)
        {
            if (Regex.IsMatch(said ?? "", @"\b(please|pls|plz|stp)\s*[.!?]*$", RegexOptions.IgnoreCase) && Site(Asked(said))) return true;
            foreach (string part in Regex.Split(said ?? "", @"[,;!?]\s*|\.\s+"))
            {
                string text = Asked(part);
                if (!Chatter.IsMatch(text) && Asks.IsMatch(text)) return true;
            }
            return false;
        }

        internal static void Rescue(string said, List<KeyValuePair<string, ToolArguments>> calls)
        {
            string text = Asked(said);
            if (calls.Exists(call => call.Key == "use_computer" || call.Key == "look_at_screen" || call.Key == "media" || call.Key == "play_music")) return;
            if (Wants(said) && Regex.IsMatch(text, @"\s(and|then|&)\s+(then\s+)?(\w+\s+)?(search|look|type|click|go|make|create|write|find|open|sign|log|send|start|join|watch|check|put|add|draw|set|turn)\b", RegexOptions.IgnoreCase))
            {
                calls.Clear();
                calls.Add(new KeyValuePair<string, ToolArguments>("use_computer", new ToolArguments { Task = text }));
                return;
            }
            for (int i = 0; i < calls.Count; i++)
            {
                ToolArguments args = calls[i].Value;
                if (calls[i].Key != "open_website" || args == null || string.IsNullOrEmpty(args.Site) || Regex.IsMatch(text, @"[a-z0-9-]+\.[a-z]{2,}", RegexOptions.IgnoreCase)) continue;
                string name = Regex.Replace(args.Site.Trim(), @"^(https?://)?(www\.)?|\.[a-z.]+(/.*)?$", "", RegexOptions.IgnoreCase);
                if (Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase) && Installed(name) && string.IsNullOrEmpty(args.Search)) calls[i] = new KeyValuePair<string, ToolArguments>("open_app", new ToolArguments { Name = name });
            }
        }

        private static readonly Regex Unknown = new Regex(@"\b(can'?t|cannot|can not|unable to|don'?t have|no way to|not able to)\b[^.!?]{0,40}\b(check|browse|look|search|google|access|know|tell|real[- ]?time|live|internet|web|online|current|up[- ]to[- ]date|forecast)\b|\b(i'?ll|i will|let me|lemme|i'?m (gonna|going to)|gonna|i gotta|i have to|i need to)\s+(\w+\s+){0,2}(search|google|look (it |that |this )?up|check)\b|\b(you('ll)? (might|may|will|would)? ?(need|want|have) to|maybe (you can|try)|you (gotta|got to|could|can|should)|try)\s+(\w+\s+){0,3}(look|check|search|google)\b|\bcheck (a|the|your) (\w+\s+){0,2}(weather|news|app|site|feed|forecast|website)\b|\bcrystal ball\b", RegexOptions.IgnoreCase);
        private static readonly Regex Question = new Regex(@"\?\s*$|^(what|what's|whats|how|how's|when|where|who|which|why|is|are|was|were|will|does|do|did|can|could|would|tell me|check|look up|google|search|any idea)\b", RegexOptions.IgnoreCase);

        internal static ToolArguments Lookup(string said, string reply)
        {
            string text = Asked(said);
            string plain = Regex.Replace(text, @"^(do|did) (you|u) (know|have any idea)\b|\byou know\b|^(tell|show|get|find) me\b|\b(near me|where i (am|live)|in my (area|city|town|suburb))\b", "", RegexOptions.IgnoreCase);
            if (text.Length < 4 || !Question.IsMatch((said ?? "").Trim()) || !Unknown.IsMatch((reply ?? "").Replace('’', '\'')) || Regex.IsMatch(plain, @"\b(screen|song|playing|track|you|your|yourself|u|ur|capri|me|my|i|i'm|im)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(reply ?? "", @"\bscreen\b", RegexOptions.IgnoreCase) || Safety.Screen(text) != null) return null;
            return new ToolArguments { Site = "google", Search = text };
        }

        internal static string Pulled(string result)
        {
            if ((result ?? "").StartsWith("Opened your own website", StringComparison.Ordinal)) return "Here's my site, choom. That's where people download me.";
            Match searched = Regex.Match(result ?? "", @"^Searched google\.com for (.+)\.$");
            return searched.Success ? "Pulled up \"" + searched.Groups[1].Value + "\" on Google for you, choom." : null;
        }

        internal const string Unpromised = "I haven't done that yet, choom. Tell me what to open or click and I'm on it.";
        private static readonly Regex Promise = new Regex(@"\b(i'?ll|i will|let me|lemme|i'?m (gonna|going to|about to)|gonna|on it,?)\s+(\w+\s+){0,2}(open|launch|load|boot|fire|pull|bring|search|google|look up|click|type|close|switch|head|install|download|play(?!\s+(along|around|with|nice|fair|dumb|it (cool|safe)|((a|some|the|this|that|any|your|my)\s+)?(games?|round|match)\b|devil))|put on|queue|blast|spin)\b|^((sure|ok|okay|alright|bet|got it|on it|say less|yessir+|yezzir+)[,.!\s]+)?(opening|launching|loading|pulling up|bringing up|firing up|booting up|heading to|searching|clicking|typing|closing|switching|playing|putting on|queuing|queueing|blasting|spinning)\b", RegexOptions.IgnoreCase);

        internal static bool Promised(string said) { return Promise.IsMatch((said ?? "").Replace('’', '\'')); }

        internal static bool Follows(string said, string asked, string answered)
        {
            string text = Asked(said);
            if (Chatter.IsMatch(text) || Regex.IsMatch(text, @"^(no|nah|never mind|nevermind|don't|do not|non|annule)\b", RegexOptions.IgnoreCase)) return false;
            if (Wants(text) && Regex.IsMatch(text, @"\b(it|this|that|them|there|again|instead|ça|celui)\b|-(le|la)\b", RegexOptions.IgnoreCase)) return true;
            if (!Wants(asked)) return false;
            if (Regex.IsMatch(text, @"^(do (it|that)|same again|again|on (spotify|youtube)( instead)?|fais(-le| ça)|encore)$", RegexOptions.IgnoreCase)) return true;
            if (!Regex.IsMatch(answered ?? "", @"\b(which|what should I play|lequel|laquelle)\b|\b(or|ou)\b[^?]*\?", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(text, @"^(the )?(first|second|other|last)( one)?$|^(le|la) (premi[èe]re?|deuxi[èe]me|seconde?|derni[èe]re?|autre)$|^(yes|yeah|yep|oui)$", RegexOptions.IgnoreCase)) return true;
            return text.Length > 1 && text.Length < 80 && Regex.IsMatch(answered ?? "", @"(?<!\w)" + Regex.Escape(text) + @"(?!\w)", RegexOptions.IgnoreCase);
        }

        private static readonly Regex Lead = new Regex(@"^((hey|yo|ok|okay|salut|bonjour|hola|oye|ey)[\s,]+)?(capri\b[\s,]*)?(((can|could|would|will) (you|u)|do (you|u)(?= mind)|please|pls|plz|stp|s'il te pla[iî]t|(est-ce que )?tu peux|peux-tu|pourrais-tu|por favor|porfa|puedes|podr[íi]as|me puedes|me podr[íi]as)\s+)*", RegexOptions.IgnoreCase);
        private static readonly Regex Chatter = new Regex(@"^(play (with|along|around|dead|dumb|nice|fair|a game with|a joke)|open up (to|about)|start (a story|a joke|an argument)|run (a mile|a race)|kill (the boss|me|time)|look (at it this way|forward)|show me (how|why)|check my (spelling|grammar)|rate my joke|mets-toi)\b", RegexOptions.IgnoreCase);

        private static string Asked(string said)
        {
            string text = Lead.Replace(Regex.Replace((said ?? "").Replace('\u2019', '\'').Trim(), @"\s+", " "), "");
            for (int i = 0; i < 3; i++) text = Regex.Replace(Regex.Replace(text, @"[\s,.!?]+$", ""), @"\s+(for me|please|pls|plz|rn|right now|now|stp|s'il te pla[iî]t|por favor|porfa|ahora|ahora mismo)$", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"^(?:que\s+me\s+|si\s+(?:me\s+)?puedes\s+|a\s+ver\s+si\s+(?:me\s+)?|quiero\s+que\s+(?:me\s+)?)", "", RegexOptions.IgnoreCase).Trim();
            text = Regex.Replace(text, @"^[áa]bre(?:me|te|s|lo)?\b", "open", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"^(?:pon(?:me|ga|gas|er)?|reproduc(?:e|ir|es))\b", "play", RegexOptions.IgnoreCase);
            return text;
        }
        private static readonly string[] MediaWords =
        {
            @"^(pause|stop the music|pause (it|this|spotify|(the|this) (music|song|track|video)))$", "pause",
            @"^(unpause|resume|unpause (it|spotify|the (music|song|video))|resume (it|spotify|the (music|song|track|video))|keep playing|play it again)$", "resume",
            @"^(skip|next|skip (this|the) (song|track)|next (song|track))$", "next",
            @"^(previous|go back|(last|previous) (song|track))$", "previous",
            @"^(turn (it|the volume|the music) up|volume up|louder)$", "volume_up",
            @"^(turn (it|the volume|the music) down|volume down|quieter)$", "volume_down",
            @"^(un)?mute( (it|the volume|the sound))?$", "mute",
            @"^((what('|\u2019)?s|what is) (playing|this song|this track|the song|on spotify)|what (song|track)( is|('|\u2019)s)? (this|playing|on)|what am i listening to|what are we listening to)$", "now_playing"
        };

        internal static bool Heard(string said, out string tool, out ToolArguments args)
        {
            List<string> tries = new List<string>();
            Match named = Regex.Match(said ?? "", @"\b[ck]app?r(?:i|ee|ie|y)\b[\s,.!?]*([^\s,.!?].*)$", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
            if (named.Success) tries.Add(named.Groups[1].Value);
            string[] sentences = Regex.Split((said ?? "").Trim(), @"(?<=[.!?])\s+");
            if (sentences.Length > 1) tries.Add(sentences[sentences.Length - 1]);
            tries.Add(said ?? "");
            foreach (string attempt in tries) if (Direct(attempt, out tool, out args)) return true;
            tool = null;
            args = new ToolArguments();
            return false;
        }

        internal static bool Direct(string said, out string tool, out ToolArguments args)
        {
            tool = null;
            args = new ToolArguments();
            string text = Asked(said);
            if (Chatter.IsMatch(text) || Regex.IsMatch(text, @"\b(if|unless|until|after|before|when) (i|you|we|it|the)\b", RegexOptions.IgnoreCase)) return false;
            string lower = text.ToLowerInvariant();
            for (int i = 0; i < MediaWords.Length; i += 2)
                if (Regex.IsMatch(lower, MediaWords[i])) { tool = "media"; args.Action = MediaWords[i + 1]; return true; }
            Match found = Regex.Match(text, @"^(?:open|launch|start|go to|fire up|load up|[áa]bre(?:me)?|abrir|iniciar?)\s+(?:el\s+|la\s+)?(spotify|youtube)\s*(?:,|and|&|y)?\s*(?:then\s+|luego\s+)?(?:play|shuffle|put on|blast|pon(?:me|er)?|reproduc(?:ir|e)|busca(?:r|me)?)\s+(?:la\s+canci[óo]n\s+|el\s+tema\s+)?(.+)$", RegexOptions.IgnoreCase);
            if (found.Success) text = "play " + found.Groups[2].Value + " on " + found.Groups[1].Value;
            if (Regex.IsMatch(text, @"\b(and|then|but|y|pero)\s+(open|launch|play|close|quit|pause|don't|do not|abre|abrir|pon|cierra)\b", RegexOptions.IgnoreCase)) return false;
            found = Regex.Match(text, @"^(open|launch|start|run|load up|boot up|fire up|[áa]bre(?:me)?|abrir|iniciar?|ejecutar?)\s+(up\s+|el\s+|la\s+)?(.+)$", RegexOptions.IgnoreCase);
            if (found.Success)
            {
                string target = found.Groups[3].Value.Trim();
                if (Regex.IsMatch(target, @"\s(and|then|&|y)\s|,\s*\S", RegexOptions.IgnoreCase)) return false;
                if (Regex.IsMatch(target, @"^(your|ur|capri'?s|the capri)( own)? (web ?site|site|page|download page)$", RegexOptions.IgnoreCase)) { tool = "open_website"; args.Site = Home; return true; }
                if (Installed(target)) { tool = "open_app"; args.Name = target; return true; }
                if (Site(target)) { tool = "open_website"; args.Site = target; return true; }
                return false;
            }
            found = Regex.Match(text, @"^(?:google|look up|busca(?:r|me)?(?:\s+en\s+google)?)\s+(.+)$", RegexOptions.IgnoreCase);
            if (found.Success && !Regex.IsMatch(found.Groups[1].Value, @"^(it|this|that|something|stuff|for me|eso|esto)$", RegexOptions.IgnoreCase) && !Regex.IsMatch(found.Groups[1].Value, @"\b(spotify|youtube)\b", RegexOptions.IgnoreCase)) { tool = "open_website"; args.Site = "google"; args.Search = found.Groups[1].Value.Trim(); return true; }
            if (Regex.IsMatch(text, LookupWords, RegexOptions.IgnoreCase) && !Regex.IsMatch(Regex.Replace(text, @"\bnear me\b", ""), @"\b(my|your|you|me|i)\b", RegexOptions.IgnoreCase) && Safety.Screen(text) == null) { tool = "open_website"; args.Site = "google"; args.Search = text; return true; }
            found = Regex.Match(text, @"^(?:play|shuffle|put on|blast|pon(?:me|er)?|reproduc(?:ir|e)|busca(?:r|me)?\s+y\s+pon(?:me)?)\s+(?:la\s+canci[óo]n\s+(?:de\s+)?|el\s+tema\s+(?:de\s+)?|una\s+canci[óo]n\s+llamada\s+)?(.+?)(\s+(?:on|en)\s+(spotify|youtube))?$", RegexOptions.IgnoreCase);
            if (found.Success)
            {
                string target = found.Groups[1].Value.Trim();
                if (!found.Groups[3].Success && Installed(target)) { tool = "open_app"; args.Name = target; return true; }
                if (Regex.IsMatch(target, @"^(with|along|around|a game|games?\b|dead|nice|fair|it\b|me\b|us\b|together|music$|some music$|m[úu]sica$|algo de m[úu]sica$|((a|any|some|random|una|alguna) )?(song|track|music|playlist|canci[óo]n|tema)s?$|(something|anything|algo)\b(?!\s+(by|de)\s))", RegexOptions.IgnoreCase)) return false;
                if (found.Groups[3].Success) args.App = found.Groups[3].Value.ToLowerInvariant();
                Match kind = Regex.Match(target, @"^(?:(?:the|my|your|la|mi|tu)\s+)?(.+?)\s+(playlist|album|[áa]lbum)$", RegexOptions.IgnoreCase);
                Match first = Regex.Match(target, @"^(?:the\s+|la\s+|el\s+|mi\s+)?(playlist|album|[áa]lbum)\s+(?:de\s+)?(.+)$", RegexOptions.IgnoreCase);
                Match by = Regex.Match(target, @"^(?:some(?:thing)?|anything|algo|(?:(?:some|a|any|unas?|algunas?)\s+)?(?:songs?|music|tracks?|canciones|temas|m[úu]sica))\s+(?:by|de)\s+(.+)$", RegexOptions.IgnoreCase);
                if (Regex.IsMatch(target, @"^((my|your|the|mis|tus|las)\s+)?((liked|saved)\s+songs|likes|canciones que (me|te) gustan|me gusta)(\s+playlist)?$", RegexOptions.IgnoreCase)) { args.Kind = "playlist"; args.Song = "Liked Songs"; }
                else if (kind.Success) { string k = kind.Groups[2].Value.ToLowerInvariant(); args.Kind = k == "álbum" ? "album" : k; args.Song = kind.Groups[1].Value; }
                else if (first.Success) { string k = first.Groups[1].Value.ToLowerInvariant(); args.Kind = k == "álbum" ? "album" : k; args.Song = first.Groups[2].Value; }
                else if (by.Success) { args.Kind = "artist"; args.Song = by.Groups[1].Value; }
                else { args.Kind = "song"; args.Song = target; }
                tool = "play_music";
                return true;
            }
            found = Regex.Match(text, @"^(close|quit|exit|kill|shut down|switch to|go to|bring up|show me|minimi[sz]e)\s+(.+)$", RegexOptions.IgnoreCase);
            if (!found.Success || Regex.IsMatch(found.Groups[2].Value, @"^(it|this|that|them|this one|that one|everything|all|all of (it|them)|the window|this window|that window)$", RegexOptions.IgnoreCase) || !Showing(found.Groups[2].Value)) return false;
            string verb = found.Groups[1].Value.ToLowerInvariant();
            tool = "app_window";
            args.Name = found.Groups[2].Value.Trim();
            args.Action = verb.StartsWith("minimi", StringComparison.Ordinal) ? "minimize" : Regex.IsMatch(verb, "^(close|quit|exit|kill|shut down)$") ? "close" : "switch_to";
            return true;
        }

        internal static bool Failed(string result) { return Regex.IsMatch(result ?? "", @"^(Which app|What should I play|Not (opening|playing) that|No app called|More than one|That didn't work|Couldn't|There's no|That isn't a website|Which job|Not doing that|Should I |Got lost|Ran out of steps|You pressed Esc|You grabbed the mouse|Using the mouse and keyboard isn't)|isn't open\.$|but (couldn't|it's still open)"); }

        private static readonly Regex LooseText = new Regex(@"\b(open_app|open_website|media|look_at_screen|play_music|app_window|use_computer)\s*(\{[^{}]*\}|\([^()]*\))");
        private static readonly Regex ToolPair = new Regex(@"[""']?(\w+)[""']?\s*[:=]\s*(?:""([^""]*)""|'([^']*)'|([^,})\s][^,})]*))");

        private static ToolArguments Pairs(string tool, string call)
        {
            ToolArguments args = new ToolArguments();
            string inside = call.Substring(1, call.Length - 2);
            bool paired = false;
            foreach (Match pair in ToolPair.Matches(inside))
            {
                paired = true;
                Set(args, pair.Groups[1].Value.ToLowerInvariant(), pair.Groups[2].Success ? pair.Groups[2].Value : pair.Groups[3].Success ? pair.Groups[3].Value : pair.Groups[4].Value.Trim());
            }
            string lone = inside.Trim().Trim('"', '\'');
            if (!paired && lone.Length > 0) Set(args, tool == "open_website" ? "site" : tool == "media" ? "action" : tool == "look_at_screen" ? "focus" : tool == "play_music" ? "song" : tool == "use_computer" ? "task" : "name", lone);
            return args;
        }

        private static void Set(ToolArguments args, string key, string value)
        {
            if (key == "name") args.Name = value;
            else if (key == "site") args.Site = value;
            else if (key == "search") args.Search = value;
            else if (key == "action") args.Action = value;
            else if (key == "focus") args.Focus = value;
            else if (key == "song") args.Song = value;
            else if (key == "app") args.App = value;
            else if (key == "kind") args.Kind = value;
            else if (key == "task") args.Task = value;
        }

        private static readonly Regex ToolText = new Regex(@"\A(open_app|open_website|media|look_at_screen|play_music|app_window|use_computer)\s*(\{.*\})\z", RegexOptions.Singleline);

        internal static bool Typed(string reply, out string tool, out ToolArguments args, bool loose = false)
        {
            tool = null;
            args = new ToolArguments();
            string text = Brain.Visible(reply).Trim();
            Match fence = Regex.Match(text, @"\A```(?:json)?\s*\n(.*?)\s*```\z", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (fence.Success) text = fence.Groups[1].Value.Trim();
            text = Regex.Replace(text, @"<\|""\|>|<escape>", "\"");
            Match found = ToolText.Match(text);
            if (found.Success) args = Read(found.Groups[2].Value);
            else
            {
                if (!loose) return false;
                found = LooseText.Match(text);
                if (!found.Success || Regex.IsMatch(text.Substring(0, found.Index), @"\b(not|never|won't|can't|cannot|don't)\b", RegexOptions.IgnoreCase)) return false;
                args = Read(found.Groups[2].Value) ?? Pairs(found.Groups[1].Value, found.Groups[2].Value);
            }
            if (!Valid(found.Groups[1].Value, args)) return false;
            tool = found.Groups[1].Value;
            return true;
        }

        internal static ToolArguments Read(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (System.Xml.XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), System.Xml.XmlDictionaryReaderQuotas.Max))
                {
                    ToolArguments args = (ToolArguments)new DataContractJsonSerializer(typeof(ToolArguments)).ReadObject(reader);
                    return reader.Read() ? null : args;
                }
            }
            catch (SerializationException) { return null; }
            catch (System.Xml.XmlException) { return null; }
        }

        internal static bool Valid(string tool, ToolArguments args)
        {
            if (args == null) return false;
            if (tool == "open_app") return Named(args.Name);
            if (tool == "open_website") return Named(args.Site);
            if (tool == "look_at_screen") return true;
            if (tool == "media") return Regex.IsMatch(args.Action ?? "", @"\A(play_pause|pause|resume|next|previous|volume_up|volume_down|mute|now_playing)\z");
            if (tool == "app_window") return Named(args.Name) && Regex.IsMatch(args.Action ?? "", @"\A(switch_to|minimize|close)\z");
            if (tool == "use_computer") return Named(args.Task);
            return tool == "play_music" && Named(args.Song) && (string.IsNullOrEmpty(args.Kind) || Regex.IsMatch(args.Kind, @"\A(song|playlist|album|artist)\z")) && (string.IsNullOrEmpty(args.App) || args.App == "spotify" || args.App == "youtube");
        }

        private static bool Named(string text) { return !string.IsNullOrWhiteSpace(text) && text.Trim() != "..."; }

        private static bool Installed(string target)
        {
            string want = Regex.Replace(Plain(target), @"^the |( app| application| game)$", "");
            if (want.Length == 0) return false;
            try
            {
                foreach (InstalledApp app in Apps())
                    if (Score(app, want) >= 3) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static bool Site(string target)
        {
            string host = target.Trim().ToLowerInvariant();
            if (Regex.IsMatch(host, @"^(https?://)?[a-z0-9-]+(\.[a-z0-9-]+)+(/\S*)?$")) return true;
            for (int i = 0; i < Searches.GetLength(0); i++) if (host == Searches[i, 0]) return true;
            return false;
        }

        private static bool Showing(string target)
        {
            string want = Regex.Replace(Plain(target), @"^the |( app| application| game| tab| window)$", "");
            if (want.Length == 0) return false;
            try
            {
                foreach (OpenWindow window in Windows())
                    if (Math.Max(Score(Plain(window.Program), want), Score(Plain(window.Title), want)) > 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static string OpenApp(string wanted)
        {
            if (string.IsNullOrWhiteSpace(wanted)) return "Which app, choom?";
            if (Safety.Screen(wanted) != null) return "Not opening that, choom.";
            List<InstalledApp> found = Match(Apps(), wanted);
            if (found.Count == 0) return "No app called " + wanted.Trim() + " is installed on this PC.";
            if (found.Count > 1) return "More than one app matches " + wanted.Trim() + ": " + string.Join(", ", found.ConvertAll(app => app.Name).ToArray()) + ". Which one?";
            Run(found[0].Id);
            return "Opened " + (AppName(found[0]) == "spotify" ? "Spotify" : found[0].Name) + ".";
        }

        internal static List<InstalledApp> Match(List<InstalledApp> apps, string wanted)
        {
            string want = Regex.Replace(Plain(wanted), @"^the |( app| application| game)$", "");
            List<InstalledApp> top = new List<InstalledApp>();
            if (want.Length == 0) return top;
            int best = 0;
            foreach (InstalledApp app in apps)
            {
                string name = AppName(app);
                int score = Score(app, want);
                if (score == 0 || score < best) continue;
                if (score > best) { best = score; top.Clear(); }
                if (!top.Exists(other => AppName(other) == name)) top.Add(app);
            }
            if (top.Count > 3) top.RemoveRange(3, top.Count - 3);
            return top;
        }

        private static int Score(string name, string want)
        {
            if (name == want || name.Replace(" ", "") == want.Replace(" ", "")) return 4;
            if (name.StartsWith(want + " ", StringComparison.Ordinal)) return 3;
            if ((" " + name + " ").Contains(" " + want + " ")) return 2;
            if (want.Length >= 4 && name.Contains(want)) return 1;
            return 0;
        }

        private static int Score(InstalledApp app, string want)
        {
            string name = AppName(app);
            return name.Length == 0 ? 0 : Math.Max(Score(name, want), Score(Plain(app.Name), want));
        }

        private static string AppName(InstalledApp app)
        {
            string name = Plain(app.Name);
            if (string.IsNullOrWhiteSpace(app.Id) || Junk.IsMatch(name) || app.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
            if (Regex.IsMatch(app.Id, @"^SpotifyAB\.SpotifyMusic_[a-z0-9]+!Spotify$|^(?:[A-Za-z]:\\|\\\\).*[\\]Spotify\.exe$", RegexOptions.IgnoreCase)) return "spotify";
            return name;
        }

        private static string Plain(string text) { return Regex.Replace(Regex.Replace((text ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant(), @"\p{M}", ""), @"[^\p{L}\p{Nd}]+", " ").Trim(); }

        private static List<InstalledApp> Apps()
        {
            lock (gate)
            {
                if (cached != null && DateTime.UtcNow - cachedAt <= TimeSpan.FromMinutes(2)) return cached;
                List<InstalledApp> apps;
                Exception failed = null;
                try { apps = new List<InstalledApp>(Catalog()); }
                catch (Exception error) { apps = new List<InstalledApp>(); failed = error; }
                string folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string path = folder.Length == 0 ? "" : Path.Combine(folder, @"Spotify\Spotify.exe");
                if (path.Length > 0 && Exists(path) && !apps.Exists(app => AppName(app) == "spotify")) apps.Add(new InstalledApp("Spotify", path));
                if (failed != null)
                {
                    if (apps.Count == 0) throw failed;
                    return apps;
                }
                cached = apps;
                cachedAt = DateTime.UtcNow;
                return cached;
            }
        }

        private static List<InstalledApp> ReadCatalog()
        {
            List<InstalledApp> apps = new List<InstalledApp>();
            Exception failed = null;
            Thread reader = new Thread(delegate()
            {
                try
                {
                    object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                    object items = Call(Call(shell, "NameSpace", "shell:AppsFolder"), "Items");
                    int count = (int)Get(items, "Count");
                    for (int i = 0; i < count; i++)
                    {
                        object item = Call(items, "Item", i);
                        string name = Get(item, "Name") as string;
                        string id = Get(item, "Path") as string;
                        if (!string.IsNullOrEmpty(id)) apps.Add(new InstalledApp(name ?? "", id));
                    }
                }
                catch (Exception error) { failed = error; }
            });
            reader.SetApartmentState(ApartmentState.STA);
            reader.IsBackground = true;
            reader.Start();
            if (!reader.Join(10000)) throw new TimeoutException("The app list took too long.");
            if (failed != null) throw failed;
            return apps;
        }

        private static object Call(object target, string method, params object[] args) { return target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args); }

        private static object Get(object target, string property) { return target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null); }

        private static void Start(string id)
        {
            using (Process.Start(Launch(id))) { }
        }

        internal static ProcessStartInfo Launch(string id)
        {
            ProcessStartInfo info = Regex.IsMatch(id, @"^[A-Za-z]:\\|^\\\\|^[a-z][a-z0-9+.-]*://|^spotify:", RegexOptions.IgnoreCase) ? new ProcessStartInfo(id) : new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + id);
            info.UseShellExecute = true;
            return info;
        }

        private static string OpenSite(string site, string search)
        {
            if (Safety.Screen((site ?? "") + " " + (search ?? "")) != null) return "Not opening that, choom.";
            string address = Address(site, search);
            if (address == null) return "That isn't a website.";
            Run(address);
            if (address.TrimEnd('/') == "https://" + Home) return "Opened your own website, " + Home + ".";
            string host = new Uri(address).Host;
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            return string.IsNullOrWhiteSpace(search) ? "Opened " + host + "." : "Searched " + host + " for " + search.Trim() + ".";
        }

        internal static string Address(string site, string search)
        {
            string host = Regex.Replace((site ?? "").Trim().ToLowerInvariant(), @"^(https?://)?(www\.)?", "");
            bool searching = !string.IsNullOrWhiteSpace(search);
            if (searching)
            {
                for (int i = 0; i < Searches.GetLength(0); i++)
                {
                    string key = Searches[i, 0];
                    if (host == key || host.StartsWith(key + ".", StringComparison.Ordinal) || host.Contains("." + key + ".")) return Searches[i, 1] + Uri.EscapeDataString(search.Trim());
                }
                return Searches[1, 1] + Uri.EscapeDataString((search.Trim() + " " + host).Trim());
            }
            if (host.Length == 0) return null;
            if (host.IndexOf('.') < 0) host += ".com";
            Uri uri;
            if (!Uri.TryCreate("https://" + host, UriKind.Absolute, out uri) || uri.Host.IndexOf('.') < 0 || uri.UserInfo.Length > 0) return null;
            return uri.AbsoluteUri;
        }

        private static string Media(string action)
        {
            if (action == "play_pause" || action == "pause" || action == "resume") return Toggle(action);
            if (action == "next") { Press(0xB0); return "Skipped to the next track."; }
            if (action == "previous") { Press(0xB1); return "Went back a track."; }
            if (action == "volume_up") { for (int i = 0; i < 5; i++) Press(0xAF); return "Turned the volume up."; }
            if (action == "volume_down") { for (int i = 0; i < 5; i++) Press(0xAE); return "Turned the volume down."; }
            if (action == "mute") { Press(0xAD); return "Toggled mute."; }
            if (action == "now_playing") return Playing();
            return "There's no media action called " + action + ".";
        }

        private static string Playing()
        {
            string title = NowPlaying();
            if (TrackTitle(title))
            {
                int dash = title.IndexOf(" - ", StringComparison.Ordinal);
                return "Spotify is playing " + (dash > 0 ? title.Substring(dash + 3) + " by " + title.Substring(0, dash) : title) + ".";
            }
            foreach (OpenWindow window in Windows())
            {
                Match video = Regex.Match(window.Title, @"^(?:\(\d+\)\s*)?(.+?) - YouTube( Music)?( - |$)");
                if (video.Success) return "Their YouTube tab is on " + video.Groups[1].Value + ".";
            }
            if (Regex.IsMatch(title, @"^Spotify( \w+)?$")) return "Spotify is open but paused.";
            return title.Length > 0 ? "Spotify is open, but I can't tell what's playing." : "Nothing's playing that I can see.";
        }

        private static string Toggle(string action)
        {
            string before = NowPlaying();
            Press(0xB3);
            for (int i = 0; i < 6 && before.Length > 0; i++)
            {
                Thread.Sleep(250);
                string after = NowPlaying();
                if (after == before) continue;
                if (TrackTitle(after)) return "Spotify is playing again.";
                if (TrackTitle(before)) return "Paused Spotify.";
            }
            return action == "pause" ? "Pressed pause." : action == "resume" ? "Pressed play." : "Pressed play/pause.";
        }

        private static void Tap(byte key)
        {
            Native.keybd_event(key, 0, 0x1, UIntPtr.Zero);
            Native.keybd_event(key, 0, 0x1 | 0x2, UIntPtr.Zero);
        }

        private static readonly string[,] KnownSongs =
        {
            { @"^in love with myself(\s+(by|from)\s+(petite meller|kalman chodakiewitz|eli shiffman|(cyberpunk:?\s+)?edgerunners)\b.*)?$", "2iyaHQlYrR8Eyr4wOmOT5D", "In Love With Myself", "In Love With Myself Petite Meller" }
        };

        internal const string Home = "lachydotmcg.github.io/capri-desktop";

        private static string PlayMusic(string song, string app, string kind)
        {
            if (string.IsNullOrWhiteSpace(song)) return "What should I play?";
            song = song.Trim();
            if (Regex.IsMatch(song, @"^((what('|\u2019)?s|what is)\s+)?((currently|now)\s+)?playing( now)?$|^(this|that|the current) (song|track)$", RegexOptions.IgnoreCase)) return Playing();
            if (Regex.IsMatch(song, @"^(play\s+)?(me\s+)?((some|any|a|random|good)\s+)?(music|songs?|tunes|tracks?|something)(\s+for me)?$", RegexOptions.IgnoreCase)) return "What should I play?";
            if (Safety.Screen(song) != null) return "Not playing that, choom.";
            Match tail = Regex.Match(song, @"^(?:(?:the|my|your)\s+)?(.+?)\s+(playlist|album)$", RegexOptions.IgnoreCase);
            if (tail.Success && kind != "artist") { kind = tail.Groups[2].Value.ToLowerInvariant(); song = tail.Groups[1].Value; }
            bool liked = Regex.IsMatch(song, @"^((my|your|the)\s+)?((liked|saved)\s+songs|likes)$", RegexOptions.IgnoreCase);
            if (liked) kind = "playlist";
            if (kind != "playlist" && kind != "album" && kind != "artist") kind = "song";
            string known = null;
            string look = song;
            if (kind == "song")
                for (int i = 0; i < KnownSongs.GetLength(0); i++)
                    if (Regex.IsMatch(song, KnownSongs[i, 0], RegexOptions.IgnoreCase)) { known = KnownSongs[i, 1]; song = KnownSongs[i, 2]; look = KnownSongs[i, 3]; }
            string named = kind == "song" ? song : "the " + song + " " + kind;
            if (app != "youtube" && Match(Apps(), "spotify").Count > 0)
            {
                if (liked) named = "their Liked Songs";
                string spotted = liked ? null : known ?? Find(Fetch("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString("site:open.spotify.com/" + (kind == "song" ? "track" : kind) + " " + song)), @"open\.spotify\.com(?:/|%2F)" + (kind == "song" ? "track" : kind) + @"(?:/|%2F)([A-Za-z0-9]{22})");
                if (kind == "song" && spotted != null)
                {
                    string before = NowPlaying();
                    Run("spotify:track:" + spotted);
                    return Started(before) || PressPlay(song) ? "Playing " + song + " on Spotify." : "Opened " + song + " in Spotify, but couldn't press play. Hit the green Play button.";
                }
                if (liked || spotted != null)
                {
                    Run(liked ? "spotify:collection:tracks" : "spotify:" + kind + ":" + spotted);
                    return PressPlay(liked ? "Liked Songs" : song) ? "Playing " + named + " on Spotify." : "Opened " + named + " in Spotify, but couldn't press play. Hit the green Play button.";
                }
                Run("spotify:search:" + Uri.EscapeDataString(song));
                return PressPlay(song) ? "Playing " + named + " on Spotify." : "Couldn't find " + named + " to play, so Spotify is open on a search for it.";
            }
            string where = Searches[0, 1] + Uri.EscapeDataString(look);
            System.Text.RegularExpressions.Match list = Regex.Match(kind == "song" ? "" : Fetch(where + "&sp=EgIQAw%253D%253D") ?? "", @"""watchEndpoint"":\{""videoId"":""([A-Za-z0-9_-]{11})"",""playlistId"":""([A-Za-z0-9_-]+)""");
            string video = list.Success ? null : Find(Fetch(where), "\"videoId\":\"([A-Za-z0-9_-]{11})\"");
            if (!list.Success && video == null) { Run(where); return "Couldn't find " + named + " to play, so YouTube is open on a search for it."; }
            Run(list.Success ? "https://www.youtube.com/watch?v=" + list.Groups[1].Value + "&list=" + list.Groups[2].Value : "https://www.youtube.com/watch?v=" + video);
            return (app == "spotify" ? "Spotify isn't installed, so playing " : "Playing ") + named + " on YouTube.";
        }

        private static bool ClickPlay(string name)
        {
#if CROSS
            return false;
#else
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                foreach (OpenWindow window in Windows())
                {
                    if (!string.Equals(window.Program, "Spotify", StringComparison.OrdinalIgnoreCase) || window.Handle == IntPtr.Zero) continue;
                    AutomationElement close = null;
                    AutomationElement fallbackPlay = null;
                    int best = 0;
                    foreach (AutomationElement button in AutomationElement.FromHandle(window.Handle).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                    {
                        string btnName = button.Current.Name ?? "";
                        int score = PlayScore(btnName, name);
                        if (score > best)
                        {
                            close = button;
                            best = score;
                            if (best == 3) break;
                        }
                        if (fallbackPlay == null && Regex.IsMatch(btnName, @"^(Play|Lire|Reproducir|Reproduir)\b", RegexOptions.IgnoreCase))
                        {
                            fallbackPlay = button;
                        }
                    }
                    AutomationElement chosen = close != null && (i >= 6 || best > 1) ? close : (i >= 8 ? fallbackPlay : null);
                    object pattern;
                    if (chosen == null || !chosen.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) continue;
                    ((InvokePattern)pattern).Invoke();
                    return true;
                }
            }
            return false;
#endif
        }

        internal static int PlayScore(string label, string name)
        {
            Match play = Regex.Match(label ?? "", @"^(?:Play|Lire|Reproducir|Reproduir)\s+(.+)$", RegexOptions.IgnoreCase);
            if (!play.Success) return 0;
            string[] shown = Words(play.Groups[1].Value);
            string[] want = Words(name);
            bool shorter = Within(shown, want);
            bool longer = Within(want, shown);
            return shorter && longer ? 3 : shorter ? 2 : longer ? 1 : 0;
        }

        private static string[] Words(string text)
        {
            string plain = Plain(Regex.Replace(text ?? "", @"\s+(by|par|de|per)\s+.*$", "", RegexOptions.IgnoreCase));
            return (plain == "titres likes" || plain == "canciones que te gustan" ? "liked songs" : plain).Split(' ');
        }

        private static bool Within(string[] part, string[] whole)
        {
            if (part.Length == 0 || part[0].Length == 0) return false;
            foreach (string word in part) if (Array.IndexOf(whole, word) < 0) return false;
            return true;
        }

        private static string SpotifyTitle()
        {
            string title = "";
            foreach (OpenWindow window in Windows())
                if (string.Equals(window.Program, "Spotify", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(window.Title))
                {
                    if (TrackTitle(window.Title)) return window.Title;
                    title = window.Title;
                }
            return title;
        }

        private static bool TrackTitle(string title) { return Regex.IsMatch(title ?? "", @"^\S.* - \S") && !Regex.IsMatch(title, @"^Spotify(?:$|\s)| - Spotify(?:$|\s)", RegexOptions.IgnoreCase); }

        internal static bool Started(string before, string title) { return title != before && TrackTitle(title); }

        private static bool Started(string before)
        {
            for (int i = 0; i < 24; i++)
            {
                string title = NowPlaying();
                if (Started(before, title)) return true;
                Thread.Sleep(500);
            }
            return false;
        }

        private static string Find(string page, string pattern)
        {
            Group found = Regex.Match(page ?? "", pattern).Groups[1];
            return found.Success ? found.Value : null;
        }

        private static string Download(string address)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
            request.Headers["Accept-Language"] = "en";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            try
            {
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd();
            }
            catch (WebException) { return ""; }
            catch (IOException) { return ""; }
        }

        private static string Window(string wanted, string action)
        {
            if (action != "switch_to" && action != "minimize" && action != "close") return "There's no window action called " + action + ".";
            string want = Regex.Replace(Plain(wanted), @"^the |( app| application| game| tab| window)$", "");
            if (want.Length == 0) return "Which app, choom?";
            wanted = wanted.Trim();
            List<OpenWindow> top = new List<OpenWindow>();
            int best = 0;
            foreach (OpenWindow window in Windows())
            {
                int score = Math.Max(Score(Plain(window.Program), want), Score(Plain(window.Title), want));
                if (score == 0 || score < best) continue;
                if (score > best) { best = score; top.Clear(); }
                top.Add(window);
            }
            if (top.Count == 0) return wanted + " isn't open.";
            List<string> programs = new List<string>();
            List<string> titles = new List<string>();
            foreach (OpenWindow window in top)
                if (!programs.Contains(window.Program)) { programs.Add(window.Program); titles.Add(window.Title.Length > 40 ? window.Title.Substring(0, 37) + "..." : window.Title); }
            if (programs.Count > 1) return "More than one open app matches " + wanted + ": " + string.Join(", ", titles.GetRange(0, Math.Min(3, titles.Count)).ToArray()) + ". Which one?";
            if (action == "switch_to") return Steer(top[0], action) ? "Switched to " + wanted + "." : "Couldn't bring " + wanted + " to the front.";
            if (action == "close" && Array.IndexOf(Browsers, top[0].Program.ToLowerInvariant()) >= 0 && Score(Plain(top[0].Program), want) == 0)
                return Steer(top[0], "close_tab") ? "Closed the " + wanted + " tab." : "Couldn't reach the " + wanted + " tab to close it.";
            bool all = true;
            foreach (OpenWindow window in top) all &= Steer(window, action);
            if (action == "minimize") return "Minimized " + wanted + ".";
            return all ? "Closed " + wanted + "." : "Asked " + wanted + " to close, but it's still open. It might be asking you to save.";
        }

        private static List<OpenWindow> ReadWindows()
        {
            List<OpenWindow> found = new List<OpenWindow>();
            Dictionary<int, string> programs = new Dictionary<int, string>();
            int own = Process.GetCurrentProcess().Id;
            Visit visit = delegate(IntPtr window, IntPtr data)
            {
                int cloaked;
                if (!IsWindowVisible(window) || Native.GetWindow(window, 4) != IntPtr.Zero || (Native.GetWindowLong(window, -20) & 0x80) != 0) return true;
                if (DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                int length = GetWindowTextLength(window);
                if (length == 0) return true;
                StringBuilder kind = new StringBuilder(64);
                GetClassName(window, kind, kind.Capacity);
                if (Array.IndexOf(Shell, kind.ToString()) >= 0) return true;
                int id;
                GetWindowThreadProcessId(window, out id);
                if (id == own) return true;
                string program;
                if (!programs.TryGetValue(id, out program))
                {
                    try { using (Process process = Process.GetProcessById(id)) program = process.ProcessName; }
                    catch (ArgumentException) { program = ""; }
                    catch (InvalidOperationException) { program = ""; }
                    programs[id] = program;
                }
                StringBuilder title = new StringBuilder(length + 1);
                GetWindowText(window, title, title.Capacity);
                found.Add(new OpenWindow(window, title.ToString(), program, IsIconic(window)));
                return true;
            };
            EnumWindows(visit, IntPtr.Zero);
            GC.KeepAlive(visit);
            return found;
        }

        private static bool Apply(OpenWindow window, string action)
        {
            IntPtr handle = window.Handle;
            if (action == "minimize") { ShowWindow(handle, 6); return true; }
            if (action == "close")
            {
                PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 20 && IsWindow(handle); i++) Thread.Sleep(100);
                return !IsWindow(handle);
            }
            if (!Front(handle)) return false;
            if (action != "close_tab") return true;
            Native.keybd_event(0x11, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x57, 0, 0, UIntPtr.Zero);
            Native.keybd_event(0x57, 0, 0x2, UIntPtr.Zero);
            Native.keybd_event(0x11, 0, 0x2, UIntPtr.Zero);
            return true;
        }

        internal static bool Front(IntPtr handle)
        {
            if (IsIconic(handle)) ShowWindow(handle, 9);
            int ignored;
            int mine = GetCurrentThreadId();
            int theirs = GetWindowThreadProcessId(Native.GetForegroundWindow(), out ignored);
            bool attached = theirs != 0 && theirs != mine && AttachThreadInput(mine, theirs, true);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            if (attached) AttachThreadInput(mine, theirs, false);
            for (int i = 0; i < 10 && Native.GetForegroundWindow() != handle; i++) Thread.Sleep(50);
            return Native.GetForegroundWindow() == handle;
        }
    }

    /// <summary>Capri Desktop Agent: decides by itself (not the brain) when a request is a job for her hands.</summary>
    internal static class AgentIntent
    {
        private const string Verbs = @"(?:(?:crea|crear|haz|hace|genera|escrib|anad|agreg|pon|met|edit|modific|cambi|corrig|correg|arregl|renombr|muev|mov|organiz|orden|copi|duplic|ejecut|corr|lanz|arranc|compil|prueb|prob|teste|rellen|complet|continu|sigu|segui|termin|guard|instal|program|desarroll|implement|refactoriz|limpi|busc|encuentr|abr|entr|ve a|vete a|ir a|naveg|acced|cierr|cerr|selecci|elig|escog|puls|presion|clic|click|pincha|teclea|peg|descarg|sub|minimiz|maximiz|cambia a|actualiz|borra|elimin|quita|extrae|descomprim|compr[ie]m|envi)\w*|(find|search for|look for|create|make|write|add|append|insert|put|edit|modify|change|fix|rename|move|organi[sz]e|sort|tidy|copy|duplicate|run|execute|launch|start|build|compile|test|fill(?: in| out)?|complete|continue|keep working|finish|save|install|code|implement|refactor|clean up|set up|go to|go into|enter|navigate|open|close|select|choose|pick|press|click|double click|type|paste|download|upload|minimi[sz]e|maximi[sz]e|switch to|update|delete|remove|extract|unzip|zip|send))";
        private const string Objects = @"(archivo|fichero|carpeta|directorio|codigo|proyecto|script|programa|funcion|clase|formulario|pagina|html|css|javascript|python|java|json|console\.log|print|repo|escritorio|documento|documentos|descargas|file|folder|directory|code|project|program|function|class|form|page|website|document|desktop|downloads|app|aplicacion|component|componente|readme|test|vs ?code|visual studio|bloc de notas|notepad|chrome|edge|firefox|navegador|browser|explorador|explorer|terminal|consola|cmd|powershell|word|excel|discord|steam|ventana|pestana|tab|window|boton|button|menu|ajustes|configuracion|settings|campo|field|enlace|link|correo|email|imagen|foto|image|photo|zip|\w+\.(js|ts|py|cs|html|css|json|txt|md|java|cpp|c|go|rs|php|rb|jsx|tsx|bat|ps1|xml|yml|yaml|csv|docx|xlsx|pdf|png|jpg))";
        private static readonly Regex Lead = new Regex(@"^(?:(?:oye|hey|eh|porfa|por favor|please|pls|capri|choom|vale|venga|ahora|y|and|then|luego|despues)[\s,]+)*(?:(?:puedes|podrias|me puedes|me podrias|can you|could you|would you|quiero que|necesito que|i want you to|i need you to|ayudame a|help me)\s+)?");
        private static readonly Regex Question = new Regex(@"^(como|que es|que significa|cual|por que|porque|cuando|donde esta|how|what|why|which|when|where|is it|es posible|se puede|sabes)\b");
        private static readonly Regex Negative = new Regex(@"\b(no|don'?t|never|nunca)\s+(lo\s+)?(hagas|crees|toques|do|make|create|touch)\b");
        private static readonly Regex LookFirst = new Regex(@"^(mira|revisa|look at|check)\s+(mi |la |my |the )?(pantalla|screen)\s*(y|and|,)\s*");
        private static readonly Regex OpenFirst = new Regex(@"^(abre|open)\s+.{1,40}?\s+(y|and|,)\s+");
        private static readonly Regex Job = new Regex(@"^" + Verbs + @"\b.*\b" + Objects + @"s?\b");

        private static readonly Regex Deflect = new Regex(@"\b(i can'?t|i cannot|i'?m (just|only) a|i don'?t have (access|hands)|you (can|could|should|need to|have to) (just )?(right[- ]?click|double[- ]?click|click|open|press|type|go to)|right[- ]?click|double[- ]?click|no puedo|no tengo acceso|solo soy|haz (doble )?clic|clic derecho|tienes que|puedes hacerlo)", RegexOptions.IgnoreCase);
        private static readonly Regex Imperative = new Regex(@"^" + Verbs + @"\b");

        /// <summary>Safety net: the brain answered "do it yourself / I can't" to something that sounds like an order.</summary>
        internal static bool Deflected(string asked, string reply)
        {
            if (string.IsNullOrWhiteSpace(asked) || string.IsNullOrWhiteSpace(reply) || !Deflect.IsMatch(reply)) return false;
            string t = LockdownWords.Normalize(asked).Trim().TrimStart('\u00bf', '\u00a1', ' ');
            if (Question.IsMatch(t) || Negative.IsMatch(t)) return false;
            Match lead = Lead.Match(t);
            return Imperative.IsMatch(t.Substring(lead.Success ? lead.Length : 0));
        }

        private static readonly Regex CallOn = new Regex(@"^(?:(?:llama(?:me|le)?|haz(?:me)? una llamada|hazle una llamada|videollama(?:me|le)?|haz(?:me|le)? una videollamada|call|ring|phone|video ?call|start a (?:voice |video )?call with)\s+(?:a\s+|al\s+|con\s+|to\s+|with\s+)?(.+?)\s+(?:en|on|por|via|in|through|desde)\s+(?:el\s+)?discord|(?:en|on)\s+discord,?\s+(?:llama(?:me|le)?|videollama|call|video ?call)\s+(?:a\s+|to\s+)?(.+?))(?:\s+(?:please|pls|porfa|por favor))?\s*[.!?]*$");

        /// <summary>"llama a pepe en discord" / "call pepe on discord" / "videollama a ana por discord".</summary>
        internal static bool DiscordCall(string said, out string name, out bool video)
        {
            name = null;
            video = false;
            if (string.IsNullOrWhiteSpace(said)) return false;
            string t = LockdownWords.Normalize(said).Trim().TrimStart('\u00bf', '\u00a1', ' ');
            Match lead = Lead.Match(t);
            string rest = t.Substring(lead.Success ? lead.Length : 0);
            Match m = CallOn.Match(rest);
            if (!m.Success) return false;
            name = (m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            name = Regex.Replace(name, @"^(?:el usuario|la usuaria|el|la|user|usuario|my friend|mi amigo|mi amiga)\s+", "");
            video = Regex.IsMatch(rest, @"video");
            return name.Length > 0 && name.Length <= 40;
        }

        internal static bool Wanted(string said)
        {
            if (string.IsNullOrWhiteSpace(said)) return false;
            string t = LockdownWords.Normalize(said).Trim().TrimStart('\u00bf', '\u00a1', ' ');
            if (Question.IsMatch(t) || Negative.IsMatch(t)) return false;
            Match lead = Lead.Match(t);
            string rest = t.Substring(lead.Success ? lead.Length : 0);
            rest = LookFirst.Replace(rest, "");
            rest = OpenFirst.Replace(rest, "");
            return Job.IsMatch(rest);
        }
    }
}
