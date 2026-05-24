using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HealthDashboard.App.ViewModels;

namespace HealthDashboard.App.Views;

public partial class NewExercisePromptWindow : Window
{
    public NewExercisePromptWindow()
    {
        InitializeComponent();
        DataContext = new NewExercisePromptViewModel();
    }

    public NewExercisePromptWindow(IEnumerable<string> newExercises)
    {
        InitializeComponent();
        DataContext = new NewExercisePromptViewModel(newExercises);
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NewExercisePromptViewModel vm)
        {
            await vm.SaveAsync();
            Close(true);
        }
        else
        {
            Close(false);
        }
    }
}
