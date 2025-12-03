using CourseXML_main.CourseXML.Services;
using Microsoft.AspNetCore.Mvc;

public class CourseController : Controller
{
    private readonly CurrencyService _currencyService;
    private readonly ILogger<CourseController> _logger;

    public CourseController(CurrencyService currencyService, ILogger<CourseController> logger)
    {
        _currencyService = currencyService;
        _logger = logger;
    }


    
    public async Task<IActionResult> Index(string office)
    {
        _logger.LogInformation($"Запрошенный офис: {office}");

        if (string.IsNullOrEmpty(office))
            return BadRequest("Укажите город (office=tlt, msk, perm)");

        // Даем время на инициализацию при первом запросе
        await Task.Delay(100);

        var officeData = _currencyService.GetOffice(office);

        if (officeData == null)
        {
            _logger.LogError($"Office {office} не найден в сервисах!");
            return NotFound($"Офис с идентификатором '{office}' не найден");
        }

        return View(officeData);
    }

    //[HttpGet("/api/rates")]
    //public async Task<IActionResult> GetRates([FromQuery] string office, [FromQuery] string? lastHash)
    //{
    //    _logger.LogInformation("Получение курсов для офиса {Office} (LastHash: {LastHash})", office, lastHash);

    //    if (string.IsNullOrWhiteSpace(office))
    //        return BadRequest("Не указан офис");

       
    //    await _currencyService.CheckAndUpdateRates();

    //    var officeData = _currencyService.GetOffice(office);
    //    if (officeData == null)
    //    {
    //        _logger.LogWarning("Офис {Office} не найден при запросе API", office);
    //        return NotFound($"Офис '{office}' не найден");
    //    }

    //    var currentHash = _currencyService.GetCurrentHash();
    //    bool hasChanged = lastHash != currentHash;

    //    return Ok(new
    //    {
    //        Changed = hasChanged,
    //        Hash = currentHash,
    //        Data = hasChanged ? officeData : null
    //    });
    //}
}