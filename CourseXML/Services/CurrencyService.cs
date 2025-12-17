using System.Globalization;
using System.Xml.Linq;
using CourseXML.Models;
using Microsoft.AspNetCore.SignalR;

namespace CourseXML_main.CourseXML.Services
{
    public class CurrencyService
    {
        private readonly string _xmlFilePath;
        private readonly ILogger<CurrencyService> _logger;
        private readonly IHubContext<CurrencyHub> _hubContext;
        private List<CityOffice> _offices = new();
        private DateTime _lastReadTime = DateTime.MinValue;
        private FileSystemWatcher _fileWatcher;

        public CurrencyService(
            ILogger<CurrencyService> logger,
            IHubContext<CurrencyHub> hubContext,
            IConfiguration configuration)
        {
            _logger = logger;
            _hubContext = hubContext;

            // Получаем путь к XML файлу
            _xmlFilePath = configuration["XmlFilePath"]
                ?? "C:\\Users\\kondr\\Desktop\\rates.xml";

            _logger.LogInformation("Используется XML файл: {Path}", _xmlFilePath);

            // Загружаем начальные данные
            LoadData();

            // Настраиваем наблюдение за файлом
            SetupFileWatcher();

            _logger.LogInformation("CurrencyService инициализирован. Офисов: {Count}", _offices.Count);
        }

        private void LoadData()
        {
            try
            {
                if (!File.Exists(_xmlFilePath))
                {
                    _logger.LogWarning("XML файл не найден: {Path}", _xmlFilePath);
                    _offices = new List<CityOffice>();
                    return;
                }

                var xml = XDocument.Load(_xmlFilePath);
                _offices = ParseXml(xml);

                _logger.LogInformation("Данные загружены. Офисов: {Count}", _offices.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки данных из XML");
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

                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(purchaseStr) || string.IsNullOrEmpty(saleStr))
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
                _logger.LogError(ex, "Ошибка парсинга XML!");
            }

            return offices;
        }

        private void SetupFileWatcher()
        {
            try
            {
                var directory = Path.GetDirectoryName(_xmlFilePath);
                var fileName = Path.GetFileName(_xmlFilePath);

                if (string.IsNullOrEmpty(directory))
                {
                    _logger.LogWarning("Не удалось получить директорию файла");
                    return;
                }

                _fileWatcher = new FileSystemWatcher
                {
                    Path = directory,
                    Filter = fileName,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 65536
                };

                _fileWatcher.Changed += async (sender, e) => await OnFileChanged(sender, e);
                _fileWatcher.Created += async (sender, e) => await OnFileChanged(sender, e);

                _logger.LogInformation("FileSystemWatcher настроен для файла: {File}", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка настройки FileSystemWatcher");
            }
        }

        private async Task OnFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Дебаунсинг - игнорируем частые события
                var now = DateTime.Now;
                if ((now - _lastReadTime).TotalSeconds < 1)
                    return;

                _lastReadTime = now;

                // Ждем освобождения файла
                await Task.Delay(500);

                _logger.LogInformation("Файл изменен: {File}", e.Name);

                // Сохраняем старые данные для сравнения
                var oldOffices = new List<CityOffice>(_offices);

                // Загружаем новые данные
                LoadData();

                // Если данные изменились - отправляем обновления
                if (!AreDataEqual(oldOffices, _offices))
                {
                    _logger.LogInformation("Данные обновились, отправка клиентам");
                    await SendUpdatesToClients();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обработки изменения файла");
            }
        }

        private bool AreDataEqual(List<CityOffice> oldData, List<CityOffice> newData)
        {
            if (oldData.Count != newData.Count) return false;

            foreach (var oldOffice in oldData)
            {
                var newOffice = newData.FirstOrDefault(o => o.Id == oldOffice.Id);
                if (newOffice == null) return false;

                if (newOffice.Currencies.Count != oldOffice.Currencies.Count) return false;

                foreach (var oldCurrency in oldOffice.Currencies)
                {
                    var newCurrency = newOffice.Currencies.FirstOrDefault(c => c.Name == oldCurrency.Name);
                    if (newCurrency == null) return false;

                    if (newCurrency.Purchase != oldCurrency.Purchase || newCurrency.Sale != oldCurrency.Sale)
                        return false;
                }
            }

            return true;
        }

        private async Task SendUpdatesToClients()
        {
            try
            {
                foreach (var office in _offices)
                {
                    try
                    {
                        var officeData = new
                        {
                            office.Id,
                            office.Location,
                            office.Currencies,
                            UpdateTime = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")
                        };

                        await _hubContext.Clients.Group(office.Id.ToLower()).SendAsync("ReceiveUpdate", officeData);
                        _logger.LogDebug("Обновление отправлено для офиса: {OfficeId}", office.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ошибка отправки обновления для офиса {OfficeId}", office.Id);
                    }
                }
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

    }
}
