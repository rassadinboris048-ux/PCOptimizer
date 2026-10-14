using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer
{
    // ==================== Модели ====================

    internal sealed class GameInfo
    {
        public int Pid { get; set; }
        public string Exe { get; set; } = "";
        public string Title { get; set; } = "";
        public string Path { get; set; } = "";
        public long MemoryMb { get; set; }
        public bool Guess { get; set; }
    }

    internal sealed class BackgroundProcess
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public long MemoryMb { get; set; }
    }

    internal sealed class GameStep
    {
        public string Icon { get; set; } = "ok";
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
    }

    // ==================== Справочник игр ====================

    internal static class GameCatalog
    {
        /// <summary>Известные исполняемые файлы игр. Сравнение точное, без учёта регистра.</summary>
        private static readonly (string Exe, string Title)[] Known =
        {
            ("cs2.exe", "Counter-Strike 2"),
            ("csgo.exe", "Counter-Strike: Global Offensive"),
            ("dota2.exe", "Dota 2"),
            ("ets2.exe", "Euro Truck Simulator 2"),
            ("eurotrucks2.exe", "Euro Truck Simulator 2"),
            ("ats.exe", "American Truck Simulator"),
            ("gta5.exe", "Grand Theft Auto V"),
            ("gta5_enhanced.exe", "Grand Theft Auto V Enhanced"),
            ("rdr2.exe", "Red Dead Redemption 2"),
            ("valorant.exe", "VALORANT"),
            ("leagueclient.exe", "League of Legends"),
            ("league of legends.exe", "League of Legends"),
            ("r5apex.exe", "Apex Legends"),
            ("fortnite.exe", "Fortnite"),
            ("tslgame.exe", "PUBG: BATTLEGROUNDS"),
            ("overwatch.exe", "Overwatch 2"),
            ("destiny2.exe", "Destiny 2"),
            ("warframe.x64.exe", "Warframe"),
            ("escapefromtarkov.exe", "Escape from Tarkov"),
            ("rustclient.exe", "Rust"),
            ("dayz.exe", "DayZ"),
            ("scum.exe", "SCUM"),
            ("witcher3.exe", "The Witcher 3"),
            ("cyberpunk2077.exe", "Cyberpunk 2077"),
            ("eldenring.exe", "Elden Ring"),
            ("sekiro.exe", "Sekiro"),
            ("dark souls iii.exe", "Dark Souls III"),
            ("minecraft.exe", "Minecraft"),
            ("javaw.exe", "Minecraft (Java)"),
            ("robloxplayerbeta.exe", "Roblox"),
            ("wow.exe", "World of Warcraft"),
            ("wowclassic.exe", "World of Warcraft Classic"),
            ("beamng.drive.x64.exe", "BeamNG.drive"),
            ("fs22game.exe", "Farming Simulator 22"),
            ("ac.exe", "Assetto Corsa"),
            ("ac2.exe", "Assetto Corsa Competizione"),
            ("hl2.exe", "Игра на движке Source"),
            ("terraria.exe", "Terraria"),
            ("stardew valley.exe", "Stardew Valley"),
            ("among us.exe", "Among Us"),
            ("palworld.exe", "Palworld"),
            ("helldivers2.exe", "Helldivers 2"),
            ("baldursgate3.exe", "Baldur's Gate 3"),
            ("bg3.exe", "Baldur's Gate 3"),
            ("starfield.exe", "Starfield"),
            ("hogwartslegacy.exe", "Hogwarts Legacy"),
            ("farcry6.exe", "Far Cry 6"),
            ("rainbowsix.exe", "Rainbow Six Siege"),
            ("r6siege.exe", "Rainbow Six Siege"),
            ("bf2042.exe", "Battlefield 2042"),
            ("cod.exe", "Call of Duty"),
            ("modernwarfare.exe", "Call of Duty: Modern Warfare")
        };

        /// <summary>Признаки папки с играми — для игр, которых нет в списке.</summary>
        private static readonly string[] GameFolders =
        {
            @"\steamapps\common\", @"\epic games\", @"\gog games\", @"\gog galaxy\games\",
            @"\riot games\", @"\battle.net\", @"\origin games\", @"\ea games\",
            @"\ubisoft\", @"\games\", @"\игры\"
        };

        public static string? TitleFor(string exeName)
        {
            foreach ((string exe, string title) in Known)
            {
                if (string.Equals(exe, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return title;
                }
            }

            return null;
        }

        public static bool LooksLikeGameFolder(string path)
        {
            string probe = path.ToLowerInvariant();

            foreach (string folder in GameFolders)
            {
                if (probe.Contains(folder))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // ==================== Справочник закрываемых процессов ====================

    internal static class ProcessCatalog
    {
        /// <summary>
        /// Что можно закрывать. Список закрытый: всё, чего в нём нет,
        /// не закрывается никогда.
        ///
        /// Браузеров, игровых лаунчеров и VPN здесь нет намеренно:
        /// их закрывать запрещено в любом случае, и они дополнительно
        /// защищены ключевыми словами в IsProtected.
        /// </summary>
        private static readonly (string Exe, string Title, string Category)[] Closable =
        {
            ("discord.exe", "Discord", "Мессенджер"),
            ("telegram.exe", "Telegram", "Мессенджер"),
            ("whatsapp.exe", "WhatsApp", "Мессенджер"),
            ("skype.exe", "Skype", "Мессенджер"),
            ("viber.exe", "Viber", "Мессенджер"),
            ("slack.exe", "Slack", "Мессенджер"),
            ("zoom.exe", "Zoom", "Связь"),

            ("utorrent.exe", "µTorrent", "Торрент"),
            ("bittorrent.exe", "BitTorrent", "Торрент"),
            ("qbittorrent.exe", "qBittorrent", "Торрент"),
            ("transmission-qt.exe", "Transmission", "Торрент"),
            ("thunder.exe", "Thunder", "Торрент"),

            ("spotify.exe", "Spotify", "Музыка"),
            ("yandexmusic.exe", "Яндекс Музыка", "Музыка"),
            ("music.ui.exe", "Groove Music", "Музыка"),
            ("vlc.exe", "VLC", "Медиа"),
            ("mpc-hc64.exe", "Media Player Classic", "Медиа"),
            ("potplayermini64.exe", "PotPlayer", "Медиа"),

            ("onedrive.exe", "OneDrive", "Облако"),
            ("dropbox.exe", "Dropbox", "Облако"),
            ("yandexdisk2.exe", "Яндекс Диск", "Облако"),
            ("googledrivefs.exe", "Google Диск", "Облако"),

            ("yourphone.exe", "Связь с телефоном", "Прочее"),
            ("phoneexperiencehost.exe", "Связь с телефоном", "Прочее"),
            ("translucenttb.exe", "TranslucentTB", "Прочее"),
            ("lightshot.exe", "Lightshot", "Прочее"),
            ("everything.exe", "Everything", "Прочее")
        };

        /// <summary>
        /// Вторая линия защиты: эти процессы не закрываем, даже если они
        /// случайно окажутся в списке выше. Система, антивирусы, драйверы,
        /// браузеры, игровые лаунчеры, VPN и их посредники.
        /// </summary>
        private static readonly string[] NeverClose =
        {
            "system", "registry", "idle", "smss", "csrss", "wininit", "winlogon", "services",
            "lsass", "lsaiso", "svchost", "dwm", "explorer", "fontdrvhost", "ctfmon", "audiodg",
            "sihost", "taskhostw", "runtimebroker", "searchindexer", "searchhost", "searchapp",
            "startmenuexperiencehost", "shellexperiencehost", "spoolsv", "wudfhost", "conhost",
            "wmiprvse", "dllhost", "backgroundtaskhost", "applicationframehost", "sppsvc",
            "trustedinstaller", "tiworker", "msiexec", "wuauclt", "usoclient", "smartscreen",

            // антивирусы и защита
            "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray", "windefend",
            "mpdefendercoreservice", "avp", "avastui", "avastsvc", "avgui", "avgsvc",
            "ekrn", "egui", "mcshield", "mcafee", "bdagent", "vsserv", "sophoshealth", "hipsvc",
            "kavfs", "kavfsslp", "nortonsecurity",

            // драйверы и оборудование
            "nvcontainer", "nvdisplay.container", "igfxem", "igfxtray", "rtkauduservice",
            "audiosrv", "atiesrxx", "atieclxx", "nvidia web helper",

            // VPN, прокси и их посредники — не закрываем никогда
            "happ", "happd", "xray", "v2ray", "sing-box", "singbox", "nekoray", "clash",
            "clash-meta", "clash-verge", "clash for windows", "shadowsocks", "ss-local", "ssr",
            "outline", "psiphon3", "lantern", "wireguard", "openvpn", "openvpn-gui",
            "openvpnserv", "openconnect", "nordvpn", "nordvpn-service", "expressvpn",
            "protonvpn", "protonvpnservice", "surfshark", "windscribe", "tunnelbear", "mullvad",
            "mullvad vpn", "mullvad-daemon", "mullvad-gui", "cloudflare warp", "warp-svc",
            "tailscale", "tailscaled", "zerotier-one_x64", "zerotier_desktop_ui",
            "vpnui", "vpnagent", "vpnclient", "vpncmgr", "proxifier", "proxycap",
            "rvrvpngui", "rvcontrolsvc", "connectify", "softether", "hideme", "anyconnect",
            "anydesk", "parsecd", "radmin",

            // браузеры — не закрываем ни один
            "chrome", "msedge", "firefox", "opera", "opera_gx", "browser", "brave", "vivaldi",
            "chromium", "iexplore", "waterfox", "palemoon", "librewolf", "tor browser",

            // античиты — их закрытие может привести к бану
            "anticheat", "easyanticheat", "battleye", "beservice", "bedaisy", "vgtray",
            "vgc", "mhyprot", "xhunter1", "nprotect", "gameguard", "sguard",

            // игровые лаунчеры и магазины — не закрываем ни один
            "steam", "steamservice", "steamwebhelper", "epicgameslauncher", "epicwebhelper",
            "battle.net", "galaxyclient", "galaxyclientservice", "riotclientservices",
            "riotclientux", "riotclientcrashhandler", "ubisoftconnect", "upc", "uplaywebcore",
            "origin", "eadesktop", "eabackgroundservice", "gog galaxy", "playnite", "heroic",
            "ovrserverlauncher", "ovrservice", "rockstargameslauncher", "socialclubhelper",
            "bethesdanetlauncher", "xboxapp",

            // консоли и наша программа
            "pwsh", "powershell", "cmd", "windowsterminal", "wt", "pcoptimizer"
        };

        /// <summary>
        /// Ключевые слова, по которым процесс закрывать нельзя ни при каких условиях.
        /// Третья линия защиты: ловит даже незнакомый VPN, браузер или лаунчер,
        /// которого нет в списке выше — например Happ, xray или новый клиент.
        /// </summary>
        private static readonly string[] ForbiddenKeywords =
        {
            // VPN, прокси и посредники
            "vpn", "proxy", "proxifier", "proxycap", "wireguard", "openvpn", "clash", "xray",
            "v2ray", "shadowsocks", "sing-box", "singbox", "nekoray", "outline", "psiphon",
            "lantern", "tailscale", "zerotier", "tunnelbear", "windscribe", "surfshark",
            "mullvad", "nordvpn", "expressvpn", "protonvpn", "happ", "warp", "softether",
            "anyconnect", "openconnect", "connectify", "socks", "radmin",

            // браузеры
            "chrome", "chromium", "msedge", "firefox", "opera", "brave", "vivaldi",
            "browser", "iexplore",

            // античиты — их закрытие может привести к бану
            "anticheat", "easyanticheat", "battleye", "beservice", "bedaisy", "vgtray",
            "vgc", "mhyprot", "xhunter1", "nprotect", "gameguard", "sguard",

            // игровые лаунчеры и магазины
            "steam", "epic", "battle.net", "galaxyclient", "riotclient", "ubisoft", "uplay",
            "origin", "eadesktop", "launcher", "playnite", "heroic", "gog"
        };

        /// <summary>
        /// То, что не закрывается НИКОГДА и ни при каком подтверждении:
        /// система, антивирусы, драйверы, консоли.
        /// </summary>
        private static readonly string[] SystemGuarded =
        {
            "system", "registry", "idle", "smss", "csrss", "wininit", "winlogon", "services",
            "lsass", "lsaiso", "svchost", "dwm", "explorer", "fontdrvhost", "ctfmon", "audiodg",
            "sihost", "taskhostw", "runtimebroker", "searchindexer", "searchhost", "searchapp",
            "startmenuexperiencehost", "shellexperiencehost", "spoolsv", "wudfhost", "conhost",
            "wmiprvse", "dllhost", "backgroundtaskhost", "applicationframehost", "sppsvc",
            "trustedinstaller", "tiworker", "msiexec", "wuauclt", "usoclient", "smartscreen",

            "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray", "windefend",
            "mpdefendercoreservice", "avp", "avastui", "avastsvc", "avgui", "avgsvc",
            "ekrn", "egui", "mcshield", "mcafee", "bdagent", "vsserv", "sophoshealth", "hipsvc",
            "kavfs", "kavfsslp", "nortonsecurity",

            "nvcontainer", "nvdisplay.container", "igfxem", "igfxtray", "rtkauduservice",
            "audiosrv", "atiesrxx", "atieclxx", "nvidia web helper",

            "pwsh", "powershell", "cmd", "windowsterminal", "wt", "pcoptimizer"
        };

        /// <summary>
        /// Браузеры. Единственная категория, которую можно закрыть —
        /// но только после отдельного подтверждения пользователя.
        /// </summary>
        private static readonly (string Exe, string Title)[] Browsers =
        {
            ("chrome.exe", "Google Chrome"),
            ("msedge.exe", "Microsoft Edge"),
            ("firefox.exe", "Mozilla Firefox"),
            ("opera.exe", "Opera"),
            ("opera_gx.exe", "Opera GX"),
            ("browser.exe", "Яндекс Браузер"),
            ("brave.exe", "Brave"),
            ("vivaldi.exe", "Vivaldi"),
            ("chromium.exe", "Chromium"),
            ("waterfox.exe", "Waterfox"),
            ("librewolf.exe", "LibreWolf"),
            ("iexplore.exe", "Internet Explorer")
        };

        private static string Normalize(string exeName)
        {
            string name = exeName.ToLowerInvariant();

            if (name.EndsWith(".exe", StringComparison.Ordinal))
            {
                name = name[..^4];
            }

            return name;
        }

        /// <summary>Система, антивирус, драйвер или консоль — не закрываем никогда.</summary>
        public static bool IsSystemProtected(string exeName)
        {
            string name = Normalize(exeName);

            foreach (string guarded in SystemGuarded)
            {
                if (name == guarded)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Точно ли это браузер из справочника.</summary>
        public static bool IsBrowser(string exeName)
        {
            foreach (var browser in Browsers)
            {
                if (string.Equals(browser.Exe, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static string BrowserTitle(string exeName)
        {
            foreach (var browser in Browsers)
            {
                if (string.Equals(browser.Exe, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return browser.Title;
                }
            }

            return exeName;
        }

        public static (string Title, string Category)? Describe(string exeName)
        {
            foreach ((string exe, string title, string category) in Closable)
            {
                if (string.Equals(exe, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return (title, category);
                }
            }

            return null;
        }

        public static bool IsProtected(string exeName)
        {
            string name = exeName.ToLowerInvariant();

            if (name.EndsWith(".exe", StringComparison.Ordinal))
            {
                name = name[..^4];
            }

            // Первая линия: точные имена.
            foreach (string guarded in NeverClose)
            {
                if (name == guarded)
                {
                    return true;
                }
            }

            // Вторая линия: ключевые слова. Сюда попадёт даже незнакомый
            // VPN, браузер или лаунчер, которого нет в списке выше.
            foreach (string keyword in ForbiddenKeywords)
            {
                if (name.Contains(keyword, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // ==================== Поиск игр и фоновых процессов ====================

    internal static class GameScanner
    {
        public static List<GameInfo> FindGames()
        {
            var found = new List<GameInfo>();
            int self = Environment.ProcessId;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == self)
                    {
                        continue;
                    }

                    string exe = process.ProcessName + ".exe";
                    string? known = GameCatalog.TitleFor(exe);

                    string path = "";
                    try
                    {
                        path = process.MainModule?.FileName ?? "";
                    }
                    catch
                    {
                        // Нет прав на чтение пути — не беда, название уже есть.
                    }

                    bool guess = false;

                    if (known is null)
                    {
                        // Незнакомая программа: считаем игрой, только если у неё
                        // есть окно и она лежит в игровой папке.
                        if (process.MainWindowHandle == IntPtr.Zero || path.Length == 0 ||
                            !GameCatalog.LooksLikeGameFolder(path))
                        {
                            continue;
                        }

                        known = Path.GetFileNameWithoutExtension(path);
                        guess = true;
                    }

                    found.Add(new GameInfo
                    {
                        Pid = process.Id,
                        Exe = exe,
                        Title = known,
                        Path = path,
                        MemoryMb = process.WorkingSet64 / 1024 / 1024,
                        Guess = guess
                    });
                }
                catch
                {
                    // Процесс завершился, пока мы его читали.
                }
                finally
                {
                    process.Dispose();
                }
            }

            found.Sort((a, b) => b.MemoryMb.CompareTo(a.MemoryMb));
            return found;
        }

        /// <summary>Запущенные браузеры — показываются в отдельном окне подтверждения.</summary>
        public static List<BackgroundProcess> FindBrowsers()
        {
            var list = new List<BackgroundProcess>();
            int self = Environment.ProcessId;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == self)
                    {
                        continue;
                    }

                    string exe = process.ProcessName + ".exe";

                    if (!ProcessCatalog.IsBrowser(exe))
                    {
                        continue;
                    }

                    list.Add(new BackgroundProcess
                    {
                        Pid = process.Id,
                        Name = exe,
                        Title = ProcessCatalog.BrowserTitle(exe),
                        Category = "Браузер",
                        MemoryMb = process.WorkingSet64 / 1024 / 1024
                    });
                }
                catch
                {
                    // Процесс уже закрылся.
                }
                finally
                {
                    process.Dispose();
                }
            }

            list.Sort((a, b) => b.MemoryMb.CompareTo(a.MemoryMb));
            return list;
        }

        public static List<BackgroundProcess> FindClosable()
        {
            var list = new List<BackgroundProcess>();
            int self = Environment.ProcessId;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == self)
                    {
                        continue;
                    }

                    string exe = process.ProcessName + ".exe";

                    var info = ProcessCatalog.Describe(exe);
                    if (info is null)
                    {
                        continue;
                    }

                    // Вторая линия защиты.
                    if (ProcessCatalog.IsProtected(exe))
                    {
                        continue;
                    }

                    list.Add(new BackgroundProcess
                    {
                        Pid = process.Id,
                        Name = exe,
                        Title = info.Value.Title,
                        Category = info.Value.Category,
                        MemoryMb = process.WorkingSet64 / 1024 / 1024
                    });
                }
                catch
                {
                    // Процесс уже закрылся.
                }
                finally
                {
                    process.Dispose();
                }
            }

            list.Sort((a, b) => b.MemoryMb.CompareTo(a.MemoryMb));
            return list;
        }
    }

    // ==================== Приоритет процесса ====================

    internal static class PriorityManager
    {
        public static string? Set(int pid, bool high)
        {
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_SET_INFORMATION | NativeMethods.PROCESS_QUERY_INFORMATION,
                false, pid);

            if (handle == IntPtr.Zero)
            {
                return "нет доступа к процессу — нужны права администратора";
            }

            try
            {
                uint priority = high
                    ? NativeMethods.HIGH_PRIORITY_CLASS
                    : NativeMethods.NORMAL_PRIORITY_CLASS;

                return NativeMethods.SetPriorityClass(handle, priority)
                    ? null
                    : "Windows отказалась менять приоритет";
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    // ==================== Закрытие процессов ====================

    internal static class ProcessCloser
    {
        /// <summary>
        /// Закрывает процесс: сначала вежливо, потом принудительно.
        /// Возвращает текст ошибки или null.
        /// </summary>
        public static (bool Closed, bool Forced, string? Error) Close(int pid, bool allowBrowsers)
        {
            Process? process = null;

            try
            {
                process = Process.GetProcessById(pid);
            }
            catch
            {
                return (true, false, null); // уже закрыт
            }

            try
            {
                // Систему, антивирусы, драйверы и консоли не закрываем никогда.
                if (ProcessCatalog.IsSystemProtected(process.ProcessName))
                {
                    return (false, false, "защищённый процесс");
                }

                // VPN, лаунчеры и всё прочее защищённое — тоже никогда.
                // Браузер закрываем только если пользователь подтвердил это
                // в отдельном окне и это точно браузер из справочника.
                if (ProcessCatalog.IsProtected(process.ProcessName) &&
                    !(allowBrowsers && ProcessCatalog.IsBrowser(process.ProcessName + ".exe")))
                {
                    return (false, false, "защищённый процесс");
                }

                // Сначала просим закрыться по-хорошему — так программа
                // успеет сохранить состояние.
                bool asked = false;
                try
                {
                    asked = process.CloseMainWindow();
                }
                catch
                {
                    // Нет окна — сразу переходим к принудительному закрытию.
                }

                if (asked && process.WaitForExit(3000))
                {
                    return (true, false, null);
                }

                process.Kill(entireProcessTree: false);
                process.WaitForExit(2000);
                return (true, true, null);
            }
            catch (Exception ex)
            {
                return (false, false, ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    // ==================== Очистка оперативной памяти ====================

    internal static class MemoryCleaner
    {
        /// <summary>
        /// Сбрасывает список ожидания (кэш файлов) — Windows освобождает
        /// зарезервированную память, не трогая работающие программы.
        /// aggressive = true нужен машинам, где памяти меньше 8 ГБ:
        /// чистится ещё и список ожидания с низким приоритетом.
        /// </summary>
        public static (long FreedMb, string? Error) Purge(bool aggressive = false)
        {
            long before = AvailableMb();

            if (!NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege"))
            {
                return (0, "не удалось получить нужное право (нужен запуск от администратора)");
            }

            int command = NativeMethods.MemoryPurgeStandbyList;
            int status = NativeMethods.NtSetSystemInformation(
                NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));

            if (status != 0)
            {
                return (0, "Windows отклонила запрос (код 0x" + status.ToString("X8", CultureInfo.InvariantCulture) + ")");
            }

            if (aggressive)
            {
                // На машинах с малым объёмом памяти дополнительно вычищаем
                // список ожидания с низким приоритетом: он самый «мусорный»,
                // и его освобождение ничего не ломает.
                int low = NativeMethods.MemoryPurgeLowPriorityStandbyList;
                NativeMethods.NtSetSystemInformation(
                    NativeMethods.SystemMemoryListInformation, ref low, sizeof(int));
            }

            System.Threading.Thread.Sleep(aggressive ? 1100 : 700);

            long after = AvailableMb();
            long freed = after - before;

            return (freed > 0 ? freed : 0, null);
        }

        private static long AvailableMb()
        {
            var status = new NativeMethods.MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();

            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return 0;
            }

            return (long)(status.ullAvailPhys / 1024 / 1024);
        }
    }

    // ==================== Game Bar и Game DVR ====================

    internal static class GameBarManager
    {
        private const string BackupPath = @"Software\PCOptimizer\GameBarBackup";

        private static readonly (string Path, string Name)[] Watched =
        {
            (@"System\GameConfigStore", "GameDVR_Enabled"),
            (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
            (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"),
            (@"Software\Microsoft\GameBar", "ShowStartupPanel"),
            (@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled")
        };

        public static bool IsDisabled()
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath);
            return backup is not null;
        }

        public static string? Disable()
        {
            try
            {
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
                        }
                    }
                }

                Write(@"System\GameConfigStore", "GameDVR_Enabled", 0);
                Write(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
                Write(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0);
                Write(@"Software\Microsoft\GameBar", "ShowStartupPanel", 0);
                Write(@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0);

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
                    Write(@"System\GameConfigStore", "GameDVR_Enabled", 1);
                    Write(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1);
                    return null;
                }

                foreach ((string path, string name) in Watched)
                {
                    object? value = backup.GetValue(path + "|" + name);
                    if (value is null)
                    {
                        continue;
                    }

                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(path);
                    key.SetValue(name, value, RegistryValueKind.DWord);
                }

                Registry.CurrentUser.DeleteSubKeyTree(BackupPath, throwOnMissingSubKey: false);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static void Write(string path, string name, int value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
    }
}
