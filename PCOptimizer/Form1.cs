using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PCOptimizer
{
    /// <summary>
    /// Безрамочное окно-дашборд. Вся отрисовка происходит внутри WebView2
    /// (HTML/CSS/JS), а C# раз в секунду собирает данные о железе и
    /// отправляет их в интерфейс.
    /// </summary>
    public partial class Form1 : Form
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HardwareMonitor _monitor = new HardwareMonitor();

        // Движок очистки диска для вкладки «Очистка».
        private readonly CleanupEngine _cleanup = new CleanupEngine();
        private bool _cleanupBusy;
        private bool _optBusy;
        private bool _gameBusy;
        private bool _setBusy;

        // Мини-оверлей на рабочем столе.
        private MiniOverlayForm? _miniOverlay;

        // Значок в трее.
        private NotifyIcon? _tray;

        // Горячие клавиши.
        private const int HotkeyOpen = 1;
        private const int HotkeyTurbo = 2;
        private const int HotkeyMini = 3;
        private const int HotkeyCleanup = 4;
        private bool _hotkeysRegistered;

        // Чтобы уведомление о перегреве не сыпалось каждую секунду.
        private DateTime _lastHeatWarn = DateTime.MinValue;

        // Afterburner пробуем поднять только один раз за запуск.
        private bool _afterburnerTried;

        // Отчёт о железе: читается один раз и используется во всех решениях.
        private SystemReport _system = new SystemReport();
        private bool _diskBusy;

        // Оверлей поверх игры.
        private OverlayForm? _overlay;
        private int _overlayPid;

        // Настройки читаются один раз при запуске и применяются сразу.
        private readonly AppSettings _settings = SettingsStore.Load();

        // Автор и ссылка на репозиторий — впишите свою, она появится в окне.
        private const string AppAuthor = "Боря";
        private const string AppRepo = "https://github.com/Proso_chelTT/PCOptimizer";

        // Чтение и отправка данных не должны накладываться друг на друга.
        private readonly SemaphoreSlim _readLock = new SemaphoreSlim(1, 1);

        private WebView2? _webView;
        private bool _webReady;
        private bool _uiActive = true;

        // Страховка: раз в секунду проверяем состояние окна,
        // чтобы анимации не остались замороженными по ошибке.
        private readonly System.Windows.Forms.Timer _activityTimer =
            new System.Windows.Forms.Timer { Interval = 1000 };
        private bool _closing;
        private bool _polling;

        public Form1()
        {
            InitializeComponent();

            _activityTimer.Tick += (sender, args) => UpdateUiActive();
            _activityTimer.Start();

            FitToScreen();
            ApplyRoundedCorners();
            ApplyWindowIcon();
            CreateTrayIcon();
        }

        // ---------------- Внешний вид окна ----------------

        /// <summary>Окно не должно оказаться больше экрана.</summary>
        private void FitToScreen()
        {
            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);

            int width = Math.Min(ClientSize.Width, Math.Max(880, work.Width - 60));
            int height = Math.Min(ClientSize.Height, Math.Max(600, work.Height - 60));

            ClientSize = new Size(width, height);
            Location = new Point(
                work.Left + (work.Width - width) / 2,
                work.Top + (work.Height - height) / 2);
        }

        /// <summary>Скругляем углы окна: у безрамочной формы их нет по умолчанию.</summary>
        private void ApplyRoundedCorners()
        {
            const int radius = 14;

            using var path = new GraphicsPath();
            int d = radius * 2;
            var bounds = new Rectangle(0, 0, Width, Height);

            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            Region?.Dispose();
            Region = new Region(path);
        }

        private void ApplyWindowIcon()
        {
            try
            {
                string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

                Icon = File.Exists(icoPath)
                    ? new Icon(icoPath, 32, 32)
                    : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // Без иконки программа работает точно так же.
            }
        }

        // ---------------- Запуск интерфейса ----------------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = StartAsync();
        }

        /// <summary>
        /// Умный режим: сообщаем интерфейсу, активно ли окно.
        /// Пока окно свёрнуто или не в фокусе, анимации в нём стоят,
        /// и движок WebView2 почти не нагружает процессор.
        /// </summary>
        private void UpdateUiActive()
        {
            bool active = WindowState != FormWindowState.Minimized && ActiveForm is not null;

            if (active == _uiActive)
            {
                return;
            }

            _uiActive = active;
            PostToUi(new { type = "state", active });
        }

        // WM_ACTIVATE приходит, когда окно получает или теряет фокус.
        private const int WM_ACTIVATE = 0x0006;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_ACTIVATE)
            {
                UpdateUiActive();
            }

            // Горячие клавиши работают, даже когда окно свёрнуто в трей.
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                HandleHotkey(m.WParam.ToInt32());
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // При пересоздании окна старые горячие клавиши перестают работать,
            // поэтому регистрируем заново.
            _hotkeysRegistered = false;
            RegisterHotkeys();
        }

        // ---------------- Значок в трее ----------------

        private void CreateTrayIcon()
        {
            try
            {
                var menu = new ContextMenuStrip();

                menu.Items.Add("Открыть", null, (sender, args) => RestoreFromTray());
                menu.Items.Add("Мини-оверлей", null, (sender, args) =>
                    SetMiniOverlay(_miniOverlay is null || _miniOverlay.IsDisposed));
                menu.Items.Add("Турбо-режим", null, (sender, args) => _ = RunTurboAsync());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Выход", null, (sender, args) =>
                {
                    _closing = true;
                    Close();
                });

                _tray = new NotifyIcon
                {
                    Icon = Icon ?? SystemIcons.Application,
                    Text = "PCOptimizer",
                    Visible = true,
                    ContextMenuStrip = menu
                };

                // Левый клик — открыть окно.
                _tray.MouseClick += (sender, args) =>
                {
                    if (args.Button == MouseButtons.Left)
                    {
                        RestoreFromTray();
                    }
                };

                _tray.DoubleClick += (sender, args) => RestoreFromTray();
            }
            catch
            {
                // Без значка в трее программа всё равно работает.
            }
        }

        private void MinimizeToTray()
        {
            Hide();
            _uiActive = false;
            PostToUi(new { type = "state", active = false });

            try
            {
                _tray?.ShowBalloonTip(
                    2500,
                    "PCOptimizer",
                    "Свёрнут в трей. Клик по значку или Ctrl+Shift+O — открыть.",
                    ToolTipIcon.Info);
            }
            catch
            {
                // Всплывающие уведомления могут быть выключены в Windows.
            }
        }

        private void RestoreFromTray()
        {
            Show();

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            Activate();
            UpdateUiActive();
        }

        // ---------------- Горячие клавиши ----------------

        private void RegisterHotkeys()
        {
            if (_hotkeysRegistered)
            {
                return;
            }

            // Ctrl+Shift+O — открыть или свернуть окно.
            // Ctrl+Shift+T — турбо-режим.
            // Ctrl+Shift+M — мини-оверлей.
            // Ctrl+Shift+C — большая чистка.
            bool ok = NativeMethods.RegisterHotKey(
                Handle, HotkeyOpen, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x4F);

            ok &= NativeMethods.RegisterHotKey(
                Handle, HotkeyTurbo, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x54);

            ok &= NativeMethods.RegisterHotKey(
                Handle, HotkeyMini, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x4D);

            ok &= NativeMethods.RegisterHotKey(
                Handle, HotkeyCleanup, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x43);

            _hotkeysRegistered = ok;

            if (!ok)
            {
                AppLog.Write("Горячие клавиши",
                    "часть сочетаний занята другой программой — они не сработают");
            }
        }

        private void UnregisterHotkeys()
        {
            if (!_hotkeysRegistered)
            {
                return;
            }

            foreach (int id in new[] { HotkeyOpen, HotkeyTurbo, HotkeyMini, HotkeyCleanup })
            {
                try
                {
                    NativeMethods.UnregisterHotKey(Handle, id);
                }
                catch
                {
                    // Окно уже уничтожается.
                }
            }

            _hotkeysRegistered = false;
        }

        private void HandleHotkey(int id)
        {
            switch (id)
            {
                case HotkeyOpen:
                    if (Visible && ActiveForm is not null)
                    {
                        MinimizeToTray();
                    }
                    else
                    {
                        RestoreFromTray();
                    }

                    break;

                case HotkeyTurbo:
                    _ = RunTurboAsync();
                    break;

                case HotkeyMini:
                    SetMiniOverlay(_miniOverlay is null || _miniOverlay.IsDisposed);
                    break;

                case HotkeyCleanup:
                    if (!_cleanupBusy)
                    {
                        RestoreFromTray();
                        _ = RunCleanupAsync(scanOnly: false);
                    }

                    break;
            }
        }

        // ---------------- Мини-оверлей на рабочем столе ----------------

        internal static Color AccentColor(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "orange": return Color.FromArgb(255, 122, 26);
                case "yellow": return Color.FromArgb(245, 192, 0);
                case "green": return Color.FromArgb(31, 196, 95);
                case "cyan": return Color.FromArgb(0, 207, 255);
                case "blue": return Color.FromArgb(61, 123, 255);
                case "purple": return Color.FromArgb(168, 85, 247);
                default: return Color.FromArgb(255, 43, 74);
            }
        }

        private void SetMiniOverlay(bool on)
        {
            if (on)
            {
                ShowMiniOverlay();
            }
            else
            {
                CloseMiniOverlay();
            }

            _settings.MiniOverlay = on;
            _settings.MiniOverlayAsked = true;
            SettingsStore.Save(_settings);

            PostToUi(new { type = "miniState", on });
        }

        private void ShowMiniOverlay()
        {
            if (_miniOverlay is not null && !_miniOverlay.IsDisposed)
            {
                return;
            }

            _miniOverlay = new MiniOverlayForm();
            _miniOverlay.HideRequested += (sender, args) => SetMiniOverlay(false);
            _miniOverlay.Moved += (sender, args) => RememberMiniOverlayPosition();
            _miniOverlay.SetAccent(AccentColor(_settings.Accent));

            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

            int x = _settings.MiniOverlayX >= 0
                ? _settings.MiniOverlayX
                : work.Right - _miniOverlay.Width - 24;

            int y = _settings.MiniOverlayY >= 0
                ? _settings.MiniOverlayY
                : work.Bottom - _miniOverlay.Height - 24;

            // Чтобы окно не уехало за пределы экрана после смены разрешения.
            x = Math.Max(work.Left, Math.Min(x, work.Right - _miniOverlay.Width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - _miniOverlay.Height));

            _miniOverlay.Location = new Point(x, y);
            _miniOverlay.Show();
        }

        private void CloseMiniOverlay()
        {
            if (_miniOverlay is null)
            {
                return;
            }

            try
            {
                _miniOverlay.Hide();
                _miniOverlay.Dispose();
            }
            catch
            {
                // Окно уже закрыто.
            }

            _miniOverlay = null;
        }

        private void RememberMiniOverlayPosition()
        {
            if (_miniOverlay is null || _miniOverlay.IsDisposed)
            {
                return;
            }

            _settings.MiniOverlayX = _miniOverlay.Location.X;
            _settings.MiniOverlayY = _miniOverlay.Location.Y;
            SettingsStore.Save(_settings);
        }

        private void UpdateMiniOverlay(Snapshot snapshot)
        {
            if (_miniOverlay is null || _miniOverlay.IsDisposed || !_miniOverlay.Visible)
            {
                return;
            }

            _miniOverlay.Apply(snapshot);
        }

        // ---------------- Отчёт о железе ----------------

        /// <summary>Читаем готовый отчёт (или сканируем, если ещё не было).</summary>
        private async Task LoadSystemAsync()
        {
            try
            {
                SystemReport report = await Task.Run(() => ProfileStore.Current);
                _system = report;

                PostToUi(new { type = "system", report = SystemPayload(report) });

                AppLog.Write("Система",
                    report.Cpu.Name + " · " + report.Gpu.Name + " · "
                    + (report.Ram.TotalMb / 1024) + " ГБ памяти"
                    + (report.FromCache ? " (из profile.json)" : " (просканировано)"));
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "systemError", text = ex.Message });
            }
        }

        private Task SendSystemAsync()
        {
            PostToUi(new { type = "system", report = SystemPayload(_system) });
            return Task.CompletedTask;
        }

        /// <summary>Кнопка «Сканировать заново»: полностью перечитываем железо.</summary>
        private async Task RescanSystemAsync()
        {
            PostToUi(new { type = "systemBusy", busy = true, text = "Сканирую компьютер…" });

            try
            {
                SystemReport report = await Task.Run(() => ProfileStore.Rescan());
                _system = report;

                PostToUi(new { type = "system", report = SystemPayload(report) });
                PostToUi(new { type = "systemBusy", busy = false, text = "" });
                PostToUi(new { type = "setResult", warn = false, text = "Компьютер пересканирован" });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "systemBusy", busy = false, text = "" });
                PostToUi(new { type = "setResult", warn = true, text = "Не удалось просканировать: " + ex.Message });
            }
        }

        /// <summary>Обслуживание выбранного диска: TRIM для SSD, дефрагментация для HDD.</summary>
        private async Task OptimizeDiskAsync(JsonElement message)
        {
            if (_diskBusy)
            {
                return;
            }

            string letter = message.TryGetProperty("letter", out JsonElement letterElement)
                ? letterElement.GetString() ?? ""
                : "";

            VolumeInfo? volume = _system.Volumes
                .FirstOrDefault(item => string.Equals(item.Letter, letter, StringComparison.OrdinalIgnoreCase));

            if (volume is null)
            {
                PostToUi(new { type = "diskResult", warn = true, text = "Диск не найден: " + letter });
                return;
            }

            _diskBusy = true;

            try
            {
                PostToUi(new
                {
                    type = "diskBusy",
                    busy = true,
                    text = DiskTools.ActionTitle(volume) + " — диск " + letter + "…"
                });

                DiskActionResult result = await DiskTools.OptimizeAsync(
                    volume,
                    line => PostToUi(new { type = "diskStatus", text = line }),
                    CancellationToken.None);

                AppLog.Write("Диск", letter + ": " + result.Text);

                PostToUi(new
                {
                    type = "diskResult",
                    warn = !result.Ok,
                    text = result.Text,
                    detail = result.Detail
                });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "diskResult", warn = true, text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _diskBusy = false;
                PostToUi(new { type = "diskBusy", busy = false, text = "" });
            }
        }

        /// <summary>Отчёт о железе в виде, удобном для интерфейса.</summary>
        private static object SystemPayload(SystemReport report)
        {
            return new
            {
                cpu = new
                {
                    report.Cpu.Name,
                    report.Cpu.Cores,
                    report.Cpu.Threads,
                    report.Cpu.MaxMhz,
                    tier = report.Cpu.Tier.ToString(),
                    tierName = report.Cpu.TierName
                },
                gpu = new
                {
                    report.Gpu.Name,
                    vendor = report.Gpu.Vendor.ToString(),
                    vendorName = report.Gpu.VendorName,
                    report.Gpu.MemoryMb,
                    report.Gpu.Driver
                },
                gpus = report.AllGpus.Select(gpu => new
                {
                    gpu.Name,
                    vendorName = gpu.VendorName,
                    gpu.MemoryMb,
                    gpu.IsMain
                }).ToArray(),
                ram = new
                {
                    totalMb = report.Ram.TotalMb,
                    report.Ram.SpeedMhz,
                    report.Ram.TypeName,
                    report.Ram.Modules,
                    report.Ram.Slots,
                    low = report.Ram.LowMemory
                },
                windows = new
                {
                    report.Windows.Name,
                    report.Windows.Version,
                    report.Windows.BuildFull,
                    report.Windows.DisplayVersion,
                    report.Windows.Arch
                },
                volumes = report.Volumes.Select(volume => new
                {
                    volume.Letter,
                    volume.Label,
                    volume.FileSystem,
                    volume.Model,
                    volume.TotalBytes,
                    volume.FreeBytes,
                    media = volume.Media.ToString(),
                    mediaName = volume.MediaName,
                    volume.IsSystem,
                    action = DiskTools.ActionTitle(volume),
                    hint = DiskTools.DescribeAction(volume)
                }).ToArray(),
                gpuMonitoring = report.GpuMonitoringMethod,
                weakCpu = report.WeakCpu,
                hasSsd = report.HasSsd,
                hasHdd = report.HasHdd,
                diskTools = DiskTools.IsAvailable(),
                scannedAt = report.ScannedAt,
                fromCache = report.FromCache,
                admin = HardwareMonitor.IsElevated(),
                notes = report.BuildNotes()
            };
        }

        /// <summary>
        /// Если своих датчиков температуры нет — один раз за запуск поднимаем
        /// MSI Afterburner: у него собственный драйвер, и на многих платах он
        /// видит процессор там, где LibreHardwareMonitor бессилен.
        /// </summary>
        private void EnsureAfterburner(Snapshot snapshot)
        {
            if (_afterburnerTried || snapshot.Cpu.Temp.HasValue)
            {
                return;
            }

            if (!_settings.UseAfterburner || !Afterburner.IsInstalled)
            {
                return;
            }

            if (Afterburner.IsRunning())
            {
                // Уже работает — запускать нечего, ждём данные от него.
                _afterburnerTried = true;
                return;
            }

            _afterburnerTried = true;
            string? error = Afterburner.Start();

            if (error is null)
            {
                AppLog.Write("Afterburner", "запущен, чтобы читать температуру процессора");

                // Читаем дальше — подсказка внизу окна уже не должна просить
                // запустить то, что мы только что запустили.
                snapshot.Cpu.TempNote = "Запускаю MSI Afterburner, чтобы прочитать температуру…";
            }
            else
            {
                AppLog.Write("Afterburner", "запустить не удалось: " + error);
            }
        }

        /// <summary>Кнопка «Запустить Afterburner» в настройках.</summary>
        private async Task StartAfterburnerNowAsync()
        {
            PostToUi(new { type = "setBusy", busy = true, text = "Запускаю MSI Afterburner…" });

            string? error = await Task.Run(() => Afterburner.Start());

            if (error is not null)
            {
                PostToUi(new { type = "setResult", warn = true, text = "Не удалось запустить: " + error });
                await SendSettingsAsync();
                return;
            }

            // Afterburner опрашивает датчики не мгновенно — даём ему время.
            await Task.Delay(3500);

            _afterburnerTried = true;
            _monitor.RetrySensors();
            await ReadAndPushAsync();

            AppLog.Write("Afterburner", "запущен вручную из настроек");
            PostToUi(new
            {
                type = "setResult",
                warn = false,
                text = "MSI Afterburner запущен — температуру беру из него"
            });

            await SendSettingsAsync();
        }

        /// <summary>Уведомление в трее, если процессор раскалился.</summary>
        private void CheckHeatAlert(Snapshot snapshot)
        {
            float? temp = snapshot.Cpu.Temp;

            if (!temp.HasValue || temp.Value < 80)
            {
                return;
            }

            // Не чаще одного раза в 10 минут, иначе это превратится в спам.
            if ((DateTime.Now - _lastHeatWarn).TotalMinutes < 10)
            {
                return;
            }

            _lastHeatWarn = DateTime.Now;
            AppLog.Write("Внимание", "температура процессора " + Math.Round(temp.Value) + " °C");

            try
            {
                _tray?.ShowBalloonTip(
                    4000,
                    "PCOptimizer · температура",
                    "Процессор " + Math.Round(temp.Value) + " °C. Это выше 80 °C — "
                        + "стоит проверить охлаждение.",
                    ToolTipIcon.Warning);
            }
            catch
            {
                // Уведомления выключены в системе.
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateUiActive();
        }

        private async Task StartAsync()
        {
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось запустить интерфейс на WebView2.\n\n" + ex.Message +
                    "\n\nСкорее всего не установлен компонент «Microsoft Edge WebView2 Runtime».",
                    "PCOptimizer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Close();
            }
        }

        private async Task InitializeWebViewAsync()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(8, 8, 12)
            };

            Controls.Add(_webView);
            _webView.SendToBack();

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PCOptimizer",
                "WebView2");

            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(null, userDataFolder);

            await _webView.EnsureCoreWebView2Async(environment);

            CoreWebView2 core = _webView.CoreWebView2;

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnNavigationCompleted;

            // Файлы интерфейса лежат рядом с программой в папке ui.
            string uiFolder = Path.Combine(AppContext.BaseDirectory, "ui");
            core.SetVirtualHostNameToFolderMapping(
                "pco.local", uiFolder, CoreWebView2HostResourceAccessKind.DenyCors);

            core.Navigate("https://pco.local/index.html");
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess || _closing)
            {
                return;
            }

            _webReady = true;
            PostToUi(new { type = "theme", theme = _settings.Theme, accent = _settings.Accent });
            PostToUi(new { type = "state", active = _uiActive });

            // Права администратора — от них зависит половина возможностей.
            PostToUi(new { type = "admin", ok = HardwareMonitor.IsElevated() });

            // Отчёт о железе читаем в фоне, чтобы запуск не подвисал.
            _ = LoadSystemAsync();

            if (_settings.MiniOverlay)
            {
                // В прошлый раз мини-оверлей был включён — включаем сразу.
                ShowMiniOverlay();
                PostToUi(new { type = "miniState", on = true });
            }
            else
            {
                // Спрашиваем при каждом запуске, пока он выключен.
                PostToUi(new { type = "miniAsk" });
            }

            await ReadAndPushAsync();
            StartPolling();
        }

        // ---------------- Опрос железа ----------------

        private void StartPolling()
        {
            if (_polling)
            {
                return;
            }

            _polling = true;
            _ = PollLoopAsync();
        }

        private async Task PollLoopAsync()
        {
            while (!_closing)
            {
                try
                {
                    await Task.Delay(1000);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                await ReadAndPushAsync();
            }
        }

        /// <summary>Один замер и одна отправка в интерфейс.</summary>
        private async Task ReadAndPushAsync()
        {
            if (_closing || !_webReady)
            {
                return;
            }

            if (!await _readLock.WaitAsync(0))
            {
                return; // предыдущий замер ещё не закончился
            }

            try
            {
                Snapshot snapshot = await Task.Run(async () => await _monitor.ReadAsync());

                if (_closing)
                {
                    return;
                }

                EnsureAfterburner(snapshot);

                PostToUi(new { type = "data", payload = snapshot });
                UpdateOverlay(snapshot);
                UpdateMiniOverlay(snapshot);
                CheckHeatAlert(snapshot);
                PostHint(snapshot);
            }
            catch
            {
                // Разовый сбой чтения не должен ронять интерфейс.
            }
            finally
            {
                _readLock.Release();
            }
        }

        /// <summary>Подсказка внизу окна: почему чего-то нет или что всё хорошо.</summary>
        private void PostHint(Snapshot snapshot)
        {
            bool canElevate = !HardwareMonitor.IsElevated();

            if (snapshot.Cpu.Temp is null && !string.IsNullOrEmpty(snapshot.Cpu.TempNote))
            {
                PostToUi(new
                {
                    type = "hint",
                    text = snapshot.Cpu.TempNote,
                    warn = true,
                    elevate = canElevate
                });
                return;
            }

            if (!string.IsNullOrEmpty(snapshot.Gpu.Error))
            {
                PostToUi(new
                {
                    type = "hint",
                    text = "Видеокарта: " + snapshot.Gpu.Error,
                    warn = true,
                    elevate = false
                });
                return;
            }

            PostToUi(new
            {
                type = "hint",
                text = "Автообновление — раз в секунду",
                warn = false,
                elevate = false
            });
        }

        private void PostToUi(object message)
        {
            WebView2? view = _webView;
            if (!_webReady || view?.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                view.CoreWebView2.PostWebMessageAsJson(
                    JsonSerializer.Serialize(message, JsonOptions));
            }
            catch
            {
                // Окно уже закрывается — отправлять некуда.
            }
        }

        // ---------------- Команды из интерфейса ----------------

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string action = "";
            string action2 = "";
            JsonElement payload = default;

            try
            {
                using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
                payload = document.RootElement.Clone();

                if (payload.TryGetProperty("action", out JsonElement element))
                {
                    action = element.GetString() ?? "";
                }

                if (payload.TryGetProperty("name", out JsonElement nameElement))
                {
                    action2 = nameElement.GetString() ?? "";
                }
            }
            catch
            {
                return;
            }

            switch (action)
            {
                case "drag":
                    StartWindowDrag();
                    break;

                case "minimize":
                    MinimizeToTray();
                    break;

                case "miniSet":
                    SetMiniOverlay(
                        payload.TryGetProperty("on", out JsonElement miniOn) &&
                        miniOn.ValueKind == JsonValueKind.True);
                    break;

                case "miniAsked":
                    // Пользователь ответил на вопрос при запуске — больше не спрашиваем.
                    _settings.MiniOverlayAsked = true;
                    SettingsStore.Save(_settings);
                    break;

                case "miniHide":
                    SetMiniOverlay(false);
                    break;

                case "systemLoad":
                    _ = SendSystemAsync();
                    break;

                case "systemRescan":
                    _ = RescanSystemAsync();
                    break;

                case "diskOptimize":
                    _ = OptimizeDiskAsync(payload);
                    break;

                case "abStart":
                    _ = StartAfterburnerNowAsync();
                    break;

                case "tempRetry":
                    _monitor.RetrySensors();
                    _afterburnerTried = false;
                    _ = ReadAndPushAsync();
                    PostToUi(new { type = "setResult", warn = false, text = "Пробую прочитать датчики заново…" });
                    break;

                case "statsLoad":
                    PostToUi(new { type = "stats", stats = StatsStore.Snapshot() });
                    break;

                case "statsReset":
                    StatsStore.Reset();
                    AppLog.Write("Счётчик", "сброшен вручную");
                    PostToUi(new { type = "stats", stats = StatsStore.Snapshot() });
                    PostToUi(new { type = "setResult", warn = false, text = "Счётчик обнулён" });
                    break;

                case "close":
                    Close();
                    break;

                case "refreshCpu":
                case "refreshGpu":
                    // Кнопки просто просят замер прямо сейчас, не дожидаясь таймера.
                    _ = ReadAndPushAsync();
                    break;

                case "gamesList":
                    _ = SendGameLibraryAsync();
                    break;

                case "gamesLaunch":
                    _ = LaunchGameAsync(payload);
                    break;

                case "turboRun":
                    _ = RunTurboAsync();
                    break;

                case "overlayToggle":
                    ToggleOverlay();
                    break;

                case "profileSave":
                    _ = SaveProfileAsync(payload);
                    break;

                case "profileApply":
                    _ = ApplyProfileAsync(payload);
                    break;

                case "profileDelete":
                    _ = DeleteProfileAsync(payload);
                    break;

                case "settingsLoad":
                    _ = SendSettingsAsync();
                    break;

                case "settingsSave":
                    _ = SaveSettingsAsync(payload);
                    break;

                case "settingsClearLog":
                    _ = ClearLogAsync();
                    break;

                case "settingsRestorePoint":
                    _ = MakeRestorePointAsync();
                    break;

                case "settingsUpdates":
                    CheckUpdates();
                    break;

                case "gamesBrowsers":
                    _ = SendBrowsersAsync();
                    break;

                case "gamesScan":
                    _ = ScanGamesAsync();
                    break;

                case "gamesBoost":
                    _ = BoostGameAsync(payload);
                    break;

                case "gamesRestore":
                    _ = RestoreGameAsync();
                    break;

                case "autoRun":
                    _ = RunAutoOptimizationAsync();
                    break;

                case "optLoad":
                    _ = SendOptStateAsync();
                    break;

                case "optApply":
                    _ = ApplyOptimizationAsync(payload);
                    break;

                case "optAction":
                    _ = RunOptActionAsync(action2, payload);
                    break;

                case "cleanupList":
                    SendCleanupList();
                    break;

                case "cleanupScan":
                    _ = RunCleanupAsync(scanOnly: true);
                    break;

                case "cleanupRun":
                    _ = RunCleanupAsync(scanOnly: false);
                    break;

                case "elevate":
                    RestartAsAdministrator();
                    break;
            }
        }

        /// <summary>
        /// Перезапуск с правами администратора — он нужен, чтобы прочитать
        /// температуру процессора: без прав драйвер датчиков не загружается.
        /// </summary>
        // ---------------- Игры ----------------

        // ---------------- Оверлей, турбо-режим, профили ----------------

        private async Task SendGameLibraryAsync()
        {
            try
            {
                var items = await Task.Run(() => GameLibrary.Scan());

                bool blocked = OnlineGuard.IsBlocked(out string reason);

                PostToUi(new
                {
                    type = "gameLibrary",
                    items,
                    profiles = _settings.Profiles,
                    blocked,
                    reason,
                    rtss = FpsReader.IsRtssRunning()
                });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "gameStep", icon = "fail", title = "Список игр", text = ex.Message });
            }
        }

        private async Task LaunchGameAsync(JsonElement message)
        {
            if (_gameBusy)
            {
                return;
            }

            if (OnlineGuard.IsBlocked(out string reason))
            {
                PostToUi(new
                {
                    type = "gameStep",
                    icon = "skip",
                    title = "Запуск отменён",
                    text = "обнаружено: " + reason + " — не вмешиваюсь"
                });

                PostToUi(new { type = "gameDone", text = "Онлайн-игра, не вмешиваюсь" });
                return;
            }

            string appId = message.TryGetProperty("appId", out JsonElement idElement)
                ? idElement.GetString() ?? ""
                : "";

            var items = await Task.Run(() => GameLibrary.Scan());
            InstalledGame? game = null;

            foreach (InstalledGame candidate in items)
            {
                if (candidate.AppId == appId)
                {
                    game = candidate;
                    break;
                }
            }

            if (game is null)
            {
                PostToUi(new { type = "gameStep", icon = "fail", title = "Запуск", text = "игра не найдена" });
                PostToUi(new { type = "gameDone", text = "Не нашёл игру" });
                return;
            }

            _gameBusy = true;

            try
            {
                string? error = GameLibrary.Launch(game);

                if (error is not null)
                {
                    PostToUi(new { type = "gameStep", icon = "fail", title = "Запуск", text = error });
                    PostToUi(new { type = "gameDone", text = "Не удалось запустить" });
                    return;
                }

                AppLog.Write("Игры", "запуск: " + game.Title);

                PostToUi(new
                {
                    type = "gameStep",
                    icon = "ok",
                    title = "Запуск игры",
                    text = game.Title + " — запускаю и применяю оптимизации"
                });

                PostToUi(new { type = "gameBusy", busy = true, text = "Жду старта игры…" });
                await Task.Delay(4000);

                var plan = new GameProfile();

                await TurboRunner.RunAsync(
                    _cleanup,
                    plan,
                    0,
                    (icon, title, text) => PostToUi(new { type = "gameStep", icon, title, text }),
                    (busy, text) => PostToUi(new { type = "gameBusy", busy, text }));

                PostToUi(new { type = "gameDone", text = game.Title + " запущена и оптимизирована" });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "gameDone", text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _gameBusy = false;
                PostToUi(new { type = "gameBusy", busy = false, text = "" });
            }
        }

        private async Task RunTurboAsync()
        {
            if (_gameBusy)
            {
                return;
            }

            _gameBusy = true;

            try
            {
                var plan = new GameProfile();

                await TurboRunner.RunAsync(
                    _cleanup,
                    plan,
                    _overlayPid,
                    (icon, title, text) => PostToUi(new { type = "gameStep", icon, title, text }),
                    (busy, text) => PostToUi(new { type = "gameBusy", busy, text }));

                AppLog.Write("Турбо-режим", "применён");
                StatsStore.AddOptimization();
                PostToUi(new
                {
                    type = "gameDone",
                    text = "Турбо-режим применён",
                    stats = StatsStore.Snapshot()
                });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "gameDone", text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _gameBusy = false;
                PostToUi(new { type = "gameBusy", busy = false, text = "" });
            }
        }

        private void ToggleOverlay()
        {
            if (_overlay is not null && !_overlay.IsDisposed)
            {
                CloseOverlay();
                PostToUi(new { type = "gameStep", icon = "skip", title = "Оверлей", text = "выключен" });
                return;
            }

            if (OnlineGuard.IsBlocked(out string reason))
            {
                PostToUi(new
                {
                    type = "gameStep",
                    icon = "fail",
                    title = "Оверлей не включён",
                    text = "обнаружено: " + reason + " — поверх онлайн-игр оверлей не показываю"
                });

                return;
            }

            var games = GameScanner.FindGames();
            _overlayPid = games.Count > 0 ? games[0].Pid : 0;

            _overlay = new OverlayForm();
            _overlay.CloseRequested += (sender, args) => CloseOverlay();

            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            _overlay.Location = new Point(work.Right - _overlay.Width - 24, work.Top + 24);
            _overlay.Show();

            PostToUi(new
            {
                type = "gameStep",
                icon = "ok",
                title = "Оверлей включён",
                text = games.Count > 0
                    ? "слежу за " + games[0].Title + " — двигайте мышкой за любое место"
                    : "игра не найдена, показываю только систему"
            });
        }

        private void CloseOverlay()
        {
            if (_overlay is null)
            {
                return;
            }

            try
            {
                _overlay.Hide();
                _overlay.Dispose();
            }
            catch
            {
                // Окно уже закрыто.
            }

            _overlay = null;
            _overlayPid = 0;
        }

        /// <summary>Обновляет оверлей. Если появилась онлайн-игра — немедленно закрывает его.</summary>
        private void UpdateOverlay(Snapshot snapshot)
        {
            if (_overlay is null || _overlay.IsDisposed)
            {
                return;
            }

            if (OnlineGuard.IsBlocked(out string reason))
            {
                CloseOverlay();

                PostToUi(new
                {
                    type = "gameStep",
                    icon = "skip",
                    title = "Оверлей выключен",
                    text = "обнаружено: " + reason + " — поверх онлайн-игр не работаю"
                });

                return;
            }

            int pid = _overlayPid;
            double? fps = pid > 0 ? FpsReader.Read(pid) : null;

            string hint = fps.HasValue
                ? "FPS считает RTSS"
                : (FpsReader.IsRtssRunning()
                    ? "RTSS запущен, но игру не видит"
                    : "Для FPS запустите RTSS (идёт с MSI Afterburner)");

            _overlay.Apply(snapshot, fps, hint);
        }

        private async Task SaveProfileAsync(JsonElement message)
        {
            string name = message.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";

            string title = message.TryGetProperty("gameTitle", out JsonElement titleElement)
                ? titleElement.GetString() ?? ""
                : "";

            if (name.Trim().Length == 0)
            {
                PostToUi(new { type = "setResult", warn = true, text = "Введите название профиля" });
                return;
            }

            _settings.Profiles.RemoveAll(item => item.Name == name);
            _settings.Profiles.Add(new GameProfile { Name = name, GameTitle = title });

            SettingsStore.Save(_settings);
            AppLog.Write("Профиль", "сохранён: " + name);

            PostToUi(new { type = "setResult", warn = false, text = "Профиль сохранён: " + name });
            await SendGameLibraryAsync();
        }

        private async Task ApplyProfileAsync(JsonElement message)
        {
            if (_gameBusy)
            {
                return;
            }

            string name = message.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";

            GameProfile? profile = null;

            foreach (GameProfile candidate in _settings.Profiles)
            {
                if (candidate.Name == name)
                {
                    profile = candidate;
                    break;
                }
            }

            if (profile is null)
            {
                PostToUi(new { type = "gameStep", icon = "fail", title = "Профиль", text = "не найден" });
                return;
            }

            _gameBusy = true;

            try
            {
                await TurboRunner.RunAsync(
                    _cleanup,
                    profile,
                    0,
                    (icon, title, text) => PostToUi(new { type = "gameStep", icon, title, text }),
                    (busy, text) => PostToUi(new { type = "gameBusy", busy, text }));

                AppLog.Write("Профиль", "применён: " + name);
                PostToUi(new { type = "gameDone", text = "Профиль «" + name + "» применён" });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "gameDone", text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _gameBusy = false;
                PostToUi(new { type = "gameBusy", busy = false, text = "" });
            }
        }

        private async Task DeleteProfileAsync(JsonElement message)
        {
            string name = message.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";

            _settings.Profiles.RemoveAll(item => item.Name == name);
            SettingsStore.Save(_settings);

            PostToUi(new { type = "setResult", warn = false, text = "Профиль удалён: " + name });
            await SendGameLibraryAsync();
        }

        // ---------------- Настройки ----------------

        private static string AppVersion()
        {
            var version = typeof(Form1).Assembly.GetName().Version;
            return version is null ? "1.0.0" : version.ToString(3);
        }

        /// <summary>Отправляет интерфейсу настройки, журнал, точки восстановления и данные о программе.</summary>
        private async Task SendSettingsAsync()
        {
            try
            {
                var log = await Task.Run(() => AppLog.ReadLast(80));
                var points = await RestorePointManager.ListAsync();

                PostToUi(new
                {
                    type = "settings",
                    theme = _settings.Theme,
                    accent = _settings.Accent,
                    mini = _settings.MiniOverlay,
                    sound = _settings.SoundEnabled,
                    stats = StatsStore.Snapshot(),
                    tempSource = _monitor.TemperatureSource,
                    tempNote = _monitor.TemperatureNote,
                    afterburnerInstalled = Afterburner.IsInstalled,
                    afterburnerPath = Afterburner.ExePath,
                    afterburnerRunning = Afterburner.IsRunning(),
                    useAfterburner = _settings.UseAfterburner,
                    autorun = SelfStartup.IsEnabled(),
                    log,
                    points,
                    version = AppVersion(),
                    author = AppAuthor,
                    repo = AppRepo,
                    settingsPath = AppPaths.SettingsFile,
                    logPath = AppPaths.LogFile
                });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "setResult", warn = true, text = "Ошибка: " + ex.Message });
            }
            finally
            {
                PostToUi(new { type = "setBusy", busy = false, text = "" });
            }
        }

        private async Task SaveSettingsAsync(JsonElement message)
        {
            if (_setBusy)
            {
                return;
            }

            var notes = new List<string>();

            if (message.TryGetProperty("theme", out JsonElement themeElement))
            {
                string theme = themeElement.GetString() ?? "dark";

                if (theme != _settings.Theme)
                {
                    _settings.Theme = theme;
                    notes.Add("тема: " + (theme == "light" ? "светлая" : "тёмная"));
                }
            }

            if (message.TryGetProperty("accent", out JsonElement accentElement))
            {
                string accent = (accentElement.GetString() ?? "red").ToLowerInvariant();

                string[] allowed = { "red", "orange", "yellow", "green", "cyan", "blue", "purple" };

                if (Array.IndexOf(allowed, accent) < 0)
                {
                    accent = "red";
                }

                if (accent != _settings.Accent)
                {
                    _settings.Accent = accent;
                    notes.Add("цвет: " + AccentName(accent));

                    if (_miniOverlay is not null && !_miniOverlay.IsDisposed)
                    {
                        _miniOverlay.SetAccent(AccentColor(accent));
                    }
                }
            }

            if (message.TryGetProperty("useAfterburner", out JsonElement abElement) &&
                (abElement.ValueKind == JsonValueKind.True || abElement.ValueKind == JsonValueKind.False))
            {
                bool use = abElement.GetBoolean();

                if (use != _settings.UseAfterburner)
                {
                    _settings.UseAfterburner = use;
                    _afterburnerTried = !use;

                    notes.Add("Afterburner " + (use ? "включён" : "выключен"));
                }
            }

            if (message.TryGetProperty("sound", out JsonElement soundElement) &&
                (soundElement.ValueKind == JsonValueKind.True || soundElement.ValueKind == JsonValueKind.False))
            {
                bool sound = soundElement.GetBoolean();

                if (sound != _settings.SoundEnabled)
                {
                    _settings.SoundEnabled = sound;
                    notes.Add("звуки " + (sound ? "включены" : "выключены"));
                }
            }

            if (message.TryGetProperty("mini", out JsonElement miniElement) &&
                (miniElement.ValueKind == JsonValueKind.True || miniElement.ValueKind == JsonValueKind.False))
            {
                bool mini = miniElement.GetBoolean();

                if (mini != _settings.MiniOverlay)
                {
                    _settings.MiniOverlay = mini;
                    _settings.MiniOverlayAsked = true;
                    notes.Add("мини-оверлей " + (mini ? "включён" : "выключен"));

                    if (mini)
                    {
                        ShowMiniOverlay();
                    }
                    else
                    {
                        CloseMiniOverlay();
                    }

                    PostToUi(new { type = "miniState", on = mini });
                }
            }

            if (message.TryGetProperty("autorun", out JsonElement autorunElement) &&
                (autorunElement.ValueKind == JsonValueKind.True || autorunElement.ValueKind == JsonValueKind.False))
            {
                bool want = autorunElement.GetBoolean();

                if (want != SelfStartup.IsEnabled())
                {
                    string? error = await Task.Run(() => SelfStartup.Set(want));

                    if (error is null)
                    {
                        _settings.RunAtStartup = want;
                        notes.Add("автозапуск " + (want ? "включён" : "выключен"));
                    }
                    else
                    {
                        notes.Add("автозапуск не изменился: " + error);
                    }
                }
            }

            string? saveError = SettingsStore.Save(_settings);

            if (saveError is not null)
            {
                PostToUi(new { type = "setResult", warn = true, text = "Не удалось сохранить: " + saveError });
                return;
            }

            if (notes.Count > 0)
            {
                AppLog.Write("Настройки", string.Join(", ", notes));
            }

            PostToUi(new
            {
                type = "setResult",
                warn = false,
                text = notes.Count > 0 ? "Сохранено — " + string.Join(", ", notes) : "Настройки сохранены"
            });

            await SendSettingsAsync();
        }

        private static string AccentName(string accent)
        {
            switch (accent)
            {
                case "orange": return "оранжевый";
                case "yellow": return "жёлтый";
                case "green": return "зелёный";
                case "cyan": return "голубой";
                case "blue": return "синий";
                case "purple": return "фиолетовый";
                default: return "красный";
            }
        }

        private async Task ClearLogAsync()
        {
            string? error = await Task.Run(() => AppLog.Clear());

            PostToUi(new
            {
                type = "setResult",
                warn = error is not null,
                text = error is null ? "Журнал очищен" : "Не удалось очистить: " + error
            });

            await SendSettingsAsync();
        }

        private async Task MakeRestorePointAsync()
        {
            if (_setBusy)
            {
                return;
            }

            _setBusy = true;

            try
            {
                PostToUi(new { type = "setBusy", busy = true, text = "Создаю точку восстановления…" });

                OptResult result = await RestorePointManager.CreateAsync();

                AppLog.Write("Точка восстановления", result.Ok ? "создана" : result.Text);

                PostToUi(new { type = "setResult", warn = !result.Ok, text = result.Text });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "setResult", warn = true, text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _setBusy = false;
                await SendSettingsAsync();
            }
        }

        /// <summary>
        /// Спрашивает у GitHub последний релиз и сравнивает с нашей версией.
        /// Раньше кнопка всегда писала «у вас последняя версия», ничего не проверяя.
        /// </summary>
        private void CheckUpdates()
        {
            _ = CheckUpdatesAsync();
        }

        private async Task CheckUpdatesAsync()
        {
            if (AppRepo.Length == 0)
            {
                PostToUi(new { type = "setResult", warn = false, text = "Версия " + AppVersion() });
                return;
            }

            PostToUi(new { type = "setResult", warn = false, text = "Спрашиваю GitHub…" });

            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.Timeout = TimeSpan.FromSeconds(12);

                // GitHub не отвечает без User-Agent.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PCOptimizer/" + AppVersion());
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                // Адрес именно api.github.com: обычная ссылка github.com/.../releases/latest
                // отдаёт HTML-страницу, из которой номер версии не вытащить.
                string api = AppRepo.TrimEnd('/')
                    .Replace("https://github.com/", "https://api.github.com/repos/", StringComparison.OrdinalIgnoreCase)
                    + "/releases/latest";

                using var response = await client.GetAsync(api);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    PostToUi(new
                    {
                        type = "setResult",
                        warn = false,
                        text = "На GitHub пока нет релизов — у вас версия " + AppVersion()
                    });
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    PostToUi(new
                    {
                        type = "setResult",
                        warn = true,
                        text = "GitHub ответил кодом " + (int)response.StatusCode
                    });
                    return;
                }

                string json = await response.Content.ReadAsStringAsync();
                using JsonDocument document = JsonDocument.Parse(json);

                string tag = document.RootElement.TryGetProperty("tag_name", out JsonElement tagElement)
                    ? tagElement.GetString() ?? ""
                    : "";

                // Из тега вида «v1.0.0» или «release-1.2» достаём сами цифры версии.
                Match numbers = Regex.Match(tag, @"\d+(\.\d+)*");
                tag = numbers.Success ? numbers.Value : tag.TrimStart('v', 'V');

                string current = AppVersion();

                if (tag.Length == 0)
                {
                    PostToUi(new { type = "setResult", warn = true, text = "Не понял ответ GitHub" });
                    return;
                }

                if (CompareVersions(tag, current) > 0)
                {
                    PostToUi(new
                    {
                        type = "setResult",
                        warn = true,
                        text = "Вышла новая версия " + tag + ", у вас " + current
                            + " — скачать: " + AppRepo + "/releases"
                    });
                }
                else
                {
                    PostToUi(new
                    {
                        type = "setResult",
                        warn = false,
                        text = "У вас последняя версия — " + current
                    });
                }
            }
            catch (Exception ex)
            {
                PostToUi(new
                {
                    type = "setResult",
                    warn = true,
                    text = "Не удалось проверить обновления: " + ex.Message
                });
            }
        }

        /// <summary>Сравнивает версии по числам, чтобы 1.10 было новее 1.9.</summary>
        private static int CompareVersions(string left, string right)
        {
            string[] a = left.Split('.');
            string[] b = right.Split('.');
            int count = Math.Max(a.Length, b.Length);

            for (int i = 0; i < count; i++)
            {
                int x = i < a.Length && int.TryParse(a[i], out int parsedA) ? parsedA : 0;
                int y = i < b.Length && int.TryParse(b[i], out int parsedB) ? parsedB : 0;

                if (x != y)
                {
                    return x.CompareTo(y);
                }
            }

            return 0;
        }

        private async Task SendBrowsersAsync()
        {
            try
            {
                var browsers = await Task.Run(() => GameScanner.FindBrowsers());
                PostToUi(new { type = "browserList", items = browsers });
            }
            catch
            {
                PostToUi(new { type = "browserList", items = Array.Empty<BackgroundProcess>() });
            }
        }

        private async Task ScanGamesAsync()
        {
            if (_gameBusy)
            {
                return;
            }

            _gameBusy = true;

            try
            {
                PostToUi(new { type = "gameStatus", busy = true, text = "Ищу запущенные игры…" });

                var games = await Task.Run(() => GameScanner.FindGames());
                var processes = await Task.Run(() => GameScanner.FindClosable());

                PostToUi(new
                {
                    type = "gameState",
                    games,
                    processes,
                    admin = HardwareMonitor.IsElevated()
                });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "gameStep", icon = "fail", title = "Поиск игры", text = ex.Message });
            }
            finally
            {
                _gameBusy = false;
                PostToUi(new { type = "gameStatus", busy = false, text = "" });
            }
        }

        private async Task BoostGameAsync(JsonElement message)
        {
            if (_gameBusy)
            {
                return;
            }

            _gameBusy = true;

            try
            {
                // 1. Приоритет игры.
                int gamePid = 0;
                if (message.TryGetProperty("gamePid", out JsonElement pidElement) &&
                    pidElement.TryGetInt32(out int parsedPid))
                {
                    gamePid = parsedPid;
                }

                if (gamePid > 0)
                {
                    PostToUi(new { type = "gameBusy", busy = true, text = "Поднимаю приоритет игры…" });
                    string? priorityError = await Task.Run(() => PriorityManager.Set(gamePid, true));

                    PostToUi(new
                    {
                        type = "gameStep",
                        icon = priorityError is null ? "ok" : "fail",
                        title = "Приоритет игры",
                        text = priorityError is null ? "поднят до «Высокий»" : priorityError
                    });
                }
                else
                {
                    PostToUi(new
                    {
                        type = "gameStep",
                        icon = "skip",
                        title = "Приоритет игры",
                        text = "игра не выбрана — сначала нажмите «Найти игру»"
                    });
                }

                // 2. Браузеры — только те, что пользователь отметил галочками.
                int closed = 0;
                int forced = 0;
                int failed = 0;

                if (message.TryGetProperty("browserPids", out JsonElement browserPids) &&
                    browserPids.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in browserPids.EnumerateArray())
                    {
                        if (!item.TryGetInt32(out int browserPid))
                        {
                            continue;
                        }

                        PostToUi(new { type = "gameBusy", busy = true, text = "Закрываю браузеры…" });

                        int targetBrowser = browserPid;
                        var browserResult = await Task.Run(() => ProcessCloser.Close(targetBrowser, true));

                        if (browserResult.Closed)
                        {
                            closed++;

                            if (browserResult.Forced)
                            {
                                forced++;
                            }
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }

                // 3. Остальные отмеченные процессы.

                if (message.TryGetProperty("pids", out JsonElement pids) && pids.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in pids.EnumerateArray())
                    {
                        if (!item.TryGetInt32(out int pid))
                        {
                            continue;
                        }

                        PostToUi(new { type = "gameBusy", busy = true, text = "Закрываю лишние программы…" });

                        int target = pid;
                        var result = await Task.Run(() => ProcessCloser.Close(target, false));

                        if (result.Closed)
                        {
                            closed++;
                            if (result.Forced)
                            {
                                forced++;
                            }
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }

                string closeText;
                if (closed == 0)
                {
                    closeText = "нечего закрывать";
                }
                else
                {
                    closeText = "закрыто: " + closed;

                    if (forced > 0)
                    {
                        closeText += " (принудительно: " + forced + ")";
                    }

                    if (failed > 0)
                    {
                        closeText += ", не удалось: " + failed;
                    }
                }

                PostToUi(new
                {
                    type = "gameStep",
                    icon = closed > 0 ? "ok" : "skip",
                    title = "Лишние программы",
                    text = closeText
                });

                // 3. Оперативная память.
                PostToUi(new { type = "gameBusy", busy = true, text = "Освобождаю память…" });
                var memory = await Task.Run(() => MemoryCleaner.Purge());

                PostToUi(new
                {
                    type = "gameStep",
                    icon = memory.Error is null ? "ok" : "fail",
                    title = "Оперативная память",
                    text = memory.Error is null
                        ? "освобождено " + memory.FreedMb + " МБ кэша"
                        : memory.Error
                });

                // 4. Game Bar и Game DVR.
                PostToUi(new { type = "gameBusy", busy = true, text = "Отключаю Game Bar…" });
                string? gameBarError = await Task.Run(() => GameBarManager.Disable());

                PostToUi(new
                {
                    type = "gameStep",
                    icon = gameBarError is null ? "ok" : "fail",
                    title = "Game Bar и запись",
                    text = gameBarError is null ? "отключены" : gameBarError
                });

                // 5. Схема питания.
                PostToUi(new { type = "gameBusy", busy = true, text = "Переключаю питание…" });
                string? powerError = await Task.Run(() => PowerPlanManager.Set(true));

                PostToUi(new
                {
                    type = "gameStep",
                    icon = powerError is null ? "ok" : "fail",
                    title = "Схема питания",
                    text = powerError is null ? "включена «Высокая производительность»" : powerError
                });

                AppLog.Write("Игры",
                    "оптимизация, закрыто программ: " + closed +
                    (gamePid > 0 ? ", приоритет поднят" : ""));

                PostToUi(new { type = "gameDone", text = "Готово. Можно играть" });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "gameDone", text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _gameBusy = false;
                PostToUi(new { type = "gameBusy", busy = false, text = "" });
            }
        }

        private async Task RestoreGameAsync()
        {
            if (_gameBusy)
            {
                return;
            }

            _gameBusy = true;

            try
            {
                PostToUi(new { type = "gameBusy", busy = true, text = "Возвращаю настройки…" });

                // Приоритет возвращаем, только если игра ещё запущена.
                var games = await Task.Run(() => GameScanner.FindGames());

                if (games.Count > 0)
                {
                    int pid = games[0].Pid;
                    string? priorityError = await Task.Run(() => PriorityManager.Set(pid, false));

                    PostToUi(new
                    {
                        type = "gameStep",
                        icon = priorityError is null ? "ok" : "skip",
                        title = "Приоритет игры",
                        text = priorityError is null ? "возвращён в «Обычный»" : priorityError
                    });
                }
                else
                {
                    PostToUi(new
                    {
                        type = "gameStep",
                        icon = "skip",
                        title = "Приоритет игры",
                        text = "игра не запущена — возвращать нечего"
                    });
                }

                string? gameBarError = await Task.Run(() => GameBarManager.Restore());

                PostToUi(new
                {
                    type = "gameStep",
                    icon = gameBarError is null ? "ok" : "fail",
                    title = "Game Bar и запись",
                    text = gameBarError is null ? "включены обратно" : gameBarError
                });

                string? powerError = await Task.Run(() => PowerPlanManager.Set(false));

                PostToUi(new
                {
                    type = "gameStep",
                    icon = powerError is null ? "ok" : "fail",
                    title = "Схема питания",
                    text = powerError is null ? "возвращена «Сбалансированная»" : powerError
                });

                AppLog.Write("Игры", "настройки возвращены");

                PostToUi(new { type = "gameDone", text = "Готово. Настройки возвращены" });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "gameDone", text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _gameBusy = false;
                PostToUi(new { type = "gameBusy", busy = false, text = "" });
            }
        }

        // ---------------- Авто-оптимизация ----------------

        private async Task RunAutoOptimizationAsync()
        {
            if (_optBusy)
            {
                return;
            }

            _optBusy = true;

            try
            {
                long freed = await AutoOptimizer.RunAsync(
                    _cleanup,
                    (icon, title, text) => PostToUi(new { type = "autoStep", icon, title, text }),
                    (busy, text) => PostToUi(new { type = "autoBusy", busy, text }));

                AppLog.Write("Авто-оптимизация", "освобождено " + AutoOptimizer.FormatSize(freed));

                StatsStore.AddFreed(freed);
                StatsStore.AddOptimization();

                PostToUi(new
                {
                    type = "autoDone",
                    freed,
                    freedText = AutoOptimizer.FormatSize(freed),
                    stats = StatsStore.Snapshot()
                });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "autoDone", freed = 0L, freedText = "", error = ex.Message });
            }
            finally
            {
                _optBusy = false;
                PostToUi(new { type = "autoBusy", busy = false, text = "" });
            }
        }

        // ---------------- Оптимизация ----------------

        private static bool IsCriticalService(string name)
        {
            foreach (var item in ServiceCatalog.Items)
            {
                if (item.Name == name && item.Critical)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Читает состояние системы и отправляет его в интерфейс.</summary>
        private async Task SendOptStateAsync()
        {
            if (_optBusy)
            {
                return;
            }

            _optBusy = true;

            try
            {
                PostToUi(new { type = "optBusy", busy = true, text = "Читаю состояние системы…" });

                var services = await Task.Run(() => ServiceManager.Read());
                var startup = await Task.Run(() => StartupManager.Read());

                bool globalDisabled = false;
                var background = await Task.Run(() => BackgroundAppsManager.Read(out globalDisabled));

                var power = await Task.Run(() => PowerPlanManager.ReadActive());

                PostToUi(new
                {
                    type = "optState",
                    services,
                    startup,
                    background,
                    backgroundGlobal = globalDisabled,
                    power,
                    visualDisabled = VisualEffectsManager.IsDisabled(),
                    storageEnabled = StorageSenseManager.IsEnabled(),
                    elevated = HardwareMonitor.IsElevated()
                });
            }
            catch (Exception ex)
            {
                PostToUi(new
                {
                    type = "optResult",
                    ok = false,
                    warn = true,
                    text = "Не удалось прочитать состояние: " + ex.Message
                });
            }
            finally
            {
                _optBusy = false;
                PostToUi(new { type = "optBusy", busy = false, text = "" });
            }
        }

        /// <summary>Применяет отмеченные галочки: службы, автозагрузка, фоновые приложения.</summary>
        private async Task ApplyOptimizationAsync(JsonElement message)
        {
            if (_optBusy)
            {
                return;
            }

            _optBusy = true;
            var notes = new List<string>();
            int changed = 0;

            try
            {
                if (message.TryGetProperty("restoreFirst", out JsonElement restoreFirst) &&
                    restoreFirst.ValueKind == JsonValueKind.True)
                {
                    PostToUi(new { type = "optBusy", busy = true, text = "Создаю точку восстановления…" });
                    OptResult point = await RestorePointManager.CreateAsync();
                    PostToUi(new { type = "optResult", ok = point.Ok, warn = point.Warn, text = point.Text });
                }

                if (message.TryGetProperty("services", out JsonElement services))
                {
                    foreach (JsonElement item in services.EnumerateArray())
                    {
                        string name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                        bool disable = item.TryGetProperty("disable", out JsonElement d) && d.GetBoolean();

                        if (name.Length == 0)
                        {
                            continue;
                        }

                        // Критичные службы не трогаем ни при каких условиях.
                        if (IsCriticalService(name))
                        {
                            notes.Add(name + " — критичная служба, пропущена");
                            continue;
                        }

                        PostToUi(new { type = "optBusy", busy = true, text = "Служба " + name + "…" });

                        string serviceName = name;
                        bool shouldDisable = disable;
                        string? error = await Task.Run(
                            () => ServiceManager.SetStartMode(serviceName, shouldDisable));

                        if (error is null)
                        {
                            changed++;
                        }
                        else
                        {
                            notes.Add(serviceName + ": " + error);
                        }
                    }
                }

                if (message.TryGetProperty("startup", out JsonElement startup))
                {
                    foreach (JsonElement item in startup.EnumerateArray())
                    {
                        string id = item.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? "" : "";
                        bool enabled = item.TryGetProperty("enabled", out JsonElement e) && e.GetBoolean();

                        if (id.Length == 0)
                        {
                            continue;
                        }

                        string startupId = id;
                        bool shouldEnable = enabled;
                        string? error = await Task.Run(() => StartupManager.SetEnabled(startupId, shouldEnable));

                        if (error is null)
                        {
                            changed++;
                        }
                        else
                        {
                            notes.Add(id + ": " + error);
                        }
                    }
                }

                if (message.TryGetProperty("background", out JsonElement background))
                {
                    foreach (JsonElement item in background.EnumerateArray())
                    {
                        string package = item.TryGetProperty("package", out JsonElement p) ? p.GetString() ?? "" : "";
                        bool enabled = item.TryGetProperty("enabled", out JsonElement e) && e.GetBoolean();

                        if (package.Length == 0)
                        {
                            continue;
                        }

                        string packageName = package;
                        bool shouldEnable = enabled;
                        string? error = await Task.Run(
                            () => BackgroundAppsManager.SetEnabled(packageName, shouldEnable));

                        if (error is null)
                        {
                            changed++;
                        }
                        else
                        {
                            notes.Add(package + ": " + error);
                        }
                    }
                }

                if (message.TryGetProperty("backgroundGlobal", out JsonElement globalElement) &&
                    (globalElement.ValueKind == JsonValueKind.True || globalElement.ValueKind == JsonValueKind.False))
                {
                    bool disabled = globalElement.GetBoolean();
                    string? error = await Task.Run(() => BackgroundAppsManager.SetGlobalDisabled(disabled));

                    if (error is null)
                    {
                        changed++;
                    }
                    else
                    {
                        notes.Add("общий запрет: " + error);
                    }
                }

                string text = changed > 0
                    ? "Готово. Изменено пунктов: " + changed
                    : "Готово. Менять было нечего";

                if (notes.Count > 0)
                {
                    text += ". " + string.Join("; ", notes.Take(3));
                }

                AppLog.Write("Оптимизация", text);

                PostToUi(new { type = "optResult", ok = true, warn = notes.Count > 0, text });

                _optBusy = false;
                await SendOptStateAsync();
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "optResult", ok = false, warn = true, text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _optBusy = false;
                PostToUi(new { type = "optBusy", busy = false, text = "" });
            }
        }

        /// <summary>Одиночные действия: питание, эффекты, контроль памяти, точка восстановления.</summary>
        private async Task RunOptActionAsync(string name, JsonElement message)
        {
            if (_optBusy || name.Length == 0)
            {
                return;
            }

            _optBusy = true;

            try
            {
                PostToUi(new { type = "optBusy", busy = true, text = "Применяю…" });

                if (name != "restore" &&
                    message.TryGetProperty("restoreFirst", out JsonElement wantRestore) &&
                    wantRestore.ValueKind == JsonValueKind.True)
                {
                    PostToUi(new { type = "optBusy", busy = true, text = "Создаю точку восстановления…" });
                    OptResult point = await RestorePointManager.CreateAsync();
                    PostToUi(new { type = "optResult", ok = point.Ok, warn = point.Warn, text = point.Text });
                }

                string? error = null;
                string done = "";

                switch (name)
                {
                    case "powerHigh":
                        error = await Task.Run(() => PowerPlanManager.Set(true));
                        done = "включена схема «Высокая производительность»";
                        break;

                    case "powerBalanced":
                        error = await Task.Run(() => PowerPlanManager.Set(false));
                        done = "включена схема «Сбалансированная»";
                        break;

                    case "visualOff":
                        error = await Task.Run(() => VisualEffectsManager.Disable());
                        done = "визуальные эффекты отключены";
                        break;

                    case "visualOn":
                        error = await Task.Run(() => VisualEffectsManager.Restore());
                        done = "визуальные эффекты возвращены";
                        break;

                    case "storageOn":
                        error = await Task.Run(() => StorageSenseManager.Set(true));
                        done = "контроль памяти включён";
                        break;

                    case "storageOff":
                        error = await Task.Run(() => StorageSenseManager.Set(false));
                        done = "контроль памяти выключен";
                        break;

                    case "restore":
                        OptResult point = await RestorePointManager.CreateAsync();
                        PostToUi(new { type = "optResult", ok = point.Ok, warn = point.Warn, text = point.Text });
                        break;

                    default:
                        return;
                }

                if (name != "restore")
                {
                    PostToUi(new
                    {
                        type = "optResult",
                        ok = error is null,
                        warn = error is not null,
                        text = error is null ? "Готово: " + done : "Не получилось: " + error
                    });
                }

                _optBusy = false;
                await SendOptStateAsync();
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "optResult", ok = false, warn = true, text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _optBusy = false;
                PostToUi(new { type = "optBusy", busy = false, text = "" });
            }
        }

        // ---------------- Очистка диска ----------------

        /// <summary>Отправляет интерфейсу список пунктов очистки.</summary>
        private void SendCleanupList()
        {
            var items = new List<object>();

            foreach (CleanupTarget target in _cleanup.Targets)
            {
                items.Add(new
                {
                    id = target.Id,
                    title = target.Title,
                    desc = target.Description,
                    admin = target.NeedsAdmin,
                    recycle = target.IsRecycleBin,
                    paths = target.Folders.ToArray()
                });
            }

            PostToUi(new
            {
                type = "cleanupList",
                items,
                elevated = HardwareMonitor.IsElevated()
            });
        }

        /// <summary>
        /// Проходит по всем пунктам: scanOnly = true — только считает размеры,
        /// false — удаляет. Занятые и защищённые файлы пропускаются.
        /// </summary>
        private async Task RunCleanupAsync(bool scanOnly)
        {
            if (_cleanupBusy)
            {
                return;
            }

            _cleanupBusy = true;

            // Интерфейс по этому сообщению включает анимацию: свою для чистки,
            // свою для анализа. Работает и от кнопки, и от горячей клавиши.
            PostToUi(new { type = "cleanupStart", phase = scanOnly ? "scan" : "clean" });

            long freedTotal = 0;
            int filesTotal = 0;
            int skippedTotal = 0;

            try
            {
                foreach (CleanupTarget target in _cleanup.Targets)
                {
                    PostToUi(new
                    {
                        type = "cleanupStatus",
                        busy = true,
                        text = (scanOnly ? "Считаю: " : "Чищу: ") + target.Title
                    });

                    CleanupTarget current = target;
                    CleanupResult result = await Task.Run(
                        () => scanOnly ? _cleanup.Measure(current) : _cleanup.Clean(current));

                    if (!scanOnly)
                    {
                        freedTotal += result.Freed;
                        filesTotal += result.Files;
                        skippedTotal += result.Skipped;
                    }

                    if (scanOnly)
                    {
                        PostToUi(new
                        {
                            type = "cleanupItem",
                            id = current.Id,
                            size = result.Size,
                            files = result.Files
                        });
                    }
                    else
                    {
                        PostToUi(new
                        {
                            type = "cleanupFreed",
                            id = current.Id,
                            freed = result.Freed,
                            files = result.Files,
                            skipped = result.Skipped
                        });
                    }
                }

                if (!scanOnly)
                {
                    StatsStore.AddFreed(freedTotal);
                    StatsStore.AddOptimization();

                    AppLog.Write("Очистка",
                        "освобождено " + AutoOptimizer.FormatSize(freedTotal) +
                        ", файлов " + filesTotal +
                        (skippedTotal > 0 ? ", пропущено " + skippedTotal : ""));
                }

                PostToUi(new { type = "cleanupStatus", busy = false, text = "" });
                PostToUi(new
                {
                    type = "cleanupDone",
                    phase = scanOnly ? "scan" : "clean",
                    freed = freedTotal,
                    files = filesTotal,
                    freedText = AutoOptimizer.FormatSize(freedTotal),
                    stats = StatsStore.Snapshot()
                });
            }
            catch (Exception ex)
            {
                PostToUi(new { type = "cleanupStatus", busy = false, text = "Ошибка: " + ex.Message });
            }
            finally
            {
                _cleanupBusy = false;
            }
        }

        private async void RestartAsAdministrator()
        {
            _closing = true;

            // Сначала отпускаем папку данных WebView2, иначе новый запуск
            // не сможет её открыть и покажет «ресурс занят».
            try
            {
                if (_webView is not null)
                {
                    Controls.Remove(_webView);
                    _webView.Dispose();
                    _webView = null;
                }
            }
            catch
            {
                // Уже неважно — процесс всё равно завершается.
            }

            _webReady = false;
            await Task.Delay(400);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось запросить права администратора.\n\n" + ex.Message +
                    "\n\nЗакройте программу и запустите её вручную: " +
                    "правый клик по PCOptimizer.exe → «Запуск от имени администратора».",
                    "PCOptimizer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            Close();
        }

        /// <summary>Перетаскивание окна за заголовок, нарисованный в HTML.</summary>
        private void StartWindowDrag()
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(
                Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
        }

        // ---------------- Завершение работы ----------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                if (_webView?.CoreWebView2 is not null)
                {
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                }
            }
            catch
            {
                // Окно уже уничтожается.
            }

            CloseOverlay();
            CloseMiniOverlay();
            UnregisterHotkeys();

            try
            {
                if (_tray is not null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch
            {
                // Значок уже убран.
            }

            StatsStore.Save();
            _monitor.Dispose();
            _readLock.Dispose();

            base.OnFormClosed(e);
        }
    }
}
