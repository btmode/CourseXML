using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Xml.Linq;

namespace CourseXML_main.CourseXML.Services
{
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
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private bool _isInitialized = false;
        private readonly CancellationTokenSource _cts = new();

        public CurrencyService(
            ILogger<CurrencyService> logger,
            IHubContext<CurrencyHub> hubContext,
            IConfiguration configuration,
            IOptions<CurrencyServiceConfig> config)  // ← УБРАН remoteSettings
        {
            _logger = logger;
            _hubContext = hubContext;
            _configuration = configuration;
            _config = config.Value;

            // АВТОМАТИЧЕСКОЕ ОПРЕДЕЛЕНИЕ ПУТЕЙ
            (_sourceXmlPath, _currentXmlPath, _archiveFolder) = GetPathsForCurrentOS();

            _logger.LogCritical("=== CURRENCY SERVICE ИНИЦИАЛИЗАЦИЯ ===");
            _logger.LogCritical("OS: {OS}", Environment.OSVersion.Platform);
            _logger.LogCritical("Source path: {Source}", _sourceXmlPath);
            _logger.LogCritical("Current path: {Current}", _currentXmlPath);
            _logger.LogCritical("Archive folder: {Archive}", _archiveFolder);
            _logger.LogCritical("Local source file exists: {Exists}", File.Exists(_sourceXmlPath));

            EnsureDirectories();
            EnsureCurrentFile();
            LoadData();

            if (_config.UseFileSystemWatcher)
            {
                SetupSourceFileWatcher();
            }

            _lastSourceCheck = GetSourceFileLastWriteTime();
            _isInitialized = true;
            _logger.LogInformation("CurrencyService инициализирован. Офисов: {Count}", _offices.Count);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Запущен polling с интервалом {Interval} секунд",
                _config.PollingIntervalSeconds);

            // Keep-alive таймер
            if (_config.EnableKeepAlive)
            {
                _ = Task.Run(async () => await KeepAliveLoopAsync(_cts.Token), _cts.Token);
            }

            // ЛОКАЛЬНЫЙ FileSystemWatcher
            if (_config.UseFileSystemWatcher)
            {
                SetupSourceFileWatcher();
            }

