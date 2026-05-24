using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core;
using HealthDashboard.Core.Models;

namespace HealthDashboard.App.ViewModels;

public partial class NewExerciseItemViewModel : ObservableObject
{
    private string _exerciseName = string.Empty;
    public string ExerciseName
    {
        get => _exerciseName;
        set => SetProperty(ref _exerciseName, value);
    }

    private string _selectedMuscleGroup = "Other";
    public string SelectedMuscleGroup
    {
        get => _selectedMuscleGroup;
        set => SetProperty(ref _selectedMuscleGroup, value);
    }

    private string _selectedMovementPattern = "Other";
    public string SelectedMovementPattern
    {
        get => _selectedMovementPattern;
        set => SetProperty(ref _selectedMovementPattern, value);
    }
}

public partial class NewExercisePromptViewModel : ViewModelBase
{
    public ObservableCollection<NewExerciseItemViewModel> Items { get; } = new();

    public static IReadOnlyList<string> AvailableMuscleGroups { get; } = new[]
    {
        "Chest", "Back", "Quads", "Hamstrings", "Shoulders", "Biceps", "Triceps", "Core", "Other"
    };

    public static IReadOnlyList<string> AvailableMovementPatterns { get; } = new[]
    {
        "Push", "Pull", "Legs", "Core", "Cardio", "Other"
    };

    public NewExercisePromptViewModel()
    {
    }

    public NewExercisePromptViewModel(IEnumerable<string> newExercises)
    {
        foreach (var name in newExercises)
        {
            Items.Add(new NewExerciseItemViewModel
            {
                ExerciseName = name,
                SelectedMuscleGroup = "Other",
                SelectedMovementPattern = "Other"
            });
        }
    }

    public async Task SaveAsync()
    {
        using var db = new AppDbContext();
        foreach (var item in Items)
        {
            var exercise = await db.Exercises.FirstOrDefaultAsync(e => e.ExerciseName == item.ExerciseName);
            if (exercise != null)
            {
                exercise.TargetMuscleGroup = item.SelectedMuscleGroup;
                exercise.MovementPattern = item.SelectedMovementPattern;
            }
        }
        await db.SaveChangesAsync();
    }
}
