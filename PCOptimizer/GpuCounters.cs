using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace PCOptimizer
{
    /// <summary>
    /// Универсальный способ следить за видеокартой: счётчики производительности
    /// Windows. Они есть на Windows 10 и 11 у любого производителя — и NVIDIA,
    /// и AMD, и Intel. Нужны там, где нет nvidia-smi.
    /// </summary>
    internal static class GpuCounters
    {
        private const string CimV2 = @"root\CIMV2";

        private const string EngineClass =
            "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine";

        private const string MemoryClass =
            "Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory";

        private static readonly object Gate = new object();
        private static bool _probed;
        private static bool _available;

        /// <summary>Есть ли на этой машине счётчики видеокарты вообще.</summary>
        public static bool IsAvailable()
        {
            lock (Gate)
            {
                if (_probed)
                {
                    return _available;
                }

                _probed = true;

                try
                {
                    var searcher = new ManagementObjectSearcher(
                        new ManagementScope(CimV2),
                        new ObjectQuery("SELECT Name FROM " + EngineClass));

                    // Достаточно одного экземпляра, чтобы понять, что класс живой.
                    foreach (ManagementBaseObject _ in searcher.Get())
                    {
                        _available = true;
                        break;
                    }
                }
                catch
                {
                    _available = false;
                }

                return _available;
            }
        }

        /// <summary>
        /// Загрузка видеокарты в процентах. Считаем так же, как диспетчер задач:
        /// складываем занятость внутри каждого движка и берём самый нагруженный.
        /// </summary>
        public static float? ReadLoad()
        {
            if (!IsAvailable())
            {
                return null;
            }

            try
            {
                var searcher = new ManagementObjectSearcher(
                    new ManagementScope(CimV2),
                    new ObjectQuery("SELECT Name, UtilizationPercentage FROM " + EngineClass));

                // Ключ — конкретный движок без учёта процесса, который его занял.
                var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (ManagementBaseObject item in searcher.Get())
                {
                    string name = HardwareScan.Str(item, "Name");
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    double value = ParsePercent(HardwareScan.Str(item, "UtilizationPercentage"));
                    if (value <= 0)
                    {
                        continue;
                    }

                    string engine = EngineKey(name);
                    totals[engine] = totals.TryGetValue(engine, out double sum) ? sum + value : value;
                }

                if (totals.Count == 0)
                {
                    // Всё по нулям — это нормально, видеокарта просто отдыхает.
                    return 0f;
                }

                double max = totals.Values.Max();
                return (float)Math.Min(100.0, Math.Round(max, 1));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Занятая видеопамять в мегабайтах — по возможности.</summary>
        public static (int UsedMb, int TotalMb) ReadMemory()
        {
            if (!IsAvailable())
            {
                return (0, 0);
            }

            try
            {
                var searcher = new ManagementObjectSearcher(
                    new ManagementScope(CimV2),
                    new ObjectQuery(
                        "SELECT DedicatedUsage, DedicatedLimit FROM " + MemoryClass));

                long used = 0;
                long limit = 0;

                foreach (ManagementBaseObject item in searcher.Get())
                {
                    used += (long)HardwareScan.U64(item, "DedicatedUsage");
                    limit = Math.Max(limit, (long)HardwareScan.U64(item, "DedicatedLimit"));
                }

                return ((int)(used / 1024 / 1024), (int)(limit / 1024 / 1024));
            }
            catch
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// Из имени вида
        /// «pid_1234_luid_0x00000000_0x0000C1A2_phys_0_eng_0_engtype_3D»
        /// оставляем «luid_0x00000000_0x0000C1A2_phys_0_eng_0».
        /// </summary>
        private static string EngineKey(string instanceName)
        {
            int start = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return instanceName;
            }

            int end = instanceName.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            if (end <= start)
            {
                end = instanceName.Length;
            }

            return instanceName[start..end];
        }

        private static double ParsePercent(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            return double.TryParse(
                text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value
                : 0;
        }
    }
}
