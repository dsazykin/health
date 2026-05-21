using CsvHelper.Configuration.Attributes;

namespace HealthDashboard.Core.CsvIngestion;

/// <summary>
/// Maps the subset of columns we need from a Cronometer daily_summary.csv export.
/// CsvHelper ignores any unmapped columns in the source file automatically.
/// </summary>
public class CronoRecord
{
    [Name("Date")]
    public string Date { get; set; } = string.Empty;

    [Name("Energy (kcal)")]
    public double Calories { get; set; }

    [Name("Protein (g)")]
    public double ProteinG { get; set; }

    [Name("Carbs (g)")]
    public double CarbsG { get; set; }

    [Name("Fat (g)")]
    public double FatG { get; set; }

    /// <summary>
    /// Cronometer marks a day as complete when the user explicitly closes it.
    /// Incomplete days are skipped during import to avoid partial data.
    /// </summary>
    [Name("Completed")]
    public bool Completed { get; set; }
}
