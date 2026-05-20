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
