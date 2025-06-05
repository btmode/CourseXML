using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;

public class CurrencyService
{
    private readonly string _currentXmlPath;
    private readonly string _archiveFolder;
    private readonly ILogger<CurrencyService> _logger;
    private readonly IHubContext<CurrencyHub> _hubContext;
    private readonly HttpClient _httpClient;
    private readonly string _sourceXmlPath;
    private FileSystemWatcher? _fileWatcher;
    private readonly object _lock = new();
    private string _lastHash = string.Empty;
    private List<CityOffice> _offices = new();
    private bool _isUpdating = false;
    private readonly Timer _updateTimer;
    private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(5);

  
    
    public CurrencyService(
        IWebHostEnvironment env,
        ILogger<CurrencyService> logger,
        IHubContext<CurrencyHub> hubContext,
        string sourceXmlPath=@"O:\q\current\rates.xml")
    {
        _logger = logger;
        _hubContext = hubContext;
        _sourceXmlPath = sourceXmlPath;
        _currentXmlPath = Path.Combine(env.ContentRootPath, "Data", "current", "rates.xml");
        _archiveFolder = Path.Combine(env.ContentRootPath, "Data", "archive");

        Directory.CreateDirectory(Path.GetDirectoryName(_currentXmlPath)!);
        Directory.CreateDirectory(_archiveFolder);

        LoadCurrentData();
        InitFileWatcher();
        
        _updateTimer = new Timer(async _ => await CheckAndUpdateRates(), 
            null, 
            _updateInterval, 
            _updateInterval);
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
                    Id = office.Attribute("id").Value,
                    Location = office.Attribute("location").Value,
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
                                    Name = c.Element("name").Value,
                                    Purchase = decimal.Parse(c.Element("purchase").Value, CultureInfo.InvariantCulture),
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
        if (!File.Exists(_sourceXmlPath)) 
        {
            _logger.LogWarning("Source XML file not found at {Path}", _sourceXmlPath);
            return;
        }

        try
        {
            // Читаем исходный файл с блокировкой для избежания конфликтов
            XDocument sourceXml;
            lock (_lock)
            {
                sourceXml = XDocument.Load(_sourceXmlPath);
            }

            var sourceOffices = ParseXml(sourceXml);
        
            if (AreEqual(_offices, sourceOffices)) 
            {
                _logger.LogDebug("No changes detected in currency rates");
                return;
            }

            _logger.LogInformation("Currency rates changes detected, updating...");

            // Архивируем текущие данные
            ArchiveCurrentData();

            // Обновляем текущий файл
            lock (_lock)
            {
                sourceXml.Save(_currentXmlPath);
                _offices = sourceOffices;
                _lastHash = CalculateFileHash(_currentXmlPath);
            }

            // Отправляем обновления через SignalR
            await SendUpdatesToClients();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обновления курсов валют");
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
                    _logger.LogInformation($"Отправка обновления для офиса {office.Id}");
                    await _hubContext.Clients.Group(office.Id).SendAsync("ReceiveUpdate", office);
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
            var path = Path.GetDirectoryName(_sourceXmlPath);
            _logger.LogInformation($"Инициализация FileSystemWatcher для пути: {path}");
        
            _fileWatcher = new FileSystemWatcher
            {
                Path = path,
                Filter = Path.GetFileName(_sourceXmlPath),
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
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
    

    public string GetCurrentHash()
    {
        lock (_lock)
        {
            return _lastHash;
        }
    }
    
}
