using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MechanicalKeyboardApp
{
    // ─── CoreAudio COM Interfaces (Read-only System Volume) ─────────────────────
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    public static class CoreAudioHelper
    {
        public static float ReadSystemVolume()
        {
            try
            {
                IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom());
                IMMDevice dev;
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out dev);
                if (hr != 0 || dev == null) return -1f;

                Guid IID_IAudioEndpointVolume = typeof(IAudioEndpointVolume).GUID;
                object epvObj;
                hr = dev.Activate(ref IID_IAudioEndpointVolume, 23, IntPtr.Zero, out epvObj);
                if (hr != 0 || epvObj == null)
                {
                    Marshal.ReleaseComObject(dev);
                    Marshal.ReleaseComObject(enumerator);
                    return -1f;
                }

                IAudioEndpointVolume epv = (IAudioEndpointVolume)epvObj;
                float level = 0f;
                epv.GetMasterVolumeLevelScalar(out level);

                Marshal.ReleaseComObject(epv);
                Marshal.ReleaseComObject(dev);
                Marshal.ReleaseComObject(enumerator);
                return level;
            }
            catch
            {
                return -1f;
            }
        }
    }

    // ─── Main Program ───────────────────────────────────────────────────────────
    static class Program
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        public static NotifyIcon TrayIcon;
        public static MainForm SettingsForm;
        public static volatile bool IsEnabled = true;
        public static float AppVolume = 0.80f;

        // ─── Audio engine: hook thread only enqueues key codes; all playback ─────
        // ─── work (locks, allocations, SoundPlayer) happens on a dedicated thread ─
        private static readonly ConcurrentQueue<int> _pressQueue = new ConcurrentQueue<int>();
        private static readonly AutoResetEvent _pressSignal = new AutoResetEvent(false);
        private static Thread _playbackThread;
        private static volatile bool _playbackThreadRunning;
        private static int _pressCount;
        private static int _firstPressLogged;

        private static int _lastTick = Environment.TickCount - 10000; // 18 ms press-dedupe window
        private static readonly Random _rng = new Random(); // touched by playback thread only

        private static System.Windows.Forms.Timer _hookWatchdogTimer;
        private static MenuItem _trayToggleItem;
        private static int _shutdownStarted;
        private static readonly object _logLock = new object();

        public static string BaseDir = "";
        private static Dictionary<string, byte[]> _rawSoundData = new Dictionary<string, byte[]>();
        private static Dictionary<string, byte[]> _scaledSoundData = new Dictionary<string, byte[]>();
        private static readonly object _dataLock = new object();
        private static readonly string[] _randKeys = { "a", "b", "c", "d", "e", "f" };

        private const int POOL_SIZE = 8;
        private static SoundPlayer[] _players = new SoundPlayer[POOL_SIZE];
        private static MemoryStream[] _streams = new MemoryStream[POOL_SIZE];
        private static int _poolIdx = 0;
        private static readonly object _playLock = new object();
        private static Mutex _appMutex;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            _appMutex = new Mutex(true, "MechanicalKeyboard_AULA_F75_Singleton", out createdNew);
            if (!createdNew)
            {
                bool alreadyRunning;
                try
                {
                    // A previous run that crashed or was killed can leave an
                    // abandoned mutex behind; WaitOne lets this launch take it
                    // over instead of mistaking a dead instance for a live one.
                    alreadyRunning = !_appMutex.WaitOne(2000);
                }
                catch (AbandonedMutexException)
                {
                    alreadyRunning = false; // previous owner died; we own it now
                }
                if (alreadyRunning)
                {
                    MessageBox.Show("Mechanical Keyboard is already running. Check your system tray in the taskbar.",
                        "Mechanical Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                BaseDir = AppDomain.CurrentDomain.BaseDirectory;
                Log("Startup", "App starting. BaseDir=" + BaseDir);

                string soundsDir = Path.Combine(BaseDir, "sounds");
                if (!Directory.Exists(soundsDir))
                {
                    soundsDir = Path.Combine(Directory.GetCurrentDirectory(), "sounds");
                }

                // Load original WAVs into memory
                string[] names = { "a", "b", "c", "d", "e", "f", "backspace", "enter", "shift", "space", "tab" };
                foreach (string name in names)
                {
                    string p = Path.Combine(soundsDir, name + ".wav");
                    if (File.Exists(p))
                    {
                        try
                        {
                            _rawSoundData[name] = File.ReadAllBytes(p);
                        }
                        catch (Exception ex)
                        {
                            Log("Audio", "Failed to load " + name + ".wav", ex);
                        }
                    }
                }
                Log("Audio", "Loaded " + _rawSoundData.Count + " sound samples from " + soundsDir);

                // Initialize scaled buffers with default volume (80%)
                UpdateScaledBuffers(AppVolume);

                // Initialize player pool
                for (int i = 0; i < POOL_SIZE; i++)
                {
                    _players[i] = new SoundPlayer();
                }

                // Audio engine first: sounds keep working even if hook/UI has trouble
                StartPlaybackThread();

                // Tray is the app's visible status; bring it up headlessly
                SetupTray();

                // Install low-level keyboard hook (retried and kept alive by watchdog)
                StartHook();
                StartHookWatchdog();

                // UI window is created but NOT shown: launching the shortcut starts
                // the app and audio engine in the background. The tray restores it.
                SettingsForm = new MainForm();

                try
                {
                    Log("Startup", "Entering background message loop (UI hidden)");
                    Application.Run();
                }
                catch (Exception runEx)
                {
                    Log("Fatal", "Unhandled exception inside message loop", runEx);
                }
            }
            catch (Exception ex)
            {
                Log("Fatal", "Unhandled exception in main loop", ex);
                try
                {
                    MessageBox.Show("Mechanical Keyboard hit an error and will exit.\n\n" + ex.Message,
                        "Mechanical Keyboard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }
            finally
            {
                Shutdown();
            }
        }

        /// <summary>
        /// Single idempotent exit path: hook off → audio engine off → UI off →
        /// tray off. Called by the tray Exit item and again in Main's finally.
        /// </summary>
        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
            Log("Exit", "Shutting down");

            try { IsEnabled = false; } catch { }

            if (_hookWatchdogTimer != null)
            {
                try { _hookWatchdogTimer.Stop(); _hookWatchdogTimer.Dispose(); } catch { }
                _hookWatchdogTimer = null;
            }

            // 1. Unregister the keyboard hook first so no more events arrive
            UnhookKeyboard();

            // 2. Stop the audio engine thread
            StopPlaybackThread();

            // 3. Close the settings UI if it is visible
            if (SettingsForm != null)
            {
                try { if (!SettingsForm.IsDisposed) SettingsForm.Dispose(); } catch { }
                SettingsForm = null;
            }

            // 4. Remove the tray icon
            if (TrayIcon != null)
            {
                try { TrayIcon.Visible = false; TrayIcon.Dispose(); } catch { }
                TrayIcon = null;
            }
            _trayToggleItem = null;

            // 5. Dispose audio resources
            for (int i = 0; i < POOL_SIZE; i++)
            {
                if (_players[i] != null)
                {
                    try { _players[i].Dispose(); } catch { }
                    _players[i] = null;
                }
            }

            Log("Exit", "Shutdown complete");
        }

        public static void UpdateScaledBuffers(float volume)
        {
            lock (_dataLock)
            {
                AppVolume = Math.Max(0f, Math.Min(2f, volume));
                _scaledSoundData.Clear();
                foreach (KeyValuePair<string, byte[]> kv in _rawSoundData)
                {
                    _scaledSoundData[kv.Key] = ScaleWavAmplitude(kv.Value, AppVolume);
                }
            }
        }

        // In-memory 16-bit PCM amplitude scaler (Never touches Windows system volume API)
        private static byte[] ScaleWavAmplitude(byte[] raw, float vol)
        {
            bool boost = vol > 1f;

            byte[] copy = (byte[])raw.Clone();
            int dataOffset = -1;
            for (int i = 12; i < raw.Length - 8; i++)
            {
                if (raw[i] == 'd' && raw[i + 1] == 'a' && raw[i + 2] == 't' && raw[i + 3] == 'a')
                {
                    dataOffset = i + 8;
                    break;
                }
            }
            if (dataOffset == -1) dataOffset = 44;

            for (int i = dataOffset; i + 1 < copy.Length; i += 2)
            {
                short sample = (short)(copy[i] | (copy[i + 1] << 8));
                int val;
                if (!boost)
                {
                    val = (int)(sample * vol);
                    if (val > short.MaxValue) val = short.MaxValue;
                    else if (val < short.MinValue) val = short.MinValue;
                }
                else
                {
                    // Boost up to 200%: linear gain, then a soft limiter above
                    // the 0.95 knee so boosted peaks fade out instead of harshly clipping.
                    double a = sample * (double)vol / 32767.0;
                    double outA = a;
                    if (a > 0.95) outA = 0.95 + 0.05 * Math.Tanh((a - 0.95) / 0.05);
                    else if (a < -0.95) outA = -0.95 - 0.05 * Math.Tanh((-a - 0.95) / 0.05);
                    val = (int)Math.Round(outA * 32767.0);
                }
                short newSample = (short)val;
                copy[i] = (byte)(newSample & 0xFF);
                copy[i + 1] = (byte)((newSample >> 8) & 0xFF);
            }
            return copy;
        }

        private static void SetupTray()
        {
            try
            {
                TrayIcon = new NotifyIcon();
                TrayIcon.Text = "Mechanical Keyboard (AULA F75)";

                string icoPath = Path.Combine(BaseDir, "app_icon.ico");
                if (File.Exists(icoPath))
                {
                    try { TrayIcon.Icon = new Icon(icoPath, new Size(32, 32)); }
                    catch (Exception ex) { Log("Tray", "Could not load app_icon.ico, using system icon", ex); TrayIcon.Icon = SystemIcons.Application; }
                }
                else
                {
                    Log("Tray", "app_icon.ico not found at " + icoPath + ", using system icon");
                    TrayIcon.Icon = SystemIcons.Application;
                }

                ContextMenu menu = new ContextMenu();

                // 1. App name item (bold/default) — restores the existing UI
                MenuItem openItem = new MenuItem("AULA F75 Mechanical Keyboard", (s, e) =>
                {
                    ShowSettingsUI();
                });
                openItem.DefaultItem = true;

                // 2. Mute / Unmute
                _trayToggleItem = new MenuItem(IsEnabled ? "Mute Sounds" : "Unmute Sounds", (s, e) =>
                {
                    IsEnabled = !IsEnabled;
                    UpdateTrayMenuState();
                    if (SettingsForm != null) SettingsForm.SyncEnabledCheckbox();
                    try
                    {
                        TrayIcon.ShowBalloonTip(1500, "Mechanical Keyboard",
                            IsEnabled ? "Sounds ON" : "Sounds MUTED", ToolTipIcon.Info);
                    }
                    catch { }
                });

                // 3. Exit — unregisters hook, stops audio, removes tray, shuts down
                MenuItem exitItem = new MenuItem("Exit", (s, e) =>
                {
                    Shutdown();
                    Application.Exit();
                });

                menu.MenuItems.Add(openItem);
                menu.MenuItems.Add("-");
                menu.MenuItems.Add(_trayToggleItem);
                menu.MenuItems.Add("-");
                menu.MenuItems.Add(exitItem);

                TrayIcon.ContextMenu = menu;
                TrayIcon.DoubleClick += (s, e) =>
                {
                    ShowSettingsUI();
                };
                TrayIcon.Click += (s, e) =>
                {
                    MouseEventArgs me = e as MouseEventArgs;
                    if (me != null && me.Button == MouseButtons.Left)
                    {
                        ShowSettingsUI();
                    }
                };

                TrayIcon.Visible = true;
                Log("Tray", "Tray icon created and visible");

                try
                {
                    TrayIcon.ShowBalloonTip(2000, "Mechanical Keyboard Active",
                        "Authentic AULA F75 sounds active.\nClick tray icon anytime to open volume control.", ToolTipIcon.Info);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log("Tray", "Tray icon setup failed", ex);
            }
        }

        /// <summary>Restores the existing settings UI from the tray, reliably.</summary>
        public static void ShowSettingsUI()
        {
            try
            {
                if (SettingsForm == null || SettingsForm.IsDisposed)
                {
                    // Recreate the existing UI if it was ever disposed, so the
                    // tray item works for the app's whole lifetime.
                    SettingsForm = new MainForm();
                }
                SettingsForm.ShowAndFocus();
            }
            catch (Exception ex)
            {
                Log("Tray", "Failed to show settings UI", ex);
            }
        }

        /// <summary>Keeps the tray Mute/Unmute label in sync with the mute state.</summary>
        public static void UpdateTrayMenuState()
        {
            if (_trayToggleItem != null)
            {
                _trayToggleItem.Text = IsEnabled ? "Mute Sounds" : "Unmute Sounds";
            }
        }

        private static void StartHook()
        {
            if (_hookID != IntPtr.Zero) return; // never install duplicate hooks
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                // WH_KEYBOARD_LL is thread-based: GetModuleHandle(null) is the
                // safe module handle (Process.MainModule can throw or be null).
                IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (hook != IntPtr.Zero)
                {
                    _hookID = hook;
                    Log("Hook", "Global keyboard hook installed (attempt " + attempt + ")");
                    return;
                }
                Log("Hook", "SetWindowsHookEx failed, Win32 error " + Marshal.GetLastWin32Error() + " (attempt " + attempt + ")");
                if (attempt < 3) Thread.Sleep(500);
            }
            Log("Hook", "Global keyboard hook NOT installed yet; watchdog will keep retrying");
        }

        private static void UnhookKeyboard()
        {
            IntPtr hook = Interlocked.Exchange(ref _hookID, IntPtr.Zero);
            if (hook != IntPtr.Zero)
            {
                try
                {
                    UnhookWindowsHookEx(hook);
                    Log("Hook", "Keyboard hook unregistered");
                }
                catch (Exception ex)
                {
                    Log("Hook", "UnhookWindowsHookEx failed", ex);
                }
            }
        }

        /// <summary>
        /// Windows silently removes a low-level hook whose callback has timed out
        /// too often (<c>LowLevelHooksTimeout</c>) — that is why sounds used to
        /// keep working only in whatever was open right after launch. The timer
        /// reinstalls the hook periodically and logs one diagnostic stats line.
        /// </summary>
        private static void StartHookWatchdog()
        {
            _hookWatchdogTimer = new System.Windows.Forms.Timer();
            _hookWatchdogTimer.Interval = 5 * 60 * 1000; // every 5 minutes
            _hookWatchdogTimer.Tick += (s, e) =>
            {
                try
                {
                    UnhookKeyboard();
                    StartHook(); // no-op retry with logging if re-install fails
                    Log("Stats", "hook alive; presses=" + _pressCount);
                }
                catch (Exception ex)
                {
                    Log("Hook", "Watchdog tick failed", ex);
                }
            };
            _hookWatchdogTimer.Start();
        }

        private static void StartPlaybackThread()
        {
            if (_playbackThread != null) return;
            _playbackThread = new Thread(PlaybackLoop);
            _playbackThread.Name = "KeyboardAudioEngine";
            _playbackThread.IsBackground = true;
            _playbackThread.Priority = ThreadPriority.AboveNormal;
            _playbackThreadRunning = true;
            _playbackThread.Start();
            Log("Audio", "Playback engine thread started");
        }

        private static void StopPlaybackThread()
        {
            try
            {
                if (_pressSignal != null) _pressSignal.Set();
            }
            catch { }
            _playbackThreadRunning = false;
            if (_playbackThread != null)
            {
                try { _playbackThread.Join(1500); } catch { }
                _playbackThread = null;
            }
        }

        /// <summary>
        /// Owner of the key mapping and the SoundPlayer pool. The hook callback
        /// only enqueues virtual-key codes here, so it stays a few instructions
        /// long and can never block or trip LowLevelHooksTimeout.
        /// </summary>
        private static void PlaybackLoop()
        {
            int vk;
            while (_playbackThreadRunning)
            {
                if (_pressQueue.TryDequeue(out vk))
                {
                    try
                    {
                        PlayKey(vk);
                    }
                    catch (Exception ex)
                    {
                        Log("Audio", "Playback failed for vk=" + vk, ex);
                    }
                    continue;
                }
                _pressSignal.WaitOne(100, false); // wake on a new press or poll
            }
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // Ultra-light: no locks, no allocations, no audio calls on the
                // hook thread. Everything happens on the audio engine thread.
                if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) && IsEnabled)
                {
                    int vkCode = Marshal.ReadInt32(lParam);

                    // Dedupe auto-repeat and any fast double-delivery: one press
                    // per 18 ms window, atomically. Repeats beyond 18 ms still
                    // sound (real auto-repeat clacks), shortcuts are untouched —
                    // the key always passes through with CallNextHookEx.
                    int now = Environment.TickCount;
                    int prev = _lastTick;
                    if (now - prev >= 18 && Interlocked.CompareExchange(ref _lastTick, now, prev) == prev)
                    {
                        _pressQueue.Enqueue(vkCode);
                        Interlocked.Increment(ref _pressCount);
                        _pressSignal.Set();

                        // One-shot diagnostic: proves the global hook receives keys.
                        if (Interlocked.CompareExchange(ref _firstPressLogged, 1, 0) == 0)
                        {
                            Log("Hook", "First keypress received system-wide (vk=" + vkCode + ", foreground app does not matter for a global hook)");
                        }
                    }
                }
            }
            catch
            {
                // Never let an exception escape the hook chain.
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private static void PlayKey(int vkCode)
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
                    soundName = _randKeys[_rng.Next(_randKeys.Length)];
                    break;
            }

            PlaySoundFile(soundName);
        }

        public static void PlaySoundFile(string soundName)
        {
            byte[] bytes;
            lock (_dataLock)
            {
                if (!_scaledSoundData.TryGetValue(soundName, out bytes))
                {
                    if (!_scaledSoundData.TryGetValue("a", out bytes))
                        return;
                }
            }

            lock (_playLock)
            {
                try
                {
                    int idx = _poolIdx;
                    _poolIdx = (_poolIdx + 1) % POOL_SIZE;

                    if (_streams[idx] != null)
                    {
                        try { _streams[idx].Dispose(); } catch { }
                    }
                    _streams[idx] = new MemoryStream(bytes);
                    _players[idx].Stream = _streams[idx];
                    _players[idx].Play();
                }
                catch (Exception ex)
                {
                    Log("Audio", "SoundPlayer.Play failed for " + soundName, ex);
                }
            }
        }

        // ─── Minimal diagnostic log (few lines per session) ─────────────────────
        public static void Log(string tag, string message)
        {
            LogInner(tag, message, null);
        }

        public static void Log(string tag, string message, Exception ex)
        {
            LogInner(tag, message, ex);
        }

        private static void LogInner(string tag, string message, Exception ex)
        {
            try
            {
                lock (_logLock)
                {
                    string path = GetLogPath();
                    if (path == null) return;
                    using (StreamWriter w = File.AppendText(path))
                    {
                        w.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + tag + "] " + message
                            + (ex != null ? " :: " + ex.GetType().Name + ": " + ex.Message : ""));
                        if (ex != null && ex.StackTrace != null)
                        {
                            w.WriteLine("    at " + ex.StackTrace.Trim().Replace(Environment.NewLine, " | "));
                        }
                    }
                }
            }
            catch { }
        }

        private static string GetLogPath()
        {
            try
            {
                string dir = !string.IsNullOrEmpty(BaseDir) && Directory.Exists(BaseDir)
                    ? BaseDir : AppDomain.CurrentDomain.BaseDirectory;
                if (!Directory.Exists(dir)) dir = Path.GetTempPath();
                string path = Path.Combine(dir, "mech_keyboard.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                {
                    File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " log rotated\n");
                }
                return path;
            }
            catch { return null; }
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

    // ─── Settings & Volume Window ───────────────────────────────────────────────
    public class MainForm : Form
    {
        private Label _lblHeader;
        private Label _lblSub;
        private PictureBox _pbLogo;
        private CheckBox _chkEnabled;

        private GroupBox _grpAppVol;
        private Label _lblAppVol;
        private TrackBar _tbAppVol;
        private Label _lblVolDesc;

        private GroupBox _grpSysVol;
        private Label _lblSysVol;
        private CheckBox _chkSync;
        private System.Windows.Forms.Timer _sysVolTimer;

        private Button _btnPreview;
        private Button _btnHide;
        private Label _lblTip;

        public MainForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Text = "AULA F75 Mechanical Keyboard";
            this.Size = new Size(480, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(20, 20, 28);
            this.ForeColor = Color.FromArgb(240, 240, 250);
            this.Font = new Font("Segoe UI", 9.25f);

            string icoPath = Path.Combine(Program.BaseDir, "app_icon.ico");
            if (File.Exists(icoPath))
            {
                try { this.Icon = new Icon(icoPath); } catch { }
            }

            // Logo picture box
            _pbLogo = new PictureBox();
            _pbLogo.Location = new Point(20, 16);
            _pbLogo.Size = new Size(48, 48);
            _pbLogo.SizeMode = PictureBoxSizeMode.Zoom;
            string logoPath = Path.Combine(Program.BaseDir, "logo.jpg");
            if (File.Exists(logoPath))
            {
                try { _pbLogo.Image = Image.FromFile(logoPath); } catch { }
            }
            this.Controls.Add(_pbLogo);

            // Title banner
            _lblHeader = new Label();
            _lblHeader.Text = "AULA F75 Mechanical Sound";
            _lblHeader.Font = new Font("Segoe UI", 13.5f, FontStyle.Bold);
            _lblHeader.ForeColor = Color.FromArgb(192, 132, 252); // Neon purple
            _lblHeader.Location = new Point(78, 16);
            _lblHeader.AutoSize = true;
            this.Controls.Add(_lblHeader);

            _lblSub = new Label();
            _lblSub.Text = "Authentic typing audio across all apps on your laptop";
            _lblSub.Font = new Font("Segoe UI", 8.5f);
            _lblSub.ForeColor = Color.FromArgb(160, 160, 185);
            _lblSub.Location = new Point(80, 42);
            _lblSub.AutoSize = true;
            this.Controls.Add(_lblSub);

            // Enable Checkbox
            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "Enable Typing Sounds";
            _chkEnabled.Font = new Font("Segoe UI", 9.75f, FontStyle.Bold);
            _chkEnabled.ForeColor = Color.FromArgb(220, 220, 240);
            _chkEnabled.Location = new Point(22, 75);
            _chkEnabled.Size = new Size(250, 26);
            _chkEnabled.Checked = Program.IsEnabled;
            _chkEnabled.CheckedChanged += (s, e) => {
                Program.IsEnabled = _chkEnabled.Checked;
                Program.UpdateTrayMenuState();
            };
            this.Controls.Add(_chkEnabled);

            // ── App Volume Group (Independent) ─────────────────────────────
            _grpAppVol = new GroupBox();
            _grpAppVol.Text = "App Volume Control (Independent)";
            _grpAppVol.ForeColor = Color.FromArgb(192, 132, 252);
            _grpAppVol.Location = new Point(20, 110);
            _grpAppVol.Size = new Size(424, 118);

            _lblAppVol = new Label();
            _lblAppVol.Text = "Keyboard Sound Volume: 80%";
            _lblAppVol.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _lblAppVol.ForeColor = Color.White;
            _lblAppVol.Location = new Point(14, 24);
            _lblAppVol.AutoSize = true;
            _grpAppVol.Controls.Add(_lblAppVol);

            _tbAppVol = new TrackBar();
            _tbAppVol.Location = new Point(10, 48);
            _tbAppVol.Size = new Size(400, 45);
            _tbAppVol.Minimum = 0;
            _tbAppVol.Maximum = 200;
            _tbAppVol.Value = (int)(Program.AppVolume * 100);
            _tbAppVol.TickFrequency = 10;
            _tbAppVol.Scroll += (s, e) => {
                float vol = _tbAppVol.Value / 100f;
                _lblAppVol.Text = string.Format("Keyboard Sound Volume: {0}%", _tbAppVol.Value);
                // Scales WAV amplitude in memory only — NEVER calls system volume API
                Program.UpdateScaledBuffers(vol);
            };
            _grpAppVol.Controls.Add(_tbAppVol);

            _lblVolDesc = new Label();
            _lblVolDesc.Text = "Scales WAV amplitude in memory before playback. Zero effect on PC volume.";
            _lblVolDesc.Font = new Font("Segoe UI", 8.0f);
            _lblVolDesc.ForeColor = Color.FromArgb(150, 150, 175);
            _lblVolDesc.Location = new Point(14, 92);
            _lblVolDesc.AutoSize = true;
            _grpAppVol.Controls.Add(_lblVolDesc);

            this.Controls.Add(_grpAppVol);

            // ── System Volume Group (Read-only + Optional Sync) ────────────
            _grpSysVol = new GroupBox();
            _grpSysVol.Text = "System Master Volume (Info Only)";
            _grpSysVol.ForeColor = Color.FromArgb(160, 160, 185);
            _grpSysVol.Location = new Point(20, 240);
            _grpSysVol.Size = new Size(424, 98);

            _lblSysVol = new Label();
            _lblSysVol.Text = "System Master Volume: Checking...";
            _lblSysVol.Font = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            _lblSysVol.ForeColor = Color.FromArgb(140, 220, 255);
            _lblSysVol.Location = new Point(14, 26);
            _lblSysVol.AutoSize = true;
            _grpSysVol.Controls.Add(_lblSysVol);

            _chkSync = new CheckBox();
            _chkSync.Text = "Sync to system volume (OFF by default - completely independent)";
            _chkSync.Font = new Font("Segoe UI", 8.75f);
            _chkSync.ForeColor = Color.FromArgb(200, 200, 215);
            _chkSync.Location = new Point(16, 56);
            _chkSync.Size = new Size(396, 26);            _chkSync.Checked = false; // OFF by default
            _chkSync.CheckedChanged += (s, e) => {
                // Keyboard sound volume is always independent of the Windows
                // master volume, so this checkbox no longer links the two.
            };
            _grpSysVol.Controls.Add(_chkSync);

            this.Controls.Add(_grpSysVol);

            // ── Buttons & Tips ─────────────────────────────────────────────
            _btnPreview = new Button();
            _btnPreview.Text = "Preview Sound";
            _btnPreview.Location = new Point(20, 354);
            _btnPreview.Size = new Size(130, 34);
            _btnPreview.FlatStyle = FlatStyle.Flat;
            _btnPreview.BackColor = Color.FromArgb(147, 51, 234);
            _btnPreview.ForeColor = Color.White;
            _btnPreview.Font = new Font("Segoe UI", 9.0f, FontStyle.Bold);
            _btnPreview.Cursor = Cursors.Hand;
            _btnPreview.Click += (s, e) => {
                Program.PlaySoundFile("space");
            };
            this.Controls.Add(_btnPreview);

            _btnHide = new Button();
            _btnHide.Text = "Minimize to Tray";
            _btnHide.Location = new Point(314, 354);
            _btnHide.Size = new Size(130, 34);
            _btnHide.FlatStyle = FlatStyle.Flat;
            _btnHide.BackColor = Color.FromArgb(40, 40, 56);
            _btnHide.ForeColor = Color.White;
            _btnHide.Cursor = Cursors.Hand;
            _btnHide.Click += (s, e) => {
                this.Hide();
                if (Program.TrayIcon != null)
                {
                    Program.TrayIcon.ShowBalloonTip(1500, "Mechanical Keyboard",
                        "App is minimized and running in your system tray.", ToolTipIcon.Info);
                }
            };
            this.Controls.Add(_btnHide);

            _lblTip = new Label();
            _lblTip.Text = "Tip: Closing this window keeps the app running in your system tray.";
            _lblTip.Font = new Font("Segoe UI", 8.25f);
            _lblTip.ForeColor = Color.FromArgb(130, 130, 150);
            _lblTip.Location = new Point(20, 400);
            _lblTip.AutoSize = true;
            this.Controls.Add(_lblTip);

            // Timer to read system volume as info without binding to app slider
            _sysVolTimer = new System.Windows.Forms.Timer();
            _sysVolTimer.Interval = 500;
            _sysVolTimer.Tick += (s, e) => {
                float sysVol = CoreAudioHelper.ReadSystemVolume();
                if (sysVol >= 0f)
                {
                    int pct = (int)Math.Round(sysVol * 100f);
                    _lblSysVol.Text = string.Format("System Master Volume: {0}% (Read-only info)", pct);

                    // Read-only info only: the app's configured keyboard volume
                    // never follows the Windows master volume.
                }
                else
                {
                    _lblSysVol.Text = "System Master Volume: Available";
                }
            };
            _sysVolTimer.Start();
        }

        public void SyncEnabledCheckbox()
        {
            if (_chkEnabled != null)
            {
                _chkEnabled.Checked = Program.IsEnabled;
            }
        }

        public void ShowAndFocus()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
            this.Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                if (Program.TrayIcon != null)
                {
                    Program.TrayIcon.ShowBalloonTip(1500, "Mechanical Keyboard",
                        "App is minimized and running in your system tray.", ToolTipIcon.Info);
                }
            }
            else
            {
                base.OnFormClosing(e);
            }
        }
    }
}
