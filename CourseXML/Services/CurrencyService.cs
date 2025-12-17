using System.Globalization;
using System.Xml.Linq;
using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;

namespace CourseXML_main.CourseXML.Services
{
    public class CurrencyService
    {
        private readonly string _sourceXmlPath;
        private readonly string _currentXmlPath;
        private readonly string _archiveFolder;

        private readonly ILogger<CurrencyService> _logger;
        private readonly IHubContext<CurrencyHub> _hubContext;

        private readonly List<CityOffice> _offices = new();
        private FileSystemWatcher? _fileWatcher;
        private DateTime _lastReadTime = DateTime.MinValue;

        public CurrencyService(
            ILogger<CurrencyService> logger,
            IHubContext<CurrencyHub> hubContext,
            IConfiguration configuration)
        {
            _logger = logger;
            _hubContext = hubContext;

            _sourceXmlPath = configuration["LocalPaths:SourceXmlPath"]!;
            _currentXmlPath = configuration["LocalPaths:CurrentXmlPath"]!;
            _archiveFolder = configuration["LocalPaths:ArchiveFolder"]!;

            // Проверяем и создаем папки с правами
            EnsureDirectories();
            EnsureCurrentFile();
            LoadData();

            // ВАЖНО: Запускаем watcher ПОСЛЕ загрузки данных
            SetupFileWatcher();

            _logger.LogInformation(
                "CurrencyService запущен для Linux. Source: {Source}, Current: {Current}",
                _sourceXmlPath,
                _currentXmlPath
            );
        }

        // ---------- ИСПРАВЛЕННЫЕ МЕТОДЫ ДЛЯ LINUX ----------

        private void EnsureDirectories()
        {
            try
            {
                // Создаем директории с правильными правами
                var currentDir = Path.GetDirectoryName(_currentXmlPath);
                if (!string.IsNullOrEmpty(currentDir))
                {
                    Directory.CreateDirectory(currentDir);
                    // Даем права на чтение для всех (Linux требует для FileSystemWatcher)
                    try { new DirectoryInfo(currentDir).Attributes &= ~FileAttributes.ReadOnly; } catch { }
                }

                Directory.CreateDirectory(_archiveFolder);

                // Даем права на запись в архив
                try { new DirectoryInfo(_archiveFolder).Attributes &= ~FileAttributes.ReadOnly; } catch { }

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
                    _logger.LogInformation("Current отсутствует, копируем source → current");
                    File.Copy(_sourceXmlPath, _currentXmlPath, true);

                    // Даем права на чтение/запись файлу
                    try { new FileInfo(_currentXmlPath).Attributes &= ~FileAttributes.ReadOnly; } catch { }
                }
                else if (!File.Exists(_currentXmlPath))
                {
                    // Создаем пустой файл если source тоже нет
                    File.WriteAllText(_currentXmlPath, @"<?xml version=""1.0"" encoding=""utf-8""?><exchangeRates></exchangeRates>");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания current файла");
            }
        }

        private void SetupFileWatcher()
        {
            try
            {
                var directory = Path.GetDirectoryName(_sourceXmlPath);
                var fileName = Path.GetFileName(_sourceXmlPath);

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    _logger.LogError("Директория не существует: {Directory}", directory);
                    return;
                }

                // ВАЖНО ДЛЯ LINUX: Увеличиваем буфер и меняем настройки
                _fileWatcher = new FileSystemWatcher
                {
                    Path = directory,
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 65536 * 8, // Увеличиваем буфер в 8 раз для Linux
                    IncludeSubdirectories = false
                };

                // Обработчики событий
                _fileWatcher.Changed += OnSourceChangedAsync;
                _fileWatcher.Created += OnSourceChangedAsync;
                _fileWatcher.Error += OnFileWatcherError;

                _logger.LogInformation(
                    "FileSystemWatcher настроен для Linux. Директория: {Directory}, Файл: {File}",
                    directory, fileName
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка настройки FileSystemWatcher для Linux");
            }
        }

        private void OnFileWatcherError(object sender, ErrorEventArgs e)
        {
            _logger.LogError(e.GetException(), "FileSystemWatcher ошибка");

            // Перезапускаем watcher при ошибке
            try
            {
                _fileWatcher?.Dispose();
                Task.Delay(1000).ContinueWith(_ => SetupFileWatcher());
            }
            catch { }
        }

