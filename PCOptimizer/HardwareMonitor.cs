using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PCOptimizer
{
    // ==================== Модели данных для интерфейса ====================

    internal sealed class CoreSample
    {
        public float Load { get; set; }
        public uint Mhz { get; set; }
    }

    internal sealed class CpuSample
    {
        public string Name { get; set; } = "";
        public float Load { get; set; }
        public float? Temp { get; set; }
        public uint FreqMhz { get; set; }
        public uint MaxMhz { get; set; }
        public string? TempNote { get; set; }

        /// <summary>Откуда взялась температура: LibreHardwareMonitor или MSI Afterburner.</summary>
        public string? TempFrom { get; set; }
        public List<CoreSample> Cores { get; } = new List<CoreSample>();
    }

    internal sealed class GpuSample
    {
        public string? Name { get; set; }
        public float Load { get; set; } = -1;
        public float Temp { get; set; } = -1;
        public int MemUsedMb { get; set; }
        public int MemTotalMb { get; set; }
        public int ClockMhz { get; set; }
        public string? Error { get; set; }
    }

    internal sealed class RamSample
    {
        public ulong UsedMb { get; set; }
        public ulong TotalMb { get; set; }
        public uint Percent { get; set; }
    }

    internal sealed class Snapshot
    {
        public string Time { get; set; } = "";
        public CpuSample Cpu { get; set; } = new CpuSample();
        public GpuSample Gpu { get; set; } = new GpuSample();
        public RamSample Ram { get; set; } = new RamSample();
    }

    // ==================== Сбор данных о железе ====================

    /// <summary>
    /// Читает всё, что показывает дашборд: процессор, память, видеокарту.
    /// Загрузка CPU берётся из NtQuerySystemInformation, температура — из
    /// LibreHardwareMonitor, видеокарта — через nvidia-smi.
    /// </summary>
    internal sealed class HardwareMonitor : IDisposable
    {
        private const int MaxCores = 256;

        // Предыдущие значения счётчиков ядер — нужны, чтобы посчитать разницу.
        private readonly long[] _prevIdle = new long[MaxCores];
        private readonly long[] _prevKernel = new long[MaxCores];
        private readonly long[] _prevUser = new long[MaxCores];
        private int _prevCoreCount;

        // Предыдущие значения для резервного способа (GetSystemTimes).
        private ulong _prevTotalIdle;
        private ulong _prevTotalKernel;
        private ulong _prevTotalUser;
        private bool _hasSystemTimesPrev;

        private readonly string _cpuName;

        // LibreHardwareMonitor открываем один раз и переиспользуем.
        private Computer? _computer;
        private string? _sensorError;
        private bool _sensorsTried;

        // Что показал последний замер — нужно для карточки «Температура CPU».
        private string? _tempSource;
        private string? _tempNote;
        private string? _sensorDiagnostic;

        // Имя видеокарты не меняется — запоминаем, чтобы не дёргать nvidia-smi зря.
        private string? _gpuName;

        // Почему не сработал nvidia-smi — показываем, если мониторить нечем.
        private string? _nvidiaNote;

        public HardwareMonitor()
        {
            _cpuName = ReadCpuName();

            // Первый замер нужен как «точка отсчёта»: загрузка считается
            // по разнице двух замеров, поэтому без него первый кадр был бы нулевым.
            TryPrimeCounters();
        }

        /// <summary>Один полный замер. Вызывать из фонового потока.</summary>
        public async Task<Snapshot> ReadAsync()
        {
            var snapshot = new Snapshot();

            ReadCpu(snapshot.Cpu);
            ReadMemory(snapshot.Ram);
            snapshot.Gpu = await ReadGpuAsync();

            snapshot.Time = DateTime.Now.ToString("HH:mm:ss");
            return snapshot;
        }

        // ---------------- Процессор ----------------

        private void ReadCpu(CpuSample cpu)
        {
            cpu.Name = _cpuName;

            uint maxMhz = 0;
            uint[] frequencies = ReadFrequencies(out maxMhz);
            cpu.MaxMhz = maxMhz;

            float totalLoad;
            List<CoreSample> cores = ReadCorePerformance(frequencies, out totalLoad);

            if (cores.Count > 0)
            {
                cpu.Load = totalLoad;
                cpu.Cores.AddRange(cores);

                long sum = 0;
                foreach (CoreSample core in cores)
                {
                    sum += core.Mhz;
                }
                cpu.FreqMhz = (uint)(sum / cores.Count);
            }
            else
            {
                // Ядерные счётчики недоступны — берём хотя бы общую загрузку.
                cpu.Load = ReadTotalLoadFallback();

                if (frequencies.Length > 0)
                {
                    long sum = 0;
                    foreach (uint mhz in frequencies)
                    {
                        sum += mhz;
                    }
                    cpu.FreqMhz = (uint)(sum / frequencies.Length);
                }
            }

            cpu.Temp = ReadCpuTemperature(out string? note, out string? from);
            cpu.TempNote = note;
            cpu.TempFrom = from;
        }

        private void TryPrimeCounters()
        {
            try
            {
                float ignored;
                ReadCorePerformance(Array.Empty<uint>(), out ignored);
                ReadTotalLoadFallback();
            }
            catch
            {
                // Первый замер — не критичный, ошибки тут не важны.
            }
        }

        /// <summary>Частота каждого логического ядра, МГц.</summary>
        private static uint[] ReadFrequencies(out uint maxMhz)
        {
            maxMhz = 0;

            int count = Environment.ProcessorCount;
            if (count <= 0 || count > MaxCores)
            {
                return Array.Empty<uint>();
            }

            int size = Marshal.SizeOf<NativeMethods.PROCESSOR_POWER_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size * count);

            try
            {
                int status = NativeMethods.CallNtPowerInformation(
                    NativeMethods.ProcessorInformation,
                    IntPtr.Zero, 0,
                    buffer, (uint)(size * count));

                if (status != 0)
                {
                    return Array.Empty<uint>();
                }

                var result = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.PROCESSOR_POWER_INFORMATION>(
                        IntPtr.Add(buffer, i * size));

                    result[i] = info.CurrentMhz;
                    if (info.MaxMhz > maxMhz)
                    {
                        maxMhz = info.MaxMhz;
                    }
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Загрузка каждого ядра. Считается как доля занятого времени
        /// между двумя замерами: (всего - простой) / всего.
        /// </summary>
        private List<CoreSample> ReadCorePerformance(uint[] frequencies, out float totalLoad)
        {
            totalLoad = 0f;
            var result = new List<CoreSample>();

            int count = Environment.ProcessorCount;
            if (count <= 0 || count > MaxCores)
            {
                return result;
            }

            int size = Marshal.SizeOf<NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size * count);

            try
            {
                int returned;
                int status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemProcessorPerformanceInformation,
                    buffer, size * count, out returned);

                if (status != 0)
                {
                    return result;
                }

                var idle = new long[count];
                var kernel = new long[count];
                var user = new long[count];

                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(
                        IntPtr.Add(buffer, i * size));

                    idle[i] = info.IdleTime;
                    kernel[i] = info.KernelTime;
                    user[i] = info.UserTime;
                }

                bool havePrevious = _prevCoreCount == count;

                if (havePrevious)
                {
                    double sumTotal = 0;
                    double sumBusy = 0;

                    for (int i = 0; i < count; i++)
                    {
                        long dIdle = idle[i] - _prevIdle[i];
                        long dTotal = (kernel[i] - _prevKernel[i]) + (user[i] - _prevUser[i]);

                        if (dIdle < 0) dIdle = 0;
                        if (dTotal <= 0) dTotal = 1;

                        double load = (dTotal - dIdle) * 100.0 / dTotal;
                        load = Math.Clamp(load, 0, 100);

                        result.Add(new CoreSample
                        {
                            Load = (float)load,
                            Mhz = i < frequencies.Length ? frequencies[i] : 0
                        });

                        sumTotal += dTotal;
                        sumBusy += dTotal - dIdle;
                    }

                    if (sumTotal > 0)
                    {
                        totalLoad = (float)Math.Clamp(sumBusy * 100.0 / sumTotal, 0, 100);
                    }
                }

                Array.Copy(idle, _prevIdle, count);
                Array.Copy(kernel, _prevKernel, count);
                Array.Copy(user, _prevUser, count);
                _prevCoreCount = count;

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Резервный способ: общая загрузка через GetSystemTimes.</summary>
        private float ReadTotalLoadFallback()
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return 0f;
            }

            ulong totalIdle = idle.Total;
            ulong totalKernel = kernel.Total;
            ulong totalUser = user.Total;

            float load = 0f;

            if (_hasSystemTimesPrev)
            {
                ulong dIdle = totalIdle - _prevTotalIdle;
                ulong dTotal = (totalKernel - _prevTotalKernel) + (totalUser - _prevTotalUser);

                if (dTotal > 0)
                {
                    double value = (double)(dTotal - dIdle) * 100.0 / dTotal;
                    load = (float)Math.Clamp(value, 0, 100);
                }
            }

            _prevTotalIdle = totalIdle;
            _prevTotalKernel = totalKernel;
            _prevTotalUser = totalUser;
            _hasSystemTimesPrev = true;

            return load;
        }

        private static string ReadCpuName()
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

                string? name = key?.GetValue("ProcessorNameString") as string;
                return name?.Trim() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Запущена ли программа с правами администратора.</summary>
        internal static bool IsElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // ---------------- Температура процессора ----------------

        /// <summary>
        /// Температура процессора. Сначала пробуем LibreHardwareMonitor,
        /// и если он датчик не видит — берём показания из MSI Afterburner
        /// (у него свой драйвер, на многих платах он видит больше).
        /// </summary>
        private float? ReadCpuTemperature(out string? note, out string? from)
        {
            note = null;
            from = null;

            float? own = ReadCpuTemperatureOwn();

            if (own.HasValue)
            {
                from = "LibreHardwareMonitor";
                _tempSource = from;
                _tempNote = null;
                return own;
            }

            bool afterburnerAnswered = Afterburner.TryRead(out AfterburnerReading afterburner);

            if (afterburnerAnswered && afterburner.CpuTemp.HasValue)
            {
                from = afterburner.CpuTempName is null
                    ? "MSI Afterburner"
                    : "MSI Afterburner · " + afterburner.CpuTempName;

                _tempSource = from;
                _tempNote = null;
                return afterburner.CpuTemp;
            }

            note = BuildTemperatureNote(afterburnerAnswered);
            _tempSource = null;
            _tempNote = note;

            return null;
        }

        /// <summary>Что сейчас является источником температуры — для окна настроек.</summary>
        public string? TemperatureSource => _tempSource;

        /// <summary>Почему температуры нет — для окна настроек и подсказки внизу.</summary>
        public string? TemperatureNote => _tempNote;

        /// <summary>Сбросить попытку открыть датчики и попробовать ещё раз.</summary>
        public void RetrySensors()
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
                // Уже закрыт.
            }

            _computer = null;
            _sensorError = null;
            _sensorsTried = false;
            _tempSource = null;
            _tempNote = null;
            _sensorDiagnostic = null;
        }

        /// <summary>Понятное объяснение, почему температуры не видно, и что делать.</summary>
        private string BuildTemperatureNote(bool afterburnerAnswered)
        {
            if (!IsElevated())
            {
                return "Температура CPU: запустите программу от имени администратора.";
            }

            // Если известно, почему не сработал встроенный модуль — говорим прямо.
            if (!Afterburner.IsInstalled)
            {
                return (_sensorDiagnostic ?? "Датчик температуры не найден.")
                    + " Можно поставить MSI Afterburner — программа возьмёт температуру оттуда.";
            }

            if (!Afterburner.IsRunning())
            {
                return (_sensorDiagnostic ?? "Датчик температуры не найден.")
                    + " Как запасной вариант — MSI Afterburner: у него свой драйвер, "
                    + "иногда он работает там, где встроенный не смог.";
            }

            if (afterburnerAnswered)
            {
                return "MSI Afterburner запущен, но температуру процессора не отдаёт. "
                    + "Включи её в его вкладке «Мониторинг».";
            }

            return _sensorDiagnostic
                ?? "MSI Afterburner запущен, но данные ещё не готовы.";
        }

        /// <summary>Температура своими силами, через LibreHardwareMonitor.</summary>
        private float? ReadCpuTemperatureOwn()
        {
            if (!_sensorsTried)
            {
                _sensorsTried = true;

                try
                {
                    var computer = new Computer { IsCpuEnabled = true };
                    computer.Open();
                    _computer = computer;
                }
                catch (Exception ex)
                {
                    _sensorError = ex.Message;
                }
            }

            if (_computer is null)
            {
                _sensorDiagnostic = "Встроенный модуль датчиков не открылся"
                    + (_sensorError is null ? "." : ": " + _sensorError);

                return null;
            }

            try
            {
                float? package = null;
                float? average = null;
                float? highest = null;

                int sensorsFound = 0;
                int sensorsWithValue = 0;

                foreach (IHardware hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType != HardwareType.Cpu)
                    {
                        continue;
                    }

                    hardware.Update();

                    foreach (ISensor sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType != SensorType.Temperature)
                        {
                            continue;
                        }

                        sensorsFound++;

                        if (!sensor.Value.HasValue)
                        {
                            continue;
                        }

                        sensorsWithValue++;

                        float value = sensor.Value.Value;
                        if (value <= 0 || value > 150)
                        {
                            continue;
                        }

                        string name = sensor.Name ?? "";
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            package = value;
                        }
                        else if (name.Contains("Average", StringComparison.OrdinalIgnoreCase))
                        {
                            average = value;
                        }

                        if (highest is null || value > highest.Value)
                        {
                            highest = value;
                        }
                    }
                }

                _sensorDiagnostic = DiagnoseSensors(sensorsFound, sensorsWithValue);

                return package ?? average ?? highest;
            }
            catch (Exception ex)
            {
                _sensorError = ex.Message;
                _sensorDiagnostic = "Встроенный модуль датчиков упал: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Разбираемся, почему температуры нет. Случаи разные, и лечатся они
        /// по-разному, поэтому пишем конкретную причину, а не «не найдено».
        /// </summary>
        private string? DiagnoseSensors(int found, int withValue)
        {
            if (found == 0)
            {
                return "На этой плате нет датчиков температуры, которые видит встроенный модуль.";
            }

            if (withValue == 0)
            {
                return "Датчики температуры найдены (" + found + " шт.), но все значения пустые: "
                    + "драйвер датчиков не загрузился. Обычно так делают Riot Vanguard "
                    + "и антивирусы — закрой Vanguard через его значок в трее и нажми «Проверить снова».";
            }

            return null;
        }

        // ---------------- Оперативная память ----------------

        private static void ReadMemory(RamSample ram)
        {
            var status = new NativeMethods.MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();

            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return;
            }

            ram.TotalMb = status.ullTotalPhys / 1024 / 1024;
            ram.UsedMb = (status.ullTotalPhys - status.ullAvailPhys) / 1024 / 1024;
            ram.Percent = status.dwMemoryLoad;
        }

        // ---------------- Видеокарта ----------------

        /// <summary>
        /// Слежение за видеокартой. Способ зависит от того, чья карта стоит:
        /// у NVIDIA — nvidia-smi, у остальных — счётчики Windows, а если
        /// запущен MSI Afterburner, берём данные и оттуда.
        /// </summary>
        private async Task<GpuSample> ReadGpuAsync()
        {
            SystemReport report;

            try
            {
                report = ProfileStore.Current;
            }
            catch
            {
                report = new SystemReport();
            }

            string knownName = report.Gpu.Name;
            int knownVram = report.Gpu.MemoryMb;

            // 1. NVIDIA — самый подробный источник.
            if (report.Gpu.Vendor == GpuVendor.Nvidia)
            {
                GpuSample nvidia = await ReadGpuNvidiaAsync();

                if (nvidia.Load >= 0)
                {
                    FillKnownGpu(nvidia, knownName, knownVram);
                    return nvidia;
                }

                _nvidiaNote = nvidia.Error;
            }

            // 2. Универсальные счётчики производительности Windows.
            GpuSample universal = ReadGpuUniversal();

            if (universal.Load >= 0)
            {
                FillKnownGpu(universal, knownName, knownVram);
                return universal;
            }

            // 3. MSI Afterburner, если он запущен.
            GpuSample? afterburner = ReadGpuFromAfterburner();

            if (afterburner is not null)
            {
                FillKnownGpu(afterburner, knownName, knownVram);
                return afterburner;
            }

            // 4. Мониторить нечем — говорим об этом прямо, без падения.
            var failed = new GpuSample
            {
                Name = knownName.Length > 0 ? knownName : null,
                MemTotalMb = knownVram,
                Error = report.AllGpus.Count == 0
                    ? "видеокарта не определена"
                    : "видеокарта не поддерживается, мониторинг GPU недоступен"
            };

            if (report.Gpu.Vendor == GpuVendor.Nvidia && _nvidiaNote is not null)
            {
                failed.Error = "nvidia-smi недоступен: " + _nvidiaNote;
            }

            return failed;
        }

        /// <summary>Дополняем данные тем, что уже знаем о карте из сканирования.</summary>
        private static void FillKnownGpu(GpuSample gpu, string name, int vramMb)
        {
            if (string.IsNullOrWhiteSpace(gpu.Name) && name.Length > 0)
            {
                gpu.Name = name;
            }

            if (gpu.MemTotalMb <= 0 && vramMb > 0)
            {
                gpu.MemTotalMb = vramMb;
            }

            gpu.Error = null;
        }

        /// <summary>Загрузка видеокарты через счётчики Windows — для AMD и Intel.</summary>
        private static GpuSample ReadGpuUniversal()
        {
            var gpu = new GpuSample { Load = -1, Temp = -1 };

            try
            {
                float? load = GpuCounters.ReadLoad();

                if (!load.HasValue)
                {
                    return gpu;
                }

                gpu.Load = load.Value;

                (int usedMb, int totalMb) = GpuCounters.ReadMemory();
                gpu.MemUsedMb = usedMb;

                if (totalMb > 0)
                {
                    gpu.MemTotalMb = totalMb;
                }
            }
            catch
            {
                // Счётчики могут быть сломаны — это не повод падать.
            }

            return gpu;
        }

        /// <summary>Данные видеокарты из монитора MSI Afterburner.</summary>
        private static GpuSample? ReadGpuFromAfterburner()
        {
            try
            {
                if (!Afterburner.TryRead(out AfterburnerReading reading))
                {
                    return null;
                }

                if (!reading.GpuTemp.HasValue && !reading.GpuLoad.HasValue)
                {
                    return null;
                }

                return new GpuSample
                {
                    Load = reading.GpuLoad ?? -1,
                    Temp = reading.GpuTemp ?? -1,
                    MemUsedMb = (int)(reading.GpuMemUsedMb ?? 0),
                    ClockMhz = (int)(reading.GpuClockMhz ?? 0)
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<GpuSample> ReadGpuNvidiaAsync()
        {
            string exePath = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exePath))
            {
                exePath = "nvidia-smi";
            }

            // Имя карты запрашиваем последним: если в нём встретится запятая,
            // мы просто склеим остаток строки обратно.
            const string arguments =
                "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total," +
                "clocks.current.graphics,name --format=csv,noheader,nounits";

            (string? output, string error) = await RunConsoleAppAsync(exePath, arguments);

            var gpu = new GpuSample();

            if (output is null)
            {
                gpu.Error = "nvidia-smi недоступен";
                return gpu;
            }

            if (output.Length == 0)
            {
                gpu.Error = error.Length > 0 ? error : "nvidia-smi не вернул данных";
                return gpu;
            }

            try
            {
                return ParseGpu(output);
            }
            catch (Exception ex)
            {
                gpu.Error = ex.Message;
                return gpu;
            }
        }

        private GpuSample ParseGpu(string output)
        {
            string line = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];

            string[] parts = line.Split(',');

            if (parts.Length < 6)
            {
                return new GpuSample { Error = "неожиданный ответ nvidia-smi" };
            }

            var gpu = new GpuSample
            {
                Load = ParseNumber(parts[0], -1),
                Temp = ParseNumber(parts[1], -1),
                MemUsedMb = ParseNumber(parts[2], 0),
                MemTotalMb = ParseNumber(parts[3], 0),
                ClockMhz = ParseNumber(parts[4], 0),
                Name = string.Join(",", parts, 5, parts.Length - 5).Trim()
            };

            if (!string.IsNullOrWhiteSpace(gpu.Name))
            {
                _gpuName = gpu.Name;
            }
            else
            {
                gpu.Name = _gpuName;
            }

            return gpu;
        }

        private static int ParseNumber(string text, int fallback)
        {
            text = text.Trim();

            if (text.Length == 0 || text.Contains("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        // ---------------- Запуск консольной программы ----------------

        private static async Task<(string? Output, string Error)> RunConsoleAppAsync(
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
                return (null, ex.Message);
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (null, "не ответил за 5 секунд");
            }

            return ((await outputTask).Trim(), (await errorTask).Trim());
        }

        public void Dispose()
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
                // При закрытии программы ошибки датчиков уже не важны.
            }

            _computer = null;
        }
    }
}
