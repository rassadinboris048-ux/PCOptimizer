using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer
{
    internal sealed class DiskActionResult
    {
        public bool Ok { get; set; }
        public string Text { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Обслуживание дисков. Главное правило: твердотельный накопитель
    /// дефрагментировать нельзя — это лишний износ без всякой пользы.
    /// Ему делают TRIM. Механическому диску, наоборот, дефрагментация нужна.
    /// </summary>
    internal static class DiskTools
    {
        /// <summary>Есть ли на этой системе средство обслуживания дисков.</summary>
        public static bool IsAvailable()
        {
            try
            {
                return File.Exists(DefragPath);
            }
            catch
            {
                return false;
            }
        }

        // Путь не зашит жёстко: берём системную папку той Windows, где запущены.
        private static string DefragPath =>
            Path.Combine(Environment.SystemDirectory, "defrag.exe");

        /// <summary>Что именно сделаем с этим томом — человеческим языком.</summary>
        public static string DescribeAction(VolumeInfo volume)
        {
            if (!IsAvailable())
            {
                return "Средство обслуживания дисков недоступно в этой Windows";
            }

            return volume.Media switch
            {
                MediaKind.Ssd =>
                    "TRIM: Windows сообщит накопителю, какие блоки больше не нужны. "
                    + "Дефрагментация для SSD не делается — она только изнашивает его.",

                MediaKind.Hdd =>
                    "Дефрагментация: файлы раскладываются подряд, механике меньше "
                    + "бегать головкой. Для жёсткого диска это реально ускоряет работу.",

                _ =>
                    "Windows сам выберет, что уместно для этого накопителя."
            };
        }

        /// <summary>Короткая подпись для кнопки.</summary>
        public static string ActionTitle(VolumeInfo volume) => volume.Media switch
        {
            MediaKind.Ssd => "Оптимизировать (TRIM)",
            MediaKind.Hdd => "Дефрагментировать",
            _ => "Оптимизировать"
        };

        /// <summary>
        /// Запускает обслуживание тома. Для SSD — только TRIM (/L),
        /// для HDD — дефрагментацию (/D), для неизвестного — /O.
        /// </summary>
        public static async Task<DiskActionResult> OptimizeAsync(
            VolumeInfo volume,
            Action<string> progress,
            CancellationToken token)
        {
            if (!IsAvailable())
            {
                return new DiskActionResult
                {
                    Ok = false,
                    Text = "Средство обслуживания дисков не найдено: " + DefragPath
                };
            }

            string letter = volume.Letter;
            if (letter.Length < 2 || !letter.EndsWith(":", StringComparison.Ordinal))
            {
                return new DiskActionResult { Ok = false, Text = "Непонятная буква диска: " + letter };
            }

            // Защита от опечаток: обслуживаем только то, что реально существует.
            try
            {
                var drive = new DriveInfo(letter + "\\");
                if (drive.DriveType != DriveType.Fixed)
                {
                    return new DiskActionResult
                    {
                        Ok = false,
                        Text = "Том " + letter + " не является постоянным диском"
                    };
                }
            }
            catch (Exception ex)
            {
                return new DiskActionResult { Ok = false, Text = "Том " + letter + " недоступен: " + ex.Message };
            }

            string mode = volume.Media switch
            {
                MediaKind.Ssd => "/L",
                MediaKind.Hdd => "/D",
                _ => "/O"
            };

            var info = new ProcessStartInfo
            {
                FileName = DefragPath,
                // /V — подробный вывод, по нему показываем, что происходит сейчас.
                Arguments = letter + " " + mode + " /V",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            var builder = new StringBuilder();
            string lastLine = "";

            try
            {
                using Process? process = Process.Start(info);
                if (process is null)
                {
                    return new DiskActionResult { Ok = false, Text = "Не удалось запустить defrag.exe" };
                }

                process.OutputDataReceived += (sender, args) =>
                {
                    if (string.IsNullOrWhiteSpace(args.Data))
                    {
                        return;
                    }

                    string line = args.Data.Trim();
                    lastLine = line;
                    progress(line);
                };

                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        builder.AppendLine(args.Data.Trim());
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Ждём с оглядкой на отмену, но без ограничения по времени:
                // дефрагментация большого HDD может идти часами.
                while (!process.WaitForExit(1000))
                {
                    if (token.IsCancellationRequested)
                    {
                        try
                        {
                            process.Kill(true);
                        }
                        catch
                        {
                            // Уже завершился.
                        }

                        return new DiskActionResult
                        {
                            Ok = false,
                            Text = "Обслуживание диска " + letter + " прервано"
                        };
                    }
                }

                await Task.CompletedTask;

                string detail = builder.ToString().Trim();

                if (process.ExitCode == 0)
                {
                    return new DiskActionResult
                    {
                        Ok = true,
                        Text = volume.Media == MediaKind.Ssd
                            ? "Диск " + letter + " оптимизирован (TRIM)"
                            : "Диск " + letter + " обслужен",
                        Detail = lastLine.Length > 0 ? lastLine : detail
                    };
                }

                return new DiskActionResult
                {
                    Ok = false,
                    Text = "defrag.exe завершился с кодом " + process.ExitCode,
                    Detail = detail.Length > 0 ? detail : lastLine
                };
            }
            catch (Exception ex)
            {
                return new DiskActionResult
                {
                    Ok = false,
                    Text = "Ошибка обслуживания диска: " + ex.Message
                };
            }
        }
    }
}
