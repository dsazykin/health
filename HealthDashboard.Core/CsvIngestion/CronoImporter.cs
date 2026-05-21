using System.Globalization;
using CsvHelper;
using Microsoft.EntityFrameworkCore;

namespace HealthDashboard.Core.CsvIngestion;

/// <summary>
/// Imports a Cronometer daily_summary.csv export into the DailyMetrics table.
/// Uses a column-level SQL upsert so that weight, Suunto, and other source data
/// already present for a given date are never overwritten.
/// </summary>
public static class CronoImporter
{
    public static async Task<ImportResult> ImportAsync(AppDbContext db, string csvPath)
    {
        var imported = 0;
        var skipped  = 0;
        var errors   = new List<string>();

        // --- Parse the CSV -------------------------------------------------------
        List<CronoRecord> records;
        try
        {
            using var reader = new StreamReader(csvPath);
            using var csv    = new CsvReader(reader, CultureInfo.InvariantCulture);
            records = csv.GetRecords<CronoRecord>().ToList();
        }
        catch (Exception ex)
        {
            return new ImportResult(0, 0, [$"Failed to read CSV '{csvPath}': {ex.Message}"]);
        }

        // --- Upsert each completed day inside a single transaction ---------------
        await using var transaction = await db.Database.BeginTransactionAsync();

        foreach (var record in records)
        {
            // Skip days the user hasn't closed in Cronometer — data may be partial.
            if (!record.Completed)
            {
                skipped++;
                continue;
            }

            try
            {
                // Round floats to integers — MidpointRounding.AwayFromZero gives
                // the intuitive result (2000.5 → 2001, not banker's rounding to 2000).
                var date     = record.Date;
                var calsIn   = (int)Math.Round(record.Calories,  MidpointRounding.AwayFromZero);
                var proteinG = (int)Math.Round(record.ProteinG,  MidpointRounding.AwayFromZero);
                var carbsG   = (int)Math.Round(record.CarbsG,    MidpointRounding.AwayFromZero);
                var fatG     = (int)Math.Round(record.FatG,      MidpointRounding.AwayFromZero);

                // Column-level upsert: only Crono columns are touched on conflict.
                // New rows get 0 defaults for Suunto/TDEE fields (populated by their own importers later).
                // WeightKg is nullable so it's omitted — NULL means "not yet measured".
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO DailyMetrics
                        (Date, TdeeCalculated, SuuntoActiveCals, SuuntoHrv, SuuntoSleepHours, SuuntoRestingHr,
                         CronoCalsIn, CronoProteinG, CronoCarbsG, CronoFatG)
                    VALUES
                        ({date}, 0, 0, 0, 0, 0,
                         {calsIn}, {proteinG}, {carbsG}, {fatG})
                    ON CONFLICT(Date) DO UPDATE SET
                        CronoCalsIn   = excluded.CronoCalsIn,
                        CronoProteinG = excluded.CronoProteinG,
                        CronoCarbsG   = excluded.CronoCarbsG,
                        CronoFatG     = excluded.CronoFatG;
                    """);

                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Row '{record.Date}': {ex.Message}");
            }
        }

        await transaction.CommitAsync();
        return new ImportResult(imported, skipped, errors);
    }
}
