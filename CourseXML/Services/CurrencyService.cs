using System.Globalization;
using System.Xml.Linq;
using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CourseXML_main.CourseXML.Services
{
    // ========== УПРОЩЕННЫЙ КЛАСС КОНФИГУРАЦИИ ==========
    public class CurrencyServiceConfig
    {
        // Основные параметры
        public int PollingIntervalSeconds { get; set; } = 2;
        public bool UseFileSystemWatcher { get; set; } = true;
        public bool EnableVerboseLogging { get; set; } = false;

        // Пути к файлам (будут определены автоматически)
        public string SourceXmlPath { get; set; } = "";
        public string CurrentXmlPath { get; set; } = "";
        public string ArchiveFolder { get; set; } = "";
    }

    // ========== ОСНОВНОЙ СЕРВИС ==========
    public class CurrencyService : BackgroundService
    {
        private readonly string _sourceXmlPath;
        private readonly string _currentXmlPath;
        private readonly string _archiveFolder;

        private readonly ILogger<CurrencyService> _logger;
        private readonly IHubContext<CurrencyHub> _hubContext;
        private readonly CurrencyServiceConfig _config;
        private readonly IConfiguration _configuration;

        private List<CityOffice> _offices = new();
        private FileSystemWatcher? _sourceFileWatcher;
        private DateTime _lastSourceCheck = DateTime.MinValue;
        private readonly PeriodicTimer _pollingTimer;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;
        public List<CityOffice> Offices => _offices;

        public CurrencyService(
            ILogger<CurrencyService> logger,
            IHubContext<CurrencyHub> hubContext,
            IConfiguration configuration,
            IOptions<CurrencyServiceConfig> config)
        {
            _logger = logger;
            _hubContext = hubContext;
            _configuration = configuration;
            _config = config.Value;

            // АВТОМАТИЧЕСКОЕ ОПРЕДЕЛЕНИЕ ПУТЕЙ ДЛЯ WINDOWS/LINUX
            (_sourceXmlPath, _currentXmlPath, _archiveFolder) = GetPathsForCurrentOS();

            _logger.LogInformation("=== CURRENCY SERVICE ИНИЦИАЛИЗАЦИЯ ===");
            _logger.LogInformation("OS: {OS}", Environment.OSVersion.Platform);
            _logger.LogInformation("Source: {Source}", _sourceXmlPath);
            _logger.LogInformation("Current: {Current}", _currentXmlPath);
            _logger.LogInformation("Archive: {Archive}", _archiveFolder);
            _logger.LogInformation("Polling интервал: {Interval} секунд", _config.PollingIntervalSeconds);

            // Создаем директории если их нет
            EnsureDirectories();

            // Копируем source в current если current не существует
            EnsureCurrentFile();

            // Загружаем данные
            LoadData();

            // Настраиваем FileSystemWatcher если включен
            if (_config.UseFileSystemWatcher)
            {
                SetupSourceFileWatcher();
            }

            // Создаем таймер с конфигурируемым интервалом
            _pollingTimer = new PeriodicTimer(TimeSpan.FromSeconds(_config.PollingIntervalSeconds));
            _lastSourceCheck = GetSourceFileLastWriteTime();

            _isInitialized = true;
            _logger.LogInformation("CurrencyService инициализирован. Офисов: {Count}", _offices.Count);
        }

        // ========== АВТОМАТИЧЕСКОЕ ОПРЕДЕЛЕНИЕ ПУТЕЙ ==========
        private (string source, string current, string archive) GetPathsForCurrentOS()
        {
            string source, current, archive;

            if (Environment.OSVersion.Platform == PlatformID.Unix ||
                Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                // Linux/Mac пути
                source = _configuration["LinuxPaths:SourceXmlPath"]
                    ?? "/var/www/coursexml/Data/rates.xml";
                current = _configuration["LinuxPaths:CurrentXmlPath"]
                    ?? "/var/www/coursexml/Data/current/rates.xml";
                archive = _configuration["LinuxPaths:ArchiveFolder"]
                    ?? "/var/www/coursexml/Data/archive/";
            }
            else
            {
                // Windows пути
                source = _configuration["LocalPaths:SourceXmlPath"]
                    ?? "C:\\Users\\danon\\Desktop\\rates.xml";
                current = _configuration["LocalPaths:CurrentXmlPath"]
                    ?? "C:\\Users\\danon\\Desktop\\CourseXML\\CourseXML\\Data\\current\\rates.xml";
                archive = _configuration["LocalPaths:ArchiveFolder"]
                    ?? "C:\\Users\\danon\\Desktop\\CourseXML\\CourseXML\\Data\\archive\\";
            }

            // Если пути заданы в конфиге сервиса - используем их
            if (!string.IsNullOrEmpty(_config.SourceXmlPath))
                source = _config.SourceXmlPath;
            if (!string.IsNullOrEmpty(_config.CurrentXmlPath))
                current = _config.CurrentXmlPath;
            if (!string.IsNullOrEmpty(_config.ArchiveFolder))
                archive = _config.ArchiveFolder;

            return (source, current, archive);
        }

        // ========== BACKGROUND SERVICE ==========
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Запущен polling с интервалом {Interval} секунд", _config.PollingIntervalSeconds);

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

        // ========== ПОЛЛИНГ ==========
        private async Task CheckForSourceUpdatesByPollingAsync()
        {
            try
            {
                var currentSourceLastWrite = GetSourceFileLastWriteTime();

                if (currentSourceLastWrite > _lastSourceCheck)
                {
                    if (_config.EnableVerboseLogging)
                    {
                        _logger.LogInformation("POLLING: Обнаружено изменение source файла");
                    }

                    _lastSourceCheck = currentSourceLastWrite;
                    await Task.Delay(300); // Ждем завершения записи
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
                return File.Exists(_sourceXmlPath) ? File.GetLastWriteTime(_sourceXmlPath) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        // ========== FILESYSTEM WATCHER ==========
        private void SetupSourceFileWatcher()
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

                _sourceFileWatcher = new FileSystemWatcher
                {
                    Path = directory,
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _sourceFileWatcher.Changed += async (sender, e) =>
                {
                    try
                    {
                        // Дебаунсинг для Linux (там может быть несколько событий)
                        await Task.Delay(500);

                        if (_config.EnableVerboseLogging)
                        {
                            _logger.LogInformation("FSWATCHER: Файл изменён");
                        }

                        await CheckAndUpdateFromSourceAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка обработки события файловой системы");
                    }
                };

                _logger.LogInformation("FileSystemWatcher настроен");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка настройки FileSystemWatcher");
                _sourceFileWatcher = null;
            }
        }

        // ========== ОСНОВНАЯ ЛОГИКА ==========
        private async Task<bool> CheckAndUpdateFromSourceAsync()
        {
            await _fileLock.WaitAsync();

            try
            {
                _logger.LogInformation("Проверка source файла на изменения...");

                if (!File.Exists(_sourceXmlPath))
                {
                    _logger.LogWarning("Source файл не найден: {Path}", _sourceXmlPath);
                    return false;
                }

                // Читаем source файл
                string sourceContent;
                using (var stream = new FileStream(_sourceXmlPath, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    sourceContent = await reader.ReadToEndAsync();
                }

                if (string.IsNullOrEmpty(sourceContent))
                {
                    _logger.LogWarning("Source файл пустой");
                    return false;
                }

                // Парсим XML
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

                // Читаем current файл для сравнения
                List<CityOffice> currentOffices = ReadCurrentFile();

                // Сравниваем данные
                bool dataChanged = !AreOfficesEqual(currentOffices, sourceOffices);

                if (!dataChanged)
                {
                    _logger.LogInformation("Данные не изменились");
                    return false;
                }

                _logger.LogInformation("Обнаружены новые курсы, начинаем обновление...");

                // Архивируем текущий файл
                if (File.Exists(_currentXmlPath))
                {
                    try
                    {
                        var archiveName = $"rates_{DateTime.Now:yyyyMMdd_HHmmss}.xml";
                        var archivePath = Path.Combine(_archiveFolder, archiveName);
                        File.Copy(_currentXmlPath, archivePath, true);
                        _logger.LogInformation("Создана архивная копия");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка создания архивной копии");
                    }
                }

                // Обновляем current файл
                await File.WriteAllTextAsync(_currentXmlPath, sourceContent);
                _logger.LogInformation("Current файл обновлён из source");

                // Обновляем данные в памяти
                _offices = sourceOffices;

                // Отправляем обновления клиентам
                await SendUpdatesToClients();

                _logger.LogInformation("Обновление успешно завершено");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления из source");
                return false;
            }
            finally
            {
                _fileLock.Release();
            }
        }

        // ========== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ==========
        private List<CityOffice> ReadCurrentFile()
        {
            if (!File.Exists(_currentXmlPath))
            {
                return new List<CityOffice>();
            }

            try
            {
                string currentContent;
                using (var stream = new FileStream(_currentXmlPath, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    currentContent = reader.ReadToEnd();
                }

                if (string.IsNullOrEmpty(currentContent))
                {
                    return new List<CityOffice>();
                }

                var currentXml = XDocument.Parse(currentContent);
                return ParseXml(currentXml);
            }
            catch
            {
                return new List<CityOffice>();
            }
        }

        private void EnsureDirectories()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_currentXmlPath)!);
                Directory.CreateDirectory(_archiveFolder);
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
                _logger.LogInformation("Отправка обновлений клиентам...");

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
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка отправки для офиса {Id}", office.Id);
                    }
                }

                _logger.LogInformation("Обновления отправлены");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отправки обновлений");
            }
        }
        public async Task<bool> ForceUpdateFromSourceAsync()
        {
            _logger.LogInformation("Принудительное обновление из source файла");
            return await CheckAndUpdateFromSourceAsync();
        }

        // ========== PUBLIC API ==========
        public CityOffice? GetOffice(string officeId)
        {
            return _offices.FirstOrDefault(o =>
                o.Id.Equals(officeId, StringComparison.OrdinalIgnoreCase));
        }

        public List<CityOffice> GetAllOffices()
        {
            return _offices.ToList();
        }

        // ========== DISPOSE ==========
        public override void Dispose()
        {
            _sourceFileWatcher?.Dispose();
            _pollingTimer?.Dispose();
            _fileLock?.Dispose();
            base.Dispose();
        }
    }
}