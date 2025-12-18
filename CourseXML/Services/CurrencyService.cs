using System.Globalization;
using System.Xml.Linq;
using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace CourseXML_main.CourseXML.Services
{
    public class CurrencyService : BackgroundService
    {
        private readonly string _sourceXmlPath;
        private readonly string _currentXmlPath;
        private readonly string _archiveFolder;

        private readonly ILogger<CurrencyService> _logger;
        private readonly IHubContext<CurrencyHub> _hubContext;
        private List<CityOffice> _offices = new();
        private FileSystemWatcher? _sourceFileWatcher;
        private DateTime _lastSourceCheck = DateTime.MinValue;
        private DateTime _lastWatcherEvent = DateTime.MinValue;
        private readonly PeriodicTimer _pollingTimer;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private bool _isInitialized = false;
        private readonly TaskCompletionSource<bool> _initializationCompleted = new();

        // Для дебаунсинга на Linux
        private readonly Dictionary<string, DateTime> _lastProcessedEvents = new();
        private const int LINUX_DEBOUNCE_MS = 500; // Увеличил для Linux

        // Публичное свойство для проверки инициализации
        public bool IsInitialized => _isInitialized;
        public Task InitializationTask => _initializationCompleted.Task;

        public CurrencyService(
            ILogger<CurrencyService> logger,
            IHubContext<CurrencyHub> hubContext,
            IConfiguration configuration)
        {
            _logger = logger;
            _hubContext = hubContext;

            _sourceXmlPath = configuration["LocalPaths:SourceXmlPath"]
                ?? "/var/www/coursexml/Data/rates.xml";
            _currentXmlPath = configuration["LocalPaths:CurrentXmlPath"]
                ?? "/var/www/coursexml/Data/current/rates.xml";
            _archiveFolder = configuration["LocalPaths:ArchiveFolder"]
                ?? "/var/www/coursexml/Data/archive/";

            _logger.LogInformation("=== CURRENCY SERVICE ИНИЦИАЛИЗАЦИЯ (LINUX) ===");
            _logger.LogInformation("Source: {Source}", _sourceXmlPath);
            _logger.LogInformation("Current: {Current}", _currentXmlPath);
            _logger.LogInformation("Archive: {Archive}", _archiveFolder);

            CheckLinuxInotifyLimits();

            EnsureDirectories();
            EnsureCurrentFile();
            LoadData();
            SetupSourceFileWatcherIfPossible();

            _pollingTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            _lastSourceCheck = GetSourceFileLastWriteTime();

            _isInitialized = true;
            _initializationCompleted.SetResult(true);

            _logger.LogInformation("CurrencyService полностью инициализирован. Офисов: {Count}", _offices.Count);
        }

        // ========== ПРОВЕРКА ОГРАНИЧЕНИЙ INOTIFY НА LINUX ==========
        private void CheckLinuxInotifyLimits()
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Unix ||
                    Environment.OSVersion.Platform == PlatformID.MacOSX)
                {
                    _logger.LogInformation("Проверка inotify ограничений на Linux...");

                    // Проверяем sysctl значения
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "sysctl",
                            Arguments = "fs.inotify.max_user_watches fs.inotify.max_user_instances",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        var process = System.Diagnostics.Process.Start(psi);
                        if (process != null)
                        {
                            string output = process.StandardOutput.ReadToEnd();
                            process.WaitForExit(1000);

                            _logger.LogInformation("Inotify limits: {Limits}", output);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось проверить inotify ограничения");
            }
        }


        // ========== BACKGROUND SERVICE (POLLING) ==========
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Запущен polling как основной механизм (Linux)");

            while (await _pollingTimer.WaitForNextTickAsync(stoppingToken)
                   && !stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckForSourceUpdatesByPollingAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в polling цикле");
                }
            }
        }

        // ========== ПОЛЛИНГ ДЛЯ ПРОВЕРКИ SOURCE ФАЙЛА ==========
        private async Task CheckForSourceUpdatesByPollingAsync()
        {
            try
            {
                var currentSourceLastWrite = GetSourceFileLastWriteTime();

                if (currentSourceLastWrite > _lastSourceCheck)
                {
                    _logger.LogInformation("POLLING: Обнаружено изменение source файла");
                    _lastSourceCheck = currentSourceLastWrite;

                    await Task.Delay(500);

                    await CheckAndUpdateFromSourceAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка проверки source файла");
            }
        }

        private DateTime GetSourceFileLastWriteTime()
        {
            try
            {
                if (File.Exists(_sourceXmlPath))
                {
                    return File.GetLastWriteTime(_sourceXmlPath);
                }
                return DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private void SetupSourceFileWatcherIfPossible()
        {
            try
            {
                var directory = Path.GetDirectoryName(_sourceXmlPath);
                var fileName = Path.GetFileName(_sourceXmlPath);

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    _logger.LogWarning("Директория source не существует: {Directory}", directory);
                    return;
                }

                // Проверяем права доступа
                if (!HasDirectoryAccess(directory))
                {
                    _logger.LogWarning("Нет прав доступа к директории: {Directory}", directory);
                    return;
                }

                _sourceFileWatcher = new FileSystemWatcher
                {
                    Path = directory,
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 65536 * 16, // Больше для Linux
                    IncludeSubdirectories = false
                };

                _sourceFileWatcher.Changed += HandleFileSystemEvent;
                _sourceFileWatcher.Created += HandleFileSystemEvent;
                _sourceFileWatcher.Renamed += HandleFileSystemEvent;

                _logger.LogInformation("FileSystemWatcher настроен (Linux mode): {Directory}/{File}",
                    directory, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось настроить FileSystemWatcher. Будем использовать только polling.");
                _sourceFileWatcher = null;
            }
        }

        private async void HandleFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                var now = DateTime.UtcNow;
                var key = $"{e.ChangeType}:{e.FullPath}";

                if (_lastProcessedEvents.TryGetValue(key, out var lastTime))
                {
                    if ((now - lastTime).TotalMilliseconds < LINUX_DEBOUNCE_MS)
                    {
                        return;
                    }
                }

                _lastProcessedEvents[key] = now;

                var oldKeys = _lastProcessedEvents.Where(kvp =>
                    (now - kvp.Value).TotalMinutes > 5).Select(kvp => kvp.Key).ToList();
                foreach (var oldKey in oldKeys)
                {
                    _lastProcessedEvents.Remove(oldKey);
                }

                _logger.LogInformation("FSWATCHER ({ChangeType}): {Name}", e.ChangeType, e.Name);

                await Task.Delay(800);

                await CheckAndUpdateFromSourceAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки события файловой системы");
            }
        }

        private bool HasDirectoryAccess(string directory)
        {
            try
            {
                if (Environment.OSVersion.Platform == PlatformID.Unix ||
                    Environment.OSVersion.Platform == PlatformID.MacOSX)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "stat",
                        Arguments = $"-c %a \"{directory}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    var process = System.Diagnostics.Process.Start(psi);
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit(1000);

                        if (int.TryParse(output, out int permissions))
                        {
                            // Проверяем что есть права на чтение (минимум 444)
                            bool hasReadAccess = (permissions & 0444) != 0;
                            _logger.LogDebug("Права доступа к {Directory}: {Permissions} (octal)",
                                directory, Convert.ToString(permissions, 8));

                            return hasReadAccess;
                        }
                    }
                }
                return true;
            }
            catch
            {
                return true; 
            }
        }

        private async Task<bool> CheckAndUpdateFromSourceAsync()
        {
            await _fileLock.WaitAsync();

            try
            {
                _logger.LogInformation("=== ПРОВЕРКА SOURCE ФАЙЛА НА ИЗМЕНЕНИЯ ===");

                if (!File.Exists(_sourceXmlPath))
                {
                    _logger.LogWarning("Source файл не найден: {Path}", _sourceXmlPath);
                    return false;
                }

                string sourceContent = null;
                int retryCount = 0;
                const int maxRetries = 3;

                while (retryCount < maxRetries && sourceContent == null)
                {
                    try
                    {
                        using (var stream = new FileStream(_sourceXmlPath, FileMode.Open,
                               FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(stream))
                        {
                            sourceContent = await reader.ReadToEndAsync();
                        }
                    }
                    catch (IOException ioEx) when (ioEx.Message.Contains("locked") ||
                                                   ioEx.Message.Contains("busy"))
                    {
                        retryCount++;
                        _logger.LogWarning("Файл заблокирован, попытка {Retry}/{Max}", retryCount, maxRetries);
                        await Task.Delay(200 * retryCount);
                    }
                }

                if (sourceContent == null)
                {
                    _logger.LogError("Не удалось прочитать source файл после {Max} попыток", maxRetries);
                    return false;
                }

                if (string.IsNullOrEmpty(sourceContent))
                {
                    _logger.LogWarning("Source файл пустой");
                    return false;
                }

                XDocument sourceXml;
                try
                {
                    sourceXml = XDocument.Parse(sourceContent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка парсинга source XML");
                    return false;
                }

                var sourceOffices = ParseXml(sourceXml);
                if (!sourceOffices.Any())
                {
                    _logger.LogWarning("Не удалось распарсить source XML");
                    return false;
                }

                List<CityOffice> currentOffices;
                if (File.Exists(_currentXmlPath))
                {
                    string currentContent = null;
                    retryCount = 0;

                    while (retryCount < maxRetries && currentContent == null)
                    {
                        try
                        {
                            using (var stream = new FileStream(_currentXmlPath, FileMode.Open,
                                   FileAccess.Read, FileShare.ReadWrite))
                            using (var reader = new StreamReader(stream))
                            {
                                currentContent = await reader.ReadToEndAsync();
                            }
                        }
                        catch (IOException ioEx) when (ioEx.Message.Contains("locked"))
                        {
                            retryCount++;
                            await Task.Delay(200 * retryCount);
                        }
                    }

                    if (!string.IsNullOrEmpty(currentContent))
                    {
                        try
                        {
                            var currentXml = XDocument.Parse(currentContent);
                            currentOffices = ParseXml(currentXml);
                        }
                        catch
                        {
                            currentOffices = new List<CityOffice>();
                        }
                    }
                    else
                    {
                        currentOffices = new List<CityOffice>();
                    }
                }
                else
                {
                    currentOffices = new List<CityOffice>();
                }

                bool dataChanged = !AreOfficesEqual(currentOffices, sourceOffices);

                if (!dataChanged)
                {
                    _logger.LogInformation("Данные в source файле не изменились");
                    return false;
                }

                _logger.LogInformation("=== ОБНАРУЖЕНЫ НОВЫЕ КУРСЫ! ===");
                _logger.LogInformation("Курсы изменились, начинаем обновление...");

                if (File.Exists(_currentXmlPath))
                {
                    var archiveName = $"rates_{DateTime.Now:yyyyMMdd_HHmmss}.xml";
                    var archivePath = Path.Combine(_archiveFolder, archiveName);

                    try
                    {
                        File.Copy(_currentXmlPath, archivePath, true);
                        _logger.LogInformation("Создана архивная копия: {Archive}", archiveName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка создания архивной копии");
                    }
                }

                try
                {
                    await File.WriteAllTextAsync(_currentXmlPath, sourceContent);
                    _logger.LogInformation("Current файл обновлён из source");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка записи в current файл");
                    return false;
                }

                _offices = sourceOffices;
                _logger.LogInformation("Данные в памяти обновлены. Офисов: {Count}", _offices.Count);

                await SendUpdatesToClients();

                _logger.LogInformation("=== ОБНОВЛЕНИЕ УСПЕШНО ЗАВЕРШЕНО ===");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка проверки и обновления из source");
                return false;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task<bool> UpdateDataFromCurrentFileAsync()
        {
            await _fileLock.WaitAsync();

            try
            {
                _logger.LogInformation("Обновление данных из current файла...");

                if (!File.Exists(_currentXmlPath))
                {
                    _logger.LogWarning("Current файл не найден: {Path}", _currentXmlPath);
                    return false;
                }

                string currentContent;
                using (var stream = new FileStream(_currentXmlPath, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    currentContent = await reader.ReadToEndAsync();
                }

                if (string.IsNullOrEmpty(currentContent))
                {
                    _logger.LogWarning("Файл пустой");
                    return false;
                }

                var currentXml = XDocument.Parse(currentContent);
                var newOffices = ParseXml(currentXml);

                if (!newOffices.Any())
                {
                    _logger.LogWarning("Не удалось распарсить XML");
                    return false;
                }

                bool dataChanged = !AreOfficesEqual(_offices, newOffices);

                if (!dataChanged)
                {
                    _logger.LogInformation("Данные не изменились");
                    return false;
                }

                _offices = newOffices;
                _logger.LogInformation("Данные в памяти обновлены. Офисов: {Count}", _offices.Count);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления данных");
                return false;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public async Task<bool> UpdateFromSourceWithArchiveAsync()
        {
            return await CheckAndUpdateFromSourceAsync();
        }

        private void LoadData()
        {
            try
            {
                _logger.LogInformation("Загрузка данных из current файла...");

                if (!File.Exists(_currentXmlPath))
                {
                    _logger.LogWarning("Current файл не найден: {Path}", _currentXmlPath);
                    return;
                }

                var xml = XDocument.Load(_currentXmlPath);
                _offices = ParseXml(xml);

                _logger.LogInformation("Данные загружены. Офисов: {Count}", _offices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки данных");
                _offices = new List<CityOffice>();
            }
        }

        private void EnsureDirectories()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_currentXmlPath)!);
                Directory.CreateDirectory(_archiveFolder);

                var sourceDir = Path.GetDirectoryName(_sourceXmlPath);
                if (!string.IsNullOrEmpty(sourceDir) && !Directory.Exists(sourceDir))
                {
                    Directory.CreateDirectory(sourceDir);
                }

                _logger.LogInformation("Директории созданы/проверены");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания директорий");
            }
        }

        private void EnsureCurrentFile()
        {
            try
            {
                if (!File.Exists(_currentXmlPath) && File.Exists(_sourceXmlPath))
                {
                    File.Copy(_sourceXmlPath, _currentXmlPath, true);
                    _logger.LogInformation("Current файл создан из source");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания current файла");
            }
        }

        private List<CityOffice> ParseXml(XDocument xml)
        {
            var offices = new List<CityOffice>();

            try
            {
                var root = xml.Root;
                if (root == null) return offices;

                foreach (var officeElement in root.Elements("office"))
                {
                    var id = officeElement.Attribute("id")?.Value;
                    var location = officeElement.Attribute("location")?.Value;

                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(location))
                        continue;

                    var office = new CityOffice
                    {
                        Id = id,
                        Location = location,
                        Currencies = new List<CurrencyRate>()
                    };

                    foreach (var currencyElement in officeElement.Elements("currency"))
                    {
                        var name = currencyElement.Element("name")?.Value;
                        var purchaseStr = currencyElement.Element("purchase")?.Value;
                        var saleStr = currencyElement.Element("sale")?.Value;

                        if (string.IsNullOrEmpty(name) ||
                            string.IsNullOrEmpty(purchaseStr) ||
                            string.IsNullOrEmpty(saleStr))
                            continue;

                        if (decimal.TryParse(purchaseStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var purchase) &&
                            decimal.TryParse(saleStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var sale))
                        {
                            office.Currencies.Add(new CurrencyRate
                            {
                                Name = name,
                                Purchase = purchase,
                                Sale = sale
                            });
                        }
                    }

                    if (office.Currencies.Any())
                    {
                        offices.Add(office);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка парсинга XML");
            }

            return offices;
        }

        private bool AreOfficesEqual(List<CityOffice> list1, List<CityOffice> list2)
        {
            if (list1.Count != list2.Count) return false;

            for (int i = 0; i < list1.Count; i++)
            {
                var o1 = list1[i];
                var o2 = list2[i];

                if (o1.Id != o2.Id || o1.Location != o2.Location) return false;
                if (o1.Currencies.Count != o2.Currencies.Count) return false;

                for (int j = 0; j < o1.Currencies.Count; j++)
                {
                    var c1 = o1.Currencies[j];
                    var c2 = o2.Currencies[j];

                    if (c1.Name != c2.Name || c1.Purchase != c2.Purchase || c1.Sale != c2.Sale)
                        return false;
                }
            }

            return true;
        }

        private async Task SendUpdatesToClients()
        {
            try
            {
                _logger.LogInformation("Отправка обновлений всем клиентам...");

                foreach (var office in _offices)
                {
                    try
                    {
                        var payload = new
                        {
                            office.Id,
                            office.Location,
                            office.Currencies,
                            UpdateTime = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                        };

                        await _hubContext
                            .Clients
                            .Group(office.Id.ToLower())
                            .SendAsync("ReceiveUpdate", payload);

                        _logger.LogDebug("Обновление отправлено для офиса: {Id}", office.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка отправки для офиса {Id}", office.Id);
                    }
                }

                await _hubContext.Clients.All.SendAsync("ReceiveNotification",
                    new
                    {
                        Message = "Курсы валют обновлены",
                        Time = DateTime.Now.ToString("HH:mm:ss")
                    });

                _logger.LogInformation("Обновления отправлены всем клиентам");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отправки обновлений");
            }
        }

        public CityOffice? GetOffice(string officeId)
        {
            return _offices.FirstOrDefault(o =>
                o.Id.Equals(officeId, StringComparison.OrdinalIgnoreCase));
        }

        public List<CityOffice> GetAllOffices()
        {
            return _offices.ToList();
        }

        public async Task ForceUpdateAsync()
        {
            _logger.LogInformation("Принудительное обновление данных");
            var dataChanged = await UpdateDataFromCurrentFileAsync();
            if (dataChanged)
            {
                await SendUpdatesToClients();
            }
        }

        public async Task ForceUpdateFromSourceAsync()
        {
            _logger.LogInformation("Принудительное обновление из source файла");
            await CheckAndUpdateFromSourceAsync();
        }

        public override void Dispose()
        {
            _sourceFileWatcher?.Dispose();
            _pollingTimer?.Dispose();
            _fileLock?.Dispose();
            base.Dispose();
        }
    }
}