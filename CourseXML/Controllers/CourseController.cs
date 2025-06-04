
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

    public IActionResult Index(string office)
    {
        _logger.LogInformation($"Запрошенный офис: {office}");
        
        if (string.IsNullOrEmpty(office))
            return BadRequest("Укажите город (office=tlt, msk, perm)");

        var officeData = _currencyService.GetOffice(office);
    
        if (officeData == null)
        {
            _logger.LogError($"Office {office} не найден в сервисах!");
            return NotFound($"Офис с идентификатором '{office}' не найден");
        }
    
        return View(officeData);
    }

    [HttpGet("api/rates")]
    public async Task<IActionResult> GetRates(string office, string? lastHash)
    {
        await _currencyService.CheckAndUpdateRates();
    
        var officeData = _currencyService.GetOffice(office);
        if (officeData == null)
            return NotFound();

        var currentHash = _currencyService.GetCurrentHash();
        var dataChanged = lastHash != currentHash;

        return Ok(new {
            Changed = dataChanged,
            Hash = currentHash,
            Data = dataChanged ? officeData : null
        });
    }
}