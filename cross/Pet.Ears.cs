using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CapriDesktop
{
    internal sealed partial class Settings
    {
        // Empty for her own offline ears (whisper.cpp), or an OpenAI-style /audio/transcriptions address.
        [DataMember] internal string EarsUrl;
        [DataMember] internal string EarsKey;
        [DataMember] internal string EarsModel;
        // A PipeWire/PulseAudio source name, or empty for the default mic.
        [DataMember] internal string MicDevice;
        // 1 normal; higher hears quieter voices, lower ignores more of the room.
        [DataMember] internal double MicSensitivity;
        [DataMember] internal bool EarsChosen;
    }

    // Voice chat on Linux: TALK turns it on, she listens until it's turned off, and each thing said goes to her like typing.
    // She doesn't listen while she thinks or talks, so she never hears herself.
    internal sealed partial class Pet
    {
        internal const string EarsHeard = "(voice chat)";
        private LinuxMic mic;
        private WhisperServer whisper;
        private readonly Endpointer endpointer = new Endpointer();
        private readonly BlockingCollection<short[]> phrases = new BlockingCollection<short[]>(4);
        private Thread hearer;
        private volatile bool chatting;
        private volatile bool micMuted;
        private volatile bool deaf;
        private volatile int micLevel;
        private volatile bool earsStarting;
        private string earsUrl, earsKey, earsModel, earsLanguage;
        private MenuItem voiceChatItem;

        internal bool VoiceChatting { get { return chatting; } }
        internal bool MicMuted { get { return micMuted; } }
        internal int MicLevel { get { return micMuted ? 0 : micLevel; } }

        private string EarsLanguage { get { string code = Brain.LanguageCode(Saved.Language); return string.IsNullOrEmpty(code) ? "en" : code; } }

        // TALK on her pill and Voice chat in her menu.
        internal void ToggleVoice()
        {
            if (chatting || earsStarting) { StopEars(true); return; }
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { Talk.Answer("(voice)", "Voice chat here is for Mac and Linux."); return; }
            if (!Saved.EarsChosen) { ChooseEars(); return; }
            StartEars();
        }

        internal void ToggleMicMute()
        {
            if (!chatting) return;
            micMuted = !micMuted;
            endpointer.Reset();
        }

        // First time: her own offline ears if whisper.cpp is in, else she asks which to use.
        private void ChooseEars()
        {
            if (WhisperServer.Binary() != null)
            {
                Saved.EarsChosen = true;
                Saved.EarsUrl = null;
                Save();
                StartEars();
                return;
            }
            Talk.AskFor(EarsHeard, "To hear you offline I need whisper.cpp: run  sudo pacman -S whisper-cpp  then type local. Or paste a transcription address like https://api.groq.com/openai/v1/audio/transcriptions and press Enter.", SetEars);
        }

        internal void SetEars(string typed)
        {
            typed = (typed ?? "").Trim();
            if (typed.Length == 0) return;
            if (typed.Equals("local", StringComparison.OrdinalIgnoreCase) || typed.Equals("offline", StringComparison.OrdinalIgnoreCase))
            {
                Saved.EarsChosen = true;
                Saved.EarsUrl = null;
                Save();
                if (WhisperServer.Binary() == null) { Talk.Answer(EarsHeard, "I still can't find whisper-server. Run  sudo pacman -S whisper-cpp  and press TALK again."); return; }
                StartEars();
                return;
            }
            Uri at;
            if (!Uri.TryCreate(typed, UriKind.Absolute, out at) || (at.Scheme != "http" && at.Scheme != "https")) { Talk.Answer(EarsHeard, "That isn't an address I can use. Try https://... or type local."); return; }
            string url = typed;
            if (!at.AbsolutePath.Contains("transcriptions") && !at.AbsolutePath.Contains("inference")) url = typed.TrimEnd('/') + "/audio/transcriptions";
            Saved.EarsUrl = url;
            Saved.EarsModel = DefaultEarsModel(url);
            Saved.EarsChosen = true;
            Save();
            if (at.IsLoopback) { StartEars(); return; }
            Talk.AskKey(EarsHeard, "Paste the API key for " + at.Host + " and press Enter. It stays on this computer.", delegate(string key)
            {
                Saved.EarsKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
                Save();
                StartEars();
            });
        }

        internal static string DefaultEarsModel(string url)
        {
            string lower = (url ?? "").ToLowerInvariant();
            if (lower.Contains("groq")) return "whisper-large-v3-turbo";
            if (lower.Contains("openai.com")) return "gpt-4o-mini-transcribe";
            return "whisper-1";
        }

        // Forgets the ears she uses, so TALK asks again.
        internal void ResetEars()
        {
            StopEars(false);
            Saved.EarsChosen = false;
            Saved.EarsUrl = null;
            Saved.EarsKey = null;
            Saved.EarsModel = null;
            Save();
        }

        private void StartEars()
        {
            if (LinuxMic.Available() == null) { Talk.Answer(EarsHeard, "I need a recorder to hear you: pw-record (pipewire), parecord (libpulse) or arecord (alsa-utils)."); return; }
            earsStarting = true;
            earsUrl = Saved.EarsUrl;
            earsKey = Saved.EarsKey;
            earsModel = string.IsNullOrWhiteSpace(Saved.EarsModel) ? DefaultEarsModel(earsUrl) : Saved.EarsModel;
            earsLanguage = EarsLanguage;
            bool local = string.IsNullOrWhiteSpace(earsUrl);
            Talk.Thinking(EarsHeard);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string problem = null;
                try
                {
                    if (local)
                    {
                        if (whisper == null) whisper = new WhisperServer(folder);
                        if (!whisper.HasModel(earsLanguage))
                        {
                            Dispatcher.UIThread.Post(delegate { Talk.Answer(EarsHeard, "Downloading my ears, about 150 MB. Once, I promise."); });
                            whisper.Download(earsLanguage, delegate(long done, long size)
                            {
                                if (size <= 0) return;
                                int percent = (int)(done * 100 / size);
                                Dispatcher.UIThread.Post(delegate { if (earsStarting) Talk.Answer(EarsHeard, "Downloading my ears... " + percent + "%"); });
                            });
                        }
                        whisper.Start(earsLanguage);
                        earsUrl = whisper.Url;
                    }
                    if (!earsStarting) return;
                    endpointer.Reset();
                    endpointer.Sensitivity = Saved.MicSensitivity > 0 ? Saved.MicSensitivity : 1;
                    mic = new LinuxMic();
                    mic.Heard += Chunk;
                    mic.Failed += delegate(string why) { Dispatcher.UIThread.Post(delegate { if (chatting) { StopEars(false); Talk.Answer(EarsHeard, why); } }); };
                    mic.Start(Saved.MicDevice);
                    if (hearer == null || !hearer.IsAlive) { hearer = new Thread(Transcribe) { IsBackground = true, Name = "Capri ears" }; hearer.Start(); }
                }
                catch (Exception error) { problem = error.Message; }
                Dispatcher.UIThread.Post(delegate
                {
                    bool wanted = earsStarting;
                    earsStarting = false;
                    if (problem != null) { StopEars(false); Talk.Answer(EarsHeard, problem); return; }
                    if (!wanted) { StopEars(false); return; }
                    chatting = true;
                    micMuted = false;
                    SyncVoiceItem();
                    Talk.Answer(EarsHeard, "I'm listening, choom. Press TALK again to stop, or the mic on the right to mute me.");
                });
            });
        }

        internal void StopEars(bool say)
        {
            bool was = chatting || earsStarting;
            chatting = false;
            earsStarting = false;
            micMuted = false;
            micLevel = 0;
            if (mic != null) { mic.Dispose(); mic = null; }
            if (whisper != null) whisper.Stop();
            short[] dropped;
            while (phrases.TryTake(out dropped)) { }
            endpointer.Reset();
            SyncVoiceItem();
            if (say && was) Talk.Answer(EarsHeard, "Voice chat off. Type to me, or press TALK when you want me listening.");
        }

        // The mic's thread: 100 ms at a time.
        private void Chunk(short[] chunk)
        {
            if (!chatting) return;
            if (micMuted || deaf) { micLevel = deaf ? 0 : Endpointer.Meter(Endpointer.Loudness(chunk)); if (endpointer.Inside) endpointer.Reset(); return; }
            short[] phrase = endpointer.Feed(chunk);
            micLevel = endpointer.LastLevel;
            if (phrase != null) phrases.TryAdd(phrase);
        }

        private void Transcribe()
        {
            foreach (short[] phrase in phrases.GetConsumingEnumerable())
            {
                if (!chatting) continue;
                string text;
                try { text = Transcriber.Hear(earsUrl, earsKey, earsModel, earsLanguage, phrase); }
                catch (Exception error)
                {
                    string why = error.Message;
                    Dispatcher.UIThread.Post(delegate { if (chatting) Talk.Answer(EarsHeard, "I couldn't make that out: " + why); });
                    continue;
                }
                if (text.Length < 2 || !chatting) continue;
                Dispatcher.UIThread.Post(delegate { if (chatting && !deaf) Program.Guard(delegate { Hear(text); }); });
            }
        }

        // Every frame: deaf while she's thinking, talking or her line is still going up, and while someone's typing to her.
        internal void TickEars()
        {
            if (!chatting) return;
            deaf = !Talk.Finished || Talk.Typing || Away;
        }

        private void SyncVoiceItem()
        {
            if (voiceChatItem != null) voiceChatItem.IsChecked = chatting;
        }

        private MenuItem VoiceChatItem()
        {
            voiceChatItem = new MenuItem { Header = "Voice chat", ToggleType = MenuItemToggleType.CheckBox };
            voiceChatItem.Click += Guarded(delegate { ToggleVoice(); });
            MenuItem ears = new MenuItem { Header = "Voice chat ears" };
            MenuItem local = new MenuItem { Header = "Offline (whisper.cpp)" };
            local.Click += Guarded(delegate { StopEars(false); SetEars("local"); });
            MenuItem server = new MenuItem { Header = "Transcription server..." };
            server.Click += Guarded(delegate { StopEars(false); Talk.AskFor(EarsHeard, "Paste an OpenAI-style transcription address, like https://api.groq.com/openai/v1/audio/transcriptions, and press Enter.", SetEars); });
            MenuItem mics = new MenuItem { Header = "Microphone" };
            mics.SubmenuOpened += delegate { FillMics(mics); };
            mics.Items.Add(new MenuItem { Header = "Looking...", IsEnabled = false });
            ears.Items.Add(local);
            ears.Items.Add(server);
            ears.Items.Add(mics);
            ears.Items.Add(new Separator());
            string[] names = { "Hear quieter voices", "Normal", "Ignore more noise" };
            double[] levels = { 1.6, 1, 0.6 };
            for (int i = 0; i < names.Length; i++)
            {
                double level = levels[i];
                MenuItem pick = new MenuItem { Header = names[i], ToggleType = MenuItemToggleType.Radio, GroupName = "capri-mic-sensitivity" };
                pick.IsChecked = Math.Abs((Saved.MicSensitivity > 0 ? Saved.MicSensitivity : 1) - level) < 0.01;
                pick.Click += Guarded(delegate { Saved.MicSensitivity = level; endpointer.Sensitivity = level; Save(); });
                ears.Items.Add(pick);
            }
            MenuItem holder = new MenuItem { Header = "Voice" };
            holder.Items.Add(voiceChatItem);
            holder.Items.Add(ears);
            return holder;
        }

        private void FillMics(MenuItem mics)
        {
            mics.Items.Clear();
            MenuItem system = new MenuItem { Header = "System default", ToggleType = MenuItemToggleType.Radio, GroupName = "capri-mic", IsChecked = string.IsNullOrEmpty(Saved.MicDevice) };
            system.Click += Guarded(delegate { UseMic(null); });
            mics.Items.Add(system);
            foreach (var source in LinuxMic.List())
            {
                string name = source.Key;
                MenuItem pick = new MenuItem { Header = source.Value, ToggleType = MenuItemToggleType.Radio, GroupName = "capri-mic", IsChecked = name == Saved.MicDevice };
                pick.Click += Guarded(delegate { UseMic(name); });
                mics.Items.Add(pick);
            }
        }

        private void UseMic(string name)
        {
            Saved.MicDevice = name;
            Save();
            if (!chatting || mic == null) return;
            try { mic.Start(name); }
            catch (Exception error) { StopEars(false); Talk.Answer(EarsHeard, error.Message); }
        }
    }
}
