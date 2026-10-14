using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PCOptimizer
{
    // ==================== Защита от онлайн-игр ====================

    /// <summary>
    /// Онлайн-игры и античиты. Если запущен хоть один такой процесс,
    /// игровые функции программы отключаются целиком: чтобы не поймать бан,
    /// мы не трогаем ни приоритеты, ни процессы, ни оверлей.
    /// </summary>
    internal static class OnlineGuard
    {
        private static readonly (string Exe, string Title)[] OnlineExe =
        {
            // Valve
            ("cs2.exe", "Counter-Strike 2"),
            ("csgo.exe", "Counter-Strike: GO"),
            ("cstrike.exe", "Counter-Strike"),
            ("dota2.exe", "Dota 2"),

            // Riot
            ("valorant.exe", "VALORANT"),
            ("valorant-win64-shipping.exe", "VALORANT"),
            ("league of legends.exe", "League of Legends"),
            ("leagueclient.exe", "League of Legends"),
            ("riotclientservices.exe", "Riot Client"),

            // шутеры
            ("tslgame.exe", "PUBG: BATTLEGROUNDS"),
            ("pubg.exe", "PUBG: BATTLEGROUNDS"),
            ("r5apex.exe", "Apex Legends"),
            ("r5apex_dx12.exe", "Apex Legends"),
            ("apex.exe", "Apex Legends"),
            ("fortnite.exe", "Fortnite"),
            ("fortniteclient-win64-shipping.exe", "Fortnite"),
            ("gta5.exe", "GTA V (онлайн)"),
            ("gta5_enhanced.exe", "GTA V (онлайн)"),
            ("fivem.exe", "FiveM"),
            ("rdr2.exe", "Red Dead Redemption 2"),
            ("rust.exe", "Rust"),
            ("rustclient.exe", "Rust"),
            ("escapefromtarkov.exe", "Escape from Tarkov"),
            ("destiny2.exe", "Destiny 2"),
            ("overwatch.exe", "Overwatch 2"),
            ("bf2042.exe", "Battlefield 2042"),
            ("cod.exe", "Call of Duty"),
            ("modernwarfare.exe", "Call of Duty"),
            ("rainbowsix.exe", "Rainbow Six Siege"),
            ("rainbowsix_dx11.exe", "Rainbow Six Siege"),
            ("worldoftanks.exe", "World of Tanks"),
            ("warthunder.exe", "War Thunder"),
            ("aces.exe", "War Thunder"),
            ("crossout.exe", "Crossout"),
            ("unturned.exe", "Unturned"),
            ("vrchat.exe", "VRChat"),
            ("blackwake.exe", "Blackwake"),
            ("totallyaccuratebattlegrounds.exe", "Totally Accurate Battlegrounds"),
            ("hl2.exe", "Source-игра (возможен онлайн)"),
            ("hl2mp.exe", "Half-Life 2: Deathmatch"),
            ("hl2_osx.exe", "Half-Life 2"),
            ("tf_win64.exe", "Team Fortress 2"),
            ("left4dead2.exe", "Left 4 Dead 2"),
            ("rocketleague.exe", "Rocket League"),
            ("deadbydaylight-win64-shipping.exe", "Dead by Daylight"),
            ("huntgame.exe", "Hunt: Showdown"),
            ("thecrew2.exe", "The Crew 2"),
            ("division2.exe", "The Division 2"),
            ("newworld.exe", "New World"),
            ("lostark.exe", "Lost Ark"),
            ("genshinimpact.exe", "Genshin Impact"),
            ("yuanshen.exe", "Genshin Impact"),
            ("starrail.exe", "Honkai: Star Rail"),
            ("minecraft.windows.exe", "Minecraft (возможен онлайн)"),
            ("palworld.exe", "Palworld (возможен онлайн)")
        };

        /// <summary>Античиты. Их присутствие — самый надёжный признак онлайн-игры.</summary>
        private static readonly string[] AntiCheat =
        {
            "easyanticheat", "easyanticheat_eos", "easyanticheatlauncher",
            "beservice", "bedaisy", "battleye",
            "vgtray", "vgc",
            "mhyprot", "mhyprot2", "mhyprot3",
            "xhunter1", "nprotect", "gameguard", "npggnt",
            "sguard", "sguarded", "ace-game", "anticheatexpert",
            "pubg heartbeatservice", "perfectworldanticheat"
        };

        /// <summary>Является ли файл запуска онлайн-игрой. Проверяется перед добавлением в список.</summary>
        public static bool IsOnlineExe(string exeName)
        {
            foreach ((string name, string _) in OnlineExe)
            {
                if (string.Equals(name, exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Найденные запущенные онлайн-игры.</summary>
        public static List<GameInfo> Detect()
        {
            var found = new List<GameInfo>();

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string exe = process.ProcessName.ToLowerInvariant() + ".exe";

                    foreach ((string name, string title) in OnlineExe)
                    {
                        if (name == exe)
                        {
                            found.Add(new GameInfo { Pid = process.Id, Exe = exe, Title = title });
                            break;
                        }
                    }
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

            return found;
        }

        /// <summary>Запущен ли античит.</summary>
        public static string? DetectAntiCheat()
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName.ToLowerInvariant();

                    foreach (string cheat in AntiCheat)
                    {
                        if (name.Contains(cheat, StringComparison.Ordinal))
                        {
                            return process.ProcessName;
                        }
                    }
                }
                catch
                {
                    // Ничего страшного.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return null;
        }

        /// <summary>
        /// Заблокированы ли игровые функции. Причина пишется в reason.
        /// </summary>
        public static bool IsBlocked(out string reason)
        {
            List<GameInfo> online = Detect();

            if (online.Count > 0)
            {
                reason = online[0].Title;
                return true;
            }

            reason = "";
            return false;
        }

        /// <summary>
        /// Запущен ли античит. Само по себе игровые функции НЕ блокирует:
        /// например Vanguard от Riot висит в системе постоянно, даже когда
        /// игра не запущена. Но приоритет процесса при античите не трогаем.
        /// </summary>
        public static bool AntiCheatRunning()
        {
            return DetectAntiCheat() is not null;
        }
    }

    // ==================== Установленные одиночные игры ====================

    internal sealed class InstalledGame
    {
        public string AppId { get; set; } = "";
        public string Title { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Exe { get; set; } = "";
    }

    /// <summary>
    /// Ищет одиночные игры в библиотеках Steam. Онлайн-игры сюда не попадают:
    /// список закрытый, а перед запуском дополнительно срабатывает OnlineGuard.
    /// </summary>
    internal static class GameLibrary
    {
        /// <summary>Одиночные игры: имя папки Steam → исполняемый файл.</summary>
        private static readonly (string Folder, string Exe, string Title)[] SinglePlayer =
        {
            ("Euro Truck Simulator 2", "eurotrucks2.exe", "Euro Truck Simulator 2"),
            ("American Truck Simulator", "amtrucks.exe", "American Truck Simulator"),
            ("BeamNG.drive", "BeamNG.drive.x64.exe", "BeamNG.drive"),
            ("The Forest", "TheForest.exe", "The Forest"),
            ("Sons Of The Forest", "SonsOfTheForest.exe", "Sons of the Forest"),
            ("Raft", "Raft.exe", "Raft"),
            ("7 Days To Die", "7DaysToDie.exe", "7 Days to Die"),
            ("Scrap Mechanic", "ScrapMechanic.exe", "Scrap Mechanic"),
            ("People Playground", "People Playground.exe", "People Playground"),
            ("Car Detailing Simulator", "Car Detailing Simulator.exe", "Car Detailing Simulator"),
            ("Orcs Must Die 3", "OrcsMustDie3.exe", "Orcs Must Die! 3"),
            ("Orcs Must Die Deathtrap", "OrcsMustDieDeathtrap.exe", "Orcs Must Die! Deathtrap"),
            ("Portal", "portal.exe", "Portal"),
            ("Portal 2", "portal2.exe", "Portal 2"),
            ("Derail Valley", "DerailValley.exe", "Derail Valley"),
            ("TGVVoyages", "TGVVoyages.exe", "TGV Voyages Train Simulator"),
            ("Subnautica", "Subnautica.exe", "Subnautica"),
            ("Subnautica Below Zero", "SubnauticaZero.exe", "Subnautica: Below Zero"),
            ("The Long Dark", "tld.exe", "The Long Dark"),
            ("Farming Simulator 2022", "FarmingSimulator2022.exe", "Farming Simulator 22"),
            ("Cities Skylines II", "Cities2.exe", "Cities: Skylines II"),
            ("Terraria", "Terraria.exe", "Terraria"),
            ("Stardew Valley", "Stardew Valley.exe", "Stardew Valley"),
            ("Aperture Desk Job", "ApertureDeskJob.exe", "Aperture Desk Job"),




            ("Skoof Simulator", "SkoofSimulator.exe", "Skoof Simulator"),
            ("Prison Boss VR", "PrisonBossVR.exe", "Prison Boss VR"),

            ("Sniper Elite 4", "SniperElite4.exe", "Sniper Elite 4"),
            ("Rise of the Tomb Raider", "ROTTR.exe", "Rise of the Tomb Raider"),
            ("The Witcher 3", "witcher3.exe", "The Witcher 3"),
            ("Cyberpunk 2077", "Cyberpunk2077.exe", "Cyberpunk 2077"),
            ("Elden Ring", "eldenring.exe", "Elden Ring"),
            ("Hogwarts Legacy", "HogwartsLegacy.exe", "Hogwarts Legacy"),
            ("Baldurs Gate 3", "bg3.exe", "Baldur's Gate 3"),
            ("Starfield", "Starfield.exe", "Starfield"),
            ("Skyrim Special Edition", "SkyrimSE.exe", "Skyrim Special Edition"),
            ("Fallout 4", "Fallout4.exe", "Fallout 4")
        };

        /// <summary>Папки библиотек Steam.</summary>
        public static List<string> SteamFolders()
        {
            var result = new List<string>();
            string? steam = FindSteam();

            if (steam is null)
            {
                return result;
            }

            result.Add(steam);

            try
            {
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");

                if (File.Exists(vdf))
                {
                    string text = File.ReadAllText(vdf);

                    foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"(.+?)\""))
                    {
                        string path = match.Groups[1].Value.Replace("\\\\", "\\");

                        if (!result.Contains(path) && Directory.Exists(path))
                        {
                            result.Add(path);
                        }
                    }
                }
            }
            catch
            {
                // Нет доступа к файлу библиотек — работаем с одной папкой.
            }

            return result;
        }

        private static string? FindSteam()
        {
            var candidates = new List<string>();

            // 1. Точный путь, который Steam сам о себе записал в реестр.
            foreach (string key in new[]
            {
                @"HKEY_CURRENT_USER\Software\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"
            })
            {
                foreach (string value in new[] { "SteamPath", "InstallPath" })
                {
                    try
                    {
                        if (Registry.GetValue(key, value, null) is string path &&
                            !string.IsNullOrWhiteSpace(path))
                        {
                            candidates.Add(path.Replace('/', '\\'));
                        }
                    }
                    catch
                    {
                        // Ключа нет — идём дальше.
                    }
                }
            }

            // 2. Стандартные места, собранные из переменных окружения.
            foreach (string root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432") ?? ""
            })
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    candidates.Add(Path.Combine(root, "Steam"));
                }
            }

            // 3. Рядом с библиотеками на других дисках.
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    string root = drive.Name;
                    candidates.Add(Path.Combine(root, "SteamLibrary"));
                    candidates.Add(Path.Combine(root, "Steam"));
                    candidates.Add(Path.Combine(root, "game", "Steam"));
                    candidates.Add(Path.Combine(root, "Games", "Steam"));
                }
                catch
                {
                    // Проблемный диск пропускаем.
                }
            }

            foreach (string path in candidates)
            {
                try
                {
                    if (Directory.Exists(Path.Combine(path, "steamapps")))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Недоступный путь пропускаем.
                }
            }

            return null;
        }

        /// <summary>Сканирует библиотеки и возвращает найденные одиночные игры.</summary>
        public static List<InstalledGame> Scan()
        {
            var result = new List<InstalledGame>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string library in SteamFolders())
            {
                string apps = Path.Combine(library, "steamapps");

                string[] manifests;
                try
                {
                    manifests = Directory.GetFiles(apps, "appmanifest_*.acf");
                }
                catch
                {
                    continue;
                }

                foreach (string manifest in manifests)
                {
                    try
                    {
                        string text = File.ReadAllText(manifest);

                        Match dirMatch = Regex.Match(text, "\"installdir\"\\s+\"(.+?)\"");
                        if (!dirMatch.Success)
                        {
                            continue;
                        }

                        string installDir = dirMatch.Groups[1].Value;
                        string appId = Path.GetFileNameWithoutExtension(manifest).Replace("appmanifest_", "");

                        foreach ((string folder, string exe, string title) in SinglePlayer)
                        {
                            if (!string.Equals(folder, installDir, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // Страховка: онлайн-игру в список не берём,
                            // даже если она случайно попала в справочник.
                            if (OnlineGuard.IsOnlineExe(exe))
                            {
                                break;
                            }

                            if (!seen.Add(title))
                            {
                                break;
                            }

                            string exePath = FindExe(Path.Combine(apps, "common", installDir), exe);

                            result.Add(new InstalledGame
                            {
                                AppId = appId,
                                Title = title,
                                Exe = exe,
                                ExePath = exePath
                            });

                            break;
                        }
                    }
                    catch
                    {
                        // Битый манифест — пропускаем.
                    }
                }
            }

            result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>Ищет exe в папке игры, заглядывая на пару уровней вглубь.</summary>
        private static string FindExe(string root, string exe)
        {
            try
            {
                string direct = Path.Combine(root, exe);

                if (File.Exists(direct))
                {
                    return direct;
                }

                if (!Directory.Exists(root))
                {
                    return "";
                }

                foreach (string found in Directory.GetFiles(root, exe, SearchOption.AllDirectories))
                {
                    return found;
                }
            }
            catch
            {
                // Папка недоступна или exe не найден.
            }

            return "";
        }

        /// <summary>Запускает игру через Steam (надёжнее, чем exe напрямую).</summary>
        public static string? Launch(InstalledGame game)
        {
            if (game.AppId.Length > 0)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "steam://rungameid/" + game.AppId,
                        UseShellExecute = true
                    });

                    return null;
                }
                catch (Exception ex)
                {
                    if (game.ExePath.Length == 0)
                    {
                        return ex.Message;
                    }
                }
            }

            if (game.ExePath.Length == 0)
            {
                return "не нашёл файл запуска — укажите игру вручную";
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.ExePath,
                    WorkingDirectory = Path.GetDirectoryName(game.ExePath) ?? "",
                    UseShellExecute = true
                });

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    // ==================== Профили под игры ====================

    internal sealed class GameProfile
    {
        public string Name { get; set; } = "";
        public string GameTitle { get; set; } = "";
        public string GameExe { get; set; } = "";

        public bool HighPower { get; set; } = true;
        public bool DisableVisualEffects { get; set; } = true;
        public bool DisableGameBar { get; set; } = true;
        public bool CloseBackground { get; set; } = true;
        public bool PurgeMemory { get; set; } = true;
        public bool RaisePriority { get; set; } = true;
    }

    // ==================== Турбо-режим и профили ====================

    internal static class TurboRunner
    {
        /// <summary>
        /// Выполняет набор действий. Перед запуском обязательно вызывается
        /// проверка онлайн-игр — при онлайне метод не делает ничего.
        /// </summary>
        public static async Task<long> RunAsync(
            CleanupEngine cleanup,
            GameProfile plan,
            int gamePid,
            Action<string, string, string> step,
            Action<bool, string> busy)
        {
            // Главная проверка: онлайн-игра или античит — не вмешиваемся.
            if (OnlineGuard.IsBlocked(out string reason))
            {
                step("skip", "Защита от бана", "обнаружено: " + reason + " — ничего не меняю");
                return 0;
            }

            long freedMemory = 0;

            // 1. Оперативная память.
            if (plan.PurgeMemory)
            {
                busy(true, "Освобождаю память…");
                var memory = await Task.Run(() => MemoryCleaner.Purge());

                freedMemory = memory.FreedMb;

                step(
                    memory.Error is null ? "ok" : "fail",
                    "Оперативная память",
                    memory.Error is null ? ("освобождено " + memory.FreedMb + " МБ кэша") : memory.Error);
            }

            // 2. Фоновые процессы.
            if (plan.CloseBackground)
            {
                busy(true, "Закрываю лишние программы…");

                (int closed, int failed) = await Task.Run(CloseBackground);

                step(
                    closed > 0 ? "ok" : "skip",
                    "Лишние программы",
                    closed > 0
                        ? ("закрыто: " + closed + (failed > 0 ? ", не удалось: " + failed : ""))
                        : "нечего закрывать");
            }

            // 3. Визуальные эффекты.
            if (plan.DisableVisualEffects)
            {
                busy(true, "Отключаю визуальные эффекты…");
                string? error = await Task.Run(() => VisualEffectsManager.Disable());

                step(error is null ? "ok" : "fail", "Визуальные эффекты",
                    error is null ? "отключены" : error);
            }

            // 4. Game Bar.
            if (plan.DisableGameBar)
            {
                busy(true, "Отключаю Game Bar…");
                string? error = await Task.Run(() => GameBarManager.Disable());

                step(error is null ? "ok" : "fail", "Game Bar и запись",
                    error is null ? "отключены" : error);
            }

            // 5. Схема питания.
            if (plan.HighPower)
            {
                busy(true, "Переключаю питание…");
                string? error = await Task.Run(() => PowerPlanManager.Set(true));

                step(error is null ? "ok" : "fail", "Схема питания",
                    error is null ? "включена «Высокая производительность»" : error);
            }

            // 6. Приоритет игры — только если игра не онлайн (проверено выше).
            if (plan.RaisePriority)
            {
                // Приоритет — единственное, что трогает сам процесс игры.
                // Если в системе античит, лучше не рисковать.
                if (OnlineGuard.AntiCheatRunning())
                {
                    step("skip", "Приоритет игры", "в системе античит — приоритет не трогаю");
                }
                else
                {
                    int pid = gamePid;

                    if (pid <= 0)
                    {
                        pid = await Task.Run(FindGamePid);
                    }

                    if (pid > 0)
                    {
                        string? error = await Task.Run(() => PriorityManager.Set(pid, true));

                        step(error is null ? "ok" : "fail", "Приоритет игры",
                            error is null ? "поднят до «Высокий»" : error);
                    }
                    else
                    {
                        step("skip", "Приоритет игры", "игра не запущена — поднимать нечего");
                    }
                }
            }

            busy(false, "");
            return freedMemory;
        }

        /// <summary>Закрывает только бытовые программы из закрытого списка.</summary>
        private static (int Closed, int Failed) CloseBackground()
        {
            int closed = 0;
            int failed = 0;

            foreach (BackgroundProcess item in GameScanner.FindClosable())
            {
                var result = ProcessCloser.Close(item.Pid, false);

                if (result.Closed)
                {
                    closed++;
                }
                else
                {
                    failed++;
                }
            }

            return (closed, failed);
        }

        private static int FindGamePid()
        {
            List<GameInfo> games = GameScanner.FindGames();
            return games.Count > 0 ? games[0].Pid : 0;
        }
    }

    // ==================== FPS из RTSS ====================

    /// <summary>
    /// Читает FPS из общей памяти RivaTuner Statistics Server.
    /// RTSS точнее любых догадок: он считает кадры прямо в игре.
    /// Если RTSS не запущен — возвращает null, и оверлей честно пишет «н/д».
    /// </summary>
    internal static class FpsReader
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMapping(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(
            IntPtr fileMapping, uint desiredAccess, uint offsetHigh, uint offsetLow, UIntPtr bytesToMap);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnmapViewOfFile(IntPtr baseAddress);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public static bool IsRtssRunning()
        {
            try
            {
                IntPtr mapping = OpenFileMapping(0x0004, false, "RTSSSharedMemoryV2");

                if (mapping == IntPtr.Zero)
                {
                    return false;
                }

                CloseHandle(mapping);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static double? Read(int pid)
        {
            IntPtr mapping = IntPtr.Zero;
            IntPtr view = IntPtr.Zero;

            try
            {
                mapping = OpenFileMapping(0x0004, false, "RTSSSharedMemoryV2");

                if (mapping == IntPtr.Zero)
                {
                    return null;
                }

                view = MapViewOfFile(mapping, 0x0004, 0, 0, UIntPtr.Zero);

                if (view == IntPtr.Zero)
                {
                    return null;
                }

                // Заголовок RTSS_SHARED_MEMORY:
                // 0 — сигнатура, 4 — версия, 8 — размер записи,
                // 12 — смещение массива, 16 — количество записей.
                int entrySize = Marshal.ReadInt32(view, 8);
                int arrayOffset = Marshal.ReadInt32(view, 12);
                int arraySize = Marshal.ReadInt32(view, 16);

                if (entrySize < 280 || entrySize > 8192 || arraySize <= 0 || arraySize > 256)
                {
                    return null;
                }

                for (int i = 0; i < arraySize; i++)
                {
                    int entry = arrayOffset + i * entrySize;
                    int entryPid = Marshal.ReadInt32(view, entry);

                    if (entryPid != pid)
                    {
                        continue;
                    }

                    int time0 = Marshal.ReadInt32(view, entry + 268);
                    int time1 = Marshal.ReadInt32(view, entry + 272);
                    int frames = Marshal.ReadInt32(view, entry + 276);

                    int elapsed = time1 - time0;

                    if (elapsed <= 0 || frames <= 0)
                    {
                        return null;
                    }

                    return frames * 1000.0 / elapsed;
                }

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (view != IntPtr.Zero)
                {
                    UnmapViewOfFile(view);
                }

                if (mapping != IntPtr.Zero)
                {
                    CloseHandle(mapping);
                }
            }
        }
    }
}
