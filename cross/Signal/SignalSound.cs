// Capri Signal — provisional "incoming call" sound.
// Windows: plays a WAV through winmm (Capri's own audio session only — other apps' audio is never paused or ducked).
// The built-in tones are synthesized once into %APPDATA%\Capri; set "soundFile" in signal.json to use your own .wav.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CapriDesktop.Signal
{
    internal sealed class SignalSound
    {
        private const int SND_ASYNC = 0x0001;
        private const int SND_NODEFAULT = 0x0002;
        private const int SND_FILENAME = 0x00020000;

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string sound, IntPtr module, int flags);
        [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr device, uint volume);

        private readonly SignalSettings settings;
        private readonly SignalLog log;
        private Timer cutoff;
        private int playing;

        internal SignalSound(SignalSettings settings, SignalLog log) { this.settings = settings; this.log = log; }

        internal static bool Supported { get { return OperatingSystem.IsWindows(); } }

        internal void Ring(SignalKind kind)
        {
            if (!Supported) return;
            string file = Pick(kind);
            if (file == null) return;
            try
            {
                uint level = (uint)(settings.Volume / 100.0 * 0xFFFF);
                waveOutSetVolume(IntPtr.Zero, level | (level << 16));
                PlaySound(file, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                int ticket = Interlocked.Increment(ref playing);
                Timer old = cutoff;
                cutoff = new Timer(delegate { if (ticket == playing) Stop(); }, null, TimeSpan.FromSeconds(settings.SoundSeconds), Timeout.InfiniteTimeSpan);
                if (old != null) old.Dispose();
            }
            catch (Exception error) { log.Write("sound error: " + error.GetType().Name); }
        }

        internal void Stop()
        {
            if (!Supported) return;
            try { PlaySound(null, IntPtr.Zero, 0); } catch (Exception) { }
        }

        private string Pick(SignalKind kind)
        {
            string custom = settings.SoundFile;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                if (File.Exists(custom) && custom.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return custom;
                log.Write("custom soundFile missing or not .wav — using built-in ring");
            }
            bool alarm = kind == SignalKind.Failed;
            string path = Path.Combine(settings.Folder, alarm ? "signal-alert-v1.wav" : "signal-ring-v1.wav");
            try
            {
                if (!File.Exists(path)) { Directory.CreateDirectory(settings.Folder); File.WriteAllBytes(path, alarm ? Alert() : Call()); }
                return path;
            }
            catch (Exception) { return null; }
        }

        // ---- provisional synthesized tones -------------------------------------------------

        private const int Rate = 22050;

        /// <summary>Two-pulse sci-fi "incoming transmission": a bright rising chirp pair, played twice (~2.2 s).</summary>
        internal static byte[] Call()
        {
            float[] samples = new float[(int)(Rate * 2.2)];
            for (int burst = 0; burst < 2; burst++)
            {
                double at = burst * 1.1;
                Chirp(samples, at, 0.16, 880, 1320, 0.55);
                Chirp(samples, at + 0.20, 0.16, 1100, 1650, 0.55);
                Chirp(samples, at + 0.40, 0.42, 1320, 1320, 0.30, shimmer: true);
            }
            return Wav(samples);
        }

        /// <summary>Lower, descending double tone for errors (~1.2 s).</summary>
        internal static byte[] Alert()
        {
            float[] samples = new float[(int)(Rate * 1.2)];
            Chirp(samples, 0.0, 0.28, 660, 440, 0.6);
            Chirp(samples, 0.40, 0.45, 520, 330, 0.6);
            return Wav(samples);
        }

        private static void Chirp(float[] into, double start, double length, double from, double to, double gain, bool shimmer = false)
        {
            int first = (int)(start * Rate);
            int count = (int)(length * Rate);
            double phase = 0;
            for (int i = 0; i < count && first + i < into.Length; i++)
            {
                double t = (double)i / count;
                double freq = from + (to - from) * t;
                phase += 2 * Math.PI * freq / Rate;
                double envelope = Math.Min(1, i / (Rate * 0.008)) * Math.Pow(1 - t, shimmer ? 1.6 : 0.7);
                double tone = Math.Sin(phase) * 0.75 + Math.Sin(phase * 2) * 0.18 + Math.Sin(phase * 3.01) * 0.07;
                if (shimmer) tone *= 0.6 + 0.4 * Math.Sin(2 * Math.PI * 18 * i / Rate);
                into[first + i] += (float)(tone * envelope * gain);
            }
        }

        private static byte[] Wav(float[] samples)
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(memory))
            {
                int bytes = samples.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes); w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
                foreach (float s in samples) w.Write((short)(Math.Max(-1, Math.Min(1, s)) * 30000));
                w.Flush();
                return memory.ToArray();
            }
        }
    }
}
