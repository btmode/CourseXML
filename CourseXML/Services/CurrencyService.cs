using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;
using Renci.SshNet;

public class CurrencyService : IDisposable
{
    private string _remoteXmlPath;
    private string _currentXmlPath;
    private string _archiveFolder;
    private readonly object _lock = new();
    private string _lastHash = string.Empty;
    private bool _disposed = false;
    private readonly ILogger<CurrencyService> _logger;
    private readonly IHubContext<CurrencyHub> _hubContext;
    private RemoteServerSettings _remoteServerSettings;
    private List<CityOffice> _offices = new();
    private FileSystemWatcher? _fileWatcher;
    private Timer _updateTimer;
    private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(5);


    public CurrencyService(
        IWebHostEnvironment env,
        ILogger<CurrencyService> logger,
        IHubContext<CurrencyHub> hubContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _hubContext = hubContext;

        // Инициализация путей
        FilePath(configuration);
        // Конфигурация подключения к серверу
        SettingsRemoteServer(configuration);

        Directory.CreateDirectory(Path.GetDirectoryName(_currentXmlPath)!);
        if (_archiveFolder != null) Directory.CreateDirectory(_archiveFolder);

        LoadCurrentData();
        InitFileWatcher();
        InitializeUpdateTimer();
    }

    public void FilePath(IConfiguration configuration)
    {
        _remoteXmlPath = "/mnt/rates/current/rates.xml"; //позже изменить на реальный путь
        _currentXmlPath = configuration["LocalPaths:CurrentXmlPath"];
        _archiveFolder = configuration["LocalPaths:ArchiveFolder"];
    }

