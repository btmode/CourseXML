namespace CourseXML.Models;

public class CityOffice
{
    public string? Id { get; set; }
    public string? Location { get; set; }
    public List<CurrencyRate> Currencies  { get; set; } = [];
}