            // ОСНОВНОЙ polling цикл
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckForSourceUpdatesByPollingAsync();
                    await Task.Delay(
                        TimeSpan.FromSeconds(_config.PollingIntervalSeconds),
                        stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в polling цикле");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Запущен keep-alive с интервалом {Interval} секунд", _config.KeepAliveIntervalSeconds);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await SendKeepAliveAsync();
                    await Task.Delay(TimeSpan.FromSeconds(_config.KeepAliveIntervalSeconds), cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в keep-alive цикле");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private async Task SendKeepAliveAsync()
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("KeepAlive", DateTime.UtcNow.ToString("o"));
                _logger.LogDebug("Отправлен keep-alive сигнал");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отправки keep-alive");
            }
        }

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
                    await Task.Delay(300);
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

                // Определяем ОС и настраиваем буфер
                bool isLinux = Environment.OSVersion.Platform == PlatformID.Unix ||
                              Environment.OSVersion.Platform == PlatformID.MacOSX;

                int bufferSize = isLinux ? 65536 * 16 : 65536;

                _sourceFileWatcher = new FileSystemWatcher
                {
                    Path = directory,
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                    InternalBufferSize = bufferSize,
                    IncludeSubdirectories = false
                };

                _logger.LogInformation("FileSystemWatcher настроен для {OS} с буфером {BufferSize} байт",
                    isLinux ? "Linux" : "Windows", bufferSize);

                int debounceMs = isLinux ? 1000 : 300;

                _sourceFileWatcher.Changed += async (sender, e) =>
                {
                    try
                    {
                        await Task.Delay(debounceMs);

                        if (_config.EnableVerboseLogging)
                        {
                            _logger.LogInformation("FSWATCHER: Файл изменён: {ChangeType}, путь: {Path}",
                                e.ChangeType, e.FullPath);
                        }

                        await CheckAndUpdateFromSourceAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка обработки события файловой системы");
                    }
                };

                _sourceFileWatcher.Created += async (sender, e) =>
                {
                    await Task.Delay(debounceMs);
                    await CheckAndUpdateFromSourceAsync();
                };

                _sourceFileWatcher.Deleted += async (sender, e) =>
                {
                    _logger.LogWarning("Файл {FileName} удален", e.Name);
                };

                _sourceFileWatcher.Error += (sender, e) =>
                {
                    var ex = e.GetException();
                    _logger.LogError(ex, "Ошибка FileSystemWatcher. Возможно буфер переполнен.");

                    if (ex is InternalBufferOverflowException)
                    {
                        _logger.LogWarning("Буфер переполнен! Увеличиваем размер...");
                        Task.Run(async () =>
                        {
                            await Task.Delay(5000);
                            _sourceFileWatcher?.Dispose();
                            SetupSourceFileWatcher();
                        });
                    }
                };

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка настройки FileSystemWatcher");
                _sourceFileWatcher = null;
            }
        }

        private async Task<bool> CheckAndUpdateFromSourceAsync()
        {
            await _fileLock.WaitAsync();

            try
            {
                // Простая проверка существования файла
                if (!File.Exists(_sourceXmlPath))
                {
                    _logger.LogWarning("Source файл не найден: {Path}", _sourceXmlPath);
                    return false;
                }

                // Чтение файла
                string sourceContent;
                try
                {
                    using (var stream = new FileStream(_sourceXmlPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream))
                    {
                        sourceContent = await reader.ReadToEndAsync();
                    }
                }
                catch (IOException ioEx)
                {
                    _logger.LogError(ioEx, "Ошибка чтения файла {Path}. Возможно файл занят другим процессом.",
                        _sourceXmlPath);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Неизвестная ошибка при чтении файла");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(sourceContent))
                {
                    _logger.LogWarning("Source файл пустой или содержит только пробелы");
                    return false;
                }

                XDocument sourceXml;
                try
                {
                    sourceXml = XDocument.Parse(sourceContent);
                    _logger.LogDebug("XML успешно распарсен");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка парсинга source XML. Проверьте формат файла.");
                    return false;
                }

                var sourceOffices = ParseXml(sourceXml);
                if (!sourceOffices.Any())
                {
                    _logger.LogWarning("Не удалось извлечь данные из XML. Файл может быть пустым или иметь неверную структуру.");
                    return false;
                }

                _logger.LogInformation("Получено {Count} офисов из source файла", sourceOffices.Count);

                List<CityOffice> currentOffices = ReadCurrentFile();
                _logger.LogDebug("Текущий файл содержит {Count} офисов", currentOffices.Count);

                bool dataChanged = !AreOfficesEqual(currentOffices, sourceOffices);

                if (!dataChanged)
                {
                    _logger.LogInformation("Данные не изменились");
                    return false;
                }

                _logger.LogInformation("Обнаружены изменения в курсах валют! Начинаем обновление...");

                if (File.Exists(_currentXmlPath))
                {
                    try
                    {
                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var archiveName = $"rates_{timestamp}.xml";
                        var archivePath = Path.Combine(_archiveFolder, archiveName);

                        Directory.CreateDirectory(_archiveFolder);

                        File.Copy(_currentXmlPath, archivePath, true);
                        _logger.LogInformation("Создана архивная копия: {ArchiveName}", archiveName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка создания архивной копии");
                    }
                }

                try
                {
                    await File.WriteAllTextAsync(_currentXmlPath, sourceContent);
                    _logger.LogInformation("Current файл обновлён");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Критическая ошибка записи current файла");
                    return false;
                }

                _offices = sourceOffices;
                _logger.LogInformation("Данные в памяти обновлены");

                try
                {
                    await SendUpdatesToClients();
                    _logger.LogInformation("Обновления отправлены клиентам");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка отправки обновлений клиентам, но данные обновлены");
                }

                if (_config.EnableVerboseLogging)
                {
                    foreach (var office in _offices)
                    {
                        _logger.LogDebug("Офис {OfficeId} ({Location}): {Count} валют",
                            office.Id, office.Location, office.Currencies.Count);

                        foreach (var currency in office.Currencies)
                        {
                            _logger.LogDebug("  {Currency}: покупка {Purchase}, продажа {Sale}",
                                currency.Name, currency.Purchase, currency.Sale);
                        }
                    }
                }

                _logger.LogInformation("Обновление успешно завершено!");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Критическая ошибка в методе CheckAndUpdateFromSourceAsync");
                return false;
            }
            finally
            {
                _fileLock.Release();
            }
        }

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
                            UpdateTime = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                            Timestamp = DateTime.UtcNow.Ticks
                        };

                        await _hubContext
                            .Clients
                            .Group(office.Id.ToLower())
                            .SendAsync("ReceiveUpdate", payload);

                        _logger.LogDebug("Отправлено обновление для офиса {Id}", office.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка отправки для офиса {Id}", office.Id);
                    }
                }

                _logger.LogInformation("Обновления отправлены для {Count} офисов", _offices.Count);
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

        public CityOffice? GetOffice(string officeId)
        {
            return _offices.FirstOrDefault(o =>
                o.Id.Equals(officeId, StringComparison.OrdinalIgnoreCase));
        }

        public List<CityOffice> GetAllOffices()
        {
            return _offices.ToList();
        }

        // ОПРЕДЕЛЕНИЕ ПУТЕЙ 
        private (string source, string current, string archive) GetPathsForCurrentOS()
        {
            string source, current, archive;

            if (Environment.OSVersion.Platform == PlatformID.Unix ||
                Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                source = _configuration["LinuxPaths:SourceXmlPath"]
                    ?? "/mnt/network-share/rates.xml";  // ← СЕТЕВОЙ ДИСК
                current = _configuration["LinuxPaths:CurrentXmlPath"]
                    ?? "/var/www/coursexml/Data/current/rates.xml";
                archive = _configuration["LinuxPaths:ArchiveFolder"]
                    ?? "/var/www/coursexml/Data/archive/";
            }
            else
            {
                source = _configuration["LocalPaths:SourceXmlPath"]
                    ?? "C:\\Users\\danon\\Desktop\\rates.xml";
                current = _configuration["LocalPaths:CurrentXmlPath"]
                    ?? "C:\\Users\\danon\\Desktop\\CourseXML\\CourseXML\\Data\\current\\rates.xml";
                archive = _configuration["LocalPaths:ArchiveFolder"]
                    ?? "C:\\Users\\danon\\Desktop\\CourseXML\\CourseXML\\Data\\archive\\";
            }

            if (!string.IsNullOrEmpty(_config.SourceXmlPath))
                source = _config.SourceXmlPath;
            if (!string.IsNullOrEmpty(_config.CurrentXmlPath))
                current = _config.CurrentXmlPath;
            if (!string.IsNullOrEmpty(_config.ArchiveFolder))
                archive = _config.ArchiveFolder;

            return (source, current, archive);
        }

        public override void Dispose()
        {
            _cts.Cancel();
            _sourceFileWatcher?.Dispose();
            _fileLock?.Dispose();
            base.Dispose();
        }
    }
}