    public void SettingsRemoteServer(IConfiguration configuration)
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));

        _remoteServerSettings = new RemoteServerSettings
        {
            Host = configuration["RemoteServer:Host"] ?? throw new ArgumentNullException("RemoteServer:Host"),
            Username = configuration["RemoteServer:Username"],
            Password = configuration["RemoteServer:Password"],
            RemoteDirectory = configuration["RemoteServer:RemoteDirectory"] ?? "/default/path"
        };
    }

    private void LoadCurrentData()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_currentXmlPath))
                {
                    _logger.LogWarning("Файл не найден: {Path}. Создаём пустой.", _currentXmlPath);
                    new XDocument(new XElement("exchangeRates")).Save(_currentXmlPath);
                    _offices = new();
                    return;
                }

                var content = File.ReadAllText(_currentXmlPath);
                var xml = XDocument.Parse(content);
                _offices = ParseXml(xml);
                _lastHash = CalculateFileHash(_currentXmlPath);

                _logger.LogInformation("Загружено {_count} офисов.", _offices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки XML");
                _offices = new();
            }
        }
    }

    private List<CityOffice> ParseXml(XDocument xml)
    {
        try
        {
            return xml.Root?.Elements("office")
                .Where(office => office.Attribute("id") != null && office.Attribute("location") != null)
                .Select(office => new CityOffice
                {
                    Id = office.Attribute("id")!.Value,
                    Location = office.Attribute("location")!.Value,
                    Currencies = office.Elements("currency")
                        .Where(c => c.Element("name") != null &&
                                    c.Element("purchase") != null &&
                                    c.Element("sale") != null)
                        .Select(c =>
                        {
                            try
                            {
                                return new CurrencyRate
                                {
                                    Name = c.Element("name")!.Value,
                                    Purchase =
                                        decimal.Parse(c.Element("purchase")!.Value, CultureInfo.InvariantCulture),
                                    Sale = decimal.Parse(c.Element("sale").Value, CultureInfo.InvariantCulture)
                                };
                            }
                            catch (FormatException ex)
                            {
                                _logger.LogWarning($"Ошибка парсинга курса валюты: {ex.Message}");
                                return null;
                            }
                        })
                        .Where(c => c != null)
                        .ToList()
                })
                .Where(office => office != null && office.Currencies.Any())
                .ToList() ?? new List<CityOffice>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка парсинга XML");
            return new List<CityOffice>();
        }
    }

    public async Task CheckAndUpdateRates()
    {
        if (!File.Exists(_remoteXmlPath))
        {
            _logger.LogWarning("Remote XML файл не найден {Path}", _remoteXmlPath);
            return;
        }

        try
        {
            XDocument sourceXml;
            lock (_lock)
            {
                sourceXml = XDocument.Load(_remoteXmlPath);
            }

            var sourceOffices = ParseXml(sourceXml);

            if (AreEqual(_offices, sourceOffices))
            {
                _logger.LogDebug("Изменения курсов не обнаружены");
                return;
            }

            _logger.LogInformation("Изменение курсов обнаружено, обновление...");

            // Архивируем текущие данные
            ArchiveCurrentData();

            // Обновляем текущий файл
            lock (_lock)
            {
                sourceXml.Save(_currentXmlPath);
                _offices = sourceOffices;
                _lastHash = CalculateFileHash(_currentXmlPath);
            }

            // Отправляем файл на удаленный сервер
            await UploadFileToRemoteServer(_currentXmlPath);

            // Отправляем обновления через SignalR
            await SendUpdatesToClients();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обновления курсов валют");
        }
    }

    

    private async Task UploadFileToRemoteServer(string filePath)
    {
        try
        {
            if (LogIncludeToRemoteServer())
            {
                _logger.LogWarning("К сожаления файл не получилось отправить на другой сервер");
                return;
            }
            
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Файл не существует: {FilePath}", filePath);
                return;
            }
            
            using var client = new SftpClient(_remoteServerSettings.Host, _remoteServerSettings.Username,
                _remoteServerSettings.Password);

            await Task.Run(() =>
            {
                client.Connect();

                // Проверяем существование директории
                if (!client.Exists(_remoteServerSettings.RemoteDirectory))
                {
                    client.CreateDirectory(_remoteServerSettings.RemoteDirectory);
                }

                using var fileStream = File.OpenRead(filePath);
                var remotePath = $"{_remoteServerSettings.RemoteDirectory}/{Path.GetFileName(filePath)}";
                client.UploadFile(fileStream, remotePath);

                client.Disconnect();
            });

            _logger.LogInformation("Файл успешно отправлен на удаленный сервер: {RemotePath}",
                $"{_remoteServerSettings.Host}{_remoteServerSettings.RemoteDirectory}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при отправке файла на удаленный сервер");
            return;
        }
    }


    private async Task SendUpdatesToClients()
    {
        try
        {
            _logger.LogInformation($"Попытка отправить обновления для {_offices.Count} офисов");

            foreach (var office in _offices)
            {
                try
                {
                    // Добавляем дату обновления в объект
                    var officeWithUpdateTime = new 
                    {
                        office.Id,
                        office.Location,
                        office.Currencies,
                        UpdateTime = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                    };

                    _logger.LogInformation($"Отправка обновления для офиса {office.Id}");
                    await _hubContext.Clients.Group(office.Id).SendAsync("ReceiveUpdate", officeWithUpdateTime);
                    _logger.LogInformation($"Успешно отправлено обновление для офиса {office.Id}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка отправки для офиса {office.Id}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при отправке обновлений");
        }
    }


    private bool AreEqual(List<CityOffice> a, List<CityOffice> b)
    {
        // Простое сравнение без хешей
        return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    }

    private void ArchiveCurrentData()
    {
        var archiveName = $"rates_{DateTime.Now:yyyyMMdd_HHmmss}.xml";
        File.Copy(_currentXmlPath, Path.Combine(_archiveFolder, archiveName));
    }


    private void InitFileWatcher()
    {
        try
        {
            var path = Path.GetDirectoryName(_remoteXmlPath);
            _logger.LogInformation($"Инициализация FileSystemWatcher для пути: {path}");

            _fileWatcher = new FileSystemWatcher
            {
                Path = path,
                Filter = Path.GetFileName(_remoteXmlPath),
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                InternalBufferSize = 65536
            };

            _fileWatcher.Changed += async (s, e) =>
            {
                _logger.LogInformation($"Обнаружено изменение файла: {e.FullPath}");
                await Task.Delay(500);
                await CheckAndUpdateRates();
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка инициализации FileSystemWatcher");
        }
    }

    public void InitializeUpdateTimer()
    {
        _updateTimer = new Timer(async _ => await CheckAndUpdateRates(),
            null,
            _updateInterval,
            _updateInterval);
    }

    private string CalculateFileHash(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = md5.ComputeHash(stream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    public CityOffice? GetOffice(string officeId)
    {
        lock (_lock)
        {
            var office = _offices.FirstOrDefault(o =>
                o.Id.Equals(officeId, StringComparison.OrdinalIgnoreCase));

            if (office == null)
            {
                _logger.LogWarning("Офис {OfficeId} не найден. Доступные: {Offices}",
                    officeId, string.Join(", ", _offices.Select(o => o.Id)));
            }

            return office;
        }
    }


    public void Dispose()
    {
        if (_disposed) return;

        _fileWatcher?.Dispose();
        _updateTimer?.Dispose();
        _disposed = true;
    }

    public string GetCurrentHash()
    {
        lock (_lock)
        {
            return _lastHash;
        }
    }
    
    public bool LogIncludeToRemoteServer()
    {
        bool isValid = true;
        
        if (string.IsNullOrEmpty(_remoteServerSettings.Host))
        {
            _logger.LogWarning("Настройки удаленного сервера не заданы, пропускаем отправку");
            isValid = false;
        }

        if (string.IsNullOrEmpty(_remoteServerSettings.Username))
        {
            _logger.LogWarning("Логин в appsettings.json неверный, измените на правильный!");
            isValid = false;
        }

        if (string.IsNullOrEmpty(_remoteServerSettings.Password))
        {
            _logger.LogWarning("Пароль в appsettings.json неверный, измините на правильный!");
            isValid = false;
        }
        
        return isValid;
    }
}