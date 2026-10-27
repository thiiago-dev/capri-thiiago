// Capri Signal — user preferences, persisted to %APPDATA%\Capri\signal.json.
// The hook scripts read Port/Token from the same file, so nothing has to be edited by hand.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CapriDesktop.Signal
{
    internal sealed class SignalSettings
    {
        internal const int DefaultPort = 47615;

        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;            // receiver + cards on/off
        [JsonPropertyName("sound")] public bool Sound { get; set; } = true;                // ring on/off
        [JsonPropertyName("silent")] public bool Silent { get; set; } = false;             // quiet mode: cards stay, no sound, no auto-pop animation
        [JsonPropertyName("volume")] public int Volume { get; set; } = 70;                 // 0-100, applied to Capri's own wave output only
        [JsonPropertyName("soundSeconds")] public double SoundSeconds { get; set; } = 4;   // hard cap on ring length
        [JsonPropertyName("soundFile")] public string SoundFile { get; set; }              // optional custom .wav (null = built-in provisional ring)
        [JsonPropertyName("soundCooldownSeconds")] public double SoundCooldownSeconds { get; set; } = 8; // no two rings closer than this
        [JsonPropertyName("maxVisible")] public int MaxVisible { get; set; } = 3;          // simultaneous cards on screen
        [JsonPropertyName("showStarted")] public bool ShowStarted { get; set; } = false;   // show a small card when an agent starts
        [JsonPropertyName("autoHideSeconds")] public double AutoHideSeconds { get; set; } = 25; // turn-ended / completed cards fade after this; needs-input/permission/failed stay
        [JsonPropertyName("dedupSeconds")] public double DedupSeconds { get; set; } = 30;
        [JsonPropertyName("port")] public int Port { get; set; } = DefaultPort;            // preferred port
        [JsonPropertyName("activePort")] public int ActivePort { get; set; }               // port actually bound (written by Capri, read by scripts)
        [JsonPropertyName("token")] public string Token { get; set; }                       // random secret, generated on first run
        [JsonPropertyName("logEvents")] public bool LogEvents { get; set; } = true;        // metadata-only log (no messages, no token)

        [JsonIgnore] internal string Folder;
        [JsonIgnore] internal string FilePath { get { return Path.Combine(Folder, "signal.json"); } }
        [JsonIgnore] internal string LogPath { get { return Path.Combine(Folder, "signal-log.txt"); } }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        internal static string DefaultFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capri");
        }

        internal static SignalSettings Load(string folder)
        {
            SignalSettings settings = null;
            string file = Path.Combine(folder, "signal.json");
            try { if (File.Exists(file)) settings = JsonSerializer.Deserialize<SignalSettings>(File.ReadAllText(file), Options); }
            catch (Exception) { settings = null; }
            if (settings == null) settings = new SignalSettings();
            settings.Folder = folder;
            bool changed = settings.Normalize();
            if (changed || !File.Exists(file)) settings.Save();
            return settings;
        }

        internal bool Normalize()
        {
            bool changed = false;
            if (string.IsNullOrWhiteSpace(Token) || Token.Length < 32) { Token = NewToken(); changed = true; }
            if (Port < 1024 || Port > 65000) { Port = DefaultPort; changed = true; }
            Volume = Math.Max(0, Math.Min(100, Volume));
            SoundSeconds = Math.Max(0.5, Math.Min(15, SoundSeconds));
            SoundCooldownSeconds = Math.Max(0, Math.Min(600, SoundCooldownSeconds));
            MaxVisible = Math.Max(1, Math.Min(6, MaxVisible));
            AutoHideSeconds = Math.Max(0, Math.Min(3600, AutoHideSeconds));
            DedupSeconds = Math.Max(1, Math.Min(3600, DedupSeconds));
            return changed;
        }

        internal static string NewToken()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }

        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
                File.Move(temp, FilePath, true);
            }
            catch (Exception) { }
        }
    }
}
