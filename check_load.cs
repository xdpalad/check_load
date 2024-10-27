using System;  
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

// Класс для хранения конфигурации, включая настройки уведомлений и пороги нагрузки
class Config
{
    public string? TELEGRAM_TOKEN { get; set; } // Токен Telegram для отправки уведомлений
    public List<string> TELEGRAM_CHAT_ID { get; set; } = new List<string>(); // Список ID чатов Telegram
    public int CPU_THRESHOLD { get; set; } = 80; // Порог нагрузки ЦП для уведомлений
    public int RAM_THRESHOLD { get; set; } = 80; // Порог нагрузки RAM для уведомлений
    public int DISK_THRESHOLD { get; set; } = 80; // Порог нагрузки диска
    public int NETWORK_THRESHOLD { get; set; } = 80; // Порог сетевой нагрузки
}

// Класс для мониторинга состояния сервера и отправки уведомлений
class ServerMonitor
{
    private static Config config = new Config(); // Экземпляр класса Config для хранения настроек
    private static readonly HttpClient client = new HttpClient(); // Объект HttpClient для отправки HTTP-запросов
    private static string? networkInterface; // Имя сетевого интерфейса
    private static double maxDownloadSpeed = 0; // Максимальная скорость загрузки

    // Списки для хранения данных о нагрузке, фиксируемых каждые 15 минут
    private static List<(DateTime, int)> cpuLoadData = new List<(DateTime, int)>();
    private static List<(DateTime, int)> ramLoadData = new List<(DateTime, int)>();
    private static List<(DateTime, int)> diskLoadData = new List<(DateTime, int)>();
    private static List<(DateTime, int)> networkLoadData = new List<(DateTime, int)>();

    // Счетчики для отслеживания количества превышений порогов
    private static int cpuExceedCounter = 0;
    private static int ramExceedCounter = 0;
    private static int diskExceedCounter = 0;
    private static int networkExceedCounter = 0;

    // Таймеры для периодической проверки нагрузки
    private static System.Timers.Timer? monitorTimer;
    private static System.Timers.Timer? maxSpeedTimer;

    // Словарь для хранения данных по процессам с суммарным %CPU и количеством регистраций
    private static Dictionary<string, List<double>> cpuProcessData = new();

    // Основной метод, запускающийся при старте программы
    public static async Task Main(string[] args)
    {
        // Получение конфигурации из аргументов командной строки или через консоль
        if (args.Length >= 2)
        {
            config.TELEGRAM_TOKEN = args[0];
            config.TELEGRAM_CHAT_ID = new List<string>(args[1].Split(','));
        }
        else
        {
            Console.Write("Введите TELEGRAM_TOKEN: ");
            config.TELEGRAM_TOKEN = Console.ReadLine();

            Console.Write("Введите TELEGRAM_CHAT_ID (если несколько, разделите запятой): ");
            var chatIdsInput = Console.ReadLine();
            if (!string.IsNullOrEmpty(chatIdsInput))
            {
                config.TELEGRAM_CHAT_ID = new List<string>(chatIdsInput.Split(','));
            }
        }

        // Проверка наличия TELEGRAM_TOKEN и TELEGRAM_CHAT_ID для отправки уведомлений
        if (string.IsNullOrEmpty(config.TELEGRAM_TOKEN) || config.TELEGRAM_CHAT_ID.Count == 0)
        {
            Console.WriteLine("Ошибка: TELEGRAM_TOKEN или TELEGRAM_CHAT_ID не заданы.");
            return;
        }

        InstallPackages(); // Установка необходимых пакетов для мониторинга

        networkInterface = GetNetworkInterface(); // Определение активного сетевого интерфейса
        if (string.IsNullOrEmpty(networkInterface))
        {
            Console.WriteLine("Не удалось найти активный сетевой интерфейс.");
            return;
        }
        Console.WriteLine($"Активный сетевой интерфейс: {networkInterface}");

        maxDownloadSpeed = await GetMaxDownloadSpeedAsync(); // Получение максимальной скорости загрузки

        // Таймер для мониторинга нагрузки каждые 2 секунды
        monitorTimer = new System.Timers.Timer(3000);
        monitorTimer.Elapsed += async (sender, e) => await MonitorServerLoad();
        monitorTimer.AutoReset = true;
        monitorTimer.Start();

        // Таймер для обновления максимальной скорости загрузки каждые 4 часа
        maxSpeedTimer = new System.Timers.Timer(4 * 60 * 60 * 1000);
        maxSpeedTimer.Elapsed += async (sender, e) => maxDownloadSpeed = await GetMaxDownloadSpeedAsync();
        maxSpeedTimer.AutoReset = true;
        maxSpeedTimer.Start();

        await Task.Delay(Timeout.Infinite); // Бесконечное ожидание для продолжения работы
    }

