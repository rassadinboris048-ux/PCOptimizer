using System;
using System.Runtime.InteropServices;

namespace PCOptimizer
{
    /// <summary>
    /// Объявления функций Windows, которые нужны дашборду.
    /// Всё это — встроенные библиотеки системы, ничего ставить не требуется.
    /// </summary>
    internal static class NativeMethods
    {
        // ---------------- Оперативная память ----------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ---------------- Частота процессора ----------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESSOR_POWER_INFORMATION
        {
            public uint Number;
            public uint MaxMhz;
            public uint CurrentMhz;
            public uint MhzLimit;
            public uint MaxIdleState;
            public uint CurrentIdleState;
        }

        [DllImport("powrprof.dll")]
        internal static extern int CallNtPowerInformation(
            int informationLevel,
            IntPtr inputBuffer,
            uint inputBufferLength,
            IntPtr outputBuffer,
            uint outputBufferLength);

        internal const int ProcessorInformation = 11;

        // ---------------- Загрузка каждого ядра ----------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySystemInformation(
            int systemInformationClass,
            IntPtr systemInformation,
            int systemInformationLength,
            out int returnLength);

        internal const int SystemProcessorPerformanceInformation = 8;

        // ---------------- Общая загрузка (резервный способ) ----------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint LowDateTime;
            public uint HighDateTime;

            public readonly ulong Total => ((ulong)HighDateTime << 32) | LowDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(
            out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        // ---------------- Перетаскивание безрамочного окна ----------------

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ---------------- Службы Windows ----------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenService(IntPtr hSCManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(
            IntPtr hService, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfig(
            IntPtr hService, uint serviceType, uint startType, uint errorControl,
            string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password, string? displayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ControlService(IntPtr hService, uint control, ref SERVICE_STATUS status);

        internal const uint SC_MANAGER_CONNECT = 0x0001;
        internal const uint SERVICE_QUERY_STATUS = 0x0004;
        internal const uint SERVICE_CHANGE_CONFIG = 0x0002;
        internal const uint SERVICE_STOP = 0x0020;
        internal const uint SERVICE_START = 0x0010;
        internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        internal const uint SERVICE_CONTROL_STOP = 0x00000001;
        internal const uint SERVICE_RUNNING = 0x00000004;
        internal const uint SERVICE_STOPPED = 0x00000001;
        internal const uint SERVICE_DISABLED = 0x00000004;
        internal const uint SERVICE_DEMAND_START = 0x00000003;
        internal const int SC_STATUS_PROCESS_INFO = 0;

        // ---------------- Приоритет и закрытие процессов ----------------

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        internal const uint PROCESS_TERMINATE = 0x0001;
        internal const uint PROCESS_SET_INFORMATION = 0x0200;
        internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
        internal const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        internal const uint HIGH_PRIORITY_CLASS = 0x00000080;

        // ---------------- Сброс кэша оперативной памяти ----------------

        [DllImport("ntdll.dll")]
        internal static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        internal const int SystemMemoryListInformation = 80;
        internal const int MemoryPurgeStandbyList = 4;
        internal const int MemoryPurgeLowPriorityStandbyList = 5;

        // ---------------- Права процесса ----------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState,
            int bufferLength, IntPtr previousState, IntPtr returnLength);

        internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        internal const uint TOKEN_QUERY = 0x0008;
        internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        // ---------------- Информация о дисках ----------------
        //
        // Спрашиваем у самого накопителя, вращающийся он или твердотельный.
        // Это надёжнее, чем гадать по названию модели, и работает у всех
        // производителей одинаково.

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            IntPtr device,
            uint controlCode,
            IntPtr inBuffer,
            uint inBufferSize,
            IntPtr outBuffer,
            uint outBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        internal const uint GENERIC_READ = 0x80000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        internal const uint StorageDeviceProperty = 0;
        internal const uint StorageDeviceSeekPenaltyProperty = 7;
        internal const uint PropertyStandardQuery = 0;

        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            public byte AdditionalParameters;
        }

        /// <summary>Начало STORAGE_DEVICE_DESCRIPTOR: дальше в буфере лежат строки.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            public byte RemovableMedia;
            public byte CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public uint BusType;
            public uint RawPropertiesLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte IncursSeekPenalty;
        }

        // ---------------- Точная версия Windows ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct OSVERSIONINFOEXW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        // Environment.OSVersion врёт про сборку, а RtlGetVersion — нет.
        [DllImport("ntdll.dll", SetLastError = true)]
        internal static extern int RtlGetVersion(ref OSVERSIONINFOEXW versionInfo);

        // ---------------- Глобальные горячие клавиши ----------------

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal const int WM_HOTKEY = 0x0312;
        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const uint MOD_NOREPEAT = 0x4000;

        /// <summary>Включает привилегию текущего процесса (например, для сброса кэша памяти).</summary>
        internal static bool EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            {
                return false;
            }

            try
            {
                if (!LookupPrivilegeValue(null, name, out LUID luid))
                {
                    return false;
                }

                var state = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES
                    {
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    }
                };

                AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);

                // Успех определяется по последней ошибке: ERROR_NOT_ALL_ASSIGNED = 1300.
                return Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        internal const int WM_NCLBUTTONDOWN = 0x00A1;
        internal const int HTCAPTION = 0x0002;

        // ---------------- Корзина ----------------

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        /// <summary>Сколько всего лежит в корзине.</summary>
        internal static void GetRecycleBinInfo(out long size, out long count)
        {
            size = 0;
            count = 0;

            var info = new SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf<SHQUERYRBINFO>();

            if (SHQueryRecycleBin(null, ref info) == 0)
            {
                size = info.i64Size;
                count = info.i64NumItems;
            }
        }

        /// <summary>Очистить корзину молча, без вопросов и окна прогресса.</summary>
        internal static bool EmptyRecycleBin()
        {
            return SHEmptyRecycleBin(
                IntPtr.Zero, null,
                SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) == 0;
        }
    }
}
