using System;
using System.Collections.Generic;
using System.IO;

namespace PCOptimizer
{
    // ==================== Описание пункта очистки ====================

    /// <summary>
    /// Один пункт списка на вкладке «Очистка»: что именно и где удаляем.
    /// Пути задаются только здесь, из интерфейса их подменить нельзя.
    /// </summary>
    internal sealed class CleanupTarget
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public bool NeedsAdmin { get; set; }
        public bool IsRecycleBin { get; set; }

        /// <summary>Папки, содержимое которых удаляем. Сами папки остаются на месте.</summary>
        public List<string> Folders { get; } = new List<string>();

        /// <summary>Папка + маска файлов: удаляем только подходящие файлы.</summary>
        public List<FileMask> Masks { get; } = new List<FileMask>();
    }

    internal sealed class FileMask
    {
        public string Folder { get; set; } = "";
        public string Pattern { get; set; } = "";
    }

    /// <summary>Результат замера или очистки одного пункта.</summary>
    internal sealed class CleanupResult
    {
        public long Size { get; set; }
        public int Files { get; set; }
        public long Freed { get; set; }
        public int Skipped { get; set; }
    }

    // ==================== Список того, что чистим ====================

    internal static class CleanupCatalog
    {
        private static string LocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private static string UserTemp => Path.GetTempPath().TrimEnd('\\');

        private static string ProgramData =>
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // Папка Windows может лежать на любом диске и называться иначе,
        // поэтому спрашиваем у системы, а не пишем «C:\Windows» руками.
        private static string WindowsDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        private static string WindowsPath(params string[] parts)
        {
            string path = WindowsDir;

            foreach (string part in parts)
            {
                path = Path.Combine(path, part);
            }

            return path;
        }

        public static List<CleanupTarget> Build()
        {
            var list = new List<CleanupTarget>();

            // 1. Временные файлы пользователя.
            //    %TEMP% и %LOCALAPPDATA%\Temp — это одна и та же папка,
            //    поэтому пункт один, иначе итог задваивался бы.
            list.Add(new CleanupTarget
            {
                Id = "temp",
                Title = "Временные файлы",
                Description = "TEMP и LOCALAPPDATA\\Temp — это одна и та же папка",
                Folders = { UserTemp }
            });

            // 2. Корзина — чистится системной функцией, а не удалением файлов.
            list.Add(new CleanupTarget
            {
                Id = "recycle",
                Title = "Корзина",
                Description = "Все удалённые файлы со всех дисков",
                IsRecycleBin = true
            });

            // 3. Кэш браузеров. Профили не трогаем — только папки кэша,
            //    поэтому закладки, пароли и история остаются целы.
            var browsers = new CleanupTarget
            {
                Id = "browsers",
                Title = "Кэш браузеров",
                Description = "Chrome, Edge, Firefox — только кэш, без закладок и паролей"
            };
            AddChromiumCaches(browsers, Path.Combine(LocalAppData, "Google", "Chrome", "User Data"));
            AddChromiumCaches(browsers, Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"));
            AddChromiumCaches(browsers, Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"));
            AddChromiumCaches(browsers, Path.Combine(LocalAppData, "Yandex", "YandexBrowser", "User Data"));
            AddFirefoxCaches(browsers, Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles"));
            list.Add(browsers);

            // 4. Временные файлы самой Windows.
            list.Add(new CleanupTarget
            {
                Id = "wintemp",
                Title = "Временные файлы Windows",
                Description = WindowsPath("Temp"),
                NeedsAdmin = true,
                Folders = { WindowsPath("Temp") }
            });

            // 5. Кэш обновлений Windows.
            list.Add(new CleanupTarget
            {
                Id = "wupdate",
                Title = "Кэш обновлений Windows",
                Description = WindowsPath("SoftwareDistribution", "Download")
                    + " — загруженные пакеты обновлений",
                NeedsAdmin = true,
                Folders = { WindowsPath("SoftwareDistribution", "Download") }
            });

            // 6. Логи Windows.
            var logs = new CleanupTarget
            {
                Id = "logs",
                Title = "Логи Windows",
                Description = WindowsPath("Logs") + " — журналы CBS, DISM и обновлений",
                NeedsAdmin = true
            };
            logs.Folders.Add(WindowsPath("Logs", "CBS"));
            logs.Folders.Add(WindowsPath("Logs", "DISM"));
            logs.Folders.Add(WindowsPath("Logs", "MoSetup"));
            logs.Folders.Add(WindowsPath("Logs", "WindowsUpdate"));
            logs.Folders.Add(WindowsPath("Logs", "SIH"));
            list.Add(logs);

            // 7. Дампы памяти после сбоев.
            var dumps = new CleanupTarget
            {
                Id = "dumps",
                Title = "Дампы памяти",
                Description = "Файлы аварийных завершений и падений программ",
                NeedsAdmin = true
            };
            dumps.Folders.Add(WindowsPath("Minidump"));
            dumps.Folders.Add(WindowsPath("LiveKernelReports"));
            dumps.Folders.Add(Path.Combine(LocalAppData, "CrashDumps"));
            dumps.Masks.Add(new FileMask { Folder = WindowsDir, Pattern = "MEMORY.DMP" });
            dumps.Masks.Add(new FileMask { Folder = WindowsDir, Pattern = "*.dmp" });
            list.Add(dumps);

            // 8. Отчёты об ошибках Windows.
            var wer = new CleanupTarget
            {
                Id = "wer",
                Title = "Отчёты об ошибках",
                Description = "Очередь и архив отчётов Windows Error Reporting",
                NeedsAdmin = true
            };
            wer.Folders.Add(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"));
            wer.Folders.Add(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"));
            wer.Folders.Add(Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "Temp"));
            wer.Folders.Add(Path.Combine(LocalAppData, "Microsoft", "Windows", "WER", "ReportQueue"));
            wer.Folders.Add(Path.Combine(LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive"));
            list.Add(wer);

            // 9. Эскизы и иконки проводника.
            var thumbs = new CleanupTarget
            {
                Id = "thumbs",
                Title = "Эскизы и иконки",
                Description = "thumbcache и iconcache проводника — Windows соберёт их заново",
            };
            thumbs.Masks.Add(new FileMask
            {
                Folder = Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"),
                Pattern = "thumbcache_*.db"
            });
            thumbs.Masks.Add(new FileMask
            {
                Folder = Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer"),
                Pattern = "iconcache_*.db"
            });
            list.Add(thumbs);

            // 10. Кэш шейдеров видеокарты — на игровых машинах бывает огромным.
            var shaders = new CleanupTarget
            {
                Id = "shaders",
                Title = "Кэш шейдеров видеокарты",
                Description = "NVIDIA, AMD и Direct3D — пересоберётся при запуске игр"
            };
            shaders.Folders.Add(Path.Combine(LocalAppData, "NVIDIA", "DXCache"));
            shaders.Folders.Add(Path.Combine(LocalAppData, "NVIDIA", "GLCache"));
            shaders.Folders.Add(Path.Combine(LocalAppData, "NVIDIA Corporation", "NV_Cache"));
            shaders.Folders.Add(Path.Combine(LocalAppData, "AMD", "DxCache"));
            shaders.Folders.Add(Path.Combine(LocalAppData, "AMD", "GLCache"));
            shaders.Folders.Add(Path.Combine(LocalAppData, "D3DSCache"));
            list.Add(shaders);

            // 11. Кэш интернета.
            list.Add(new CleanupTarget
            {
                Id = "inetcache",
                Title = "Кэш интернета",
                Description = "INetCache — временные файлы из интернета",
                Folders = { Path.Combine(LocalAppData, "Microsoft", "Windows", "INetCache") }
            });

            // 12. Кэш предзагрузки.
            list.Add(new CleanupTarget
            {
                Id = "prefetch",
                Title = "Кэш предзагрузки (Prefetch)",
                Description = WindowsPath("Prefetch") + " — Windows соберёт его заново",
                NeedsAdmin = true,
                Folders = { WindowsPath("Prefetch") }
            });

            // Убираем пустые пункты (например, браузера нет) и дубли папок.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<CleanupTarget>();

            foreach (CleanupTarget target in list)
            {
                var unique = new List<string>();
                foreach (string folder in target.Folders)
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    if (!seen.Add(Path.GetFullPath(folder))) continue;
                    unique.Add(folder);
                }

                target.Folders.Clear();
                target.Folders.AddRange(unique);

                bool hasWork = target.IsRecycleBin || target.Folders.Count > 0 || target.Masks.Count > 0;
                if (hasWork)
                {
                    result.Add(target);
                }
            }

            return result;
        }

        private static void AddChromiumCaches(CleanupTarget target, string userDataRoot)
        {
            if (!Directory.Exists(userDataRoot))
            {
                return;
            }

            string[] cacheNames =
            {
                "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache",
                "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Media Cache"
            };

            foreach (string name in cacheNames)
            {
                AddIfExists(target, Path.Combine(userDataRoot, name));
            }

            string[] profiles;
            try
            {
                profiles = Directory.GetDirectories(userDataRoot);
            }
            catch
            {
                return;
            }

            foreach (string profile in profiles)
            {
                foreach (string name in cacheNames)
                {
                    AddIfExists(target, Path.Combine(profile, name));
                }

                AddIfExists(target, Path.Combine(profile, "Service Worker", "CacheStorage"));
            }
        }

        private static void AddFirefoxCaches(CleanupTarget target, string profilesRoot)
        {
            if (!Directory.Exists(profilesRoot))
            {
                return;
            }

            string[] profiles;
            try
            {
                profiles = Directory.GetDirectories(profilesRoot);
            }
            catch
            {
                return;
            }

            foreach (string profile in profiles)
            {
                AddIfExists(target, Path.Combine(profile, "cache2"));
            }
        }

        private static void AddIfExists(CleanupTarget target, string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    target.Folders.Add(folder);
                }
            }
            catch
            {
                // Недоступная папка — просто пропускаем.
            }
        }
    }

    // ==================== Защита от опасных путей ====================

    internal static class CleanupSafety
    {
        /// <summary>
        /// То, что удалять нельзя ни при каких условиях.
        /// Проверка идёт по вхождению подстроки, поэтому покрывает и подпапки.
        /// </summary>
        private static readonly string[] Forbidden =
        {
            @"\Windows\WinSxS",
            @"\Windows\System32",
            @"\Windows\SysWOW64",
            @"\Windows\Installer",
            @"\Windows\assembly",
            @"\Windows\Microsoft.NET",
            @"\Windows\servicing",
            @"\Windows\Boot",
            @"\Windows\Fonts",
            @"\Windows\Security",
            @"\Windows\CSC",
            @"\SoftwareDistribution\DataStore",
            @"\Program Files",
            @"\ProgramData\Microsoft\Windows\Start Menu",
            @"\Users\Default",
            @"\Users\Public"
        };

        public static bool IsForbidden(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return true;
            }

            // Корень диска удалять нельзя никогда.
            if (full.Length <= 3)
            {
                return true;
            }

            string probe = "\\" + full.Trim('\\');

            foreach (string bad in Forbidden)
            {
                if (probe.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    // ==================== Замер и удаление ====================

    internal sealed class CleanupEngine
    {
        private readonly List<CleanupTarget> _targets = CleanupCatalog.Build();

        public IReadOnlyList<CleanupTarget> Targets => _targets;

        /// <summary>Считает, сколько места занимает пункт. Ничего не удаляет.</summary>
        public CleanupResult Measure(CleanupTarget target)
        {
            var result = new CleanupResult();

            if (target.IsRecycleBin)
            {
                NativeMethods.GetRecycleBinInfo(out long size, out long count);
                result.Size = size;
                result.Files = (int)Math.Min(count, int.MaxValue);
                return result;
            }

            int files = 0;
            long bytes = 0;

            foreach (string folder in target.Folders)
            {
                if (CleanupSafety.IsForbidden(folder))
                {
                    continue;
                }

                MeasureFolder(folder, ref bytes, ref files);
            }

            foreach (FileMask mask in target.Masks)
            {
                MeasureMask(mask, ref bytes, ref files);
            }

            result.Size = bytes;
            result.Files = files;
            return result;
        }

        /// <summary>
        /// Удаляет всё, что относится к пункту. Занятые и защищённые файлы
        /// пропускаются — метод не бросает исключений наружу.
        /// </summary>
        public CleanupResult Clean(CleanupTarget target)
        {
            var result = new CleanupResult();

            if (target.IsRecycleBin)
            {
                NativeMethods.GetRecycleBinInfo(out long size, out long count);
                result.Size = size;
                result.Files = (int)Math.Min(count, int.MaxValue);

                bool ok = NativeMethods.EmptyRecycleBin();
                result.Freed = ok ? size : 0;
                result.Skipped = ok ? 0 : 1;
                return result;
            }

            long freed = 0;
            int files = 0;
            int skipped = 0;

            foreach (string folder in target.Folders)
            {
                if (CleanupSafety.IsForbidden(folder))
                {
                    skipped++;
                    continue;
                }

                DeleteFolderContents(folder, ref freed, ref files, ref skipped);
            }

            foreach (FileMask mask in target.Masks)
            {
                DeleteByMask(mask, ref freed, ref files, ref skipped);
            }

            result.Freed = freed;
            result.Files = files;
            result.Skipped = skipped;
            return result;
        }

        /// <summary>Чистит только перечисленные пункты (по идентификаторам).</summary>
        public CleanupResult CleanOnly(string[] ids)
        {
            var total = new CleanupResult();

            foreach (CleanupTarget target in _targets)
            {
                bool wanted = false;

                foreach (string id in ids)
                {
                    if (target.Id == id)
                    {
                        wanted = true;
                        break;
                    }
                }

                if (!wanted)
                {
                    continue;
                }

                CleanupResult part = Clean(target);

                total.Freed += part.Freed;
                total.Files += part.Files;
                total.Skipped += part.Skipped;
                total.Size += part.Size;
            }

            return total;
        }

        // ---------- обход дерева ----------

        private static void MeasureFolder(string folder, ref long bytes, ref int files)
        {
            var stack = new Stack<string>();
            stack.Push(folder);

            while (stack.Count > 0)
            {
                string dir = stack.Pop();

                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(dir);
                }
                catch
                {
                    continue; // нет доступа — пропускаем
                }

                foreach (string entry in entries)
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch
                    {
                        continue;
                    }

                    // По символическим ссылкам и точкам монтирования не ходим.
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        stack.Push(entry);
                    }
                    else
                    {
                        try
                        {
                            bytes += new FileInfo(entry).Length;
                            files++;
                        }
                        catch
                        {
                            // файл исчез между обходом и чтением — не страшно
                        }
                    }
                }
            }
        }

        private static void MeasureMask(FileMask mask, ref long bytes, ref int files)
        {
            if (CleanupSafety.IsForbidden(mask.Folder))
            {
                return;
            }

            string[] found;
            try
            {
                found = Directory.GetFiles(mask.Folder, mask.Pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return;
            }

            foreach (string file in found)
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch
                {
                    // уже удалён кем-то другим
                }
            }
        }

        private static void DeleteFolderContents(string folder, ref long freed, ref int files, ref int skipped)
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(folder);
            }
            catch
            {
                return;
            }

            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                // Ссылку убираем саму, внутрь не заходим: там может быть что угодно.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    try
                    {
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            Directory.Delete(entry, false);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                    catch
                    {
                        skipped++;
                    }
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteFolderContents(entry, ref freed, ref files, ref skipped);

                    try
                    {
                        Directory.Delete(entry, false);
                    }
                    catch
                    {
                        skipped++; // внутри остались занятые файлы — папка остаётся
                    }
                }
                else
                {
                    DeleteFile(entry, attributes, ref freed, ref files, ref skipped);
                }
            }
        }

        private static void DeleteByMask(FileMask mask, ref long freed, ref int files, ref int skipped)
        {
            if (CleanupSafety.IsForbidden(mask.Folder))
            {
                return;
            }

            string[] found;
            try
            {
                found = Directory.GetFiles(mask.Folder, mask.Pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return;
            }

            foreach (string file in found)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(file);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                DeleteFile(file, attributes, ref freed, ref files, ref skipped);
            }
        }

        private static void DeleteFile(
            string file, FileAttributes attributes, ref long freed, ref int files, ref int skipped)
        {
            long length = 0;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch
            {
                // размер неважен, главное удалить
            }

            try
            {
                // Только для чтения иначе не удалится.
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(file);
                freed += length;
                files++;
            }
            catch
            {
                // Файл занят другой программой или нет прав — пропускаем.
                skipped++;
            }
        }
    }
}
