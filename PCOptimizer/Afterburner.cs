using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace PCOptimizer
{
    /// <summary>Что удалось вычитать из монитора MSI Afterburner.</summary>
    internal sealed class AfterburnerReading
    {
        public float? CpuTemp { get; set; }
        public string? CpuTempName { get; set; }
        public float? GpuTemp { get; set; }
        public float? GpuLoad { get; set; }
        public float? GpuMemUsedMb { get; set; }
        public float? GpuClockMhz { get; set; }
        public float? CpuLoad { get; set; }
        public float? CpuClockMhz { get; set; }
        public DateTime PolledAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Читает показания из общей памяти MSI Afterburner (MAHMSharedMemory).
    /// Нужен потому, что на некоторых платах LibreHardwareMonitor не видит
    /// датчик температуры процессора, а Afterburner со своим драйвером видит.
    ///
    /// Формат структуры взят из заголовка, который лежит рядом с самим
    /// Afterburner: Program Files (x86)\MSI Afterburner\SDK\Include\MAHMSharedMemory.h
    /// </summary>
    internal static class Afterburner
    {
        private const string ProcessName = "MSIAfterburner";
        private const string MapName = "MAHMSharedMemory";

        private const uint SignatureMahm = 0x4D48414D;   // 'MAHM' — данные готовы
        private const uint SignatureDead = 0x0000DEAD;   // память помечена на удаление

        // char szXxx[MAX_PATH] — пять строк по 260 байт.
        private const int MaxPath = 260;
        private const int MinEntrySize = MaxPath * 5 + 12 + 12;  // 1324 байта

        // Идентификаторы источников из MAHMSharedMemory.h
        private const uint SrcGpuTemperature = 0x00000000;
        private const uint SrcCpuTemperature = 0x00000080;
        private const uint SrcCpuUsage = 0x00000090;
        private const uint SrcCpuClock = 0x000000A0;
        private const uint SrcGpuUsage = 0x00000030;
        private const uint SrcMemoryUsage = 0x00000031;
        private const uint SrcCoreClock = 0x00000020;

        private static readonly object Gate = new object();

        // Если общей памяти нет — не долбим систему проверкой каждую секунду.
        private static DateTime _lastFailure = DateTime.MinValue;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(6);

        private static bool _runningCache;
        private static DateTime _runningAt = DateTime.MinValue;

        /// <summary>
        /// Где искать MSIAfterburner.exe. Пути собираются из переменных
        /// окружения, поэтому работают независимо от буквы диска и языка системы.
        /// </summary>
        private static IEnumerable<string> CandidatePaths()
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
                @"D:\"
            };

            foreach (string root in roots)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    yield return Path.Combine(root, "MSI Afterburner", "MSIAfterburner.exe");
                }
            }
        }

        /// <summary>Где лежит MSIAfterburner.exe, или null, если не нашли.</summary>
        public static string? ExePath
        {
            get
            {
                foreach (string path in CandidatePaths())
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                    catch
                    {
                        // Недоступный путь просто пропускаем.
                    }
                }

                return null;
            }
        }

        public static bool IsInstalled => ExePath is not null;

        /// <summary>Запущен ли Afterburner. Результат кэшируется на 5 секунд.</summary>
        public static bool IsRunning()
        {
            lock (Gate)
            {
                if (DateTime.Now - _runningAt < TimeSpan.FromSeconds(5))
                {
                    return _runningCache;
                }
            }

            bool running = false;

            try
            {
                running = Process.GetProcessesByName(ProcessName).Length > 0;
            }
            catch
            {
                // Список процессов может быть недоступен — считаем, что не запущен.
            }

            lock (Gate)
            {
                _runningCache = running;
                _runningAt = DateTime.Now;
            }

            return running;
        }

        /// <summary>Признак того, что общая память сейчас отдаёт валидные данные.</summary>
        public static bool HasData => TryRead(out _);

        /// <summary>
        /// Запускает Afterburner свёрнутым в трей. Возвращает текст ошибки
        /// или null, если всё получилось.
        /// </summary>
        public static string? Start()
        {
            string? path = ExePath;

            if (path is null)
            {
                return "MSI Afterburner не найден ни в одной из стандартных папок";
            }

            if (IsRunning())
            {
                return null;
            }

            // Просим Afterburner открываться свёрнутым, иначе он вылезет
            // окном поверх того, что человек смотрит.
            EnsureStartMinimized(path);

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = path,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                    UseShellExecute = true
                };

                Process.Start(info);

                lock (Gate)
                {
                    _runningCache = true;
                    _runningAt = DateTime.Now;
                    _lastFailure = DateTime.MinValue;
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// В конфиге Afterburner есть флаг StartMinimized. Если он выключен,
        /// включаем: так запуск программы не выкидывает лишнее окно.
        /// Файл читаем и пишем побайтово, чтобы не испортить кодировку.
        /// </summary>
        private static void EnsureStartMinimized(string exePath)
        {
            try
            {
                string? folder = Path.GetDirectoryName(exePath);
                if (folder is null)
                {
                    return;
                }

                string cfg = Path.Combine(folder, "Profiles", "MSIAfterburner.cfg");
                if (!File.Exists(cfg))
                {
                    return;
                }

                byte[] raw = File.ReadAllBytes(cfg);
                string text = Encoding.Latin1.GetString(raw);

                string updated = Regex.Replace(
                    text, @"(?m)^([ \t]*StartMinimized[ \t]*=[ \t]*)0([ \t]*)$", "${1}1${2}");

                if (updated == text)
                {
                    return;
                }

                File.WriteAllBytes(cfg, Encoding.Latin1.GetBytes(updated));
                AppLog.Write("Afterburner", "включил запуск свёрнутым в его настройках");
            }
            catch
            {
                // Не смогли поправить настройку — не беда, просто окно мелькнёт.
            }
        }

        /// <summary>
        /// Пробует прочитать общую память. false — Afterburner не запущен,
        /// версия не та или данные ещё не готовы.
        /// </summary>
        public static bool TryRead(out AfterburnerReading reading)
        {
            reading = new AfterburnerReading();

            lock (Gate)
            {
                if (DateTime.Now - _lastFailure < RetryDelay)
                {
                    return false;
                }
            }

            try
            {
                using MemoryMappedFile map = MemoryMappedFile.OpenExisting(
                    MapName, MemoryMappedFileRights.Read);

                using MemoryMappedViewStream view = map.CreateViewStream(
                    0, 0, MemoryMappedFileAccess.Read);

                using var reader = new BinaryReader(view, Encoding.UTF8, leaveOpen: true);

                if (view.Length < 32)
                {
                    MarkUnavailable();
                    return false;
                }

                uint signature = reader.ReadUInt32();

                if (signature == SignatureDead)
                {
                    MarkUnavailable();
                    return false;
                }

                if (signature != SignatureMahm)
                {
                    MarkUnavailable();
                    return false;
                }

                reader.ReadUInt32();                     // версия (0x00020000)
                uint headerSize = reader.ReadUInt32();
                uint entryCount = reader.ReadUInt32();
                uint entrySize = reader.ReadUInt32();
                reader.ReadUInt32();                     // время последнего опроса
                reader.ReadUInt32();                     // сколько записей о видеокартах
                reader.ReadUInt32();                     // размер такой записи

                if (entryCount == 0 || entryCount > 1024)
                {
                    MarkUnavailable();
                    return false;
                }

                if (headerSize < 32)
                {
                    headerSize = 32;
                }

                if (entrySize < MinEntrySize)
                {
                    entrySize = MinEntrySize;
                }

                long needed = headerSize + ((long)entrySize * entryCount);
                if (view.Length < needed)
                {
                    MarkUnavailable();
                    return false;
                }

                float? cpuTemp = null;
                string? cpuTempName = null;
                float? gpuTemp = null;
                float? cpuLoad = null;
                float? cpuClock = null;
                float? gpuLoad = null;
                float? gpuMemUsed = null;
                float? gpuClock = null;

                for (uint i = 0; i < entryCount; i++)
                {
                    view.Position = headerSize + ((long)entrySize * i);

                    string name = ReadFixedString(reader, MaxPath);
                    ReadFixedString(reader, MaxPath);       // единицы
                    ReadFixedString(reader, MaxPath);       // локализованное имя
                    ReadFixedString(reader, MaxPath);       // локализованные единицы
                    ReadFixedString(reader, MaxPath);       // рекомендуемый формат

                    float data = reader.ReadSingle();
                    reader.ReadSingle();                    // минимум для графика
                    reader.ReadSingle();                    // максимум для графика
                    reader.ReadUInt32();                    // флаги
                    reader.ReadUInt32();                    // индекс видеокарты
                    uint sourceId = reader.ReadUInt32();

                    // FLT_MAX означает «сейчас данных нет».
                    if (float.IsNaN(data) || data >= float.MaxValue)
                    {
                        continue;
                    }

                    switch (sourceId)
                    {
                        case SrcCpuTemperature:
                            // Записей может быть несколько — берём самую горячую.
                            if (data > 0 && data < 130 && (cpuTemp is null || data > cpuTemp.Value))
                            {
                                cpuTemp = data;
                                cpuTempName = name.Length > 0 ? name : null;
                            }

                            break;

                        case SrcGpuTemperature:
                            if (data > 0 && data < 130 && (gpuTemp is null || data > gpuTemp.Value))
                            {
                                gpuTemp = data;
                            }

                            break;

                        case SrcCpuUsage:
                            if (data >= 0 && data <= 100)
                            {
                                cpuLoad = data;
                            }

                            break;

                        case SrcCpuClock:
                            if (data > 0 && data < 10000)
                            {
                                cpuClock = data;
                            }

                            break;

                        case SrcGpuUsage:
                            if (data >= 0 && data <= 100)
                            {
                                gpuLoad = data;
                            }

                            break;

                        case SrcMemoryUsage:
                            if (data >= 0 && data < 1048576)
                            {
                                gpuMemUsed = data;
                            }

                            break;

                        case SrcCoreClock:
                            if (gpuClock is null && data > 0 && data < 10000)
                            {
                                gpuClock = data;
                            }

                            break;
                    }
                }

                // Если по идентификаторам ничего не нашли — пробуем по названию.
                if (cpuTemp is null)
                {
                    cpuTemp = FindByTemperatureName(view, reader, headerSize, entrySize, entryCount, out cpuTempName);
                }

                if (cpuTemp is null && gpuTemp is null && cpuLoad is null && cpuClock is null
                    && gpuLoad is null && gpuClock is null)
                {
                    MarkUnavailable();
                    return false;
                }

                reading.CpuTemp = cpuTemp;
                reading.CpuTempName = cpuTempName;
                reading.GpuTemp = gpuTemp;
                reading.CpuLoad = cpuLoad;
                reading.CpuClockMhz = cpuClock;
                reading.GpuLoad = gpuLoad;
                reading.GpuMemUsedMb = gpuMemUsed;
                reading.GpuClockMhz = gpuClock;

                lock (Gate)
                {
                    _lastFailure = DateTime.MinValue;
                }

                return true;
            }
            catch (FileNotFoundException)
            {
                // Afterburner не запущен — самый обычный случай.
                MarkUnavailable();
                return false;
            }
            catch (Exception)
            {
                MarkUnavailable();
                return false;
            }
        }

        /// <summary>
        /// Запасной путь: ищем запись, у которой в названии есть и «cpu»,
        /// и «temp». Пригодится, если Afterburner переименует источники.
        /// </summary>
        private static float? FindByTemperatureName(
            MemoryMappedViewStream view,
            BinaryReader reader,
            uint headerSize,
            uint entrySize,
            uint entryCount,
            out string? foundName)
        {
            foundName = null;
            float? best = null;

            for (uint i = 0; i < entryCount; i++)
            {
                view.Position = headerSize + ((long)entrySize * i);

                string name = ReadFixedString(reader, MaxPath);
                ReadFixedString(reader, MaxPath);
                ReadFixedString(reader, MaxPath);
                ReadFixedString(reader, MaxPath);
                ReadFixedString(reader, MaxPath);

                float data = reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();

                if (float.IsNaN(data) || data >= float.MaxValue || data <= 0 || data >= 130)
                {
                    continue;
                }

                string lower = name.ToLowerInvariant();

                bool looksLikeCpu = lower.Contains("cpu") || lower.Contains("processor");
                bool looksLikeTemp = lower.Contains("temp") || lower.Contains("темп");

                if (looksLikeCpu && looksLikeTemp && (best is null || data > best.Value))
                {
                    best = data;
                    foundName = name;
                }
            }

            return best;
        }

        /// <summary>Строка фиксированной длины: до нулевого байта, только ASCII.</summary>
        private static string ReadFixedString(BinaryReader reader, int length)
        {
            byte[] bytes = reader.ReadBytes(length);

            if (bytes.Length == 0)
            {
                return "";
            }

            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0)
            {
                end = bytes.Length;
            }

            var chars = new char[end];

            for (int i = 0; i < end; i++)
            {
                byte b = bytes[i];
                chars[i] = b >= 32 && b < 127 ? (char)b : ' ';
            }

            return new string(chars).Trim();
        }

        private static void MarkUnavailable()
        {
            lock (Gate)
            {
                _lastFailure = DateTime.Now;
            }
        }
    }
}
