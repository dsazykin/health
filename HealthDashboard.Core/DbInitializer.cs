using System;
using System.Collections.Generic;
using System.Linq;
using HealthDashboard.Core.Models;

namespace HealthDashboard.Core
{
    public static class DbInitializer
    {
        public static void Initialize(AppDbContext context)
        {
            // Ensure the SQLite database file and schema is created
            context.Database.EnsureCreated();

            // Seed default Suunto config keys if they do not exist
            var changed = false;
            if (!context.Configs.Any(c => c.Key == "SuuntoClientId"))
            {
                context.Configs.Add(new Config { Key = "SuuntoClientId", Value = "YOUR_SUUNTO_CLIENT_ID" });
                changed = true;
            }
            if (!context.Configs.Any(c => c.Key == "SuuntoClientSecret"))
            {
                context.Configs.Add(new Config { Key = "SuuntoClientSecret", Value = "YOUR_SUUNTO_CLIENT_SECRET" });
                changed = true;
            }
            if (!context.Configs.Any(c => c.Key == "SuuntoRedirectUri"))
            {
                context.Configs.Add(new Config { Key = "SuuntoRedirectUri", Value = "http://127.0.0.1:5005/callback" });
                changed = true;
            }
            if (!context.Configs.Any(c => c.Key == "SuuntoSubscriptionKey"))
            {
                context.Configs.Add(new Config { Key = "SuuntoSubscriptionKey", Value = "YOUR_SUUNTO_SUBSCRIPTION_KEY" });
                changed = true;
            }
            if (!context.Configs.Any(c => c.Key == "TargetRateOfChange"))
            {
                context.Configs.Add(new Config { Key = "TargetRateOfChange", Value = "0.0" });
                changed = true;
            }
            if (!context.Configs.Any(c => c.Key == "GoalWeightKg"))
            {
                context.Configs.Add(new Config { Key = "GoalWeightKg", Value = "0.0" });
                changed = true;
            }
            if (changed)
            {
                context.SaveChanges();
            }

            // Seed default exercises if none exist
            if (!context.Exercises.Any())
            {
                var defaultExercises = new List<Exercise>
                {
                    new Exercise { ExerciseName = "Bench Press (Barbell)", TargetMuscleGroup = "Chest", MovementPattern = "Push" },
                    new Exercise { ExerciseName = "Squat (Barbell)", TargetMuscleGroup = "Quads", MovementPattern = "Legs" },
                    new Exercise { ExerciseName = "Deadlift (Barbell)", TargetMuscleGroup = "Hamstrings", MovementPattern = "Pull" },
                    new Exercise { ExerciseName = "Pull Up", TargetMuscleGroup = "Lats", MovementPattern = "Pull" },
                    new Exercise { ExerciseName = "Overhead Press (Barbell)", TargetMuscleGroup = "Shoulders", MovementPattern = "Push" },
                    new Exercise { ExerciseName = "Barbell Row", TargetMuscleGroup = "Upper Back", MovementPattern = "Pull" },
                    new Exercise { ExerciseName = "Incline Bench Press (Barbell)", TargetMuscleGroup = "Chest", MovementPattern = "Push" },
                    new Exercise { ExerciseName = "Romanian Deadlift (Barbell)", TargetMuscleGroup = "Hamstrings", MovementPattern = "Legs" },
                    new Exercise { ExerciseName = "Lateral Raise (Dumbbell)", TargetMuscleGroup = "Shoulders", MovementPattern = "Push" },
                    new Exercise { ExerciseName = "Bicep Curl (Dumbbell)", TargetMuscleGroup = "Biceps", MovementPattern = "Pull" },
                    new Exercise { ExerciseName = "Triceps Pushdown (Cable)", TargetMuscleGroup = "Triceps", MovementPattern = "Push" },
                    new Exercise { ExerciseName = "Leg Press", TargetMuscleGroup = "Quads", MovementPattern = "Legs" }
                };

                context.Exercises.AddRange(defaultExercises);
                context.SaveChanges();
            }
        }
    }
}
