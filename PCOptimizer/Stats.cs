using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PCOptimizer
{
    /// <summary>
    /// Накопительный счётчик: сколько всего освобождено, сколько оптимизаций сделано.
    /// Лежит в файле рядом с настройками, поэтому не сбрасывается между запусками.
    /// </summary>
    internal sealed class SavingsStats
    {
        /// <summary>Всего освобождено байт (мусор + освобождённая память при чистке).</summary>
        public long FreedBytes { get; set; }

        /// <summary>Сколько раз запускали оптимизацию или большую чистку.</summary>
        public int Optimizations { get; set; }

        /// <summary>Сколько раз программа вообще запускалась.</summary>
        public int Sessions { get; set; }

        /// <summary>Когда счётчик завели — для справки в интерфейсе.</summary>
        public string Since { get; set; } = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public double FreedGigabytes => FreedBytes / 1024.0 / 1024.0 / 1024.0;

        /// <summary>Оценка сэкономленного времени: одна оптимизация ≈ 5 минут ручной возни.</summary>
        public double SavedHours => Optimizations * 5.0 / 60.0;
    }

    internal static class StatsStore
    {
        private static readonly object Gate = new object();
        private static SavingsStats? _current;

        private static string FilePath => Path.Combine(AppPaths.Folder, "stats.json");

        public static SavingsStats Current
        {
            get
            {
                lock (Gate)
                {
                    return _current ??= Load();
                }
            }
        }

        private static SavingsStats Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<SavingsStats>(json);

                    if (loaded != null)
                    {
                        loaded.Sessions++;
                        return loaded;
                    }
                }
            }
            catch
            {
                // Битый файл — начинаем считать заново, это не повод падать.
            }

            return new SavingsStats { Sessions = 1 };
        }

        public static void Save()
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.Folder);

                    string json = JsonSerializer.Serialize(
                        Current,
                        new JsonSerializerOptions { WriteIndented = true });

                    File.WriteAllText(FilePath, json);
                }
                catch
                {
                    // Не смогли сохранить — не критично, просто не запомним.
                }
            }
        }

        /// <summary>Прибавить освобождённые байты и сразу записать на диск.</summary>
        public static void AddFreed(long bytes)
        {
            if (bytes <= 0)
            {
                return;
            }

            lock (Gate)
            {
                Current.FreedBytes += bytes;
            }

            Save();
        }

        /// <summary>Отметить одну выполненную оптимизацию.</summary>
        public static void AddOptimization()
        {
            lock (Gate)
            {
                Current.Optimizations++;
            }

            Save();
        }

        public static void Reset()
        {
            lock (Gate)
            {
                _current = new SavingsStats { Sessions = Current.Sessions };
            }

            Save();
        }

        /// <summary>Снимок для интерфейса.</summary>
        public static object Snapshot()
        {
            var stats = Current;

            return new
            {
                freedGb = Math.Round(stats.FreedGigabytes, 2),
                optimizations = stats.Optimizations,
                sessions = stats.Sessions,
                savedHours = Math.Round(stats.SavedHours, 1),
                since = stats.Since
            };
        }
    }
}
