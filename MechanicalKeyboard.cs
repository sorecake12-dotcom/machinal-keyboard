using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        private static int _voiceIndex = 0;
        private static readonly object _voiceLock = new object();

        private static Dictionary<string, SoundSample> _samples = new Dictionary<string, SoundSample>();
        public static Dictionary<string, float> KeyVolume = new Dictionary<string, float>();
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
                    KeyVolume[name] = 1.0f;
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
                Program.Log("Audio", string.Format("Loaded {0} sound samples and initialized {1} hardware voices.", _samples.Count, VOICE_COUNT));
                return true;
            }
            catch (Exception ex)
            {
                Program.Log("Audio", "Initialization exception", ex);
                return false;
            }
        }

        public static void PlaySound(string soundName)
        {
            if (!_initialized || !Program.IsEnabled || Program.MasterVolume <= 0.001f) return;

            SoundSample sample;
            if (!_samples.TryGetValue(soundName, out sample))
            {
                if (!_samples.TryGetValue("a", out sample)) return;
            }

            float keyVol = 1.0f;
            if (KeyVolume.ContainsKey(soundName))
            {
                keyVol = KeyVolume[soundName];
            }

            float effectiveVol = Math.Max(0f, Math.Min(1.0f, Program.MasterVolume * keyVol));
            ushort level = (ushort)(effectiveVol * 0xFFFF);
            uint dwVol = (uint)(level | (level << 16));

            lock (_voiceLock)
            {
                int v = _voiceIndex;
                _voiceIndex = (_voiceIndex + 1) % VOICE_COUNT;

                IntPtr hVoice = _voices[v];
                if (hVoice == IntPtr.Zero) return;

                waveOutSetVolume(hVoice, dwVol);
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
        public static volatile bool PlayOnKeypress = true;
        public static float MasterVolume = 1.0f;
        public static int PitchShift = 0;
        public static int TotalPresses = 0;

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
                    if (_hookID == IntPtr.Zero && PlayOnKeypress)
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
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) && IsEnabled && PlayOnKeypress)
            {
                int vk = Marshal.ReadInt32(lParam);
                int now = Environment.TickCount;

                if (vk != _lastVk || (now - _lastTick) >= 12)
                {
                    _lastVk = vk;
                    _lastTick = now;
                    Interlocked.Increment(ref TotalPresses);
                    WaveAudioEngine.PlayKey(vk);
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static void SetupTray()
        {
            TrayIcon = new NotifyIcon();
            TrayIcon.Text = "AULA F75 Mechanical Keyboard Studio Pro";

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
            MenuItem openItem = new MenuItem("AULA F75 Studio Pro", (s, e) => ShowSettings());
            openItem.DefaultItem = true;

            MenuItem toggleItem = new MenuItem(IsEnabled ? "Mute All Audio" : "Unmute Audio", (s, e) =>
            {
                IsEnabled = !IsEnabled;
                (s as MenuItem).Text = IsEnabled ? "Mute All Audio" : "Unmute Audio";
                if (SettingsForm != null) SettingsForm.SyncState();
            });

            MenuItem exitItem = new MenuItem("Exit", (s, e) =>
            {
                Shutdown();
                Application.Exit();
            });

            menu.MenuItems.Add(openItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(toggleItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add(exitItem);

            TrayIcon.ContextMenu = menu;
            TrayIcon.DoubleClick += (s, e) => ShowSettings();
            TrayIcon.Click += (s, e) =>
            {
                MouseEventArgs me = e as MouseEventArgs;
                if (me != null && me.Button == MouseButtons.Left) ShowSettings();
            };
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

    // ─── Custom UI Controls (Exact AULA Studio Pro Aesthetic) ───────────────────
    public class StudioCard : Panel
    {
        public StudioCard()
        {
            this.DoubleBuffered = true;
            this.BackColor = Color.FromArgb(17, 14, 28);
            this.Padding = new Padding(14);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath path = GetRoundedPath(new Rectangle(0, 0, this.Width - 1, this.Height - 1), 10))
            {
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(19, 16, 31)))
                {
                    e.Graphics.FillPath(bgBrush, path);
                }
                using (Pen borderPen = new Pen(Color.FromArgb(45, 34, 68), 1.2f))
                {
                    e.Graphics.DrawPath(borderPen, path);
                }
            }
        }

        public static GraphicsPath GetRoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class StudioSlider : Control
    {
        private int _value = 100;
        private int _minimum = 0;
        private int _maximum = 100;
        private bool _isDragging = false;
        public event EventHandler ValueChanged;

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(_minimum, Math.Min(_maximum, value));
                if (_value != clamped)
                {
                    _value = clamped;
                    this.Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        public int Minimum { get { return _minimum; } set { _minimum = value; Invalidate(); } }
        public int Maximum { get { return _maximum; } set { _maximum = value; Invalidate(); } }

        public StudioSlider()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            this.Height = 22;
            this.Cursor = Cursors.Hand;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                UpdateValueFromMouse(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_isDragging)
            {
                UpdateValueFromMouse(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _isDragging = false;
        }

        private void UpdateValueFromMouse(int mouseX)
        {
            int pad = 8;
            int trackWidth = this.Width - (pad * 2);
            if (trackWidth <= 0) return;

            float ratio = (float)(mouseX - pad) / trackWidth;
            ratio = Math.Max(0f, Math.Min(1f, ratio));
            this.Value = _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = 8;
            int trackY = (this.Height / 2) - 3;
            int trackHeight = 6;
            int trackWidth = this.Width - (pad * 2);

            float ratio = (_maximum > _minimum) ? (float)(_value - _minimum) / (_maximum - _minimum) : 0f;
            int fillWidth = (int)(ratio * trackWidth);

            // Background track
            using (GraphicsPath bgPath = StudioCard.GetRoundedPath(new Rectangle(pad, trackY, trackWidth, trackHeight), 3))
            {
                using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(38, 28, 58)))
                {
                    e.Graphics.FillPath(trackBrush, bgPath);
                }
            }

            // Filled active track (Neon purple gradient)
            if (fillWidth > 4)
            {
                using (GraphicsPath fillPath = StudioCard.GetRoundedPath(new Rectangle(pad, trackY, fillWidth, trackHeight), 3))
                {
                    using (LinearGradientBrush fillBrush = new LinearGradientBrush(
                        new Rectangle(pad, trackY, Math.Max(10, fillWidth), trackHeight),
                        Color.FromArgb(139, 92, 246), Color.FromArgb(192, 132, 252), LinearGradientMode.Horizontal))
                    {
                        e.Graphics.FillPath(fillBrush, fillPath);
                    }
                }
            }

            // Circular thumb with neon glow
            int thumbX = pad + fillWidth;
            int thumbY = this.Height / 2;
            int thumbRadius = 7;

            using (SolidBrush glowBrush = new SolidBrush(Color.FromArgb(70, 168, 85, 247)))
            {
                e.Graphics.FillEllipse(glowBrush, thumbX - thumbRadius - 2, thumbY - thumbRadius - 2, (thumbRadius + 2) * 2, (thumbRadius + 2) * 2);
            }
            using (SolidBrush thumbBrush = new SolidBrush(Color.FromArgb(216, 180, 254)))
            {
                e.Graphics.FillEllipse(thumbBrush, thumbX - thumbRadius, thumbY - thumbRadius, thumbRadius * 2, thumbRadius * 2);
            }
            using (Pen thumbPen = new Pen(Color.White, 1.2f))
            {
                e.Graphics.DrawEllipse(thumbPen, thumbX - thumbRadius, thumbY - thumbRadius, thumbRadius * 2, thumbRadius * 2);
            }
        }
    }

    public class StudioPillButton : Button
    {
        public bool IsPrimary { get; set; }
        public bool IsActiveTab { get; set; }

        public StudioPillButton()
        {
            this.FlatStyle = FlatStyle.Flat;
            this.FlatAppearance.BorderSize = 0;
            this.Font = new Font("Segoe UI", 9.0f, FontStyle.Bold);
            this.Cursor = Cursors.Hand;
            this.DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);

            Color bgColor;
            if (IsActiveTab) bgColor = Color.FromArgb(109, 40, 217);
            else if (IsPrimary) bgColor = Color.FromArgb(124, 58, 237);
            else bgColor = Color.FromArgb(28, 22, 44);

            Color textColor = (IsActiveTab || IsPrimary) ? Color.White : Color.FromArgb(190, 190, 215);

            using (GraphicsPath path = StudioCard.GetRoundedPath(rect, 6))
            {
                using (SolidBrush brush = new SolidBrush(bgColor))
                {
                    pevent.Graphics.FillPath(brush, path);
                }

                if (!IsPrimary && !IsActiveTab)
                {
                    using (Pen pen = new Pen(Color.FromArgb(60, 46, 88), 1f))
                    {
                        pevent.Graphics.DrawPath(pen, path);
                    }
                }

                TextRenderer.DrawText(pevent.Graphics, this.Text, this.Font, rect, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // ─── Main Form (Matches Exact UI in Uploaded Image) ─────────────────────────
    public class MainForm : Form
    {
        private StudioSlider _slMasterVol;
        private Label _lblMasterVol;
        private CheckBox _chkMuteAll;
        private StudioSlider _slPitch;
        private Label _lblPitch;
        private CheckBox _chkSyncSys;
        private CheckBox _chkAutoLaunch;
        private CheckBox _chkPlayOnKey;
        private Label _lblBottomStatus;

        private Dictionary<string, StudioSlider> _keySliders = new Dictionary<string, StudioSlider>();
        private Dictionary<string, Label> _keyPctLabels = new Dictionary<string, Label>();

        // Custom Window Dragger Win32
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        public MainForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(1040, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(13, 11, 20);
            this.ForeColor = Color.FromArgb(235, 235, 245);
            this.Font = new Font("Segoe UI", 9.0f);
            this.DoubleBuffered = true;

            string icoPath = Path.Combine(Program.BaseDir, "app_icon.ico");
            if (File.Exists(icoPath))
            {
                try { this.Icon = new Icon(icoPath); } catch { }
            }

            // ── Top Titlebar ────────────────────────────────────────────────────────
            Panel titleBar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(15, 12, 24) };
            titleBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
                }
            };

            Label lblLogo = new Label
            {
                Text = "⌨",
                ForeColor = Color.FromArgb(168, 85, 247),
                Font = new Font("Segoe UI", 12.0f, FontStyle.Bold),
                Location = new Point(14, 6),
                AutoSize = true
            };
            titleBar.Controls.Add(lblLogo);

            Label lblAppTitle = new Label
            {
                Text = "AULA F75 Mechanical Keyboard Studio Pro",
                ForeColor = Color.FromArgb(220, 220, 240),
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                Location = new Point(42, 9),
                AutoSize = true
            };
            lblAppTitle.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0); }
            };
            titleBar.Controls.Add(lblAppTitle);

            // Window control buttons
            Button btnClose = CreateTitleButton("✕", 1000, 4);
            btnClose.Click += (s, e) => this.Hide();
            titleBar.Controls.Add(btnClose);

            Button btnMin = CreateTitleButton("—", 965, 4);
            btnMin.Click += (s, e) => this.Hide();
            titleBar.Controls.Add(btnMin);

            this.Controls.Add(titleBar);

            // ── Left Sidebar Navigation ─────────────────────────────────────────────
            Panel sideBar = new Panel { Dock = DockStyle.Left, Width = 74, BackColor = Color.FromArgb(15, 12, 24) };
            sideBar.Padding = new Padding(8, 20, 8, 0);

            sideBar.Controls.Add(CreateNavButton("ℹ\nAbout", 260, false));
            sideBar.Controls.Add(CreateNavButton("🎨\nTheme", 180, false));
            sideBar.Controls.Add(CreateNavButton("⚙\nSettings", 100, false));
            sideBar.Controls.Add(CreateNavButton("⌂\nMain", 20, true)); // Active

            this.Controls.Add(sideBar);

            // ── Bottom Status Bar ───────────────────────────────────────────────────
            Panel statusBar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Color.FromArgb(11, 9, 17) };
            _lblBottomStatus = new Label
            {
                Text = "🟣  Mechanical Keyboard Studio Pro - Ready",
                ForeColor = Color.FromArgb(160, 160, 185),
                Font = new Font("Segoe UI", 8.25f),
                Location = new Point(14, 6),
                AutoSize = true
            };
            statusBar.Controls.Add(_lblBottomStatus);

            Label lblTrayHint = new Label
            {
                Text = "Right-click tray icon to manage or exit",
                ForeColor = Color.FromArgb(110, 110, 135),
                Font = new Font("Segoe UI", 8.25f),
                Location = new Point(780, 6),
                AutoSize = true
            };
            statusBar.Controls.Add(lblTrayHint);

            this.Controls.Add(statusBar);

            // ── Central Workspace (Scrollable or 2-Column Grid) ─────────────────────
            Panel workPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(13, 11, 20),
                Padding = new Padding(20, 10, 20, 10),
                AutoScroll = true
            };

            // 1. Hero Header Banner
            Panel heroBanner = new Panel { Dock = DockStyle.Top, Height = 74 };
            Label heroIcon = new Label
            {
                Text = "⌨",
                Font = new Font("Segoe UI", 32.0f, FontStyle.Bold),
                ForeColor = Color.FromArgb(168, 85, 247),
                Location = new Point(6, 6),
                Size = new Size(54, 54)
            };
            heroBanner.Controls.Add(heroIcon);

            Label heroTitle = new Label
            {
                Text = "AULA F75",
                Font = new Font("Segoe UI", 18.0f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(64, 8),
                AutoSize = true
            };
            heroBanner.Controls.Add(heroTitle);

            Label heroSub = new Label
            {
                Text = "Mechanical Keyboard Studio Pro",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(180, 160, 220),
                Location = new Point(66, 38),
                AutoSize = true
            };
            heroBanner.Controls.Add(heroSub);

            Label heroSlogan = new Label
            {
                Text = "Type. Play. Feel the Sound.",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
                ForeColor = Color.FromArgb(192, 132, 252),
                Location = new Point(730, 24),
                AutoSize = true
            };
            heroBanner.Controls.Add(heroSlogan);

            workPanel.Controls.Add(heroBanner);

            // 2. Main Content Split Panels (Left & Right)
            Panel contentContainer = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

            int colWidth = 450;

            // ── LEFT COLUMN ─────────────────────────────────────────────────────────
            Panel leftCol = new Panel { Location = new Point(4, 80), Width = colWidth, Height = 510 };

            // Card A: Master Controls
            StudioCard cardMaster = new StudioCard { Location = new Point(0, 0), Width = colWidth, Height = 195 };

            Label lblH1 = CreateCardHeader("🔊  Master Controls", 14, 12, Color.FromArgb(230, 230, 245));
            cardMaster.Controls.Add(lblH1);

            Label lblMVolTitle = new Label { Text = "Master Volume", Location = new Point(16, 40), ForeColor = Color.White, AutoSize = true };
            cardMaster.Controls.Add(lblMVolTitle);

            _lblMasterVol = new Label { Text = "100%", Location = new Point(360, 40), ForeColor = Color.FromArgb(192, 132, 252), Font = new Font("Segoe UI", 9.0f, FontStyle.Bold), AutoSize = true };
            cardMaster.Controls.Add(_lblMasterVol);

            _slMasterVol = new StudioSlider { Location = new Point(14, 62), Width = 410, Value = (int)(Program.MasterVolume * 100) };
            _slMasterVol.ValueChanged += (s, e) =>
            {
                Program.MasterVolume = _slMasterVol.Value / 100f;
                _lblMasterVol.Text = string.Format("{0}%", _slMasterVol.Value);
            };
            cardMaster.Controls.Add(_slMasterVol);

            _chkMuteAll = new CheckBox { Text = "Mute All Audio", Location = new Point(16, 96), ForeColor = Color.FromArgb(210, 210, 230), AutoSize = true };
            _chkMuteAll.Checked = !Program.IsEnabled;
            _chkMuteAll.CheckedChanged += (s, e) =>
            {
                Program.IsEnabled = !_chkMuteAll.Checked;
                SyncState();
            };
            cardMaster.Controls.Add(_chkMuteAll);

            Label lblEffect = new Label { Text = "Effect:", Location = new Point(230, 97), ForeColor = Color.FromArgb(170, 170, 195), AutoSize = true };
            cardMaster.Controls.Add(lblEffect);

            ComboBox cbEffect = CreateDarkComboBox(new string[] { "None", "Echo", "Reverb", "Studio Room" }, "Echo", 276, 94, 130);
            cardMaster.Controls.Add(cbEffect);

            Label lblPitchTitle = new Label { Text = "Pitch Shift", Location = new Point(16, 128), ForeColor = Color.White, AutoSize = true };
            cardMaster.Controls.Add(lblPitchTitle);

            _lblPitch = new Label { Text = "+0 st", Location = new Point(370, 128), ForeColor = Color.FromArgb(192, 132, 252), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), AutoSize = true };
            cardMaster.Controls.Add(_lblPitch);

            _slPitch = new StudioSlider { Location = new Point(120, 126), Width = 240, Minimum = -12, Maximum = 12, Value = 0 };
            _slPitch.ValueChanged += (s, e) =>
            {
                Program.PitchShift = _slPitch.Value;
                _lblPitch.Text = (_slPitch.Value >= 0 ? "+" : "") + _slPitch.Value + " st";
            };
            cardMaster.Controls.Add(_slPitch);

            Label lblSysVol = new Label { Text = "System Volume: 86% (Read-only info)", Location = new Point(16, 162), ForeColor = Color.FromArgb(130, 130, 155), Font = new Font("Segoe UI", 7.75f), AutoSize = true };
            cardMaster.Controls.Add(lblSysVol);

            _chkSyncSys = new CheckBox { Text = "Sync to system (OFF by default)", Location = new Point(215, 160), ForeColor = Color.FromArgb(170, 170, 195), Font = new Font("Segoe UI", 7.75f), AutoSize = true, Checked = false };
            cardMaster.Controls.Add(_chkSyncSys);

            leftCol.Controls.Add(cardMaster);

            // Card B: Per-Key Volume Balancer
            StudioCard cardPerKey = new StudioCard { Location = new Point(0, 205), Width = colWidth, Height = 295 };

            Label lblH2 = CreateCardHeader("⌨  Per-Key Volume Balancer", 14, 12, Color.FromArgb(230, 230, 245));
            cardPerKey.Controls.Add(lblH2);

            Label lblSubH2 = new Label { Text = "WAV Amplitude Scaling", Location = new Point(285, 14), ForeColor = Color.FromArgb(130, 130, 155), Font = new Font("Segoe UI", 8.0f), AutoSize = true };
            cardPerKey.Controls.Add(lblSubH2);

            // Scrollable Panel for Keys
            Panel keyListPanel = new Panel { Location = new Point(10, 40), Width = 430, Height = 240, AutoScroll = true };

            string[] keyRows = { "A", "B", "C", "D", "E", "F", "Backspace", "Enter", "Space", "Tab", "Shift" };
            int rowY = 0;
            foreach (string kName in keyRows)
            {
                string soundKey = kName.ToLower();
                Panel row = new Panel { Location = new Point(4, rowY), Width = 400, Height = 28 };

                Label lblK = new Label { Text = kName, Location = new Point(4, 5), ForeColor = Color.FromArgb(230, 230, 245), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Width = 70 };
                row.Controls.Add(lblK);

                StudioSlider sl = new StudioSlider { Location = new Point(80, 4), Width = 175, Value = 100 };
                _keySliders[soundKey] = sl;

                Label lblVal = new Label { Text = "100%", Location = new Point(265, 5), ForeColor = Color.FromArgb(192, 132, 252), Font = new Font("Segoe UI", 8.0f), Width = 40 };
                _keyPctLabels[soundKey] = lblVal;

                sl.ValueChanged += (s, e) =>
                {
                    float factor = sl.Value / 100f;
                    WaveAudioEngine.KeyVolume[soundKey] = factor;
                    lblVal.Text = string.Format("{0}%", sl.Value);
                };
                row.Controls.Add(sl);
                row.Controls.Add(lblVal);

                StudioPillButton btnTest = new StudioPillButton { Text = "Test", Location = new Point(315, 2), Width = 55, Height = 22, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), IsPrimary = true };
                btnTest.Click += (s, e) => WaveAudioEngine.PlaySound(soundKey);
                row.Controls.Add(btnTest);

                keyListPanel.Controls.Add(row);
                rowY += 30;
            }

            cardPerKey.Controls.Add(keyListPanel);
            leftCol.Controls.Add(cardPerKey);

            contentContainer.Controls.Add(leftCol);

            // ── RIGHT COLUMN ────────────────────────────────────────────────────────
            Panel rightCol = new Panel { Location = new Point(470, 80), Width = 465, Height = 510 };

            // Card C: Customization & Settings
            StudioCard cardCustom = new StudioCard { Location = new Point(0, 0), Width = 465, Height = 220 };

            Label lblH3 = CreateCardHeader("⚙  Customization & Settings", 14, 12, Color.FromArgb(230, 230, 245));
            cardCustom.Controls.Add(lblH3);

            AddDropdownRow(cardCustom, "Sound Pack", new string[] { "sounds (Default AULA F75)", "Cream Yellow Switches", "Holy Panda Thock" }, 0, 38);
            AddDropdownRow(cardCustom, "Theme", new string[] { "Dark Neon (Obsidian & Violet)", "Cyberpunk Blue", "Deep Charcoal" }, 0, 82);
            AddDropdownRow(cardCustom, "Keycap Style", new string[] { "Neon Violet", "Retro Classic", "RGB Wave" }, 0, 126);
            AddDropdownRow(cardCustom, "Keyboard Layout", new string[] { "QWERTY (Standard)", "AZERTY", "QWERTZ" }, 0, 170);

            rightCol.Controls.Add(cardCustom);

            // Card D: System & Hook Options
            StudioCard cardSys = new StudioCard { Location = new Point(0, 230), Width = 465, Height = 145 };

            Label lblH4 = CreateCardHeader("System & Hook Options", 14, 12, Color.FromArgb(230, 230, 245));
            cardSys.Controls.Add(lblH4);

            _chkAutoLaunch = new CheckBox { Text = "Start with Windows (Auto-launch on boot)", Location = new Point(16, 36), ForeColor = Color.White, AutoSize = true };
            _chkAutoLaunch.Checked = IsStartupEnabled();
            _chkAutoLaunch.CheckedChanged += (s, e) => SetStartupEnabled(_chkAutoLaunch.Checked);
            cardSys.Controls.Add(_chkAutoLaunch);

            _chkPlayOnKey = new CheckBox { Text = "Play on Keypress (Global OS hook)", Location = new Point(16, 62), ForeColor = Color.White, AutoSize = true, Checked = true };
            _chkPlayOnKey.CheckedChanged += (s, e) =>
            {
                Program.PlayOnKeypress = _chkPlayOnKey.Checked;
                if (!Program.PlayOnKeypress) Program.Unhook();
                else Program.StartHook();
            };
            cardSys.Controls.Add(_chkPlayOnKey);

            StudioPillButton btnOpenFolder = new StudioPillButton { Text = "📁  Open Sound Folder", Location = new Point(16, 94), Width = 180, Height = 32 };
            btnOpenFolder.Click += (s, e) =>
            {
                string p = Path.Combine(Program.BaseDir, "sounds");
                if (Directory.Exists(p)) Process.Start("explorer.exe", p);
            };
            cardSys.Controls.Add(btnOpenFolder);

            StudioPillButton btnReset = new StudioPillButton { Text = "🔄  Reset Defaults", Location = new Point(206, 94), Width = 150, Height = 32 };
            btnReset.Click += (s, e) => ResetDefaults();
            cardSys.Controls.Add(btnReset);

            rightCol.Controls.Add(cardSys);

            // Card E: Engine Status
            StudioCard cardStatus = new StudioCard { Location = new Point(0, 385), Width = 465, Height = 115 };

            Label lblH5 = CreateCardHeader("Engine Status", 14, 10, Color.FromArgb(230, 230, 245));
            cardStatus.Controls.Add(lblH5);

            Label lblStat1 = CreateBulletLabel("● Low-Latency waveOut Pool (16 Ch Hardware Direct)", 16, 32, Color.FromArgb(16, 185, 129));
            Label lblStat2 = CreateBulletLabel("● Hook: LowLevelKeyboardProc (Active, <2ms delay)", 16, 54, Color.FromArgb(16, 185, 129));
            Label lblStat3 = CreateBulletLabel("● System Volume: Completely Independent", 16, 76, Color.FromArgb(16, 185, 129));

            cardStatus.Controls.Add(lblStat1);
            cardStatus.Controls.Add(lblStat2);
            cardStatus.Controls.Add(lblStat3);

            rightCol.Controls.Add(cardStatus);

            contentContainer.Controls.Add(rightCol);

            workPanel.Controls.Add(contentContainer);
            this.Controls.Add(workPanel);
        }

        private static Label CreateCardHeader(string text, int x, int y, Color c)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                ForeColor = c,
                Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
                AutoSize = true
            };
        }

        private static Label CreateBulletLabel(string text, int x, int y, Color c)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                ForeColor = c,
                Font = new Font("Segoe UI", 8.25f),
                AutoSize = true
            };
        }

        private static void AddDropdownRow(Panel p, string labelText, string[] items, int selectedIdx, int y)
        {
            Label lbl = new Label { Text = labelText, Location = new Point(16, y), ForeColor = Color.FromArgb(210, 210, 230), AutoSize = true, Font = new Font("Segoe UI", 8.5f) };
            p.Controls.Add(lbl);

            ComboBox cb = CreateDarkComboBox(items, items[selectedIdx], 14, y + 18, 435);
            p.Controls.Add(cb);
        }

        private static ComboBox CreateDarkComboBox(string[] items, string defaultItem, int x, int y, int w)
        {
            ComboBox cb = new ComboBox
            {
                Location = new Point(x, y),
                Width = w,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(24, 19, 38),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f)
            };
            cb.Items.AddRange(items);
            cb.SelectedItem = defaultItem;
            return cb;
        }

        private static Button CreateTitleButton(string text, int x, int y)
        {
            Button b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(28, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(170, 170, 195),
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.5f)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 30, 70);
            return b;
        }

        private static StudioPillButton CreateNavButton(string text, int y, bool active)
        {
            return new StudioPillButton
            {
                Text = text,
                Location = new Point(6, y),
                Size = new Size(62, 56),
                Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
                IsActiveTab = active
            };
        }

        public void SyncState()
        {
            if (_chkMuteAll != null) _chkMuteAll.Checked = !Program.IsEnabled;
            if (_lblBottomStatus != null)
            {
                _lblBottomStatus.Text = Program.IsEnabled ?
                    "🟣  Mechanical Keyboard Studio Pro - Ready" : "🟠  Mechanical Keyboard Studio Pro - MUTED";
            }
        }

        private void ResetDefaults()
        {
            _slMasterVol.Value = 100;
            _slPitch.Value = 0;
            foreach (KeyValuePair<string, StudioSlider> kv in _keySliders)
            {
                kv.Value.Value = 100;
            }
            MessageBox.Show("All audio controls reset to default (100%).", "Mechanical Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                if (enable) CreateShortcut(lnk, "AULA F75 Mechanical Keyboard Sounds - AutoStart");
                else if (File.Exists(lnk)) File.Delete(lnk);
            }
            catch { }
        }

        private static void CreateShortcut(string shortcutPath, string description)
        {
            string exePath = Path.Combine(Program.BaseDir, "MechanicalKeyboard.exe");
            if (!File.Exists(exePath)) exePath = Process.GetCurrentProcess().MainModule.FileName;

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Program.BaseDir;
                shortcut.Description = description;

                string ico = Path.Combine(Program.BaseDir, "app_icon.ico");
                if (File.Exists(ico)) shortcut.IconLocation = ico + ",0";
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
