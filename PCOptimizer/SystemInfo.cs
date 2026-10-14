using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PCOptimizer
{
    // ==================== Модели ====================

    internal enum GpuVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel,
        Microsoft
    }

    internal enum MediaKind
    {
        Unknown,
        Ssd,
        Hdd
    }

    /// <summary>Насколько процессор шустрый — от этого зависит, что ему предлагать.</summary>
    internal enum CpuTier
    {
        VeryWeak,   // 2 потока и меньше
        Weak,       // до 4 потоков
        Normal,     // до 12 потоков
        Strong      // больше 12 потоков
    }

    internal sealed class CpuInfo
    {
        public string Name { get; set; } = "неизвестно";
        public int Cores { get; set; }
        public int Threads { get; set; }
        public uint BaseMhz { get; set; }
        public uint MaxMhz { get; set; }
        public CpuTier Tier { get; set; } = CpuTier.Normal;

        public string TierName => Tier switch
        {
            CpuTier.VeryWeak => "очень слабый",
            CpuTier.Weak => "слабый",
            CpuTier.Normal => "средний",
            _ => "мощный"
        };
    }

    internal sealed class GpuInfo
    {
        public string Name { get; set; } = "";
        public GpuVendor Vendor { get; set; } = GpuVendor.Unknown;
        public int MemoryMb { get; set; }
        public string Driver { get; set; } = "";
        public bool IsMain { get; set; }

        public string VendorName => Vendor switch
        {
            GpuVendor.Nvidia => "NVIDIA",
            GpuVendor.Amd => "AMD",
            GpuVendor.Intel => "Intel",
            GpuVendor.Microsoft => "Microsoft (базовый)",
            _ => "неизвестен"
        };
    }

    internal sealed class RamInfo
    {
        public ulong TotalMb { get; set; }
        public int SpeedMhz { get; set; }
        public string TypeName { get; set; } = "неизвестно";
        public int Modules { get; set; }
        public int Slots { get; set; }

        /// <summary>Меньше 8 ГБ — памяти мало, чистим агрессивнее.</summary>
        public bool LowMemory => TotalMb > 0 && TotalMb < 8192;
    }

    internal sealed class VolumeInfo
    {
        public string Letter { get; set; } = "";
        public string Label { get; set; } = "";
        public string FileSystem { get; set; } = "";
        public string Model { get; set; } = "";
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public MediaKind Media { get; set; } = MediaKind.Unknown;
        public bool IsSystem { get; set; }

        public string MediaName => Media switch
        {
            MediaKind.Ssd => "SSD",
            MediaKind.Hdd => "HDD",
            _ => "тип неизвестен"
        };
    }

    internal sealed class WindowsInfo
    {
        public string Name { get; set; } = "Windows";
        public string Version { get; set; } = "";
        public int Build { get; set; }
        public string BuildFull { get; set; } = "";
        public string DisplayVersion { get; set; } = "";
        public string Arch { get; set; } = "";

        public bool IsWindows11 => Build >= 22000;
    }

    internal sealed class SystemReport
    {
        public string Fingerprint { get; set; } = "";
        public string ScannedAt { get; set; } = "";
        public bool FromCache { get; set; }

        public CpuInfo Cpu { get; set; } = new CpuInfo();
        public GpuInfo Gpu { get; set; } = new GpuInfo();
        public List<GpuInfo> AllGpus { get; set; } = new List<GpuInfo>();
        public RamInfo Ram { get; set; } = new RamInfo();
        public WindowsInfo Windows { get; set; } = new WindowsInfo();
        public List<VolumeInfo> Volumes { get; set; } = new List<VolumeInfo>();

        // ---- Удобные выводы, на которых строятся все решения ----

        public VolumeInfo? SystemVolume =>
            Volumes.FirstOrDefault(v => v.IsSystem) ?? Volumes.FirstOrDefault();

        public bool HasSsd => Volumes.Any(v => v.Media == MediaKind.Ssd);
        public bool HasHdd => Volumes.Any(v => v.Media == MediaKind.Hdd);

        /// <summary>Очень слабый процессор — тяжёлые операции не предлагаем.</summary>
        public bool WeakCpu => Cpu.Tier is CpuTier.VeryWeak or CpuTier.Weak;

        /// <summary>Есть ли у нас способ вообще читать видеокарту.</summary>
        public bool GpuMonitoringWorks => !string.IsNullOrEmpty(GpuMonitoringMethod);

        /// <summary>Чем программа собирается мониторить видеокарту.</summary>
        public string GpuMonitoringMethod { get; set; } = "";

        /// <summary>Русское согласование числительного с существительным.</summary>
        private static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10;
            int m100 = n % 100;

            if (m10 == 1 && m100 != 11)
            {
                return one;
            }

            if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20))
            {
                return few;
            }

            return many;
        }

        /// <summary>
        /// Что программа подстроила под этот конкретный компьютер.
        /// Список собирается из данных сканирования, поэтому всегда честный.
        /// </summary>
        public List<string> BuildNotes()
        {
            var notes = new List<string>();

            // ---- процессор ----
            notes.Add("Процессор: " + Cpu.Name + " — "
                + Cpu.Cores + " " + Plural(Cpu.Cores, "ядро", "ядра", "ядер") + ", "
                + Cpu.Threads + " " + Plural(Cpu.Threads, "поток", "потока", "потоков")
                + ", класс «" + Cpu.TierName + "».");

            notes.Add(Cpu.Tier switch
            {
                CpuTier.VeryWeak =>
                    "Процессор очень слабый: тяжёлые оптимизации не предлагаются, "
                    + "анимации упрощены, чтобы не грузить машину.",
                CpuTier.Weak =>
                    "Процессор слабый: службы лучше отключать по одной и проверять, "
                    + "как система себя ведёт.",
                CpuTier.Normal =>
                    "Процессор средний: доступны все оптимизации.",
                _ =>
                    "Процессор мощный: доступны все оптимизации, ограничений нет."
            });

            // ---- видеокарта ----
            if (AllGpus.Count == 0)
            {
                notes.Add("Видеокарта не определена — мониторинг GPU будет недоступен.");
            }
            else
            {
                string memory = Gpu.MemoryMb > 0
                    ? AutoOptimizer.FormatSize(Gpu.MemoryMb * 1024L * 1024L)
                    : "объём неизвестен";

                notes.Add("Видеокарта: " + Gpu.Name + " (" + Gpu.VendorName + ", " + memory + ").");

                notes.Add(GpuMonitoringMethod switch
                {
                    "nvidia-smi" =>
                        "Карта NVIDIA: слежу через nvidia-smi — загрузка, температура, "
                        + "видеопамять и частота.",
                    "счётчики Windows" =>
                        "Карта не NVIDIA: слежу через счётчики Windows — показывает загрузку. "
                        + "Температуру дают только утилиты производителя (например, MSI Afterburner).",
                    _ =>
                        "Видеокарта не поддерживается, мониторинг GPU недоступен."
                });

                if (AllGpus.Count > 1)
                {
                    notes.Add("Всего видеодаптеров: " + AllGpus.Count
                        + " — считаю основной самую производительную.");
                }
            }

            // ---- память ----
            string ram = "Память: " + (Ram.TotalMb / 1024) + " ГБ";

            if (Ram.TypeName != "неизвестно")
            {
                ram += " " + Ram.TypeName;
            }

            if (Ram.SpeedMhz > 0)
            {
                ram += ", " + Ram.SpeedMhz + " МГц";
            }

            if (Ram.Slots > 0)
            {
                ram += ", занято слотов " + Ram.Modules + " из " + Ram.Slots;
            }

            notes.Add(ram + ".");

            notes.Add(Ram.LowMemory
                ? "Памяти меньше 8 ГБ: очистка памяти работает агрессивнее, "
                    + "фоновые программы закрываются охотнее."
                : "Памяти достаточно: очистка памяти обычная.");

            // ---- диски ----
            if (Volumes.Count == 0)
            {
                notes.Add("Диски определить не удалось — обслуживание дисков недоступно.");
            }

            foreach (VolumeInfo volume in Volumes)
            {
                string model = volume.Model.Length > 0 ? volume.Model : "модель неизвестна";
                string size = volume.TotalBytes > 0
                    ? AutoOptimizer.FormatSize(volume.TotalBytes)
                    : "объём неизвестен";

                string what = volume.Media switch
                {
                    MediaKind.Ssd => "SSD — дефрагментация не предлагается, вместо неё TRIM.",
                    MediaKind.Hdd => "жёсткий диск — предлагается дефрагментация.",
                    _ => "тип не определён — обслуживание отдаю Windows."
                };

                notes.Add("Диск " + volume.Letter + " (" + model + ", " + size + "): " + what);
            }

            // ---- система ----
            notes.Add(Windows.Name + " " + Windows.Version + ", сборка " + Windows.BuildFull
                + " — службы и функции сверяются с тем, что реально есть в этой системе.");

            return notes;
        }
    }

    // ==================== Сканирование ====================

    /// <summary>
    /// Определяет, что за компьютер: процессор, видеокарту, память, диски
    /// и версию Windows. Всё через WMI, реестр и опрос самих накопителей,
    /// поэтому работает на любой машине, а не только на одной конкретной.
    /// </summary>
    internal static class HardwareScan
    {
        private const string CimV2 = @"root\CIMV2";
        private const string Storage = @"root\Microsoft\Windows\Storage";

        private const string AdapterClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        // ---------------- Полное сканирование ----------------

        public static SystemReport Scan()
        {
            var report = new SystemReport
            {
                ScannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                FromCache = false
            };

            try
            {
                report.Windows = ReadWindows();
            }
            catch
            {
                // Оставим значения по умолчанию.
            }

            try
            {
                report.Cpu = ReadCpu();
            }
            catch
            {
                // Оставим значения по умолчанию.
            }

            try
            {
                report.Ram = ReadRam();
            }
            catch
            {
                // Оставим значения по умолчанию.
            }

            try
            {
                ReadGpus(report);
            }
            catch
            {
                // Оставим значения по умолчанию.
            }

            try
            {
                ReadVolumes(report);
            }
            catch
            {
                // Оставим значения по умолчанию.
            }

            report.GpuMonitoringMethod = DescribeGpuMonitoring(report);
            report.Fingerprint = CheapFingerprint();

            return report;
        }

        // ---------------- Процессор ----------------

        private static CpuInfo ReadCpu()
        {
            var cpu = new CpuInfo();

            // Основные данные берём из реестра: он есть всегда и не тормозит.
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

                if (key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0)
                {
                    cpu.Name = name.Trim();
                }

                if (key?.GetValue("~MHz") is int mhz && mhz > 0)
                {
                    cpu.BaseMhz = (uint)mhz;
                    cpu.MaxMhz = (uint)mhz;
                }
            }
            catch
            {
                // Ничего страшного, попробуем WMI.
            }

            // Ядра, потоки и максимальную частоту уточняем через WMI.
            foreach (ManagementBaseObject item in Wmi(CimV2, "Win32_Processor"))
            {
                string wmiName = Str(item, "Name");
                if (wmiName.Length > 0 && cpu.Name == "неизвестно")
                {
                    cpu.Name = wmiName;
                }

                cpu.Cores = (int)U32(item, "NumberOfCores");
                cpu.Threads = (int)U32(item, "NumberOfLogicalProcessors");

                uint max = U32(item, "MaxClockSpeed");
                if (max > 0)
                {
                    cpu.MaxMhz = max;

                    if (cpu.BaseMhz == 0)
                    {
                        cpu.BaseMhz = max;
                    }
                }

                break;
            }

            if (cpu.Threads <= 0)
            {
                cpu.Threads = Environment.ProcessorCount;
            }

            if (cpu.Cores <= 0)
            {
                cpu.Cores = Math.Max(1, cpu.Threads / 2);
            }

            cpu.Tier = cpu.Threads switch
            {
                <= 2 => CpuTier.VeryWeak,
                <= 4 => CpuTier.Weak,
                <= 12 => CpuTier.Normal,
                _ => CpuTier.Strong
            };

            // Очень низкая частота тоже говорит о слабом процессоре:
            // ядер может быть много, а толку мало (старые Atom, нетбуки).
            if (cpu.MaxMhz > 0)
            {
                if (cpu.Tier == CpuTier.Normal && cpu.MaxMhz < 1800)
                {
                    cpu.Tier = CpuTier.Weak;
                }
                else if (cpu.Tier == CpuTier.Strong && cpu.MaxMhz < 1200)
                {
                    cpu.Tier = CpuTier.Normal;
                }
            }

            return cpu;
        }

        // ---------------- Оперативная память ----------------

        private static RamInfo ReadRam()
        {
            var ram = new RamInfo();

            ulong totalMb = 0;
            int speed = 0;
            int modules = 0;
            uint smbiosType = 0;

            foreach (ManagementBaseObject item in Wmi(CimV2, "Win32_PhysicalMemory"))
            {
                ulong capacity = U64(item, "Capacity");
                if (capacity > 0)
                {
                    totalMb += capacity / 1024 / 1024;
                    modules++;
                }

                uint configured = U32(item, "ConfiguredClockSpeed");
                uint rated = U32(item, "Speed");
                uint found = configured > 0 ? configured : rated;

                // Модули могут быть с разной частотой — берём самую высокую.
                if (found > (uint)speed)
                {
                    speed = (int)found;
                }

                uint type = U32(item, "SMBIOSMemoryType");
                if (type == 0)
                {
                    type = U32(item, "MemoryType");
                }

                if (type > 0)
                {
                    smbiosType = type;
                }
            }

            foreach (ManagementBaseObject item in Wmi(CimV2, "Win32_PhysicalMemoryArray"))
            {
                ram.Slots = (int)U32(item, "MemoryDevices");
                break;
            }

            if (totalMb == 0)
            {
                // WMI не ответил — берём общий объём у системы.
                var status = new NativeMethods.MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
                };

                if (NativeMethods.GlobalMemoryStatusEx(ref status))
                {
                    totalMb = status.ullTotalPhys / 1024 / 1024;
                }
            }

            ram.TotalMb = totalMb;
            ram.SpeedMhz = speed;
            ram.Modules = modules;
            ram.TypeName = DescribeDdrType(smbiosType);

            if (ram.Slots <= 0)
            {
                ram.Slots = modules;
            }

            return ram;
        }

        private static string DescribeDdrType(uint smbiosType) => smbiosType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            27 => "LPDDR",
            28 => "LPDDR2",
            29 => "LPDDR3",
            30 => "LPDDR4",
            34 => "DDR5",
            35 => "LPDDR5",
            0 => "неизвестно",
            _ => "тип " + smbiosType
        };

        // ---------------- Видеокарты ----------------

        private static void ReadGpus(SystemReport report)
        {
            var found = new List<GpuInfo>();

            foreach (ManagementBaseObject item in Wmi(CimV2, "Win32_VideoController"))
            {
                string name = Str(item, "Name");
                if (name.Length == 0)
                {
                    continue;
                }

                string pnp = Str(item, "PNPDeviceID");

                found.Add(new GpuInfo
                {
                    Name = name,
                    Vendor = DetectVendor(pnp, name),
                    Driver = Str(item, "DriverVersion"),
                    MemoryMb = (int)(U64(item, "AdapterRAM") / 1024 / 1024)
                });
            }

            // Уточняем объём видеопамяти: AdapterRAM обрезается на 4 ГБ.
            foreach (GpuInfo gpu in found)
            {
                int real = ReadRealVram(gpu);
                if (real > gpu.MemoryMb)
                {
                    gpu.MemoryMb = real;
                }
            }

            // Основной считаем дискретную карту, а не встроенную.
            GpuInfo? main = found
                .OrderByDescending(g => VendorScore(g.Vendor))
                .ThenByDescending(g => g.MemoryMb)
                .FirstOrDefault();

            if (main is not null)
            {
                main.IsMain = true;
                report.Gpu = main;
            }

            report.AllGpus = found;
        }

        private static int VendorScore(GpuVendor vendor) => vendor switch
        {
            GpuVendor.Nvidia => 4,
            GpuVendor.Amd => 3,
            GpuVendor.Intel => 1,
            GpuVendor.Microsoft => 0,
            _ => 2
        };

        private static GpuVendor DetectVendor(string pnpDeviceId, string name)
        {
            string pnp = pnpDeviceId.ToUpperInvariant();

            if (pnp.Contains("VEN_10DE"))
            {
                return GpuVendor.Nvidia;
            }

            if (pnp.Contains("VEN_1002") || pnp.Contains("VEN_1022"))
            {
                return GpuVendor.Amd;
            }

            if (pnp.Contains("VEN_8086"))
            {
                return GpuVendor.Intel;
            }

            // Если идентификатора нет — смотрим на название.
            string lower = name.ToLowerInvariant();

            if (lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("quadro")
                || lower.Contains("rtx ") || lower.Contains("gtx "))
            {
                return GpuVendor.Nvidia;
            }

            if (lower.Contains("radeon") || lower.Contains("amd ") || lower.Contains("ati "))
            {
                return GpuVendor.Amd;
            }

            if (lower.Contains("intel") || lower.Contains("hd graphics") || lower.Contains("iris")
                || lower.Contains("uhd graphics"))
            {
                return GpuVendor.Intel;
            }

            if (lower.Contains("microsoft basic"))
            {
                return GpuVendor.Microsoft;
            }

            return GpuVendor.Unknown;
        }

        /// <summary>
        /// Настоящий объём видеопамяти. У NVIDIA его отдаёт nvidia-smi,
        /// у остальных — реестр драйвера дисплея.
        /// </summary>
        private static int ReadRealVram(GpuInfo gpu)
        {
            if (gpu.Vendor == GpuVendor.Nvidia)
            {
                string? smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");

                if (File.Exists(smi))
                {
                    string? output = RunTool(smi,
                        "--query-gpu=memory.total --format=csv,noheader,nounits", 4000);

                    if (output is not null)
                    {
                        string first = output
                            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault() ?? "";

                        if (int.TryParse(first.Trim(), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int mb) && mb > 0)
                        {
                            return mb;
                        }
                    }
                }
            }

            return ReadVramFromRegistry(gpu.Name);
        }

        private static int ReadVramFromRegistry(string gpuName)
        {
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(AdapterClassKey);
                if (root is null)
                {
                    return 0;
                }

                foreach (string sub in root.GetSubKeyNames())
                {
                    using RegistryKey? key = root.OpenSubKey(sub);
                    if (key is null)
                    {
                        continue;
                    }

                    string desc = key.GetValue("DriverDesc") as string ?? "";

                    if (desc.Length == 0 || !NamesMatch(desc, gpuName))
                    {
                        continue;
                    }

                    using RegistryKey? info = key.OpenSubKey("HardwareInformation");
                    if (info is null)
                    {
                        continue;
                    }

                    // Правильное значение — 64-битное.
                    if (info.GetValue("qwMemorySize") is long qw && qw > 0)
                    {
                        return (int)(qw / 1024 / 1024);
                    }

                    if (info.GetValue("MemorySize") is byte[] raw && raw.Length >= 4)
                    {
                        long size = raw.Length >= 8
                            ? BitConverter.ToInt64(raw, 0)
                            : BitConverter.ToUInt32(raw, 0);

                        if (size > 0)
                        {
                            return (int)(size / 1024 / 1024);
                        }
                    }

                    if (info.GetValue("MemorySize") is int dw && dw > 0)
                    {
                        return dw / 1024 / 1024;
                    }
                }
            }
            catch
            {
                // Не смогли — значит покажем то, что дал WMI.
            }

            return 0;
        }

        private static bool NamesMatch(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0)
            {
                return false;
            }

            string x = a.Trim().ToLowerInvariant();
            string y = b.Trim().ToLowerInvariant();

            if (x == y)
            {
                return true;
            }

            int take = Math.Min(10, Math.Min(x.Length, y.Length));
            return take >= 4 && x[..take] == y[..take];
        }

        /// <summary>Каким способом получится следить за видеокартой на этой машине.</summary>
        private static string DescribeGpuMonitoring(SystemReport report)
        {
            if (report.AllGpus.Count == 0)
            {
                return "";
            }

            if (report.Gpu.Vendor == GpuVendor.Nvidia)
            {
                string smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
                return File.Exists(smi) ? "nvidia-smi" : "";
            }

            if (report.Gpu.Vendor is GpuVendor.Amd or GpuVendor.Intel or GpuVendor.Unknown)
            {
                // Универсальные счётчики производительности Windows.
                return GpuCounters.IsAvailable() ? "счётчики Windows" : "";
            }

            return "";
        }

        // ---------------- Диски ----------------

        private static void ReadVolumes(SystemReport report)
        {
            // Системный диск вычисляем от папки Windows, а если и её нет —
            // от папки самой системы. Литерал с буквой диска не нужен.
            string systemRoot = Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                ?? Path.GetPathRoot(Environment.SystemDirectory)
                ?? "";

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    var volume = new VolumeInfo
                    {
                        Letter = drive.Name.TrimEnd('\\'),
                        IsSystem = string.Equals(
                            drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)
                    };

                    try
                    {
                        volume.Label = drive.VolumeLabel;
                    }
                    catch
                    {
                        volume.Label = "";
                    }

                    try
                    {
                        volume.FileSystem = drive.DriveFormat;
                    }
                    catch
                    {
                        volume.FileSystem = "";
                    }

                    try
                    {
                        volume.TotalBytes = drive.TotalSize;
                        volume.FreeBytes = drive.AvailableFreeSpace;
                    }
                    catch
                    {
                        volume.TotalBytes = 0;
                        volume.FreeBytes = 0;
                    }

                    // Спрашиваем сам накопитель: он вращается или нет.
                    (string model, MediaKind media) = StorageProbe.Probe(volume.Letter);
                    volume.Model = model;
                    volume.Media = media;

                    report.Volumes.Add(volume);
                }
                catch
                {
                    // Один проблемный том не должен ломать весь отчёт.
                }
            }
        }

        // ---------------- Windows ----------------

        private static WindowsInfo ReadWindows()
        {
            var info = new WindowsInfo
            {
                Arch = Environment.Is64BitOperatingSystem ? "64-бит" : "32-бит"
            };

            // Точную сборку даёт только RtlGetVersion.
            try
            {
                var version = new NativeMethods.OSVERSIONINFOEXW
                {
                    dwOSVersionInfoSize = (uint)Marshal.SizeOf<NativeMethods.OSVERSIONINFOEXW>(),
                    szCSDVersion = ""
                };

                if (NativeMethods.RtlGetVersion(ref version) == 0)
                {
                    info.Build = (int)version.dwBuildNumber;
                }
            }
            catch
            {
                info.Build = Environment.OSVersion.Version.Build;
            }

            string product = "Windows";
            string display = "";
            int ubr = 0;

            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

                if (key is not null)
                {
                    product = key.GetValue("ProductName") as string ?? "Windows";
                    display = key.GetValue("DisplayVersion") as string
                        ?? key.GetValue("ReleaseId") as string ?? "";

                    if (key.GetValue("UBR") is int rev)
                    {
                        ubr = rev;
                    }

                    if (info.Build == 0 &&
                        int.TryParse(key.GetValue("CurrentBuildNumber") as string, out int build))
                    {
                        info.Build = build;
                    }
                }
            }
            catch
            {
                // Обойдёмся тем, что уже есть.
            }

            // Важная тонкость: Windows 11 в реестре до сих пор называется
            // «Windows 10». Различаем по номеру сборки.
            if (info.Build >= 22000 && product.Contains("Windows 10"))
            {
                product = product.Replace("Windows 10", "Windows 11");
            }

            info.Name = product;
            info.Version = info.Build >= 22000 ? "11" : "10";
            info.DisplayVersion = display;
            info.BuildFull = ubr > 0 ? info.Build + "." + ubr : info.Build.ToString();

            return info;
        }

        // ---------------- Отпечаток железа ----------------

        /// <summary>
        /// Дешёвая проверка «железо не поменялось». Обходимся реестром,
        /// опросом накопителей и памятью — без WMI, поэтому это быстро.
        /// </summary>
        public static string CheapFingerprint()
        {
            var parts = new List<string>();

            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

                parts.Add(key?.GetValue("ProcessorNameString") as string ?? "cpu?");
            }
            catch
            {
                parts.Add("cpu?");
            }

            parts.Add(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));

            try
            {
                var status = new NativeMethods.MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
                };

                if (NativeMethods.GlobalMemoryStatusEx(ref status))
                {
                    // Округляем до 256 МБ: мелкие колебания не считаем сменой железа.
                    parts.Add((status.ullTotalPhys / 1024 / 1024 / 256).ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                parts.Add("ram?");
            }

            try
            {
                var version = new NativeMethods.OSVERSIONINFOEXW
                {
                    dwOSVersionInfoSize = (uint)Marshal.SizeOf<NativeMethods.OSVERSIONINFOEXW>(),
                    szCSDVersion = ""
                };

                if (NativeMethods.RtlGetVersion(ref version) == 0)
                {
                    parts.Add(version.dwBuildNumber.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                parts.Add("os?");
            }

            // Названия видеокарт из реестра драйверов дисплея.
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(AdapterClassKey);

                if (root is not null)
                {
                    var names = new List<string>();

                    foreach (string sub in root.GetSubKeyNames())
                    {
                        using RegistryKey? key = root.OpenSubKey(sub);
                        if (key?.GetValue("DriverDesc") is string desc && desc.Length > 0)
                        {
                            names.Add(desc);
                        }
                    }

                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    parts.Add(string.Join(",", names));
                }
            }
            catch
            {
                parts.Add("gpu?");
            }

            // Диски: буква + модель + тип.
            try
            {
                var disks = new List<string>();

                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    (string model, MediaKind media) = StorageProbe.Probe(drive.Name.TrimEnd('\\'));
                    disks.Add(drive.Name + model + media);
                }

                disks.Sort(StringComparer.OrdinalIgnoreCase);
                parts.Add(string.Join(",", disks));
            }
            catch
            {
                parts.Add("disk?");
            }

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts)));

            return Convert.ToHexString(hash, 0, 8);
        }

        // ---------------- Вспомогательное ----------------

        private static IEnumerable<ManagementBaseObject> Wmi(string scope, string className)
        {
            List<ManagementBaseObject> list = new();

            try
            {
                var searcher = new ManagementObjectSearcher(
                    new ManagementScope(scope),
                    new ObjectQuery("SELECT * FROM " + className));

                foreach (ManagementBaseObject item in searcher.Get())
                {
                    list.Add(item);
                }
            }
            catch
            {
                // Нет класса, нет прав, сломан WMI — вернём пусто, вызывающий разберётся.
            }

            return list;
        }

        internal static string Str(ManagementBaseObject item, string property)
        {
            try
            {
                return item[property] as string ?? "";
            }
            catch
            {
                return "";
            }
        }

        internal static uint U32(ManagementBaseObject item, string property)
        {
            try
            {
                object? value = item[property];
                return value is null ? 0u : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        internal static ulong U64(ManagementBaseObject item, string property)
        {
            try
            {
                object? value = item[property];
                return value is null ? 0ul : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Запустить консольную утилиту и вернуть её вывод. Без падений.</summary>
        internal static string? RunTool(string exePath, string arguments, int timeoutMs)
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using Process? process = Process.Start(info);
                if (process is null)
                {
                    return null;
                }

                string output = process.StandardOutput.ReadToEnd();

                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                        // Уже завершился.
                    }

                    return null;
                }

                return output;
            }
            catch
            {
                return null;
            }
        }
    }

    // ==================== Опрос накопителя ====================

    /// <summary>
    /// Определяет, вращающийся диск или твердотельный, спрашивая об этом
    /// сам накопитель. Работает одинаково у всех производителей.
    /// </summary>
    internal static class StorageProbe
    {
        public static (string Model, MediaKind Media) Probe(string volumeRoot)
        {
            string model = "";
            MediaKind media = MediaKind.Unknown;

            string path = volumeRoot.EndsWith(":", StringComparison.Ordinal)
                ? @"\\.\" + volumeRoot
                : @"\\.\" + volumeRoot.TrimEnd('\\');

            IntPtr handle = NativeMethods.CreateFile(
                path,
                0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle == NativeMethods.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            {
                // Некоторые системы не отдают том без прав на чтение.
                handle = NativeMethods.CreateFile(
                    path,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);
            }

            if (handle == NativeMethods.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            {
                return (model, media);
            }

            try
            {
                model = QueryModel(handle);
                media = QueryMedia(handle);
            }
            catch
            {
                // Оставим то, что успели узнать.
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }

            return (model, media);
        }

        private static string QueryModel(IntPtr handle)
        {
            int size = 1024;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            IntPtr query = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>());

            try
            {
                var property = new NativeMethods.STORAGE_PROPERTY_QUERY
                {
                    PropertyId = NativeMethods.StorageDeviceProperty,
                    QueryType = NativeMethods.PropertyStandardQuery
                };

                Marshal.StructureToPtr(property, query, false);

                if (!NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                        query,
                        (uint)Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>(),
                        buffer,
                        (uint)size,
                        out uint returned,
                        IntPtr.Zero) || returned < 36)
                {
                    return "";
                }

                var descriptor = Marshal.PtrToStructure<NativeMethods.STORAGE_DEVICE_DESCRIPTOR>(buffer);

                string vendor = ReadAnsi(buffer, size, (int)descriptor.VendorIdOffset);
                string product = ReadAnsi(buffer, size, (int)descriptor.ProductIdOffset);

                string result = (vendor + " " + product).Trim();

                // Приводим обычный вид: «Samsung SSD 860 EVO 500GB».
                return result.Length > 0 ? result : "";
            }
            catch
            {
                return "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(query);
            }
        }

        private static MediaKind QueryMedia(IntPtr handle)
        {
            IntPtr query = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>());
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.DEVICE_SEEK_PENALTY_DESCRIPTOR>());

            try
            {
                var property = new NativeMethods.STORAGE_PROPERTY_QUERY
                {
                    PropertyId = NativeMethods.StorageDeviceSeekPenaltyProperty,
                    QueryType = NativeMethods.PropertyStandardQuery
                };

                Marshal.StructureToPtr(property, query, false);

                if (!NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                        query,
                        (uint)Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>(),
                        buffer,
                        (uint)Marshal.SizeOf<NativeMethods.DEVICE_SEEK_PENALTY_DESCRIPTOR>(),
                        out _,
                        IntPtr.Zero))
                {
                    return MediaKind.Unknown;
                }

                var penalty = Marshal.PtrToStructure<NativeMethods.DEVICE_SEEK_PENALTY_DESCRIPTOR>(buffer);

                // Есть штраф за позиционирование — значит механика.
                return penalty.IncursSeekPenalty != 0 ? MediaKind.Hdd : MediaKind.Ssd;
            }
            catch
            {
                return MediaKind.Unknown;
            }
            finally
            {
                Marshal.FreeHGlobal(query);
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadAnsi(IntPtr buffer, int bufferSize, int offset)
        {
            if (offset <= 0 || offset >= bufferSize)
            {
                return "";
            }

            var bytes = new List<byte>();

            for (int i = offset; i < bufferSize; i++)
            {
                byte b = Marshal.ReadByte(buffer, i);

                if (b == 0)
                {
                    break;
                }

                bytes.Add(b);
            }

            // Строки в дескрипторе в кодировке системы — для названий моделей
            // это чистый ASCII, поэтому читаем как Latin1 без потерь.
            return Encoding.Latin1.GetString(bytes.ToArray()).Trim();
        }
    }

    // ==================== Хранилище отчёта ====================

    /// <summary>
    /// Отчёт о железе лежит в profile.json рядом с настройками.
    /// При запуске берём его из файла, а если железо изменилось — сканируем заново.
    /// </summary>
    internal static class ProfileStore
    {
        private static readonly object Gate = new object();
        private static SystemReport? _current;

        private static string FilePath => Path.Combine(AppPaths.Folder, "profile.json");

        public static SystemReport Current
        {
            get
            {
                lock (Gate)
                {
                    return _current ??= LoadOrScan();
                }
            }
        }

        private static SystemReport LoadOrScan()
        {
            SystemReport? cached = ReadFile();

            if (cached is not null && cached.Fingerprint == HardwareScan.CheapFingerprint())
            {
                cached.FromCache = true;
                return cached;
            }

            SystemReport fresh = HardwareScan.Scan();
            Save(fresh);
            return fresh;
        }

        private static SystemReport? ReadFile()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<SystemReport>(json);
            }
            catch
            {
                // Битый или устаревший файл — просто отсканируем заново.
                return null;
            }
        }

        public static void Save(SystemReport report)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.Folder);

                    string json = JsonSerializer.Serialize(
                        report, new JsonSerializerOptions { WriteIndented = true });

                    File.WriteAllText(FilePath, json);
                }
                catch
                {
                    // Не смогли сохранить — не критично, отсканируем в следующий раз.
                }
            }
        }

        /// <summary>Принудительное пересканирование (кнопка «Сканировать заново»).</summary>
        public static SystemReport Rescan()
        {
            SystemReport fresh = HardwareScan.Scan();

            lock (Gate)
            {
                _current = fresh;
            }

            Save(fresh);
            AppLog.Write("Система", "железо пересканировано: " + fresh.Cpu.Name);

            return fresh;
        }
    }
}
