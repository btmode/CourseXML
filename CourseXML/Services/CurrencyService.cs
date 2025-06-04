using System.Globalization;
using System.Xml.Linq;
using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;

public class CurrencyService
{
    private readonly string _currentXmlPath;
    private readonly string _archiveFolder;
    private readonly string _sourceXmlPath;
    private string _lastHash = string.Empty;
    private List<CityOffice> _offices = new();
    private readonly object _lock = new();
    private readonly ILogger<CurrencyService> _logger;
    private readonly IHubContext<CurrencyHubService> _hubContext;

    public CurrencyService(
        IWebHostEnvironment env, 
        ILogger<CurrencyService> logger,
        IHubContext<CurrencyHubService> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
        _currentXmlPath = Path.Combine(env.ContentRootPath, "Data", "current", "rates.xml");
        _archiveFolder = Path.Combine(env.ContentRootPath, "Data", "archive");
        _sourceXmlPath = Path.Combine(env.ContentRootPath, "Data", "source", "rates_source.xml");

        Directory.CreateDirectory(Path.GetDirectoryName(_currentXmlPath)!);
        Directory.CreateDirectory(_archiveFolder);
        InitializeData();
    }

    private void InitializeData()
    {
        lock (_lock)
        {
            if (!File.Exists(_currentXmlPath))
            {
                if (File.Exists(_sourceXmlPath))
                {
                    File.Copy(_sourceXmlPath, _currentXmlPath);
                    _logger.LogInformation($"Скопировано из исходного кода в: {_currentXmlPath}");
                }
                else
                {
                    _logger.LogError($"Исходный файл не найден: {_sourceXmlPath}");
                    new XDocument(new XElement("exchangeRates")).Save(_currentXmlPath);
                }
            }
            else
            {
                _logger.LogInformation($"Использование существующего файла: {_currentXmlPath}");
            }

            LoadCurrentData();

            if (_offices.Count == 0)
            {
                _logger.LogError("Нет загруженых! Проверьте XML файл: " + _currentXmlPath);
            }
        }
    }

    private void LoadCurrentData()
    {
        try
        {
            var xml = XDocument.Load(_currentXmlPath);
            _offices = ParseXml(xml);
            _lastHash = CalculateFileHash(_currentXmlPath);
        }
        catch (Exception ex)
        {
            // Логирование ошибки
            Console.WriteLine($"Ошибка загрузки XML: {ex.Message}");
            _offices = new List<CityOffice>();
        }
    }

    private List<CityOffice> ParseXml(XDocument xml)
    {
        try
        {
            var offices = xml.Root?.Elements("office")
                .Where(o => o.Attribute("id") != null)
                .Select(office => new CityOffice
                {
                    Id = office.Attribute("id")?.Value?.Trim(),
                    Location = office.Attribute("location")?.Value,
                    Currencies = office.Elements("currency")
                        .Where(c => c.Element("name") != null)
                        .Select(c =>
                        {
                            // Добавляем логирование для отладки
                            var name = c.Element("name")?.Value?.Trim();
                            var purchaseStr = c.Element("purchase")?.Value;
                            var saleStr = c.Element("sale")?.Value;

                            _logger.LogDebug($"Анализ валюты: {name}, purchase: {purchaseStr}, sale: {saleStr}");

                            decimal.TryParse(purchaseStr,
                                NumberStyles.Any,
                                CultureInfo.InvariantCulture,
                                out var purchase);

                            decimal.TryParse(saleStr,
                                NumberStyles.Any,
                                CultureInfo.InvariantCulture,
                                out var sale);

                            return new CurrencyRate
                            {
                                Name = name ?? "USD",
                                Purchase = purchase,
                                Sale = sale
                            };
                        }).ToList()
                })
                .Where(o => !string.IsNullOrEmpty(o.Id))
                .ToList() ?? new List<CityOffice>();

            _logger.LogInformation(
                $"Анализ offices: {string.Join(", ", offices.Select(o => $"{o.Id} ({o.Location})"))}");
            return offices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing XML");
            return new List<CityOffice>();
        }
    }


    public async Task CheckAndUpdateRates()
    {
        bool hasChanges = false;
    
        lock (_lock)
        {
            if (!File.Exists(_sourceXmlPath)) 
            {
                _logger.LogWarning("Source XML file not found");
                return;
            }

            var newHash = CalculateFileHash(_sourceXmlPath);
            if (newHash == _lastHash) return;

            try
            {
                ArchiveCurrentData();
                File.Copy(_sourceXmlPath, _currentXmlPath, true);
                LoadCurrentData();
                hasChanges = true;
                _logger.LogInformation("Обновление прошло успешно:)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при обновлении:(");
                return;
            }
        }

        if (hasChanges)
        {
            foreach (var office in _offices)
            {
                try
                {
                    await _hubContext.Clients.Group(office.Id)
                        .SendAsync("ReceiveCurrencyUpdate", office);
                    _logger.LogInformation($"Sent update to group: {office.Id}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error sending update to group: {office.Id}");
                }
            }
        }
    }


    private void ArchiveCurrentData()
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var archivePath = Path.Combine(_archiveFolder, $"rates_{timestamp}.xml");
        File.Copy(_currentXmlPath, archivePath);
    }

    private string CalculateFileHash(string filePath)
    {
        try
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    public CityOffice? GetOffice(string officeId)
    {
        if (string.IsNullOrWhiteSpace(officeId))
            return null;

        lock (_lock)
        {
            var office = _offices.FirstOrDefault(o =>
                string.Equals(o.Id, officeId, StringComparison.OrdinalIgnoreCase));

            _logger.LogInformation(office == null
                ? $"Office '{officeId}' не найден. Доступный: {string.Join(", ", _offices.Select(o => o.Id))}"
                : $"Ищем office: {office.Id} ({office.Location})");

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