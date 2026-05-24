using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HealthDashboard.Core.HevyApi
{
    public class HevyWorkoutResponse
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("page_count")]
        public int PageCount { get; set; }

        [JsonPropertyName("workouts")]
        public List<HevyApiWorkout> Workouts { get; set; } = new List<HevyApiWorkout>();
    }

    public class HevyApiWorkout
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("routine_id")]
        public string? RoutineId { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("start_time")]
        public string StartTime { get; set; } = string.Empty;

        [JsonPropertyName("end_time")]
        public string EndTime { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("exercises")]
        public List<HevyApiExercise> Exercises { get; set; } = new List<HevyApiExercise>();
    }

    public class HevyApiExercise
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        [JsonPropertyName("exercise_template_id")]
        public string ExerciseTemplateId { get; set; } = string.Empty;

        [JsonPropertyName("supersets_id")]
        public int? SupersetsId { get; set; }

        [JsonPropertyName("sets")]
        public List<HevyApiSet> Sets { get; set; } = new List<HevyApiSet>();
    }

    public class HevyApiSet
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("weight_kg")]
        public double? WeightKg { get; set; }

        [JsonPropertyName("reps")]
        public int? Reps { get; set; }

        [JsonPropertyName("distance_meters")]
        public double? DistanceMeters { get; set; }

        [JsonPropertyName("duration_seconds")]
        public int? DurationSeconds { get; set; }

        [JsonPropertyName("rpe")]
        public double? Rpe { get; set; }

        [JsonPropertyName("custom_metric")]
        public double? CustomMetric { get; set; }
    }

    public class HevyApiUserInfoResponse
    {
        [JsonPropertyName("data")]
        public HevyApiUserInfo Data { get; set; } = new HevyApiUserInfo();
    }

    public class HevyApiUserInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}
