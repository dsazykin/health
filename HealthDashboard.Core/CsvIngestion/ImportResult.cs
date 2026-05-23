namespace HealthDashboard.Core.CsvIngestion;

/// <summary>
/// Returned by every CSV importer to summarise what happened during a run.
/// </summary>
public record ImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors, IReadOnlyList<string>? NewExercises = null)
{
    public bool HasErrors => Errors.Count > 0;
}
