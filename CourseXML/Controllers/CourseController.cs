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

        await Task.Delay(100);

        var officeData = _currencyService.GetOffice(office);

        if (officeData == null)
        {
            _logger.LogError($"Office {office} не найден в сервисах!");
            return NotFound($"Офис с идентификатором '{office}' не найден");
        }

        return View(officeData);
    }

}