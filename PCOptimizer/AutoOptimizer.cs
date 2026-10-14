using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace PCOptimizer
{
    /// <summary>
    /// Авто-оптимизация: применяет только заведомо безопасные настройки.
    /// Службы не трогает вообще, системные файлы не удаляет, критичные
    /// параметры Windows не меняет.
    /// </summary>
    internal static class AutoOptimizer
    {
        /// <summary>
        /// Закрытый список пакетов, которым можно запретить работу в фоне.
        /// Сравнение идёт по точному имени пакета, а не по подстроке,
        /// поэтому случайно задеть системный компонент невозможно.
        /// Всё, чего нет в списке, не трогается никогда.
        /// </summary>
        private static readonly string[] SafePackages =
        {
            // игры и Xbox
            "Microsoft.XboxApp",
            "Microsoft.XboxGameOverlay",
            "Microsoft.XboxGamingOverlay",
            "Microsoft.XboxSpeechToTextOverlay",
            "Microsoft.Xbox.TCUI",
            "Microsoft.MicrosoftSolitaireCollection",
            "Microsoft.Microsoft3DViewer",
            "Microsoft.MixedReality.Portal",

            // магазины
            "Microsoft.WindowsStore",
            "Microsoft.StorePurchaseApp",

            // новости, погода, советы, рекламные предложения
            "Microsoft.BingWeather",
            "Microsoft.GetHelp",
            "Microsoft.Getstarted",
            "Microsoft.WindowsFeedbackHub",
            "Microsoft.Windows.ContentDeliveryManager",

            // медиа и прочее несистемное
            "Microsoft.ZuneMusic",
            "Microsoft.ZuneVideo",
            "Microsoft.WindowsMaps",
            "Microsoft.Office.OneNote",
            "Microsoft.MicrosoftOfficeHub",
            "Microsoft.WindowsAlarms",
            "Microsoft.WindowsCamera",
            "Microsoft.WindowsSoundRecorder",
            "Microsoft.WindowsCalculator",
            "Microsoft.Wallet"
        };

        /// <summary>
        /// Чистим только эти пункты: временные файлы пользователя и корзину.
        /// Системные папки, кэш обновлений и логи авто-оптимизация не трогает.
        /// </summary>
        private static readonly string[] SafeCleanupIds = { "temp", "recycle" };

        /// <summary>Имя пакета без суффикса издателя: «Microsoft.XboxApp_8wekyb3d8bbwe» → «Microsoft.XboxApp».</summary>
        internal static string PackageName(string familyName)
        {
            int underscore = familyName.IndexOf('_');
            return underscore > 0 ? familyName[..underscore] : familyName;
        }

        internal static bool IsSafePackage(string familyName)
        {
            string name = PackageName(familyName);

            foreach (string safe in SafePackages)
            {
                if (string.Equals(name, safe, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Сколько приложений на этой машине подпадает под авто-отключение.</summary>
        public static int CountSafeTargets()
        {
            int count = 0;

            bool global;
            List<BackgroundEntry> apps = BackgroundAppsManager.Read(out global);

            foreach (BackgroundEntry app in apps)
            {
                if (app.Enabled && IsSafePackage(app.Package))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Человекочитаемый размер.</summary>
        public static string FormatSize(long bytes)
        {
            string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };

            double value = bytes;
            int index = 0;

            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }

            string number = value.ToString(index == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);
            return number.Replace('.', ',') + " " + units[index];
        }

        /// <summary>
        /// Выполняет безопасный набор. step вызывается после каждого шага,
        /// busy — для строки прогресса. Возвращает освобождённые байты.
        /// </summary>
        public static async Task<long> RunAsync(
            CleanupEngine cleanup,
            Action<string, string, string> step,
            Action<bool, string> busy)
        {
            long freed = 0;

            // 0. Показываем, что за компьютер мы оптимизируем: дальше
            // часть шагов зависит именно от этих данных.
            SystemReport machine = ProfileStore.Current;

            step("ok", "Компьютер",
                machine.Cpu.Name + " · " + machine.Cpu.Cores + " ядер / "
                + machine.Cpu.Threads + " потоков · " + (machine.Ram.TotalMb / 1024) + " ГБ "
                + (machine.Ram.TypeName != "неизвестно" ? machine.Ram.TypeName + " " : "")
                + "памяти");

            // 1. Точка восстановления — на случай, если что-то пойдёт не так.
            busy(true, "Создаю точку восстановления…");
            OptResult point = await RestorePointManager.CreateAsync();

            step(
                point.Ok ? "ok" : "skip",
                "Точка восстановления",
                point.Ok ? "создана" : point.Text + " — продолжаю без неё");

            // 2. Схема питания.
            busy(true, "Переключаю схему питания…");
            string? error = await Task.Run(() => PowerPlanManager.Set(true));

            step(
                error is null ? "ok" : "fail",
                "Схема питания",
                error is null ? "включена «Высокая производительность»" : error);

            // 3. Визуальные эффекты.
            busy(true, "Отключаю визуальные эффекты…");
            error = await Task.Run(() => VisualEffectsManager.Disable());

            step(
                error is null ? "ok" : "fail",
                "Визуальные эффекты",
                error is null ? "отключены, система отзывчивее" : error);

            // 4. Контроль памяти.
            busy(true, "Включаю контроль памяти…");
            error = await Task.Run(() => StorageSenseManager.Set(true));

            step(
                error is null ? "ok" : "fail",
                "Контроль памяти",
                error is null ? "включён, Windows будет чистить сам" : error);

            // 5. Фоновые приложения — только из закрытого списка.
            busy(true, "Отключаю фоновые приложения…");
            (int done, int failed) = await Task.Run(DisableSafeBackgroundApps);

            string backgroundText;
            if (done == 0 && failed == 0)
            {
                backgroundText = "подходящих приложений не нашлось";
            }
            else if (failed == 0)
            {
                backgroundText = "отключено приложений: " + done;
            }
            else
            {
                backgroundText = "отключено: " + done + ", не удалось: " + failed;
            }

            step(done > 0 ? "ok" : "skip", "Фоновые приложения", backgroundText);

            // 6. Чистка: только временные файлы пользователя и корзина.
            busy(true, "Чищу временные файлы и корзину…");
            CleanupResult cleaned = await Task.Run(() => cleanup.CleanOnly(SafeCleanupIds));

            freed = cleaned.Freed;

            string cleanupText = freed > 0
                ? "освобождено " + FormatSize(freed) + " (" + cleaned.Files + " файлов)"
                : "чистить было нечего";

            if (cleaned.Skipped > 0)
            {
                cleanupText += ", пропущено занятых: " + cleaned.Skipped;
            }

            step(freed > 0 ? "ok" : "skip", "Временные файлы и корзина", cleanupText);

            // 7. Память. Где её меньше 8 ГБ — чистим усиленно, там каждый
            // мегабайт на счету.
            bool lowMemory = machine.Ram.LowMemory;

            busy(true, lowMemory ? "Освобождаю память (усиленный режим)…" : "Освобождаю память…");

            (long freedMb, string? memoryError) = await Task.Run(() => MemoryCleaner.Purge(lowMemory));

            string memoryText;

            if (memoryError is not null)
            {
                memoryText = memoryError;
            }
            else if (freedMb > 0)
            {
                memoryText = "освобождено " + freedMb + " МБ"
                    + (lowMemory ? " — памяти мало, поэтому чистил усиленно" : "");
            }
            else
            {
                memoryText = "память и так была свободна";
            }

            step(
                memoryError is null ? "ok" : "fail",
                lowMemory ? "Память (мало ОЗУ)" : "Память",
                memoryText);

            busy(false, "");
            return freed;
        }

        private static (int Done, int Failed) DisableSafeBackgroundApps()
        {
            int done = 0;
            int failed = 0;

            bool global;
            List<BackgroundEntry> apps = BackgroundAppsManager.Read(out global);

            foreach (BackgroundEntry app in apps)
            {
                // Уже отключённые не трогаем.
                if (!app.Enabled)
                {
                    continue;
                }

                // Не из закрытого списка — не трогаем.
                if (!IsSafePackage(app.Package))
                {
                    continue;
                }

                string? error = BackgroundAppsManager.SetEnabled(app.Package, false);

                if (error is null)
                {
                    done++;
                }
                else
                {
                    failed++;
                }
            }

            return (done, failed);
        }
    }
}