    // Метод для установки необходимых пакетов
    private static void InstallPackages()
    {
        ExecuteCommand("apt-get update");
        ExecuteCommand("apt-get install -y curl bc ifstat");
        ExecuteCommand("curl -s https://packagecloud.io/install/repositories/ookla/speedtest-cli/script.deb.sh | sudo bash");
        ExecuteCommand("apt-get install -y speedtest");
    }

    // Метод для выполнения команды в bash и получения результата
    private static string ExecuteCommand(string command)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return result;
    }

    // Метод для определения активного сетевого интерфейса
    private static string? GetNetworkInterface()
    {
        var output = ExecuteCommand("ip route | grep default | awk '{print $5}'").Trim();
        return string.IsNullOrEmpty(output) ? null : output;
    }

    // Метод для отправки сообщений в Telegram с использованием Telegram API
    private static async Task SendTelegramMessage(string message)
    {
        if (string.IsNullOrEmpty(config.TELEGRAM_TOKEN) || config.TELEGRAM_CHAT_ID == null || config.TELEGRAM_CHAT_ID.Count == 0)
        {
            Console.WriteLine("Не задан TELEGRAM_TOKEN или TELEGRAM_CHAT_ID.");
            return;
        }

        foreach (var chatId in config.TELEGRAM_CHAT_ID)
        {
            string url = $"https://api.telegram.org/bot{config.TELEGRAM_TOKEN}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(message)}";

            try
            {
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Уведомление успешно отправлено в чат {chatId}.");
                }
                else
                {
                    Console.WriteLine($"Не удалось отправить уведомление в чат {chatId}. Статус: {response.StatusCode}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Ошибка при отправке уведомления в чат {chatId}: {e.Message}");
            }
        }
    }

    // Метод для определения максимальной скорости загрузки, возвращает значение в Мбит/с
    private static async Task<double> GetMaxDownloadSpeedAsync()
    {
        int attempts = 0;
        double defaultSpeed = 250; // Значение по умолчанию, если попытки не удались
        double speed = 0;

        while (attempts < 2)
        {
            var output = ExecuteCommand("speedtest --accept-license --accept-gdpr --format=json");

            try
            {
                var json = System.Text.Json.JsonDocument.Parse(output);
                speed = json.RootElement.GetProperty("download").GetProperty("bandwidth").GetDouble() / (1024 * 1024) * 8;

                Console.WriteLine($"Максимальная скорость загрузки определена: {speed:F2} Мбит/с");
                return speed;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Ошибка при разборе JSON-ответа: {e.Message}");
                Console.WriteLine("Полученный вывод:\n" + output);
            }

            attempts++;
            Console.WriteLine("Не удалось определить максимальную скорость загрузки. Попробую снова через 20 секунд.");
            await Task.Delay(20000); // Ждем 20 секунд перед повторной попыткой
        }

        Console.WriteLine($"Не удалось определить максимальную скорость загрузки после {attempts} попыток. Установлено значение по умолчанию: {defaultSpeed} Мбит/с.");

        string ipAddress = await GetPublicIpAddress();
        string message = $"⚠️ Не удалось определить максимальную скорость загрузки после {attempts} попыток. Установлено значение по умолчанию: {defaultSpeed} Мбит/с. IP сервера: {ipAddress}";
        await SendTelegramMessage(message);

        return defaultSpeed;
    }

    // Получение текущей загрузки CPU
    private static int GetCpuUsage()
    {
        var output = ExecuteCommand("top -bn1 | grep 'Cpu(s)' | sed 's/.*, *\\([0-9.]*\\)%* id.*/\\1/' | awk '{print 100 - $1}'");
        return (int)Math.Round(double.Parse(output.Trim(), CultureInfo.InvariantCulture));
    }

    // Получение текущей загрузки RAM
    private static int GetRamUsage()
    {
        var output = ExecuteCommand("free | grep Mem | awk '{print $3/$2 * 100.0}'");
        return (int)Math.Round(double.Parse(output.Trim(), CultureInfo.InvariantCulture));
    }

    // Получение текущей загрузки диска
    private static int GetDiskUsage()
    {
        var output = ExecuteCommand("df / | grep / | awk '{ print $5}' | sed 's/%//g'");
        return int.Parse(output.Trim());
    }

    // Определение сетевой загрузки в процентах относительно максимальной скорости
    private static int GetNetworkLoadPercentage()
    {
        if (string.IsNullOrEmpty(networkInterface))
        {
            Console.WriteLine("Сетевой интерфейс не найден.");
            return 0;
        }

        long initialReceivedBytes = long.Parse(ExecuteCommand($"cat /sys/class/net/{networkInterface}/statistics/rx_bytes").Trim());
        long initialTransmittedBytes = long.Parse(ExecuteCommand($"cat /sys/class/net/{networkInterface}/statistics/tx_bytes").Trim());

        Thread.Sleep(1000);

        long finalReceivedBytes = long.Parse(ExecuteCommand($"cat /sys/class/net/{networkInterface}/statistics/rx_bytes").Trim());
        long finalTransmittedBytes = long.Parse(ExecuteCommand($"cat /sys/class/net/{networkInterface}/statistics/tx_bytes").Trim());

        double receivedSpeedMbps = (finalReceivedBytes - initialReceivedBytes) * 8 / (1024 * 1024);
        double transmittedSpeedMbps = (finalTransmittedBytes - initialTransmittedBytes) * 8 / (1024 * 1024);

        double currentTotalSpeedMbps = receivedSpeedMbps + transmittedSpeedMbps;

        double networkLoad = Math.Min((currentTotalSpeedMbps / maxDownloadSpeed) * 100, 100);
        return (int)Math.Round(networkLoad);
    }

    // Регистрация данных по процессам, использующим наибольший % CPU
    private static void RegisterCpuProcesses()
    {
        string output = ExecuteCommand("ps -eo pid,comm,%cpu --sort=-%cpu | head -n 4");

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Skip(1).Take(3))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 3)
            {
                string command = columns[1];
                double cpuUsage;

                if (double.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out cpuUsage))
                {
                    if (cpuProcessData.ContainsKey(command))
                    {
                        cpuProcessData[command].Add(cpuUsage);
                    }
                    else
                    {
                        cpuProcessData[command] = new List<double> { cpuUsage };
                    }
                }
            }
        }
    }

    // Метод для вычисления средней загрузки % CPU по процессам
    private static string GetAverageCpuUsage()
    {
        var formattedLines = new List<string> { "Top load processes:\n\n" };

        var sortedProcesses = cpuProcessData
            .Select(entry => new { Command = entry.Key, AverageCpu = entry.Value.Average() })
            .OrderByDescending(entry => entry.AverageCpu);

        foreach (var process in sortedProcesses)
        {
            formattedLines.Add($"CMD: {process.Command} CPU: {process.AverageCpu:F1}%\n");
        }

        return string.Join("", formattedLines);
    }

    // Основной метод мониторинга нагрузки сервера
    private static async Task MonitorServerLoad()
    {
        var cpuUsage = GetCpuUsage();
        var ramUsage = GetRamUsage();
        var diskUsage = GetDiskUsage();
        var networkLoad = GetNetworkLoadPercentage();

        var now = DateTime.Now;

        cpuLoadData.Add((now, cpuUsage));
        ramLoadData.Add((now, ramUsage));
        diskLoadData.Add((now, diskUsage));
        networkLoadData.Add((now, networkLoad));

        cpuLoadData.RemoveAll(d => (now - d.Item1).TotalMinutes > 15);
        ramLoadData.RemoveAll(d => (now - d.Item1).TotalMinutes > 15);
        diskLoadData.RemoveAll(d => (now - d.Item1).TotalMinutes > 15);
        networkLoadData.RemoveAll(d => (now - d.Item1).TotalMinutes > 15);

        bool notificationNeeded = false;

        if (cpuUsage > config.CPU_THRESHOLD)
        {
            cpuExceedCounter++;
            RegisterCpuProcesses();

            if (cpuExceedCounter >= 5) notificationNeeded = true;
        }
        else cpuExceedCounter = 0;

        if (ramUsage > config.RAM_THRESHOLD) ramExceedCounter++;
        else ramExceedCounter = 0;
        if (ramExceedCounter >= 20) notificationNeeded = true;

        if (diskUsage > config.DISK_THRESHOLD) diskExceedCounter++;
        else diskExceedCounter = 0;
        if (diskExceedCounter >= 20) notificationNeeded = true;

        if (networkLoad > config.NETWORK_THRESHOLD) networkExceedCounter++;
        else networkExceedCounter = 0;
        if (networkExceedCounter >= 5) notificationNeeded = true;

        if (notificationNeeded)
        {
            await SendTelegramNotification(cpuUsage, ramUsage, diskUsage, networkLoad);
            cpuExceedCounter = ramExceedCounter = diskExceedCounter = networkExceedCounter = 0;
        }
    }

    // Метод для отправки уведомления с данными о сервере
    private static async Task SendTelegramNotification(int cpu, int ram, int disk, int network)
    {
        string ipAddress = await GetPublicIpAddress();
        string moscowTime = GetMoscowTime();

        double avgCpu1Min = CalculateAverageLoad(cpuLoadData, 1);
        double avgCpu5Min = CalculateAverageLoad(cpuLoadData, 5);
        double avgCpu15Min = CalculateAverageLoad(cpuLoadData, 15);

        double avgNetwork1Min = CalculateAverageLoad(networkLoadData, 1);
        double avgNetwork5Min = CalculateAverageLoad(networkLoadData, 5);
        double avgNetwork15Min = CalculateAverageLoad(networkLoadData, 15);

        string message = $"🆘 High load Server IP: {ipAddress} 🆘\n\n";
        message += $"{(cpu > config.CPU_THRESHOLD ? "🔴" : "🟢")} CPU: {cpu}% Avg: {avgCpu1Min:F1}%, {avgCpu5Min:F1}%, {avgCpu15Min:F1}%\n";
        message += $"{(ram > config.RAM_THRESHOLD ? "🔴" : "🟢")} RAM: {ram}%\n";
        message += $"{(disk > config.DISK_THRESHOLD ? "🔴" : "🟢")} DISK: {disk}%\n";
        message += $"{(network > config.NETWORK_THRESHOLD ? "🔴" : "🟢")} NETWORK: {network}% Avg: {avgNetwork1Min:F1}%, {avgNetwork5Min:F1}%, {avgNetwork15Min:F1}% Max: {maxDownloadSpeed:F1} Mbit/s\n\n";

        // Если CPU превышает порог, добавляем список процессов с высоким %CPU
        if (cpu > config.CPU_THRESHOLD)
        {
            string topProcesses = GetAverageCpuUsage();
            message += topProcesses + "\n"; // Добавляем отступ после топ-процессов
        }

        message += $"🕒 Notification time: {moscowTime}";

        Console.WriteLine(message); // Вывод сообщения на консоль

        // Отправка сообщения в каждый указанный Telegram чат
        foreach (var chatId in config.TELEGRAM_CHAT_ID)
        {
            string url = $"https://api.telegram.org/bot{config.TELEGRAM_TOKEN}/sendMessage?chat_id={chatId}&text={Uri.EscapeDataString(message)}";

            try
            {
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Уведомление успешно отправлено в чат {chatId}.");
                }
                else
                {
                    Console.WriteLine($"Не удалось отправить уведомление в чат {chatId}. Статус: {response.StatusCode}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Ошибка при отправке уведомления в чат {chatId}: {e.Message}");
            }
        }
    }

    // Метод для расчета средней нагрузки за последние n минут
    private static double CalculateAverageLoad(List<(DateTime, int)> data, int minutes)
    {
        var cutoff = DateTime.Now.AddMinutes(-minutes); // Устанавливаем временной предел
        var relevantData = data.Where(d => d.Item1 >= cutoff).Select(d => d.Item2).ToList(); // Данные за последние n минут
        return relevantData.Count > 0 ? relevantData.Average() : 0; // Вычисление среднего, если есть данные
    }

    // Асинхронный метод для получения публичного IP-адреса сервера
    private static async Task<string> GetPublicIpAddress()
    {
        try
        {
            var response = await client.GetStringAsync("https://api.ipify.org"); // Получение IP с сервиса
            return response.Trim();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Ошибка при получении IP-адреса: {e.Message}");
            return "Не удалось определить IP";
        }
    }

    // Метод для получения текущего времени в Московском часовом поясе
    private static string GetMoscowTime()
    {
        var moscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        var moscowTime = TimeZoneInfo.ConvertTime(DateTime.Now, moscowTimeZone);
        return moscowTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); // Форматированная строка времени
    }
}
