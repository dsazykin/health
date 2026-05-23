using CsvHelper.Configuration.Attributes;

namespace HealthDashboard.Core.CsvIngestion;

public class HevyRecord
{
    [Name("title")]
    public string Title { get; set; } = string.Empty;

    [Name("start_time")]
    public string StartTime { get; set; } = string.Empty;

    [Name("end_time")]
    public string EndTime { get; set; } = string.Empty;

    [Name("description")]
    [Optional]
    public string Description { get; set; } = string.Empty;

    [Name("exercise_title")]
    public string ExerciseTitle { get; set; } = string.Empty;

    [Name("superset_id")]
    [Optional]
    public int? SupersetId { get; set; }

    [Name("exercise_notes")]
    [Optional]
    public string ExerciseNotes { get; set; } = string.Empty;

    [Name("set_index")]
    public int SetIndex { get; set; }

    [Name("set_type")]
    public string SetType { get; set; } = string.Empty;

    [Name("weight_kg")]
    [Optional]
    public double? WeightKg { get; set; }

    [Name("weight_lbs")]
    [Optional]
    public double? WeightLbs { get; set; }

    [Name("weight")]
    [Optional]
    public double? Weight { get; set; }

    [Name("weight_unit")]
    [Optional]
    public string WeightUnit { get; set; } = string.Empty;

    [Name("reps")]
    [Optional] // Can be empty for some exercises (e.g. cardio)
    public int? Reps { get; set; }

    [Name("distance_km")]
    [Optional]
    public double? DistanceKm { get; set; }

    [Name("distance_miles")]
    [Optional]
    public double? DistanceMiles { get; set; }

    [Name("distance")]
    [Optional]
    public double? Distance { get; set; }

    [Name("distance_unit")]
    [Optional]
    public string DistanceUnit { get; set; } = string.Empty;

    [Name("duration_seconds", "seconds")]
    [Optional]
    public int? DurationSeconds { get; set; }

    [Name("rpe")]
    [Optional]
    public double? Rpe { get; set; }
}
