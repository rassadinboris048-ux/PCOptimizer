using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PCOptimizer
{
    // ==================== Модели ====================

    internal sealed class ServiceEntry
    {
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string About { get; set; } = "";
        public bool Critical { get; set; }
        public string StartMode { get; set; } = "нет";
        public bool Running { get; set; }
    }

    internal sealed class StartupEntry
    {
        public string Id { get; set; } = "";      // источник|имя
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Source { get; set; } = "";
        public bool Enabled { get; set; }
    }

    internal sealed class BackgroundEntry
    {
        public string Package { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Enabled { get; set; }
    }

    internal sealed class OptResult
    {
        public bool Ok { get; set; } = true;
        public string Text { get; set; } = "";
        public bool Warn { get; set; }
    }

    // ==================== Службы ====================

    internal static class ServiceCatalog
    {
        /// <summary>
        /// Список подобран вручную: только те службы, которые действительно
        /// можно настраивать без риска для Windows. Критичные показаны
        /// отдельно и заблокированы — на них срабатывает предупреждение.
        /// </summary>
        public static readonly (string Name, string Title, string About, bool Critical)[] Items =
        {
            ("WSearch", "Windows Search", "Индексация файлов. Отключение ускоряет систему, но поиск станет медленнее", false),
            ("SysMain", "SysMain (Superfetch)", "Предзагрузка программ в память. На SSD пользы почти нет", false),
            ("wuauserv", "Центр обновления Windows", "Доставка обновлений. Надолго отключать не стоит", false),
            ("Spooler", "Диспетчер печати", "Обслуживает печать. Нет принтера — можно отключить", false),
            ("Fax", "Факс", "Отправка факсов. Нужна крайне редко", false),
            ("RemoteRegistry", "Удалённый реестр", "Доступ к реестру по сети. Обычно не нужен", false),
            ("WerSvc", "Отчёты об ошибках", "Сбор отчётов о сбоях программ", false),
            ("DiagTrack", "Телеметрия", "Сбор диагностических данных для Microsoft", false),
            ("dmwappushservice", "Push-сообщения WAP", "Служебная телеметрия устройства", false),
            ("XblAuthManager", "Xbox Live: вход", "Нужна только для игр Xbox", false),
            ("XboxNetApiSvc", "Xbox Live: сеть", "Нужна только для игр Xbox", false),
            ("bthserv", "Поддержка Bluetooth", "Нужна, только если пользуетесь Bluetooth", false),
            ("wisvc", "Предварительная оценка Windows", "Участие в программе тестирования сборок", false),
            ("RetailDemo", "Демонстрация магазина", "Режим витрины. Дома не нужен", false),
            ("MapsBroker", "Скачанные карты", "Офлайн-карты. Дома обычно не нужен", false),
            ("PcaSvc", "Помощник по совместимости", "Следит за старыми программами", false),
            ("seclogon", "Вторичный вход в систему", "Запуск программ от другого пользователя", false),
            ("TabletInputService", "Сенсорная клавиатура", "Нужна только на сенсорных экранах", false),
            ("WdiServiceHost", "Узел службы диагностики", "Диагностика неполадок", false),
            ("WdiSystemHost", "Системный узел диагностики", "Диагностика неполадок", false),

            ("RpcSs", "Удалённый вызов процедур (RPC)", "Основа работы Windows. Отключение ломает систему", true),
            ("DcomLaunch", "Модуль запуска DCOM", "Без него не стартуют почти все службы", true),
            ("RpcEptMapper", "Сопоставитель конечных точек RPC", "Часть механизма RPC", true),
            ("PlugPlay", "Plug and Play", "Определение устройств. Критично", true),
            ("MpsSvc", "Брандмауэр Защитника Windows", "Защита сети. Критично", true),
            ("EventLog", "Журнал событий Windows", "Системные журналы. Критично", true),
            ("Winmgmt", "Инструментарий WMI", "Управление системой. Критично", true),
            ("Power", "Служба питания", "Управление электропитанием. Критично", true),
            ("LSM", "Диспетчер сеансов", "Работа сеансов Windows. Критично", true),
            ("SamSs", "Диспетчер учётных записей", "Безопасность учётных записей. Критично", true)
        };
    }

    internal static class ServiceManager
    {
        public static List<ServiceEntry> Read()
        {
            var list = new List<ServiceEntry>();

            foreach (var item in ServiceCatalog.Items)
            {
                // В разных редакциях Windows набор служб отличается: чего-то
                // может просто не быть. Такие пункты не показываем вовсе,
                // иначе человек будет искать службу, которой нет.
                if (!Exists(item.Name))
                {
                    continue;
                }

                var entry = new ServiceEntry
                {
                    Name = item.Name,
                    Title = item.Title,
                    About = item.About,
                    Critical = item.Critical,
                    StartMode = ReadStartMode(item.Name),
                    Running = IsRunning(item.Name)
                };

                list.Add(entry);
            }

            return list;
        }

        private static string ReadStartMode(string name)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + name);

                object? value = key?.GetValue("Start");
                if (value is int start)
                {
                    return start switch
                    {
                        2 => "авто",
                        3 => "вручную",
                        4 => "отключена",
                        _ => "особый"
                    };
                }
            }
            catch
            {
                // нет доступа — покажем «нет»
            }

            return "нет";
        }

        public static bool Exists(string name)
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + name);
                return key is not null;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsRunning(string name)
        {
            IntPtr manager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                IntPtr service = NativeMethods.OpenService(manager, name, NativeMethods.SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    return QueryState(service) == NativeMethods.SERVICE_RUNNING;
                }
                finally
                {
                    NativeMethods.CloseServiceHandle(service);
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(manager);
            }
        }

        private static uint QueryState(IntPtr service)
        {
            int size = Marshal.SizeOf<NativeMethods.SERVICE_STATUS_PROCESS>();
            IntPtr buffer = Marshal.AllocHGlobal(size);

            try
            {
                if (!NativeMethods.QueryServiceStatusEx(
                        service, NativeMethods.SC_STATUS_PROCESS_INFO, buffer, size, out _))
                {
                    return 0;
                }

                var status = Marshal.PtrToStructure<NativeMethods.SERVICE_STATUS_PROCESS>(buffer);
                return status.dwCurrentState;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Меняет режим запуска службы. Возвращает текст ошибки или null при успехе.
        /// </summary>
        public static string? SetStartMode(string name, bool disabled)
        {
            // Никогда не трогаем службу, которой нет: на другой версии
            // Windows это другой набор, и ошибка была бы непонятной.
            if (!Exists(name))
            {
                return "служба «" + name + "» не найдена в этой системе";
            }

            return SetStartModeCore(name, disabled);
        }

        private static string? SetStartModeCore(string name, bool disabled)
        {
            uint startType = disabled ? NativeMethods.SERVICE_DISABLED : NativeMethods.SERVICE_DEMAND_START;

            IntPtr manager = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero)
            {
                return "нет доступа к диспетчеру служб (нужны права администратора)";
            }

            try
            {
                IntPtr service = NativeMethods.OpenService(
                    manager, name,
                    NativeMethods.SERVICE_CHANGE_CONFIG | NativeMethods.SERVICE_QUERY_STATUS |
                    NativeMethods.SERVICE_STOP | NativeMethods.SERVICE_START);

                if (service == IntPtr.Zero)
                {
                    return "служба не найдена или нет прав";
                }

                try
                {
                    bool ok = NativeMethods.ChangeServiceConfig(
                        service,
                        NativeMethods.SERVICE_NO_CHANGE,
                        startType,
                        NativeMethods.SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null);

                    if (!ok)
                    {
                        return "Windows отказалась менять режим запуска";
                    }

                    // Отключаем службу — значит, надо её ещё и остановить.
                    if (disabled)
                    {
                        Stop(service);
                    }

                    return null;
                }
                finally
                {
                    NativeMethods.CloseServiceHandle(service);
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(manager);
            }
        }

        private static void Stop(IntPtr service)
        {
            var status = new NativeMethods.SERVICE_STATUS();

            // Просим остановиться и ждём до 10 секунд.
            NativeMethods.ControlService(service, NativeMethods.SERVICE_CONTROL_STOP, ref status);

            for (int i = 0; i < 20; i++)
            {
                if (QueryState(service) == NativeMethods.SERVICE_STOPPED)
                {
                    return;
                }

                Thread.Sleep(500);
            }
        }
    }

    // ==================== Автозагрузка ====================

    internal static class StartupManager
    {
        private const string BackupKey = @"Software\PCOptimizer\DisabledStartup";

        private static readonly (string Source, string Path)[] Sources =
        {
            ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
            ("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
            ("HKLM32", @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run")
        };

        public static List<StartupEntry> Read()
        {
            var list = new List<StartupEntry>();

            foreach ((string source, string path) in Sources)
            {
                RegistryKey? root = source == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;

                using RegistryKey? key = root.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (string name in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    list.Add(new StartupEntry
                    {
                        Id = source + "|" + name,
                        Name = name,
                        Command = key.GetValue(name)?.ToString() ?? "",
                        Source = source == "HKCU" ? "пользователь" : "все пользователи",
                        Enabled = true
                    });
                }
            }

            // Уже отключённые ранее — показываем с пометкой.
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupKey);
            if (backup is not null)
            {
                foreach (string id in backup.GetValueNames())
                {
                    int separator = id.IndexOf('|');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    list.Add(new StartupEntry
                    {
                        Id = id,
                        Name = id[(separator + 1)..],
                        Command = backup.GetValue(id)?.ToString() ?? "",
                        Source = id[..separator] == "HKCU" ? "пользователь" : "все пользователи",
                        Enabled = false
                    });
                }
            }

            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Отключает или включает элемент автозагрузки.</summary>
        public static string? SetEnabled(string id, bool enabled)
        {
            int separator = id.IndexOf('|');
            if (separator <= 0)
            {
                return "непонятный идентификатор";
            }

            string source = id[..separator];
            string name = id[(separator + 1)..];

            string? path = null;
            foreach ((string s, string p) in Sources)
            {
                if (s == source)
                {
                    path = p;
                    break;
                }
            }

            if (path is null)
            {
                return "неизвестный источник автозагрузки";
            }

            RegistryKey root = source == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;

            try
            {
                if (enabled)
                {
                    // Возвращаем обратно из нашей резервной копии.
                    using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupKey);
                    object? command = backup?.GetValue(id);

                    if (command is null)
                    {
                        return "не нашёл сохранённую запись";
                    }

                    using RegistryKey? target = root.OpenSubKey(path, writable: true);
                    if (target is null)
                    {
                        return "нет доступа к разделу автозагрузки";
                    }

                    target.SetValue(name, command, RegistryValueKind.String);
                    backup?.Close();

                    using RegistryKey? forDelete = Registry.CurrentUser.OpenSubKey(BackupKey, writable: true);
                    forDelete?.DeleteValue(id, throwOnMissingValue: false);

                    return null;
                }
                else
                {
                    using RegistryKey? source_key = root.OpenSubKey(path, writable: true);
                    object? command = source_key?.GetValue(name);

                    if (command is null)
                    {
                        return "запись уже отключена";
                    }

                    // Сначала сохраняем копию, потом удаляем — иначе можно потерять.
                    using (RegistryKey? backup = Registry.CurrentUser.CreateSubKey(BackupKey))
                    {
                        backup?.SetValue(id, command, RegistryValueKind.String);
                    }

                    source_key?.DeleteValue(name, throwOnMissingValue: false);
                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    // ==================== Фоновые приложения ====================

    internal static class BackgroundAppsManager
    {
        private const string KeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

        public static List<BackgroundEntry> Read(out bool globalDisabled)
        {
            globalDisabled = false;
            var list = new List<BackgroundEntry>();

            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (root is null)
            {
                return list;
            }

            globalDisabled = Convert.ToInt32(root.GetValue("GlobalUserDisabled") ?? 0) == 1;

            foreach (string package in root.GetSubKeyNames())
            {
                using RegistryKey? app = root.OpenSubKey(package);
                if (app is null)
                {
                    continue;
                }

                int disabled = Convert.ToInt32(app.GetValue("Disabled") ?? 0);

                list.Add(new BackgroundEntry
                {
                    Package = package,
                    Title = MakeTitle(package),
                    Enabled = disabled != 1
                });
            }

            list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Из «Microsoft.WindowsCalculator_8wekyb3d8bbwe» делает читаемое имя.</summary>
        private static string MakeTitle(string package)
        {
            int underscore = package.IndexOf('_');
            string name = underscore > 0 ? package[..underscore] : package;

            // Убираем служебные префиксы и точки, чтобы было читаемо.
            return name.Replace("Microsoft.", "").Replace('.', ' ');
        }

        public static string? SetEnabled(string package, bool enabled)
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
                if (root is null)
                {
                    return "нет доступа к настройкам фоновых приложений";
                }

                using RegistryKey? app = root.CreateSubKey(package);
                if (app is null)
                {
                    return "не удалось открыть приложение";
                }

                app.SetValue("Disabled", enabled ? 0 : 1, RegistryValueKind.DWord);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static string? SetGlobalDisabled(bool disabled)
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.CreateSubKey(KeyPath);
                root?.SetValue("GlobalUserDisabled", disabled ? 1 : 0, RegistryValueKind.DWord);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    // ==================== Схема питания ====================

    internal static class PowerPlanManager
    {
        private const string High = "SCHEME_MIN";
        private const string Balanced = "SCHEME_BALANCED";

        public static string ReadActive()
        {
            (int code, string output, _) = ConsoleRunner.RunSync("powercfg", "/getactivescheme");

            if (code != 0 || output.Length == 0)
            {
                return "неизвестно";
            }

            // Формат: "GUID схемы питания: <guid>  (Название)"
            int open = output.IndexOf('(');
            int close = output.LastIndexOf(')');

            if (open >= 0 && close > open)
            {
                return output.Substring(open + 1, close - open - 1).Trim();
            }

            return "неизвестно";
        }

        public static string? Set(bool highPerformance)
        {
            string scheme = highPerformance ? High : Balanced;

            (int code, _, string error) = ConsoleRunner.RunSync("powercfg", "/setactive " + scheme);

            if (code != 0)
            {
                return error.Length > 0 ? error : "powercfg вернул ошибку";
            }

            return null;
        }
    }

    // ==================== Визуальные эффекты ====================

    internal static class VisualEffectsManager
    {
        private const string BackupPath = @"Software\PCOptimizer\VisualEffectsBackup";
        private const string DesktopPath = @"Control Panel\Desktop";
        private const string ExplorerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
        private const string VisualFxPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
        private const string PersonalizePath =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static readonly (string Path, string Name)[] Watched =
        {
            (DesktopPath, "UserPreferencesMask"),
            (DesktopPath, "DragFullWindows"),
            (DesktopPath, "MenuShowDelay"),
            (DesktopPath, "FontSmoothing"),
            (VisualFxPath, "VisualFXSetting"),
            (ExplorerPath + @"\Advanced", "TaskbarAnimations"),
            (ExplorerPath + @"\Advanced", "ListviewAlphaSelect"),
            (PersonalizePath, "EnableTransparency")
        };

        public static bool IsDisabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(BackupPath);
            return key is not null;
        }

        public static string? Disable()
        {
            try
            {
                // Сначала сохраняем то, что было, чтобы можно было вернуть как было.
                if (!IsDisabled())
                {
                    using RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupPath);

                    foreach ((string path, string name) in Watched)
                    {
                        using RegistryKey? source = Registry.CurrentUser.OpenSubKey(path);
                        object? value = source?.GetValue(name);

                        if (value is not null)
                        {
                            backup.SetValue(path + "|" + name, value);
                            if (source?.GetValueKind(name) is RegistryValueKind kind)
                            {
                                backup.SetValue(path + "|" + name + "|kind", (int)kind, RegistryValueKind.DWord);
                            }
                        }
                    }
                }

                // Включаем режим «наилучшее быстродействие».
                Write(DesktopPath, "UserPreferencesMask",
                    new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 },
                    RegistryValueKind.Binary);
                Write(DesktopPath, "DragFullWindows", "0", RegistryValueKind.String);
                Write(DesktopPath, "MenuShowDelay", "0", RegistryValueKind.String);
                Write(VisualFxPath, "VisualFXSetting", 2, RegistryValueKind.DWord);
                Write(ExplorerPath + @"\Advanced", "TaskbarAnimations", 0, RegistryValueKind.DWord);
                Write(ExplorerPath + @"\Advanced", "ListviewAlphaSelect", 0, RegistryValueKind.DWord);
                Write(PersonalizePath, "EnableTransparency", 0, RegistryValueKind.DWord);

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static string? Restore()
        {
            try
            {
                using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath);

                if (backup is null)
                {
                    // Копии нет — возвращаем стандартное поведение Windows.
                    Write(VisualFxPath, "VisualFXSetting", 0, RegistryValueKind.DWord);
                    Write(DesktopPath, "DragFullWindows", "1", RegistryValueKind.String);
                    Write(DesktopPath, "MenuShowDelay", "400", RegistryValueKind.String);
                    Write(ExplorerPath + @"\Advanced", "TaskbarAnimations", 1, RegistryValueKind.DWord);
                    Write(ExplorerPath + @"\Advanced", "ListviewAlphaSelect", 1, RegistryValueKind.DWord);
                    Write(PersonalizePath, "EnableTransparency", 1, RegistryValueKind.DWord);
                    return null;
                }

                foreach ((string path, string name) in Watched)
                {
                    object? value = backup.GetValue(path + "|" + name);
                    if (value is null)
                    {
                        continue;
                    }

                    object? kindValue = backup.GetValue(path + "|" + name + "|kind");
                    RegistryValueKind kind = kindValue is int number
                        ? (RegistryValueKind)number
                        : RegistryValueKind.String;

                    Write(path, name, value, kind);
                }

                Registry.CurrentUser.DeleteSubKeyTree(BackupPath, throwOnMissingSubKey: false);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static void Write(string path, string name, object value, RegistryValueKind kind)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue(name, value, kind);
        }
    }

    // ==================== Контроль памяти (Storage Sense) ====================

    internal static class StorageSenseManager
    {
        private const string PolicyPath =
            @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";

        public static bool IsEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PolicyPath);
                return Convert.ToInt32(key?.GetValue("01") ?? 0) == 1;
            }
            catch
            {
                return false;
            }
        }

        public static string? Set(bool enabled)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(PolicyPath);

                // 01 — сама функция, 04 — удалять временные файлы,
                // 08 — чистить корзину, 256 — как часто запускать (1 = каждый день).
                key.SetValue("01", enabled ? 1 : 0, RegistryValueKind.DWord);

                if (enabled)
                {
                    key.SetValue("04", 1, RegistryValueKind.DWord);
                    key.SetValue("08", 1, RegistryValueKind.DWord);
                    key.SetValue("256", 1, RegistryValueKind.DWord);
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    // ==================== Точка восстановления ====================

    internal sealed class RestorePointInfo
    {
        public string Number { get; set; } = "";
        public string Description { get; set; } = "";
        public string Created { get; set; } = "";
    }

    internal static class RestorePointManager
    {
        /// <summary>Последние точки восстановления системы.</summary>
        public static async Task<List<RestorePointInfo>> ListAsync()
        {
            var result = new List<RestorePointInfo>();

            const string script =
                "Get-ComputerRestorePoint -ErrorAction SilentlyContinue | " +
                "Sort-Object SequenceNumber -Descending | Select-Object -First 10 | " +
                "ForEach-Object { \"$($_.SequenceNumber)|$($_.Description)|" +
                "$($_.ConvertToDateTime($_.CreationTime).ToString('dd.MM.yyyy HH:mm'))\" }";

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            string powershell = Path.Combine(
                Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

            if (!File.Exists(powershell))
            {
                powershell = "powershell";
            }

            (string output, _, int code) = await ConsoleRunner.RunAsync(
                powershell, "-NoProfile -NonInteractive -EncodedCommand " + encoded);

            if (code != 0 || output.Length == 0)
            {
                return result;
            }

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');

                if (parts.Length < 3)
                {
                    continue;
                }

                result.Add(new RestorePointInfo
                {
                    Number = parts[0].Trim(),
                    Description = parts[1].Trim(),
                    Created = parts[2].Trim()
                });
            }

            return result;
        }

        public static async Task<OptResult> CreateAsync()
        {
            const string script =
                "$ErrorActionPreference='Continue';" +
                "Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' " +
                "-Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue;" +
                "Enable-ComputerRestore -Drive 'C:\\' -ErrorAction SilentlyContinue;" +
                "Checkpoint-Computer -Description 'PCOptimizer' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop;" +
                "Write-Output 'OK'";

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            string powershell = Path.Combine(
                Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

            if (!File.Exists(powershell))
            {
                powershell = "powershell";
            }

            (string output, string error, int code) = await ConsoleRunner.RunAsync(
                powershell, "-NoProfile -NonInteractive -EncodedCommand " + encoded);

            if (output.Contains("OK", StringComparison.Ordinal))
            {
                return new OptResult { Ok = true, Text = "Точка восстановления создана" };
            }

            return new OptResult
            {
                Ok = false,
                Warn = true,
                Text = "Не удалось создать точку восстановления. " +
                       (error.Length > 0 ? error : "Возможно, защита системы отключена на диске C:")
            };
        }
    }

    // ==================== Запуск консольных программ ====================

    internal static class ConsoleRunner
    {
        public static (int Code, string Output, string Error) RunSync(
            string exePath, string arguments, int timeoutSeconds = 20)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return (-1, "", "не удалось запустить " + exePath);
                }

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(timeoutSeconds * 1000))
                {
                    process.Kill(entireProcessTree: true);
                    return (-1, "", "программа не ответила вовремя");
                }

                return (process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message);
            }
        }

        public static async Task<(string Output, string Error, int Code)> RunAsync(
            string exePath, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return ("", ex.Message, -1);
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return ("", "точка восстановления создаётся слишком долго", -1);
            }

            return ((await outputTask).Trim(), (await errorTask).Trim(), process.ExitCode);
        }
    }
}
