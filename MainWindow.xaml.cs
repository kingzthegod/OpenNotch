
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OpenNotch
{
    internal record Settings(double Left, double Top, string? ThemeColor = null);

    public partial class MainWindow : Window
    {
        // ---------- dimensions ----------

        const double CollapsedW = 340;
        const double ExpandedW = 400;
        const double CollapsedH = 56;
        const double ExpandedH = 340;

        const bool HaloEnabled = true;

        static readonly Color DefaultThemeColor =
            Color.FromRgb(0x35, 0xD0, 0x7F);

        Color _themeColor = DefaultThemeColor;
        Color? _albumArtColor;

        // ---------- state ----------

        bool _expanded;
        bool _pinned;
        bool _wantExpanded;
        bool _closing;

        readonly DispatcherTimer _hover = new();
        readonly DispatcherTimer _stats =
            new() { Interval = TimeSpan.FromSeconds(1) };

        readonly DispatcherTimer _timerTick =
            new() { Interval = TimeSpan.FromMilliseconds(500) };

        readonly DispatcherTimer _transientTimer = new();

        System.Windows.Forms.NotifyIcon? _tray;

        GlobalSystemMediaTransportControlsSessionManager? _mgr;
        GlobalSystemMediaTransportControlsSession? _session;

        MMDeviceEnumerator? _enum;
        MMDevice? _dev;

        HwndSource? _src;
        IntPtr _hwnd;

        bool _clipboardListenerAdded;

        readonly SolidColorBrush EdgeBrush =
            new(DefaultThemeColor);

        UIElement _glowHost => Island;

        // The cat's head needs a TranslateTransform.
        TranslateTransform _headBobTransform = null!;

        public MainWindow()
        {
            InitializeComponent();

            Island.BorderBrush = EdgeBrush;

            // Apply the default theme before the window is shown.
            ApplyTheme();

            // Make HeadBob compatible with the animation code.
            _headBobTransform =
                HeadBob.RenderTransform as TranslateTransform
                ?? new TranslateTransform();

            HeadBob.RenderTransform = _headBobTransform;

            // Hover expansion.
            _hover.Tick += (_, _) =>
            {
                _hover.Stop();
                SetExpanded(_wantExpanded || _pinned);
            };

            Island.MouseEnter += (_, _) =>
                Schedule(true, 150);

            Island.MouseLeave += (_, _) =>
                Schedule(false, 400);

            LocationChanged += (_, _) =>
                UpdateCorners();

            // CPU and RAM.
            _stats.Tick += (_, _) =>
                UpdateStats();

            // Focus timer.
            _timerTick.Tick += (_, _) =>
                OnTimerTick();

            // Temporary popup expiration.
            _transientTimer.Tick += (_, _) =>
            {
                _transientTimer.Stop();

                _transientText = null;
                _transientKind = null;
                _transientBar = null;

                RefreshLeft();
            };

            Loaded += OnLoaded;
        }

        // =====================================================
        // STARTUP
        // =====================================================

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadPosition();
            UpdateCorners();

            SetupTray();
            SetupVolume();

            UpdateStats();
            _stats.Start();

            try
            {
                _mgr =
                    await GlobalSystemMediaTransportControlsSessionManager
                        .RequestAsync();

                if (_closing)
                    return;

                _mgr.CurrentSessionChanged +=
                    OnCurrentSessionChanged;

                Attach();
            }
            catch (Exception)
            {
                _mgr = null;
                _session = null;

                PlaybackTitle.Text = "Nothing playing";
                PlaybackArtist.Text = "";
                PlayButton.Content = "▶";

                RefreshLeft();
            }
        }

        // =====================================================
        // EXPAND / COLLAPSE
        // =====================================================

        void Schedule(bool expand, int ms)
        {
            _wantExpanded = expand;

            _hover.Stop();
            _hover.Interval = TimeSpan.FromMilliseconds(ms);
            _hover.Start();
        }

        void ExpandButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            _pinned = !_pinned;

            ExpandButton.Content = _pinned ? "–" : "+";

            SetExpanded(_pinned || _wantExpanded);
        }

        void SetExpanded(bool expanded)
        {
            if (expanded == _expanded)
                return;

            _expanded = expanded;

            double targetWidth =
                expanded ? ExpandedW : CollapsedW;

            double targetHeight =
                expanded ? ExpandedH : CollapsedH;

            // Keep the window centered while its width changes.
            double currentWidth = Width;

            if (double.IsNaN(currentWidth) ||
                double.IsInfinity(currentWidth))
            {
                currentWidth = CollapsedW;
            }

            Left -= (targetWidth - currentWidth) / 2;

            var ease = new CubicEase
            {
                EasingMode = expanded
                    ? EasingMode.EaseOut
                    : EasingMode.EaseInOut
            };

            var duration = TimeSpan.FromMilliseconds(
                expanded ? 320 : 260);

            var widthAnimation =
                new DoubleAnimation(targetWidth, duration)
                {
                    EasingFunction = ease
                };

            var heightAnimation =
                new DoubleAnimation(targetHeight, duration)
                {
                    EasingFunction = ease
                };

            // Animate the actual WPF window.
            BeginAnimation(
                FrameworkElement.WidthProperty,
                widthAnimation);

            BeginAnimation(
                FrameworkElement.HeightProperty,
                heightAnimation);

            // Animate the visible notch.
            Island.BeginAnimation(
                FrameworkElement.WidthProperty,
                new DoubleAnimation(targetWidth, duration)
                {
                    EasingFunction = ease
                });

            Island.BeginAnimation(
                FrameworkElement.HeightProperty,
                new DoubleAnimation(targetHeight, duration)
                {
                    EasingFunction = ease
                });

            if (expanded)
            {
                ExpandedPanel.Visibility =
                    Visibility.Visible;
            }

            ExpandedPanel.IsHitTestVisible = expanded;

            var opacityAnimation =
                new DoubleAnimation(
                    expanded ? 1 : 0,
                    TimeSpan.FromMilliseconds(
                        expanded ? 200 : 120))
                {
                    BeginTime = TimeSpan.FromMilliseconds(
                        expanded ? 100 : 0)
                };

            if (!expanded)
            {
                opacityAnimation.Completed += (_, _) =>
                {
                    if (!_expanded)
                    {
                        ExpandedPanel.Visibility =
                            Visibility.Collapsed;
                    }
                };
            }

            ExpandedPanel.BeginAnimation(
                UIElement.OpacityProperty,
                opacityAnimation);

            ApplyCat();
        }

        // =====================================================
        // DRAGGING + POSITION
        // =====================================================

        void Island_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ResetPosition();
                SavePosition();
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Ignore invalid drag operations.
            }

            SavePosition();
        }

        void ResetPosition()
        {
            Left =
                (SystemParameters.PrimaryScreenWidth - Width) / 2;

            Top = 0;
        }

        void UpdateCorners()
        {
            bool docked = Top < 2;

            Island.CornerRadius = docked
                ? new CornerRadius(0, 0, 28, 28)
                : new CornerRadius(28);

            Island.BorderThickness = docked
                ? new Thickness(1.5, 0, 1.5, 1.5)
                : new Thickness(1.5);
        }

        static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData),
                "OpenNotch",
                "settings.json");

        static Color BlendColor(Color baseColor, Color tint, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);

            byte BlendChannel(byte from, byte to) =>
                (byte)Math.Clamp(
                    (int)Math.Round(from + (to - from) * amount),
                    0,
                    255);

            return Color.FromRgb(
                BlendChannel(baseColor.R, tint.R),
                BlendChannel(baseColor.G, tint.G),
                BlendChannel(baseColor.B, tint.B));
        }

        static bool IsLightColor(Color color)
        {
            double luminance =
                (0.2126 * color.R +
                 0.7152 * color.G +
                 0.0722 * color.B) / 255.0;

            return luminance >= 0.58;
        }

        void ApplyTheme()
        {
            Color panelColor = BlendColor(
                Color.FromRgb(8, 9, 13),
                _themeColor,
                0.24);

            Color cardColor = BlendColor(
                Color.FromRgb(23, 24, 33),
                _themeColor,
                0.22);

            Color buttonColor = BlendColor(
                Color.FromRgb(37, 38, 48),
                _themeColor,
                0.42);

            Resources["PanelBg"] =
                new SolidColorBrush(panelColor);

            Resources["CardBg"] =
                new SolidColorBrush(cardColor);

            Resources["AccentBrush"] =
                new SolidColorBrush(_themeColor);

            Resources["ButtonBrush"] =
                new SolidColorBrush(buttonColor);

            Resources["ButtonForegroundBrush"] =
                new SolidColorBrush(
                    IsLightColor(buttonColor)
                        ? Colors.Black
                        : Colors.White);

            // Keep the selected colour as the border colour unless
            // album art is currently providing a temporary glow.
            SetGlow(_albumArtColor);
        }

        void ChooseTheme_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                using var dialog = new System.Windows.Forms.ColorDialog
                {
                    AllowFullOpen = true,
                    AnyColor = true,
                    FullOpen = true,
                    Color = System.Drawing.Color.FromArgb(
                        _themeColor.R,
                        _themeColor.G,
                        _themeColor.B)
                };

                if (dialog.ShowDialog() !=
                    System.Windows.Forms.DialogResult.OK)
                {
                    return;
                }

                _themeColor = Color.FromRgb(
                    dialog.Color.R,
                    dialog.Color.G,
                    dialog.Color.B);

                ApplyTheme();
                SavePosition();

                ShowTransient(
                    "theme",
                    "✓ Colour updated",
                    null,
                    1400);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"The colour picker could not be opened.\n\n{ex.Message}",
                    "OpenNotch",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        void LoadPosition()
        {
            bool positionLoaded = false;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    var settings =
                        JsonSerializer.Deserialize<Settings>(
                            File.ReadAllText(SettingsPath));

                    if (settings != null)
                    {
                        Left = Math.Clamp(
                            settings.Left,
                            SystemParameters.VirtualScreenLeft - 40,
                            SystemParameters.VirtualScreenLeft +
                            SystemParameters.VirtualScreenWidth -
                            Width + 40);

                        Top = Math.Clamp(
                            settings.Top,
                            SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenTop +
                            SystemParameters.VirtualScreenHeight - 60);

                        if (!string.IsNullOrWhiteSpace(settings.ThemeColor))
                        {
                            try
                            {
                                object? parsed =
                                    ColorConverter.ConvertFromString(
                                        settings.ThemeColor);

                                if (parsed is Color savedColor)
                                    _themeColor = savedColor;
                            }
                            catch
                            {
                                // Ignore an invalid saved colour.
                            }
                        }

                        positionLoaded = true;
                    }
                }
            }
            catch
            {
                // Use default position and theme.
            }

            ApplyTheme();

            if (!positionLoaded)
                ResetPosition();
        }

        void SavePosition()
        {
            try
            {
                string? directory =
                    Path.GetDirectoryName(SettingsPath);

                if (directory != null)
                    Directory.CreateDirectory(directory);

                File.WriteAllText(
                    SettingsPath,
                    JsonSerializer.Serialize(
                        new Settings(Left, Top, _themeColor.ToString())));
            }
            catch
            {
                // Position saving is optional.
            }
        }

        // =====================================================
        // LEFT SLOT + POPUPS
        // =====================================================

        string? _transientText;
        string? _transientKind;

        double? _transientBar;

        string _leftKind = "";

        bool _hasTrack;
        string _title = "";

        void ShowTransient(
            string kind,
            string text,
            double? bar,
            int ms)
        {
            if (_closing)
                return;

            _transientKind = kind;
            _transientText = text;
            _transientBar = bar;

            _transientTimer.Stop();

            _transientTimer.Interval =
                TimeSpan.FromMilliseconds(ms);

            _transientTimer.Start();

            RefreshLeft();
        }

        // Priority:
        // Popup > Focus timer > Song title > OpenNotch

        void RefreshLeft()
        {
            if (_transientText != null)
            {
                SetLeft(
                    _transientKind ?? "popup",
                    _transientText,
                    _transientBar);

                return;
            }

            if (_timerOn)
            {
                double remaining = Math.Max(
                    0,
                    (_timerEnd - DateTime.UtcNow).TotalSeconds);

                int seconds =
                    (int)Math.Ceiling(remaining);

                double progress =
                    _timerTotal.TotalSeconds > 0
                    ? remaining / _timerTotal.TotalSeconds
                    : 0;

                SetLeft(
                    "timer",
                    $"Focus {seconds / 60:00}:{seconds % 60:00}",
                    progress);

                return;
            }

            if (_hasTrack)
            {
                SetLeft("track", _title, null);
                return;
            }

            SetLeft("idle", "OPENNOTCH", null);
        }

        void SetLeft(
            string kind,
            string text,
            double? bar)
        {
            if (kind != _leftKind)
            {
                _leftKind = kind;

                LeftText.FontWeight =
                    kind == "idle"
                    ? FontWeights.Bold
                    : FontWeights.SemiBold;

                LeftText.BeginAnimation(
                    UIElement.OpacityProperty,
                    new DoubleAnimation(
                        0.2,
                        1,
                        TimeSpan.FromMilliseconds(180)));
            }

            LeftText.Text = text;

            if (bar is double value)
            {
                LiveBarTrack.Visibility =
                    Visibility.Visible;

                LiveScale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(
                        Math.Clamp(value, 0, 1),
                        TimeSpan.FromMilliseconds(200)));
            }
            else
            {
                LiveBarTrack.Visibility =
                    Visibility.Collapsed;
            }
        }

        // =====================================================
        // FOCUS TIMER
        // =====================================================

        bool _timerOn;

        DateTime _timerEnd;

        TimeSpan _timerTotal =
            TimeSpan.FromMinutes(25);

        void StartTimer(int minutes)
        {
            if (minutes <= 0 || _closing)
                return;

            _timerTotal =
                TimeSpan.FromMinutes(minutes);

            _timerEnd =
                DateTime.UtcNow + _timerTotal;

            _timerOn = true;

            _timerTick.Start();

            RefreshLeft();
        }

        void StopTimer()
        {
            _timerOn = false;

            _timerTick.Stop();

            RefreshLeft();
        }

        void OnTimerTick()
        {
            if (!_timerOn)
                return;

            if (_timerEnd <= DateTime.UtcNow)
            {
                _timerOn = false;

                _timerTick.Stop();

                System.Media.SystemSounds.Asterisk.Play();

                ShowTransient(
                    "done",
                    "✓ Focus done",
                    null,
                    5000);

                return;
            }

            RefreshLeft();
        }

        void StartTimer_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button &&
                int.TryParse(button.Tag?.ToString(), out int minutes))
            {
                StartTimer(minutes);
            }
        }

        void StopTimer_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopTimer();
        }

        // =====================================================
        // VOLUME POPUP
        // =====================================================

        void SetupVolume()
        {
            try
            {
                _enum = new MMDeviceEnumerator();

                _dev = _enum.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

                _dev.AudioEndpointVolume.OnVolumeNotification +=
                    OnVolume;
            }
            catch
            {
                // No available audio device.
            }
        }

        void OnVolume(AudioVolumeNotificationData data)
        {
            if (_closing)
                return;

            float volume = data.MasterVolume;
            bool muted = data.Muted;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing)
                    return;

                int percentage =
                    (int)Math.Round(volume * 100);

                ShowTransient(
                    "vol",
                    muted ? "Muted" : $"Volume {percentage}%",
                    muted ? 0 : volume,
                    1600);
            }));
        }

        // =====================================================
        // CLIPBOARD POPUP
        // =====================================================

        bool _showCopied = true;

        DateTime _lastCopy = DateTime.MinValue;

        IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            const int WM_CLIPBOARDUPDATE = 0x031D;

            if (msg == WM_CLIPBOARDUPDATE)
            {
                var now = DateTime.UtcNow;

                if (_showCopied &&
                    (now - _lastCopy).TotalMilliseconds > 250)
                {
                    _lastCopy = now;

                    ShowTransient(
                        "copied",
                        "✓ Copied",
                        null,
                        1400);
                }
            }

            return IntPtr.Zero;
        }

        // =====================================================
        // CPU + RAM MONITOR
        // =====================================================

        ulong _prevIdle;
        ulong _prevKernel;
        ulong _prevUser;

        void UpdateStats()
        {
            if (_closing)
                return;

            double cpu = 0;

            if (GetSystemTimes(
                out var idle,
                out var kernel,
                out var user))
            {
                ulong i = idle.ToUInt64();
                ulong k = kernel.ToUInt64();
                ulong u = user.ToUInt64();

                if (_prevKernel != 0 || _prevUser != 0)
                {
                    ulong idleDelta = i - _prevIdle;

                    ulong totalDelta =
                        (k - _prevKernel) +
                        (u - _prevUser);

                    if (totalDelta > 0)
                    {
                        cpu =
                            (1.0 -
                            (double)idleDelta / totalDelta) *
                            100.0;
                    }
                }

                _prevIdle = i;
                _prevKernel = k;
                _prevUser = u;
            }

            cpu = Math.Clamp(cpu, 0, 100);

            var memory = new MemoryStatus
            {
                Length = (uint)Marshal.SizeOf<MemoryStatus>()
            };

            double ramPercentage = 0;
            double usedGb = 0;
            double totalGb = 0;

            if (GlobalMemoryStatusEx(ref memory))
            {
                ramPercentage = memory.MemoryLoad;

                totalGb =
                    memory.TotalPhys / 1073741824.0;

                usedGb =
                    (memory.TotalPhys - memory.AvailPhys) /
                    1073741824.0;
            }

            StatsText.Text =
                $"CPU {cpu:0}%  RAM {ramPercentage:0}%";

            CpuText.Text = $"{cpu:0}%";

            RamText.Text =
                $"{usedGb:0.0} / {totalGb:0.0} GB";

            AnimateBar(CpuScale, cpu / 100.0);

            AnimateBar(
                RamScale,
                ramPercentage / 100.0);
        }

        static void AnimateBar(
            ScaleTransform bar,
            double value)
        {
            bar.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(
                    Math.Clamp(value, 0, 1),
                    TimeSpan.FromMilliseconds(600))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseOut
                    }
                });
        }

        struct FileTime
        {
            public uint Low;
            public uint High;

            public ulong ToUInt64()
            {
                return ((ulong)High << 32) | Low;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;

            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(
            out FileTime idle,
            out FileTime kernel,
            out FileTime user);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(
            ref MemoryStatus status);

        // =====================================================
        // WINDOWS MEDIA CONTROLS
        // =====================================================

        int _refreshId;

        bool _playing;

        void OnCurrentSessionChanged(
            GlobalSystemMediaTransportControlsSessionManager sender,
            CurrentSessionChangedEventArgs args)
        {
            if (_closing)
                return;

            Dispatcher.BeginInvoke(new Action(Attach));
        }

        void Attach()
        {
            if (_closing)
                return;

            GlobalSystemMediaTransportControlsSession? next = null;

            try
            {
                next = _mgr?.GetCurrentSession();
            }
            catch
            {
                next = null;
            }

            // Avoid unnecessarily detaching the same session.
            if (ReferenceEquals(next, _session))
            {
                _ = RefreshAsync();
                return;
            }

            // Invalidate old refresh operations.
            _refreshId++;

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnChanged;
                _session.PlaybackInfoChanged -= OnChanged;
            }

            _session = next;

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnChanged;
                _session.PlaybackInfoChanged += OnChanged;
            }

            _ = RefreshAsync();
        }

        void OnChanged(object? sender, object e)
        {
            if (_closing)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing)
                    return;

                // Ignore notifications from old sessions.
                if (sender != null &&
                    !ReferenceEquals(sender, _session))
                    return;

                _ = RefreshAsync();
            }));
        }

        async Task RefreshAsync()
        {
            int id = ++_refreshId;

            var session = _session;

            if (session == null)
            {
                _hasTrack = false;
                _title = "";
                _playing = false;

                PlaybackTitle.Text = "Nothing playing";
                PlaybackArtist.Text = "";
                PlayButton.Content = "▶";

                SetArt(null, false);

                ApplyCat();
                RefreshLeft();

                return;
            }

            try
            {
                var props =
                    await session.TryGetMediaPropertiesAsync();

                if (_closing ||
                    id != _refreshId ||
                    !ReferenceEquals(session, _session))
                    return;

                bool playing =
                    session.GetPlaybackInfo().PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                _hasTrack =
                    !string.IsNullOrWhiteSpace(props.Title);

                _title = props.Title ?? "";

                _playing = playing;

                PlaybackTitle.Text =
                    _hasTrack ? _title : "Nothing playing";

                PlaybackArtist.Text =
                    props.Artist ?? "";

                PlayButton.Content =
                    playing ? "❚❚" : "▶";

                ApplyCat();
                RefreshLeft();

                BitmapSource? art = null;

                if (props.Thumbnail != null)
                {
                    try
                    {
                        art = await LoadThumbAsync(
                            props.Thumbnail);
                    }
                    catch
                    {
                        art = null;
                    }
                }

                if (_closing ||
                    id != _refreshId ||
                    !ReferenceEquals(session, _session))
                    return;

                SetArt(art, playing);
            }
            catch
            {
                // A session may disappear during refresh.
                if (_closing ||
                    id != _refreshId ||
                    !ReferenceEquals(session, _session))
                    return;

                _hasTrack = false;
                _title = "";
                _playing = false;

                PlaybackTitle.Text = "Nothing playing";
                PlaybackArtist.Text = "";
                PlayButton.Content = "▶";

                SetArt(null, false);

                ApplyCat();
                RefreshLeft();
            }
        }

        static async Task<BitmapImage?> LoadThumbAsync(
            IRandomAccessStreamReference thumbnail)
        {
            using var ras =
                await thumbnail.OpenReadAsync();

            using var stream =
                ras.AsStreamForRead();

            using var memory = new MemoryStream();

            await stream.CopyToAsync(memory);

            memory.Position = 0;

            var bitmap = new BitmapImage();

            bitmap.BeginInit();

            bitmap.CacheOption =
                BitmapCacheOption.OnLoad;

            bitmap.StreamSource = memory;

            bitmap.EndInit();

            bitmap.Freeze();

            return bitmap;
        }

        // =====================================================
        // ALBUM ART + DOMINANT COLOR
        // =====================================================

        void SetArt(
            BitmapSource? art,
            bool playing)
        {
            if (art == null)
            {
                _albumArtColor = null;
                ArtBorder.Visibility =
                    Visibility.Collapsed;

                Dot.Visibility =
                    Visibility.Visible;

                BigArtBorder.Background =
                    new SolidColorBrush(
                        Color.FromRgb(0x1B, 0x1C, 0x25));

                BigArtNote.Visibility =
                    Visibility.Visible;

                SetGlow(null);

                return;
            }

            ArtBorder.Background =
                new ImageBrush(art)
                {
                    Stretch = Stretch.UniformToFill
                };

            BigArtBorder.Background =
                new ImageBrush(art)
                {
                    Stretch = Stretch.UniformToFill
                };

            ArtBorder.Visibility =
                Visibility.Visible;

            Dot.Visibility =
                Visibility.Collapsed;

            BigArtNote.Visibility =
                Visibility.Collapsed;

            _albumArtColor =
                playing ? DominantColor(art) : null;

            SetGlow(_albumArtColor);
        }

        static Color DominantColor(BitmapSource source)
        {
            if (source.PixelWidth <= 0 ||
                source.PixelHeight <= 0)
            {
                return Color.FromRgb(200, 200, 200);
            }

            var small = new TransformedBitmap(
                source,
                new ScaleTransform(
                    16.0 / source.PixelWidth,
                    16.0 / source.PixelHeight));

            var converted = new FormatConvertedBitmap(
                small,
                PixelFormats.Bgra32,
                null,
                0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int stride = width * 4;

            var pixels = new byte[height * stride];

            converted.CopyPixels(
                pixels,
                stride,
                0);

            double r = 0;
            double g = 0;
            double b = 0;
            double weightTotal = 0;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                double blue = pixels[i];
                double green = pixels[i + 1];
                double red = pixels[i + 2];

                double max =
                    Math.Max(red, Math.Max(green, blue));

                double min =
                    Math.Min(red, Math.Min(green, blue));

                double saturation =
                    max == 0 ? 0 : (max - min) / max;

                double weight =
                    0.02 +
                    saturation * saturation *
                    (max / 255.0);

                r += red * weight;
                g += green * weight;
                b += blue * weight;

                weightTotal += weight;
            }

            if (weightTotal <= 0)
                return Color.FromRgb(200, 200, 200);

            r /= weightTotal;
            g /= weightTotal;
            b /= weightTotal;

            double top =
                Math.Max(r, Math.Max(g, b));

            if (top < 1)
                return Color.FromRgb(200, 200, 200);

            double factor = 255.0 / top;

            r *= factor;
            g *= factor;
            b *= factor;

            double gray = (r + g + b) / 3;

            r = gray + (r - gray) * 1.2;
            g = gray + (g - gray) * 1.2;
            b = gray + (b - gray) * 1.2;

            return Color.FromRgb(
                (byte)Math.Clamp(r, 0, 255),
                (byte)Math.Clamp(g, 0, 255),
                (byte)Math.Clamp(b, 0, 255));
        }

        // =====================================================
        // DYNAMIC GLOW
        // =====================================================

        bool _glowOn;

        void SetGlow(Color? color)
        {
            var duration =
                TimeSpan.FromMilliseconds(500);

            Color targetColor = color ?? _themeColor;

            EdgeBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    Color.FromArgb(
                        235,
                        targetColor.R,
                        targetColor.G,
                        targetColor.B),
                    duration));

            if (color is Color glowColor && HaloEnabled)
            {
                _glowOn = true;

                if (GlowHost.Effect is not DropShadowEffect fx)
                {
                    fx = new DropShadowEffect
                    {
                        Color = glowColor,
                        BlurRadius = 18,
                        ShadowDepth = 0,
                        Opacity = 0
                    };

                    GlowHost.Effect = fx;
                }

                fx.BeginAnimation(
                    DropShadowEffect.ColorProperty,
                    new ColorAnimation(glowColor, duration));

                fx.BeginAnimation(
                    DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(0.7, duration));
            }
            else
            {
                _glowOn = false;

                if (GlowHost.Effect is DropShadowEffect fx)
                {
                    var fade =
                        new DoubleAnimation(0, duration);

                    fade.Completed += (_, _) =>
                    {
                        if (!_glowOn)
                            GlowHost.Effect = null;
                    };

                    fx.BeginAnimation(
                        DropShadowEffect.OpacityProperty,
                        fade);
                }
            }
        }

        // =====================================================
        // MEDIA BUTTONS
        // =====================================================

        async void Previous_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                var session = _session;

                if (session != null)
                    await session.TrySkipPreviousAsync();
            }
            catch { }
        }

        async void PlayPause_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                var session = _session;

                if (session != null)
                    await session.TryTogglePlayPauseAsync();
            }
            catch { }
        }

        async void Next_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                var session = _session;

                if (session != null)
                    await session.TrySkipNextAsync();
            }
            catch { }
        }

        // =====================================================
        // ANIMATED CAT
        // =====================================================

        bool _catRunning;
        bool _catBobbing;

        ScaleTransform? TryFindEyeScale()
        {
            return FindName("EyeScale") as ScaleTransform;
        }

        void ApplyCat()
{
    bool run = _expanded;

    if (run != _catRunning)
    {
        _catRunning = run;

        if (run)
        {
            TailRot.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(-14, 20,
                    TimeSpan.FromMilliseconds(850))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    }
                });

            var blink = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(4),
                RepeatBehavior = RepeatBehavior.Forever
            };

            blink.KeyFrames.Add(new LinearDoubleKeyFrame(
                1, KeyTime.FromTimeSpan(TimeSpan.Zero)));

            blink.KeyFrames.Add(new LinearDoubleKeyFrame(
                1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.6))));

            blink.KeyFrames.Add(new LinearDoubleKeyFrame(
                0.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.7))));

            blink.KeyFrames.Add(new LinearDoubleKeyFrame(
                1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.8))));

            blink.KeyFrames.Add(new LinearDoubleKeyFrame(
                1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4))));

            EyeScale.BeginAnimation(
                ScaleTransform.ScaleYProperty, blink);
        }
        else
        {
            TailRot.BeginAnimation(
                RotateTransform.AngleProperty, null);

            EyeScale.BeginAnimation(
                ScaleTransform.ScaleYProperty, null);
        }
    }

    bool bob = run && _playing;

    if (bob != _catBobbing)
    {
        _catBobbing = bob;

        if (bob)
        {
            _headBobTransform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(0, -2.5,
                    TimeSpan.FromMilliseconds(380))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    }
                });
        }
        else
        {
            _headBobTransform.BeginAnimation(
                TranslateTransform.YProperty, null);

            _headBobTransform.Y = 0;
        }
    }
}

        // =====================================================
        // SYSTEM TRAY
        // =====================================================

        void SetupTray()
        {
            try
            {
                var menu =
                    new System.Windows.Forms.ContextMenuStrip();

                menu.Items.Add(
                    "Start 25 min focus",
                    null,
                    (_, _) => StartTimer(25));

                menu.Items.Add(
                    "Stop timer",
                    null,
                    (_, _) => StopTimer());

                menu.Items.Add(
                    new System.Windows.Forms.ToolStripSeparator());

                var copied =
                    new System.Windows.Forms.ToolStripMenuItem(
                        "Show copied popup")
                    {
                        Checked = true,
                        CheckOnClick = true
                    };

                copied.CheckedChanged += (_, _) =>
                    _showCopied = copied.Checked;

                menu.Items.Add(copied);

                menu.Items.Add(
                    "Reset position",
                    null,
                    (_, _) =>
                    {
                        ResetPosition();
                        SavePosition();
                    });

                menu.Items.Add(
                    "Exit",
                    null,
                    (_, _) => Close());

                _tray =
                    new System.Windows.Forms.NotifyIcon
                    {
                        Icon = System.Drawing.SystemIcons.Application,
                        Text = "OpenNotch",
                        ContextMenuStrip = menu,
                        Visible = true
                    };
            }
            catch
            {
                // App can run without the tray icon.
            }
        }

        // =====================================================
        // CLEANUP
        // =====================================================

        protected override void OnClosed(EventArgs e)
        {
            _closing = true;

            _hover.Stop();
            _stats.Stop();
            _timerTick.Stop();
            _transientTimer.Stop();

            // Remove media event handlers.
            try
            {
                if (_mgr != null)
                {
                    _mgr.CurrentSessionChanged -=
                        OnCurrentSessionChanged;
                }

                if (_session != null)
                {
                    _session.MediaPropertiesChanged -= OnChanged;
                    _session.PlaybackInfoChanged -= OnChanged;
                }
            }
            catch { }

            _session = null;
            _mgr = null;

            // Remove clipboard listener.
            if (_clipboardListenerAdded &&
                _hwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_hwnd);
                _clipboardListenerAdded = false;
            }

            _src?.RemoveHook(WndProc);
            _src = null;

            // Dispose audio objects.
            try
            {
                if (_dev != null)
                {
                    _dev.AudioEndpointVolume.OnVolumeNotification -=
                        OnVolume;
                }

                _dev?.Dispose();
                _enum?.Dispose();
            }
            catch { }

            // Remove tray icon.
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            base.OnClosed(e);
        }

        // =====================================================
        // NO-FOCUS WINDOW + CLIPBOARD LISTENER
        // =====================================================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _hwnd =
                new WindowInteropHelper(this).Handle;

            // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            SetWindowLong(
                _hwnd,
                -20,
                GetWindowLong(_hwnd, -20) |
                0x00000080 |
                0x08000000);

            _src = HwndSource.FromHwnd(_hwnd);

            _src?.AddHook(WndProc);

            _clipboardListenerAdded =
                AddClipboardFormatListener(_hwnd);
        }

        [DllImport("user32.dll")]
        static extern int GetWindowLong(
            IntPtr hWnd,
            int nIndex);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(
            IntPtr hWnd,
            int nIndex,
            int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool AddClipboardFormatListener(
            IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RemoveClipboardFormatListener(
            IntPtr hwnd);
    }
}
