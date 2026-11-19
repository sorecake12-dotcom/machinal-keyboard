using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MechanicalKeyboardApp
{
    // ─── Native WaveOut Audio Engine (True Polyphony, Ultra-low Latency) ────────
    public static class WaveAudioEngine
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr hWaveOut, ref WAVEHDR lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WAVEHDR lpWaveHdr, int uSize);

        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        public static extern int waveOutSetVolume(IntPtr hWaveOut, uint dwVolume);

        public class SoundSample
        {
            public IntPtr Ptr;
            public int Length;
        }

        private const int VOICE_COUNT = 16;
        private static IntPtr[] _voices = new IntPtr[VOICE_COUNT];
        private static WAVEHDR[] _headers = new WAVEHDR[VOICE_COUNT];
        private static int _voiceIndex = 0;
        private static readonly object _voiceLock = new object();

        private static Dictionary<string, SoundSample> _samples = new Dictionary<string, SoundSample>();
        private static readonly string[] _randomKeys = { "a", "b", "c", "d", "e", "f" };
        private static Random _rng = new Random();
        private static bool _initialized = false;

        public static bool Initialize(string soundsDir)
        {
            try
            {
                WAVEFORMATEX fmt = new WAVEFORMATEX();
                fmt.wFormatTag = 1; // PCM
                fmt.nChannels = 2; // Stereo
                fmt.nSamplesPerSec = 44100;
                fmt.nAvgBytesPerSec = 176400;
                fmt.nBlockAlign = 4;
                fmt.wBitsPerSample = 16;
                fmt.cbSize = 0;

                for (int i = 0; i < VOICE_COUNT; i++)
                {
                    int res = waveOutOpen(out _voices[i], -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
                    if (res != 0)
                    {
                        Program.Log("Audio", string.Format("waveOutOpen failed for voice {0}, error={1}", i, res));
                    }
                }

                string[] soundNames = { "a", "b", "c", "d", "e", "f", "backspace", "enter", "shift", "space", "tab" };
                foreach (string name in soundNames)
                {
                    string filePath = Path.Combine(soundsDir, name + ".wav");
                    if (!File.Exists(filePath)) continue;

                    byte[] wav = File.ReadAllBytes(filePath);
                    int dataPos = -1;
                    int dataLen = 0;

                    for (int i = 12; i < wav.Length - 8; i++)
                    {
                        if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a')
                        {
                            dataPos = i + 8;
                            dataLen = BitConverter.ToInt32(wav, i + 4);
                            break;
                        }
                    }

                    if (dataPos == -1 || dataPos + dataLen > wav.Length)
                    {
                        dataPos = 44;
                        dataLen = Math.Max(0, wav.Length - 44);
                    }

                    IntPtr pData = Marshal.AllocHGlobal(dataLen);
                    Marshal.Copy(wav, dataPos, pData, dataLen);

                    SoundSample sample = new SoundSample();
                    sample.Ptr = pData;
                    sample.Length = dataLen;
                    _samples[name] = sample;
                }

                _initialized = true;
                UpdateVolume(Program.VolumeLevel);
                Program.Log("Audio", string.Format("Loaded {0} sound samples and initialized {1} hardware voices.", _samples.Count, VOICE_COUNT));
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("Audio", "Initialization exception", ex);
                return false;
            }
        }

        public static void UpdateVolume(float vol)
        {
            if (!_initialized) return;
            float clamped = Math.Max(0f, Math.Min(1.5f, vol));
            ushort level = (ushort)(Math.Min(1.0f, clamped) * 0xFFFF);
            uint dwVol = (uint)(level | (level << 16));

            for (int i = 0; i < VOICE_COUNT; i++)
            {
                if (_voices[i] != IntPtr.Zero)
                {
                    waveOutSetVolume(_voices[i], dwVol);
                }
            }
        }

        public static void PlaySound(string soundName)
        {
            if (!_initialized || !Program.IsEnabled || Program.VolumeLevel <= 0.01f) return;

            SoundSample sample;
            if (!_samples.TryGetValue(soundName, out sample))
            {
                if (!_samples.TryGetValue("a", out sample)) return;
            }

            lock (_voiceLock)
            {
                int v = _voiceIndex;
                _voiceIndex = (_voiceIndex + 1) % VOICE_COUNT;

                IntPtr hVoice = _voices[v];
                if (hVoice == IntPtr.Zero) return;

                waveOutReset(hVoice);

                WAVEHDR hdr = new WAVEHDR();
                hdr.lpData = sample.Ptr;
                hdr.dwBufferLength = (uint)sample.Length;

                waveOutPrepareHeader(hVoice, ref hdr, Marshal.SizeOf(hdr));
                waveOutWrite(hVoice, ref hdr, Marshal.SizeOf(hdr));
            }
        }

        public static void PlayKey(int vkCode)
        {
            string soundName;
            switch (vkCode)
            {
                case 0x08: // Backspace
                    soundName = "backspace";
                    break;
                case 0x09: // Tab
                    soundName = "tab";
                    break;
                case 0x0D: // Enter
                    soundName = "enter";
                    break;
                case 0x20: // Space
                    soundName = "space";
                    break;
                case 0x10:
                case 0xA0:
                case 0xA1: // Shift
                    soundName = "shift";
                    break;
                default:
                    soundName = _randomKeys[_rng.Next(_randomKeys.Length)];
                    break;
            }
            PlaySound(soundName);
        }

        public static void Shutdown()
        {
            _initialized = false;
            lock (_voiceLock)
            {
                for (int i = 0; i < VOICE_COUNT; i++)
                {
                    if (_voices[i] != IntPtr.Zero)
                    {
                        waveOutReset(_voices[i]);
                        waveOutClose(_voices[i]);
                        _voices[i] = IntPtr.Zero;
                    }
                }

                foreach (KeyValuePair<string, SoundSample> kvp in _samples)
                {
                    if (kvp.Value.Ptr != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(kvp.Value.Ptr);
                    }
                }
                _samples.Clear();
            }
        }
    }

    // ─── Main Program & Global Keyboard Hook ────────────────────────────────────
    static class Program
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        public static NotifyIcon TrayIcon;
        public static MainForm SettingsForm;
        public static volatile bool IsEnabled = true;
        public static float VolumeLevel = 0.85f;

        private static int _lastVk = 0;
        private static int _lastTick = 0;
        private static Mutex _appMutex;
        private static System.Windows.Forms.Timer _watchdogTimer;
        public static string BaseDir = "";

        [STAThread]
        static void Main()
        {
            bool createdNew;
            _appMutex = new Mutex(true, "MechanicalKeyboard_AULA_F75_Singleton", out createdNew);
            if (!createdNew)
            {
                bool owned = false;
                try
                {
                    owned = _appMutex.WaitOne(1000);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (!owned)
                {
                    MessageBox.Show("Mechanical Keyboard is already running in your system tray.", "Mechanical Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                BaseDir = AppDomain.CurrentDomain.BaseDirectory;
                string soundsDir = Path.Combine(BaseDir, "sounds");
                if (!Directory.Exists(soundsDir))
                {
                    soundsDir = Path.Combine(Directory.GetCurrentDirectory(), "sounds");
                }

                WaveAudioEngine.Initialize(soundsDir);
                StartHook();
                SetupTray();

                _watchdogTimer = new System.Windows.Forms.Timer();
                _watchdogTimer.Interval = 60000;
                _watchdogTimer.Tick += (s, e) =>
                {
                    if (_hookID == IntPtr.Zero)
                    {
                        StartHook();
                    }
                };
                _watchdogTimer.Start();

                SettingsForm = new MainForm();

                Application.Run();
            }
            catch (Exception ex)
            {
                Log("Fatal", "Crash in Application.Run", ex);
            }
            finally
            {
                Shutdown();
            }
        }

        public static void StartHook()
        {
            if (_hookID != IntPtr.Zero) return;
            _hookID = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hookID == IntPtr.Zero)
            {
                Log("Hook", "SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
            }
            else
            {
                Log("Hook", "Keyboard hook installed successfully.");
            }
        }

        public static void Unhook()
        {
            if (_hookID != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookID);
                _hookID = IntPtr.Zero;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) && IsEnabled)
            {
                int vk = Marshal.ReadInt32(lParam);
                int now = Environment.TickCount;

                // Only debounce the exact same key if pressed under 12ms (hardware key chatter)
                // Different keys chorded together are NEVER dropped!
                if (vk != _lastVk || (now - _lastTick) >= 12)
                {
                    _lastVk = vk;
                    _lastTick = now;
                    WaveAudioEngine.PlayKey(vk);
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static void SetupTray()
        {
            TrayIcon = new NotifyIcon();
            TrayIcon.Text = "AULA F75 Mechanical Keyboard";

            string icoPath = Path.Combine(BaseDir, "app_icon.ico");
            if (File.Exists(icoPath))
            {
                try { TrayIcon.Icon = new Icon(icoPath); }
                catch { TrayIcon.Icon = SystemIcons.Application; }
            }
            else
            {
                TrayIcon.Icon = SystemIcons.Application;
            }

            ContextMenu menu = new ContextMenu();
            MenuItem openItem = new MenuItem("Open Settings", (s, e) => ShowSettings());
            openItem.DefaultItem = true;

            MenuItem toggleItem = new MenuItem(IsEnabled ? "Mute Sounds" : "Unmute Sounds", (s, e) =>
            {
                IsEnabled = !IsEnabled;
                (s as MenuItem).Text = IsEnabled ? "Mute Sounds" : "Unmute Sounds";
                if (SettingsForm != null) SettingsForm.SyncState();
            });

            MenuItem exitItem = new MenuItem("Exit", (s, e) =>
            {
                Shutdown();
                Application.Exit();
            });

            menu.MenuItems.Add(openItem);
            menu.MenuItems.Add(toggleItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(exitItem);

            TrayIcon.ContextMenu = menu;
            TrayIcon.DoubleClick += (s, e) => ShowSettings();
            TrayIcon.Visible = true;
        }

        public static void ShowSettings()
        {
            if (SettingsForm == null || SettingsForm.IsDisposed)
            {
                SettingsForm = new MainForm();
            }
            SettingsForm.Show();
            SettingsForm.WindowState = FormWindowState.Normal;
            SettingsForm.BringToFront();
            SettingsForm.Activate();
        }

        public static void Shutdown()
        {
            Unhook();
            if (_watchdogTimer != null)
            {
                _watchdogTimer.Stop();
                _watchdogTimer.Dispose();
                _watchdogTimer = null;
            }
            WaveAudioEngine.Shutdown();
            if (TrayIcon != null)
            {
                TrayIcon.Visible = false;
                TrayIcon.Dispose();
                TrayIcon = null;
            }
        }

        public static void Log(string tag, string msg, Exception ex = null)
        {
            try
            {
                string path = Path.Combine(BaseDir, "mech_keyboard.log");
                using (StreamWriter sw = File.AppendText(path))
                {
                    sw.WriteLine(string.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2} {3}",
                        DateTime.Now, tag, msg, ex != null ? ex.Message : ""));
                }
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }

    // ─── Settings Window ────────────────────────────────────────────────────────
    public class MainForm : Form
    {
        private CheckBox _chkEnabled;
        private TrackBar _tbVolume;
        private Label _lblVolume;
        private CheckBox _chkStartup;
        private Button _btnTest;
        private Button _btnDesktopShortcut;
        private Button _btnHide;

        public MainForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "AULA F75 Mechanical Keyboard";
            this.Size = new Size(420, 360);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(24, 24, 32);
            this.ForeColor = Color.FromArgb(235, 235, 245);
            this.Font = new Font("Segoe UI", 9.5f);

            string icoPath = Path.Combine(Program.BaseDir, "app_icon.ico");
            if (File.Exists(icoPath))
            {
                try { this.Icon = new Icon(icoPath); } catch { }
            }

            Label lblTitle = new Label();
            lblTitle.Text = "AULA F75 Typing Sound";
            lblTitle.Font = new Font("Segoe UI", 13.5f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(168, 85, 247);
            lblTitle.Location = new Point(20, 16);
            lblTitle.AutoSize = true;
            this.Controls.Add(lblTitle);

            Label lblSub = new Label();
            lblSub.Text = "True polyphony mechanical switch audio for Windows";
            lblSub.Font = new Font("Segoe UI", 8.5f);
            lblSub.ForeColor = Color.FromArgb(150, 150, 170);
            lblSub.Location = new Point(22, 44);
            lblSub.AutoSize = true;
            this.Controls.Add(lblSub);

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "Enable Typing Sounds";
            _chkEnabled.Font = new Font("Segoe UI", 10.0f, FontStyle.Bold);
            _chkEnabled.Location = new Point(22, 75);
            _chkEnabled.Size = new Size(220, 26);
            _chkEnabled.Checked = Program.IsEnabled;
            _chkEnabled.CheckedChanged += (s, e) =>
            {
                Program.IsEnabled = _chkEnabled.Checked;
            };
            this.Controls.Add(_chkEnabled);

            GroupBox grpVol = new GroupBox();
            grpVol.Text = "Volume Control";
            grpVol.ForeColor = Color.FromArgb(180, 180, 205);
            grpVol.Location = new Point(20, 110);
            grpVol.Size = new Size(365, 85);

            _lblVolume = new Label();
            _lblVolume.Text = string.Format("Volume: {0}%", (int)(Program.VolumeLevel * 100));
            _lblVolume.ForeColor = Color.White;
            _lblVolume.Location = new Point(15, 22);
            _lblVolume.AutoSize = true;
            grpVol.Controls.Add(_lblVolume);

            _tbVolume = new TrackBar();
            _tbVolume.Location = new Point(10, 42);
            _tbVolume.Size = new Size(345, 35);
            _tbVolume.Minimum = 0;
            _tbVolume.Maximum = 100;
            _tbVolume.Value = (int)(Program.VolumeLevel * 100);
            _tbVolume.Scroll += (s, e) =>
            {
                float v = _tbVolume.Value / 100f;
                Program.VolumeLevel = v;
                _lblVolume.Text = string.Format("Volume: {0}%", _tbVolume.Value);
                WaveAudioEngine.UpdateVolume(v);
            };
            grpVol.Controls.Add(_tbVolume);
            this.Controls.Add(grpVol);

            _chkStartup = new CheckBox();
            _chkStartup.Text = "Start automatically with Windows";
            _chkStartup.Location = new Point(22, 205);
            _chkStartup.Size = new Size(260, 24);
            _chkStartup.Checked = IsStartupEnabled();
            _chkStartup.CheckedChanged += (s, e) => SetStartupEnabled(_chkStartup.Checked);
            this.Controls.Add(_chkStartup);

            _btnTest = new Button();
            _btnTest.Text = "Test Sound";
            _btnTest.Location = new Point(20, 245);
            _btnTest.Size = new Size(110, 32);
            _btnTest.FlatStyle = FlatStyle.Flat;
            _btnTest.BackColor = Color.FromArgb(147, 51, 234);
            _btnTest.ForeColor = Color.White;
            _btnTest.Font = new Font("Segoe UI", 9.0f, FontStyle.Bold);
            _btnTest.Click += (s, e) => WaveAudioEngine.PlaySound("space");
            this.Controls.Add(_btnTest);

            _btnDesktopShortcut = new Button();
            _btnDesktopShortcut.Text = "Desktop Shortcut";
            _btnDesktopShortcut.Location = new Point(140, 245);
            _btnDesktopShortcut.Size = new Size(130, 32);
            _btnDesktopShortcut.FlatStyle = FlatStyle.Flat;
            _btnDesktopShortcut.BackColor = Color.FromArgb(45, 45, 60);
            _btnDesktopShortcut.ForeColor = Color.White;
            _btnDesktopShortcut.Click += (s, e) => CreateDesktopShortcut();
            this.Controls.Add(_btnDesktopShortcut);

            _btnHide = new Button();
            _btnHide.Text = "Hide to Tray";
            _btnHide.Location = new Point(280, 245);
            _btnHide.Size = new Size(105, 32);
            _btnHide.FlatStyle = FlatStyle.Flat;
            _btnHide.BackColor = Color.FromArgb(45, 45, 60);
            _btnHide.ForeColor = Color.White;
            _btnHide.Click += (s, e) => this.Hide();
            this.Controls.Add(_btnHide);

            Label lblHint = new Label();
            lblHint.Text = "Tip: Runs silently in system tray. Double-click tray icon to reopen.";
            lblHint.Font = new Font("Segoe UI", 8.0f);
            lblHint.ForeColor = Color.FromArgb(130, 130, 150);
            lblHint.Location = new Point(22, 290);
            lblHint.AutoSize = true;
            this.Controls.Add(lblHint);
        }

        public void SyncState()
        {
            if (_chkEnabled != null)
            {
                _chkEnabled.Checked = Program.IsEnabled;
            }
        }

        private static string GetStartupShortcutPath()
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Path.Combine(folder, "AULA F75 Keyboard Sounds.lnk");
        }

        private static bool IsStartupEnabled()
        {
            return File.Exists(GetStartupShortcutPath());
        }

        private static void SetStartupEnabled(bool enable)
        {
            try
            {
                string lnk = GetStartupShortcutPath();
                if (enable)
                {
                    CreateShortcut(lnk, "AULA F75 Mechanical Keyboard Sounds - AutoStart");
                }
                else if (File.Exists(lnk))
                {
                    File.Delete(lnk);
                }
            }
            catch { }
        }

        private static void CreateDesktopShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string lnk = Path.Combine(desktop, "AULA F75 Mechanical Keyboard.lnk");
                CreateShortcut(lnk, "AULA F75 Mechanical Keyboard");
                MessageBox.Show("Desktop shortcut created successfully!", "Mechanical Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to create shortcut: " + ex.Message, "Mechanical Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void CreateShortcut(string shortcutPath, string description)
        {
            string exePath = Path.Combine(Program.BaseDir, "MechanicalKeyboard.exe");
            if (!File.Exists(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule.FileName;
            }

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Program.BaseDir;
                shortcut.Description = description;

                string ico = Path.Combine(Program.BaseDir, "app_icon.ico");
                if (File.Exists(ico))
                {
                    shortcut.IconLocation = ico + ",0";
                }
                shortcut.Save();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
            else
            {
                base.OnFormClosing(e);
            }
        }
    }
}
