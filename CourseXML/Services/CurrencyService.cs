using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CourseXML_main.CourseXML.Services
{
    public class CurrencyService : BackgroundService
    {
        private readonly string _sourceXmlPathPattern;
        private readonly string _currentXmlPath;
        private readonly string _archiveFolder;
        private string? _lastProcessedSourceFile;

        private readonly ILogger<CurrencyService> _logger;
        private readonly IHubContext<CurrencyHub> _hubContext;
        private readonly CurrencyServiceConfig _config;
        private readonly IConfiguration _configuration;

        private List<CityOffice> _offices = new();
        private FileSystemWatcher? _folderWatcher;
        private DateTime _lastSourceCheck = DateTime.MinValue;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private bool _isInitialized = false;
        private readonly CancellationTokenSource _cts = new();

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

            // АВТОМАТИЧЕСКОЕ ОПРЕДЕЛЕНИЕ ПУТЕЙ
            (_sourceXmlPathPattern, _currentXmlPath, _archiveFolder) = GetPathsForCurrentOS();

            _logger.LogCritical("=== CURRENCY SERVICE ИНИЦИАЛИЗАЦИЯ ===");
            _logger.LogCritical("OS: {OS}", Environment.OSVersion.Platform);
            _logger.LogCritical("Source path pattern (отслеживаем): {Pattern}", _sourceXmlPathPattern);
            _logger.LogCritical("Current path (выводим): {Current}", _currentXmlPath);
            _logger.LogCritical("Archive folder (архивируем): {Archive}", _archiveFolder);
            _logger.LogCritical("Current файл существует: {Exists}", File.Exists(_currentXmlPath));

            // Проверяем доступные source файлы
            var sourceFiles = GetAvailableSourceFiles();
            _logger.LogCritical("Доступно source файлов по паттерну: {Count}", sourceFiles.Count);
            if (sourceFiles.Count > 0)
            {
                _logger.LogCritical("Самый свежий source файл: {File}", GetLatestSourceFileName());
            }

            EnsureDirectories();
            EnsureCurrentFile();
            LoadData(); // ЗАГРУЖАЕМ ИЗ CURRENT ФАЙЛА!

            if (_config.UseFileSystemWatcher)
            {
                SetupFolderWatcher();
            }

            _lastSourceCheck = GetLatestSourceFileTimestamp();
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
                var currentSourceLastWrite = GetLatestSourceFileTimestamp();

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

        private DateTime GetLatestSourceFileTimestamp()
        {
            try
            {
                var latestSourceFile = GetLatestSourceFilePath();
                if (latestSourceFile != null && File.Exists(latestSourceFile))
                {
                    _lastProcessedSourceFile = latestSourceFile;
                    return File.GetLastWriteTime(latestSourceFile);
                }
                return DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private string? GetLatestSourceFilePath()
        {
            try
            {
                var files = GetAvailableSourceFilesWithFullPath();
                if (files.Count == 0)
                {
                    return null;
                }

                // Определяем метод сортировки
                if (_sourceXmlPathPattern.Contains("*") || _sourceXmlPathPattern.Contains("rates_"))
                {
                    // Если в пути есть паттерн, ищем самый свежий по дате в имени файла
                    return files
                        .OrderByDescending(f => ExtractDateFromFileName(Path.GetFileNameWithoutExtension(f)))
                        .ThenByDescending(f => File.GetLastWriteTime(f))
                        .FirstOrDefault();
                }
                else
                {
                    // проверяем существование указанного файла
                    return File.Exists(_sourceXmlPathPattern) ? _sourceXmlPathPattern : null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка поиска самого свежего source файла");
                return null;
            }
        }

        private string? GetLatestSourceFileName()
        {
            var latestFile = GetLatestSourceFilePath();
            return latestFile != null ? Path.GetFileName(latestFile) : null;
        }

        private DateTime ExtractDateFromFileName(string fileName)
        {
            try
            {
                // Пытаемся извлечь дату из формата rates_YYYYMMDDHHMM.xml
                if (fileName.StartsWith("rates_"))
                {
                    var datePart = fileName.Substring(6); // Убираем "rates_"

                    // разные форматы дат
                    string[] formats = {
                        "yyyyMMddHHmm",  
                        "yyyyMMddHH",  
                        "yyyyMMdd",    
                        "yyyyMM",       
                        "yyyy"         
                    };

                    foreach (var format in formats)
                    {
                        if (datePart.Length >= format.Length)
                        {
                            var partToParse = datePart.Substring(0, format.Length);
                            if (DateTime.TryParseExact(partToParse, format,
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                            {
                                return parsedDate;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Не удалось извлечь дату из имени файла {FileName}", fileName);
            }

            return DateTime.MinValue;
        }

        private List<string> GetAvailableSourceFilesWithFullPath()
        {
            var result = new List<string>();
            try
            {
                if (_sourceXmlPathPattern.Contains("*"))
                {
                    // Извлекаем директорию и паттерн из пути
                    var directory = Path.GetDirectoryName(_sourceXmlPathPattern);
                    var pattern = Path.GetFileName(_sourceXmlPathPattern);

                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        result.AddRange(Directory.GetFiles(directory, pattern));
                    }
                }
                else if (File.Exists(_sourceXmlPathPattern))
                {
                    // Если указан конкретный файл и он существует
                    result.Add(_sourceXmlPathPattern);
                }
                else
                {
                    // Если файла нет, ищем по умолчанию rates_*.xml в той же директории
                    var directory = Path.GetDirectoryName(_sourceXmlPathPattern);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        result.AddRange(Directory.GetFiles(directory, "rates_*.xml"));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения списка source файлов");
            }

            return result;
        }

        private void SetupFolderWatcher()
        {
            try
            {
                var directory = Path.GetDirectoryName(_sourceXmlPathPattern);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    _logger.LogWarning("Директория source не существует: {Directory}", directory);
                    return;
                }

                // Определяем ОС и настраиваем буфер
                bool isLinux = Environment.OSVersion.Platform == PlatformID.Unix ||
                              Environment.OSVersion.Platform == PlatformID.MacOSX;

                int bufferSize = isLinux ? 65536 * 16 : 65536;

                _folderWatcher = new FileSystemWatcher
                {
                    Path = directory,
                    Filter = "*.xml", // Следим за всеми XML файлами
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                    InternalBufferSize = bufferSize,
                    IncludeSubdirectories = false
                };

                _logger.LogInformation("FileSystemWatcher настроен для папки {Directory}",
                    directory);

                int debounceMs = isLinux ? 1000 : 300;

                _folderWatcher.Changed += async (sender, e) =>
                {
                    try
                    {
                        if (IsFileMatchingPattern(e.Name))
                        {
                            await Task.Delay(debounceMs);

                            if (_config.EnableVerboseLogging)
                            {
                                _logger.LogInformation("FSWATCHER: Source файл изменён: {FileName}", e.Name);
                            }

                            await CheckAndUpdateFromSourceAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка обработки события файловой системы");
                    }
                };

                _folderWatcher.Created += async (sender, e) =>
                {
                    if (IsFileMatchingPattern(e.Name))
                    {
                        await Task.Delay(debounceMs);
                        _logger.LogInformation("FSWATCHER: Создан новый source файл: {FileName}", e.Name);
                        await CheckAndUpdateFromSourceAsync();
                    }
                };

                _folderWatcher.Deleted += (sender, e) =>
                {
                    if (IsFileMatchingPattern(e.Name))
                    {
                        _logger.LogWarning("FSWATCHER: Source файл удален: {FileName}", e.Name);
                    }
                };

                _folderWatcher.Error += (sender, e) =>
                {
                    var ex = e.GetException();
                    _logger.LogError(ex, "Ошибка FileSystemWatcher");

                    if (ex is InternalBufferOverflowException)
                    {
                        _logger.LogWarning("Буфер переполнен! Увеличиваем размер...");
                        Task.Run(async () =>
                        {
                            await Task.Delay(5000);
                            _folderWatcher?.Dispose();
                            SetupFolderWatcher();
                        });
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка настройки FileSystemWatcher");
                _folderWatcher = null;
            }
        }

        private bool IsFileMatchingPattern(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                return false;

            // Если в паттерне есть звездочка
            if (_sourceXmlPathPattern.Contains("*"))
            {
                var pattern = Path.GetFileName(_sourceXmlPathPattern);
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
            }

            return fileName.StartsWith("rates_", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> CheckAndUpdateFromSourceAsync()
        {
            await _fileLock.WaitAsync();

            try
            {
                // Находим самый свежий source файл
                var sourceFile = GetLatestSourceFilePath();
                if (string.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile))
                {
                    _logger.LogWarning("Source файл не найден по паттерну: {Pattern}", _sourceXmlPathPattern);
                    return false;
                }

                _logger.LogInformation("Обнаружен source файл: {File}", Path.GetFileName(sourceFile));

                // Читаем source файл
                string sourceContent;
                try
                {
                    var encoding = DetectFileEncoding(sourceFile);
                    _logger.LogDebug("Определена кодировка source файла: {Encoding}", encoding?.EncodingName ?? "UTF-8");

                    using (var stream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream, encoding ?? Encoding.UTF8, true))
                    {
                        sourceContent = await reader.ReadToEndAsync();
                    }

                    if (_config.EnableVerboseLogging && sourceContent.Length > 0)
                    {
                        var previewLength = Math.Min(500, sourceContent.Length);
                        var preview = sourceContent.Substring(0, previewLength);
                        _logger.LogDebug("Содержимое source файла (первые {Length} символов): {Content}", previewLength, preview);
                    }
                }
                catch (IOException ioEx)
                {
                    _logger.LogError(ioEx, "Ошибка чтения source файла {Path}", sourceFile);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Неизвестная ошибка при чтении source файла");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(sourceContent))
                {
                    _logger.LogWarning("Source файл пустой или содержит только пробелы");
                    return false;
                }

                // Парсим source XML
                XDocument sourceXml;
                try
                {
                    sourceXml = XDocument.Parse(sourceContent);
                    _logger.LogDebug("Source XML успешно распарсен");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка парсинга source XML. Попытка очистки...");

                    try
                    {
                        sourceContent = RemoveInvalidXmlChars(sourceContent);
                        sourceContent = TryFixEncoding(sourceContent);
                        sourceXml = XDocument.Parse(sourceContent);
                        _logger.LogInformation("Source XML перепарсен после очистки");
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Не удалось исправить source XML");
                        return false;
                    }
                }

                // Получаем данные из source файла
                var sourceOffices = ParseXml(sourceXml);
                if (!sourceOffices.Any())
                {
                    _logger.LogWarning("Не удалось извлечь данные из source XML");
                    return false;
                }

                _logger.LogInformation("Получено {Count} офисов из source файла", sourceOffices.Count);

                // Получаем данные из текущего current файла
                List<CityOffice> currentOffices = ReadCurrentFile();
                _logger.LogDebug("Текущий файл содержит {Count} офисов", currentOffices.Count);

                // Сравниваем данные
                bool dataChanged = !AreOfficesEqual(currentOffices, sourceOffices);

                if (!dataChanged)
                {
                    _logger.LogInformation("Данные не изменились, обновление не требуется");
                    return false;
                }

                _logger.LogInformation("Обнаружены изменения в курсах валют! Начинаем обновление...");

                // Архивируем старый current файл
                if (File.Exists(_currentXmlPath))
                {
                    try
                    {
                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var archiveName = $"rates_{timestamp}.xml";
                        var archivePath = Path.Combine(_archiveFolder, archiveName);

                        Directory.CreateDirectory(_archiveFolder);
                        File.Copy(_currentXmlPath, archivePath, true);
                        _logger.LogInformation("Создана архивная копия текущего файла: {ArchiveName}", archiveName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка создания архивной копии");
                    }
                }

                try
                {
                    await File.WriteAllTextAsync(_currentXmlPath, sourceContent, Encoding.UTF8);
                    _logger.LogInformation("Current файл обновлён из source файла");
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

        // определения кодировки файла
        private Encoding? DetectFileEncoding(string filePath)
        {
            try
            {
                byte[] bom = new byte[4];
                using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    int bytesRead = file.Read(bom, 0, 4);
                    if (bytesRead < 2) return Encoding.UTF8;
                }

                if (bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;
                if (bom[0] == 0xff && bom[1] == 0xfe) return Encoding.Unicode;
                if (bom[0] == 0xfe && bom[1] == 0xff) return Encoding.BigEndianUnicode;

                return Encoding.UTF8;
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        private string RemoveInvalidXmlChars(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var validXmlChars = text.Where(ch =>
                (ch == 0x9 || ch == 0xA || ch == 0xD) ||
                ((ch >= 0x20) && (ch <= 0xD7FF)) ||
                ((ch >= 0xE000) && (ch <= 0xFFFD)) ||
                ((ch >= 0x10000) && (ch <= 0x10FFFF))
            ).ToArray();

            return new string(validXmlChars);
        }

        // Метод исправления кодировки
        private string TryFixEncoding(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            Encoding[] encodingsToTry =
            {
                Encoding.UTF8,
                Encoding.GetEncoding("windows-1251"),
                Encoding.GetEncoding("iso-8859-1")
            };

            foreach (var encoding in encodingsToTry)
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    var converted = encoding.GetString(bytes);
                    if (converted.Any(c => char.IsLetter(c) || char.IsDigit(c)))
                    {
                        return converted;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return text;
        }

        private List<CityOffice> ReadCurrentFile()
        {
            if (!File.Exists(_currentXmlPath))
            {
                _logger.LogDebug("Current файл не существует: {Path}", _currentXmlPath);
                return new List<CityOffice>();
            }

            try
            {
                string currentContent;
                using (var stream = new FileStream(_currentXmlPath, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    currentContent = reader.ReadToEnd();
                }

                if (string.IsNullOrEmpty(currentContent))
                {
                    _logger.LogDebug("Current файл пустой");
                    return new List<CityOffice>();
                }

                var currentXml = XDocument.Parse(currentContent);
                var offices = ParseXml(currentXml);
                _logger.LogDebug("Прочитано {Count} офисов из current файла", offices.Count);
                return offices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка чтения current файла");
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
                if (!File.Exists(_currentXmlPath))
                {
                    var sourceFile = GetLatestSourceFilePath();
                    if (sourceFile != null && File.Exists(sourceFile))
                    {
                        File.Copy(sourceFile, _currentXmlPath, true);
                        _logger.LogInformation("Current файл создан из source файла: {SourceFile}",
                            Path.GetFileName(sourceFile));
                    }
                    else
                    {
                        _logger.LogWarning("Не удалось создать current файл - source файлы не найдены");
                    }
                }
                else
                {
                    _logger.LogInformation("Current файл уже существует: {Path}", _currentXmlPath);
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

                _logger.LogInformation("Данные загружены из current файла. Офисов: {Count}", _offices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки данных из current файла");
                _offices = new List<CityOffice>();
            }
        }

        private List<CityOffice> ParseXml(XDocument xml)
        {
            var offices = new List<CityOffice>();

            try
            {
                var root = xml.Root;
                if (root == null)
                {
                    _logger.LogWarning("XML root is null");
                    return offices;
                }

                foreach (var officeElement in root.Elements("office"))
                {
                    var id = officeElement.Attribute("id")?.Value?.Trim();
                    var location = officeElement.Attribute("location")?.Value;

                    if (string.IsNullOrEmpty(id))
                    {
                        _logger.LogWarning("Найден office без ID, пропускаем");
                        continue;
                    }

                    //  маппинг городов
                    location = GetLocationName(id, location);

                    var office = new CityOffice
                    {
                        Id = id,
                        Location = location,
                        Currencies = new List<CurrencyRate>()
                    };

                    foreach (var currencyElement in officeElement.Elements("currency"))
                    {
                        var name = currencyElement.Element("name")?.Value?.Trim();
                        var purchaseStr = currencyElement.Element("purchase")?.Value?.Replace(",", ".");
                        var saleStr = currencyElement.Element("sale")?.Value?.Replace(",", ".");

                        if (string.IsNullOrEmpty(name) ||
                            string.IsNullOrEmpty(purchaseStr) ||
                            string.IsNullOrEmpty(saleStr))
                        {
                            _logger.LogDebug("Пропускаем валюту с неполными данными: {Name}", name);
                            continue;
                        }

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
                        else
                        {
                            _logger.LogWarning("Не удалось распарсить курсы для валюты {Name}: purchase={Purchase}, sale={Sale}",
                                name, purchaseStr, saleStr);
                        }
                    }

                    if (office.Currencies.Any())
                    {
                        offices.Add(office);
                    }
                    else
                    {
                        _logger.LogWarning("Офис {Id} не содержит валидных валют, пропускаем", id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка парсинга XML");
            }

            return offices;
        }

        // маппинга ID офисов на читаемые названия
        private string GetLocationName(string officeId, string? locationFromXml)
        {
            var locationMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "tlt", "Тольятти" },
                { "msk", "Москва" },
                { "perm", "Пермь" },
            };

            if (locationMapping.TryGetValue(officeId.ToLower(), out var mappedLocation))
            {
                return mappedLocation;
            }

            return officeId.ToUpper();
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
                _logger.LogInformation("Отправка обновлений клиентам из current файла...");

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
        private (string sourcePattern, string current, string archive) GetPathsForCurrentOS()
        {
            string sourcePattern, current, archive;

            if (Environment.OSVersion.Platform == PlatformID.Unix ||
                Environment.OSVersion.Platform == PlatformID.MacOSX)
            {
                sourcePattern = _configuration["LinuxPaths:SourceXmlPath"]
                    ?? "/var/www/coursexml/Data/rates_*.xml";
                current = _configuration["LinuxPaths:CurrentXmlPath"]
                    ?? "/var/www/coursexml/Data/current/rates.xml";
                archive = _configuration["LinuxPaths:ArchiveFolder"]
                    ?? "/var/www/coursexml/Data/archive/";
            }
            else
            {
                sourcePattern = _configuration["LocalPaths:SourceXmlPath"]
                    ?? "C:\\Users\\danon\\Desktop\\rates_*.xml";
                current = _configuration["LocalPaths:CurrentXmlPath"]
                    ?? "C:\\Users\\danon\\Desktop\\CourseXML\\CourseXML\\Data\\current\\rates.xml";
                archive = _configuration["LocalPaths:ArchiveFolder"]
                    ?? "C:\\Users\\danon\\Desktop\\CourseXML\\CourseXML\\Data\\archive\\";
            }

            if (!string.IsNullOrEmpty(_config.SourceXmlPath))
                sourcePattern = _config.SourceXmlPath;
            if (!string.IsNullOrEmpty(_config.CurrentXmlPath))
                current = _config.CurrentXmlPath;
            if (!string.IsNullOrEmpty(_config.ArchiveFolder))
                archive = _config.ArchiveFolder;

            return (sourcePattern, current, archive);
        }
        
        // Получить список доступных source файлов
        public List<string> GetAvailableSourceFiles()
        {
            try
            {
                return GetAvailableSourceFilesWithFullPath()
                    .Select(Path.GetFileName)
                    .Where(f => f != null)
                    .Select(f => f!)
                    .OrderByDescending(f => ExtractDateFromFileName(Path.GetFileNameWithoutExtension(f)))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка получения списка source файлов");
                return new List<string>();
            }
        }

        public override void Dispose()
        {
            _cts.Cancel();
            _folderWatcher?.Dispose();
            _fileLock?.Dispose();
            base.Dispose();
        }
    }
}