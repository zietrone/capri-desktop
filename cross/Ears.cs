using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace CapriDesktop
{
    // Her mic on Linux (and Mac with sox): a recorder child process writing 16 kHz mono 16-bit PCM to its stdout, read in 100 ms
    // chunks. PipeWire's pw-record first, then PulseAudio's parecord, then ALSA's arecord, so it works on any Arch desktop.
    internal sealed class LinuxMic : IDisposable
    {
        internal const int Rate = 16000;
        internal const int Chunk = Rate / 10;
        internal event Action<short[]> Heard;
        internal event Action<string> Failed;
        private Process recorder;
        private Thread reader;
        private volatile bool running;
        internal string Tool { get; private set; }

        // A mic picked by name: a PipeWire node name or PulseAudio source for pw-record and parecord, an ALSA device for arecord.
        internal static string[] Recorders = { "pw-record", "parecord", "arecord", "rec" };

        internal static string Which(string tool)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            foreach (string dir in path.Split(':'))
            {
                if (dir.Length == 0) continue;
                string full = Path.Combine(dir, tool);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        internal static string Available()
        {
            foreach (string tool in Recorders) if (Which(tool) != null) return tool;
            return null;
        }

        internal static List<string> Arguments(string tool, string device)
        {
            bool picked = !string.IsNullOrWhiteSpace(device);
            List<string> args = new List<string>();
            switch (tool)
            {
                case "pw-record":
                    // --raw: plain samples, no AU header, which pw-record puts on stdout otherwise.
                    args.AddRange(new[] { "--raw", "--rate", "16000", "--channels", "1", "--format", "s16" });
                    if (picked) { args.Add("--target"); args.Add(device); }
                    args.Add("-");
                    break;
                case "parecord":
                    args.AddRange(new[] { "--raw", "--rate=16000", "--channels=1", "--format=s16le", "--latency-msec=100" });
                    if (picked) args.Add("--device=" + device);
                    break;
                case "arecord":
                    args.AddRange(new[] { "-q", "-t", "raw", "-f", "S16_LE", "-r", "16000", "-c", "1" });
                    if (picked) { args.Add("-D"); args.Add(device); }
                    args.Add("-");
                    break;
                default:
                    args.AddRange(new[] { "-q", "-t", "raw", "-r", "16000", "-c", "1", "-b", "16", "-e", "signed-integer", "-" });
                    break;
            }
            return args;
        }

        internal bool Listening { get { return running; } }

        internal void Start(string device)
        {
            Stop();
            string tool = Available();
            if (tool == null) throw new IOException("I need pw-record, parecord or arecord to hear you. Install pipewire or alsa-utils, choom.");
            Tool = tool;
            ProcessStartInfo start = new ProcessStartInfo(Which(tool))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
            };
            foreach (string arg in Arguments(tool, device)) start.ArgumentList.Add(arg);
            recorder = Process.Start(start);
            if (recorder == null) throw new IOException(tool + " wouldn't start.");
            running = true;
            Process mine = recorder;
            ThreadPool.QueueUserWorkItem(delegate { try { mine.StandardError.ReadToEnd(); } catch (Exception) { } });
            reader = new Thread(delegate() { Read(mine); }) { IsBackground = true, Name = "Capri mic" };
            reader.Start();
        }

        private void Read(Process from)
        {
            Stream input = from.StandardOutput.BaseStream;
            byte[] bytes = new byte[Chunk * 2];
            int have = 0;
            try
            {
                while (running)
                {
                    int read = input.Read(bytes, have, bytes.Length - have);
                    if (read <= 0) break;
                    have += read;
                    if (have < bytes.Length) continue;
                    short[] samples = new short[Chunk];
                    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                    have = 0;
                    Action<short[]> heard = Heard;
                    if (heard != null) heard(samples);
                }
            }
            catch (Exception) { }
            if (running && from == recorder)
            {
                running = false;
                Action<string> failed = Failed;
                if (failed != null) failed("My mic went quiet. Is it plugged in, choom?");
            }
        }

        internal void Stop()
        {
            running = false;
            Process old = recorder;
            recorder = null;
            if (old == null) return;
            try { if (!old.HasExited) old.Kill(); } catch (Exception) { }
            try { old.Dispose(); } catch (Exception) { }
        }

        public void Dispose() { Stop(); }

        // Mics PipeWire or PulseAudio knows, as (name, description), from pactl. Empty without pactl.
        internal static List<KeyValuePair<string, string>> List()
        {
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();
            if (Which("pactl") == null) return found;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("pactl", "list sources") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                start.Environment["LC_ALL"] = "C";
                using (Process pactl = Process.Start(start))
                {
                    string text = pactl.StandardOutput.ReadToEnd();
                    pactl.WaitForExit(3000);
                    string name = null;
                    foreach (string raw in text.Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("Name: ", StringComparison.Ordinal)) name = line.Substring(6);
                        else if (line.StartsWith("Description: ", StringComparison.Ordinal) && name != null)
                        {
                            if (!name.EndsWith(".monitor", StringComparison.Ordinal)) found.Add(new KeyValuePair<string, string>(name, line.Substring(13)));
                            name = null;
                        }
                    }
                }
            }
            catch (Exception) { }
            return found;
        }
    }

    // Splits the mic into things said: a phrase starts when the sound rises well over the room's noise, and ends after a
    // stretch of quiet. A little from before the start is kept so the first word isn't clipped.
    internal sealed class Endpointer
    {
        internal const int PreChunks = 3;
        internal double Floor = 120;
        internal double Sensitivity = 1;
        internal int QuietChunks = 8;
        internal int MostChunks = 200;
        internal int LeastVoiced = 3;
        private readonly Queue<short[]> before = new Queue<short[]>();
        private readonly List<short[]> phrase = new List<short[]>();
        private int voiced;
        private int quiet;
        private bool inside;
        internal int LastLevel;

        internal static double Loudness(short[] samples)
        {
            double sum = 0;
            for (int i = 0; i < samples.Length; i++) sum += (double)samples[i] * samples[i];
            return Math.Sqrt(sum / Math.Max(1, samples.Length));
        }

        // 0..5 for her pill's meter.
        internal static int Meter(double rms)
        {
            if (rms < 80) return 0;
            return Math.Max(0, Math.Min(5, (int)Math.Round(Math.Log(rms / 80, 2))));
        }

        internal void Reset() { before.Clear(); phrase.Clear(); voiced = 0; quiet = 0; inside = false; }

        internal bool Inside { get { return inside; } }

        // Feeds one chunk; returns a whole phrase when one just ended, else null.
        internal short[] Feed(short[] chunk)
        {
            double rms = Loudness(chunk);
            LastLevel = Meter(rms);
            double gate = Math.Max(350, Floor * 3.2) / Math.Max(0.25, Sensitivity);
            bool loud = rms > gate;
            if (!inside)
            {
                // The floor follows the room slowly while nobody's talking, faster down than up.
                Floor = rms < Floor ? Floor * 0.9 + rms * 0.1 : Floor * 0.995 + rms * 0.005;
                before.Enqueue(chunk);
                while (before.Count > PreChunks) before.Dequeue();
                if (!loud) { voiced = 0; return null; }
                inside = true;
                phrase.Clear();
                phrase.AddRange(before);
                before.Clear();
                voiced = 1;
                quiet = 0;
                return null;
            }
            phrase.Add(chunk);
            if (loud) { voiced++; quiet = 0; } else quiet++;
            if (quiet < QuietChunks && phrase.Count < MostChunks) return null;
            inside = false;
            bool real = voiced >= LeastVoiced;
            short[] all = real ? Join(phrase) : null;
            phrase.Clear();
            voiced = 0;
            quiet = 0;
            return all;
        }

        private static short[] Join(List<short[]> chunks)
        {
            int length = 0;
            foreach (short[] c in chunks) length += c.Length;
            short[] all = new short[length];
            int at = 0;
            foreach (short[] c in chunks) { Array.Copy(c, 0, all, at, c.Length); at += c.Length; }
            return all;
        }
    }

    [DataContract]
    internal sealed class TranscriptReply
    {
        [DataMember(Name = "text")] internal string Text;
    }

    // Turns a phrase into words with any OpenAI-style transcription endpoint: whisper.cpp's whisper-server (which she runs
    // herself), Groq, OpenAI, speaches, faster-whisper-server or LocalAI.
    internal static class Transcriber
    {
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        internal static byte[] Wav(short[] samples, int rate)
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(memory))
            {
                int bytes = samples.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                foreach (short s in samples) w.Write(s);
                w.Flush();
                return memory.ToArray();
            }
        }

        internal static string Hear(string url, string key, string model, string language, short[] samples)
        {
            using (MultipartFormDataContent form = new MultipartFormDataContent())
            {
                // Names quoted by hand: whisper.cpp's parser wants name="file", which .NET leaves unquoted.
                ByteArrayContent file = new ByteArrayContent(Wav(samples, LinuxMic.Rate));
                file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = "\"capri.wav\"" };
                form.Add(file);
                Field(form, "model", string.IsNullOrWhiteSpace(model) ? "whisper-1" : model);
                Field(form, "response_format", "json");
                Field(form, "temperature", "0");
                if (!string.IsNullOrEmpty(language)) Field(form, "language", language);
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form })
                {
                    if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                    using (HttpResponseMessage response = client.SendAsync(request).GetAwaiter().GetResult())
                    {
                        byte[] body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        if (!response.IsSuccessStatusCode) throw new IOException("Her ears' server said " + (int)response.StatusCode + ".");
                        return Clean(Parse(body));
                    }
                }
            }
        }

        private static void Field(MultipartFormDataContent form, string name, string value)
        {
            StringContent content = new StringContent(value);
            content.Headers.ContentType = null;
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
            form.Add(content);
        }

        internal static string Parse(byte[] body)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(body))
                {
                    TranscriptReply reply = (TranscriptReply)new DataContractJsonSerializer(typeof(TranscriptReply)).ReadObject(stream);
                    if (reply != null && reply.Text != null) return reply.Text;
                }
            }
            catch (Exception) { }
            return Encoding.UTF8.GetString(body);
        }

        // Whisper's habits on silence and noise: bracketed sound tags, and the lines it makes up from nothing.
        private static readonly string[] Ghosts = { "thank you.", "thanks for watching!", "thank you for watching.", "you", "bye.", ".", "thanks for watching.", "subtitles by the amara.org community" };

        internal static string Clean(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            foreach (string ghost in Ghosts) if (string.Equals(text, ghost, StringComparison.OrdinalIgnoreCase)) return "";
            return text;
        }
    }

    // whisper.cpp's server, run by her on a free localhost port and kept up while voice chat's on so the model stays loaded.
    // Arch has it in extra/whisper-cpp. The model is fetched once into her data folder.
    internal sealed class WhisperServer : IDisposable
    {
        internal static readonly string[] Binaries = { "whisper-server", "whisper.cpp-server", "whisper-cpp-server" };
        internal const string ModelBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
        private readonly string folder;
        private Process server;
        internal int Port { get; private set; }

        internal WhisperServer(string data) { folder = Path.Combine(data, "ears"); }

        internal static string Binary()
        {
            foreach (string name in Binaries) { string found = LinuxMic.Which(name); if (found != null) return found; }
            return null;
        }

        // Base for English, base for the rest too but multilingual. Small enough to answer fast on any CPU.
        internal static string ModelFor(string language) { return language == null || language == "en" ? "ggml-base.en.bin" : "ggml-base.bin"; }

        internal string ModelPath(string language) { return Path.Combine(folder, ModelFor(language)); }

        internal bool HasModel(string language) { string path = ModelPath(language); return File.Exists(path) && new FileInfo(path).Length > 1000000; }

        internal void Download(string language, Action<long, long> progress)
        {
            Directory.CreateDirectory(folder);
            string target = ModelPath(language);
            string part = target + ".part";
            using (HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (HttpResponseMessage response = http.GetAsync(ModelBase + ModelFor(language), HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                long size = response.Content.Headers.ContentLength ?? 0;
                using (Stream from = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (FileStream to = File.Create(part))
                {
                    byte[] buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    long last = 0;
                    while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        to.Write(buffer, 0, read);
                        done += read;
                        if (progress != null && done - last > (4 << 20)) { last = done; progress(done, size); }
                    }
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
        }

        internal bool Running { get { try { return server != null && !server.HasExited; } catch (Exception) { return false; } } }

        internal string Url { get { return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/inference"; } }

        internal void Start(string language)
        {
            if (Running) return;
            string binary = Binary();
            if (binary == null) throw new IOException("I need whisper.cpp to hear you offline. Run: sudo pacman -S whisper-cpp");
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));
            ProcessStartInfo start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in new[] { "--host", "127.0.0.1", "--port", Port.ToString(CultureInfo.InvariantCulture), "-t", threads.ToString(CultureInfo.InvariantCulture), "-m", ModelPath(language), "-l", language ?? "en", "-nt" })
                start.ArgumentList.Add(arg);
            server = Process.Start(start);
            Process mine = server;
            ThreadPool.QueueUserWorkItem(delegate { try { mine.StandardOutput.ReadToEnd(); } catch (Exception) { } });
            ThreadPool.QueueUserWorkItem(delegate { try { mine.StandardError.ReadToEnd(); } catch (Exception) { } });
            // Up once its port takes a connection, which is after the model's loaded.
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < 30000)
            {
                if (!Running) throw new IOException("whisper-server stopped as it started. Is its model whole?");
                try { using (TcpClient tcp = new TcpClient()) { tcp.Connect(IPAddress.Loopback, Port); return; } }
                catch (SocketException) { Thread.Sleep(200); }
            }
            Stop();
            throw new IOException("whisper-server took too long to start.");
        }

        internal void Stop()
        {
            Process old = server;
            server = null;
            if (old == null) return;
            try { if (!old.HasExited) old.Kill(); } catch (Exception) { }
            try { old.Dispose(); } catch (Exception) { }
        }

        public void Dispose() { Stop(); }
    }
}