        private async void OnSourceChangedAsync(object sender, FileSystemEventArgs e)
        {
            await ProcessSourceChangeAsync(e);
        }

        private async Task ProcessSourceChangeAsync(FileSystemEventArgs e)
        {
            try
            {
                // Дебаунсинг для Linux - увеличиваем время
                var now = DateTime.Now;
                if ((now - _lastReadTime).TotalSeconds < 3) // 3 секунды для Linux
                    return;

                _lastReadTime = now;

                _logger.LogInformation("Обнаружено изменение файла: {Name}, тип: {ChangeType}",
                    e.Name, e.ChangeType);

                // Ждем завершения записи - для Linux нужно больше времени
                await Task.Delay(1000);

                // ВАЖНО: Проверяем доступность файла
                if (!IsFileReady(_sourceXmlPath))
                {
                    _logger.LogWarning("Файл ещё заблокирован, пропускаем");
                    return;
                }

                // Сравниваем содержимое
                string sourceContent, currentContent;

                try
                {
                    // Читаем файлы с retry
                    sourceContent = await ReadFileWithRetryAsync(_sourceXmlPath);
                    currentContent = await ReadFileWithRetryAsync(_currentXmlPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось прочитать файлы, пропускаем");
                    return;
                }

                if (sourceContent == currentContent)
                {
                    _logger.LogInformation("Изменений в курсах нет");
                    return;
                }

                // Архивируем current
                var archiveName = $"rates_{DateTime.Now:yyyyMMdd_HHmmss}.xml";
                var archivePath = Path.Combine(_archiveFolder, archiveName);

                if (File.Exists(_currentXmlPath))
                {
                    File.Copy(_currentXmlPath, archivePath, true);
                    _logger.LogInformation("Создана архивная копия: {Archive}", archiveName);
                }

                // Обновляем current
                File.Copy(_sourceXmlPath, _currentXmlPath, true);

                // Даем права на новый файл
                try { new FileInfo(_currentXmlPath).Attributes &= ~FileAttributes.ReadOnly; } catch { }

                _logger.LogInformation("Курсы обновлены");

                // Загружаем новые данные
                LoadData();

                // Отправляем обновления
                await SendUpdatesToClients();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки изменения файла");
            }
        }

        // Вспомогательные методы для Linux
        private bool IsFileReady(string filePath)
        {
            try
            {
                using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        private async Task<string> ReadFileWithRetryAsync(string filePath, int maxRetries = 3)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    return await File.ReadAllTextAsync(filePath);
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    await Task.Delay(200 * (i + 1));
                }
            }
            throw new IOException($"Не удалось прочитать файл: {filePath}");
        }

        // ---------- ОСТАЛЬНЫЕ МЕТОДЫ БЕЗ ИЗМЕНЕНИЙ ----------

        private void LoadData()
        {
            try
            {
                if (!File.Exists(_currentXmlPath))
                {
                    _logger.LogWarning("Current XML не найден");
                    _offices.Clear();
                    return;
                }

                var xml = XDocument.Load(_currentXmlPath);
                _offices.Clear();
                _offices.AddRange(ParseXml(xml));

                _logger.LogInformation("Данные загружены. Офисов: {Count}", _offices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки current XML");
                _offices.Clear();
            }
        }

        private List<CityOffice> ParseXml(XDocument xml)
        {
            var offices = new List<CityOffice>();
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
                    offices.Add(office);
            }

            return offices;
        }

        private async Task SendUpdatesToClients()
        {
            foreach (var office in _offices)
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
        }

        public CityOffice? GetOffice(string officeId)
        {
            return _offices.FirstOrDefault(o =>
                o.Id.Equals(officeId, StringComparison.OrdinalIgnoreCase));
        }

        // Дополнительный метод для принудительного обновления
        public async Task ForceUpdateAsync()
        {
            try
            {
                _logger.LogInformation("Принудительное обновление курсов");
                LoadData();
                await SendUpdatesToClients();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка принудительного обновления");
            }
        }
    }
}