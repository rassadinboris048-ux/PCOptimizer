using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PCOptimizer
{
    // ==================== Пути к файлам программы ====================

    /// <summary>
    /// Файлы программы лежат рядом с exe. Если туда писать нельзя
    /// (например, программа в Program Files без прав), берём папку
    /// пользователя — программа в этом случае просто работает как обычно.
    /// </summary>
    internal static class AppPaths
    {
        private static string? _folder;

        public static string Folder
        {
            get
            {
                if (_folder is not null)
                {
                    return _folder;
                }

                string baseDir = AppContext.BaseDirectory;

                try
                {
                    string probe = Path.Combine(baseDir, ".pcoptimizer.tmp");
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                    _folder = baseDir;
                }
                catch
                {
                    string fallback = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PCOptimizer");

                    Directory.CreateDirectory(fallback);
                    _folder = fallback;
                }

                return _folder;
            }
        }

        public static string SettingsFile => Path.Combine(Folder, "settings.json");

        public static string LogFile => Path.Combine(Folder, "actions.log");
    }

    // ==================== Настройки ====================

    internal sealed class AppSettings
    {
        public string Theme { get; set; } = "dark";
        public bool RunAtStartup { get; set; }

        // Акцентный цвет интерфейса: red, orange, yellow, green, cyan, blue, purple.
        public string Accent { get; set; } = "red";

        // Мини-оверлей на рабочем столе.
        public bool MiniOverlay { get; set; }
        public bool MiniOverlayAsked { get; set; }

        // Где пользователь оставил мини-оверлей. -1 — ещё не двигали.
        public int MiniOverlayX { get; set; } = -1;
        public int MiniOverlayY { get; set; } = -1;

        // Звуки во время большой чистки.
        public bool SoundEnabled { get; set; } = true;

        // Если своих датчиков температуры нет — поднимать MSI Afterburner
        // и брать температуру процессора из него.
        public bool UseAfterburner { get; set; } = true;

        // Профили под игры: набор настроек, который применяется одним кликом.
        public List<GameProfile> Profiles { get; set; } = new List<GameProfile>();
    }

    internal static class SettingsStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Читает настройки. Если файла нет или он битый — отдаёт значения по умолчанию.</summary>
        public static AppSettings Load()
        {
            try
            {
                string path = AppPaths.SettingsFile;

                if (!File.Exists(path))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

                return settings ?? new AppSettings();
            }
            catch
            {
                // Битый файл настроек не должен мешать программе запуститься.
                return new AppSettings();
            }
        }

        /// <summary>Сохраняет настройки. Возвращает текст ошибки или null.</summary>
        public static string? Save(AppSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, Options);
                File.WriteAllText(AppPaths.SettingsFile, json, new UTF8Encoding(false));
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    // ==================== Журнал действий ====================

    internal sealed class LogLine
    {
        public string Time { get; set; } = "";
        public string Category { get; set; } = "";
        public string Text { get; set; } = "";
    }

    internal static class AppLog
    {
        private const int MaxLines = 500;

        private static readonly object Sync = new object();

        /// <summary>Добавляет запись в журнал. Ошибки записи молча игнорируются.</summary>
        public static void Write(string category, string text)
        {
            try
            {
                lock (Sync)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                  "|" + category + "|" + text.Replace('|', '/').Replace('\n', ' ');

                    File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine, new UTF8Encoding(false));
                    Trim();
                }
            }
            catch
            {
                // Журнал — не повод падать.
            }
        }

        /// <summary>Последние записи, свежие сверху.</summary>
        public static List<LogLine> ReadLast(int count)
        {
            var result = new List<LogLine>();

            try
            {
                string path = AppPaths.LogFile;
                if (!File.Exists(path))
                {
                    return result;
                }

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);

                for (int i = lines.Length - 1; i >= 0 && result.Count < count; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] parts = line.Split(new[] { '|' }, 3);

                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    result.Add(new LogLine
                    {
                        Time = parts[0],
                        Category = parts[1],
                        Text = parts[2]
                    });
                }
            }
            catch
            {
                // Нет доступа к файлу — покажем пустой журнал.
            }

            return result;
        }

        public static string? Clear()
        {
            try
            {
                lock (Sync)
                {
                    if (File.Exists(AppPaths.LogFile))
                    {
                        File.Delete(AppPaths.LogFile);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>Оставляет только последние записи, чтобы файл не рос бесконечно.</summary>
        private static void Trim()
        {
            string path = AppPaths.LogFile;

            if (!File.Exists(path))
            {
                return;
            }

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            if (lines.Length <= MaxLines)
            {
                return;
            }

            var keep = new string[MaxLines];
            Array.Copy(lines, lines.Length - MaxLines, keep, 0, MaxLines);
            File.WriteAllLines(path, keep, new UTF8Encoding(false));
        }
    }

    // ==================== Автозапуск самой программы ====================

    internal static class SelfStartup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PCOptimizer";

        public static bool IsEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                object? value = key?.GetValue(ValueName);

                return value is string text && text.Length > 0;
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
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

                if (key is null)
                {
                    return "нет доступа к разделу автозагрузки";
                }

                if (enabled)
                {
                    string exe = Environment.ProcessPath ?? "";

                    if (exe.Length == 0)
                    {
                        return "не удалось определить путь к программе";
                    }

                    key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
