using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using HealthDashboard.Core;
using HealthDashboard.Core.CsvIngestion;
using HealthDashboard.App.Views;

namespace HealthDashboard.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private string _greeting = "Welcome to Health Dashboard!";
    public string Greeting
    {
        get => _greeting;
        set => SetProperty(ref _greeting, value);
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public ICommand ImportHevyCsvCommand { get; }

    public MainWindowViewModel()
    {
        ImportHevyCsvCommand = new AsyncRelayCommand(ImportHevyCsvAsync);
    }

    private async Task ImportHevyCsvAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            StatusText = "Error: Desktop application lifetime not found.";
            return;
        }

        var parentWindow = desktop.MainWindow;
        if (parentWindow == null)
        {
            StatusText = "Error: Main window not found.";
            return;
        }

        StatusText = "Selecting CSV file...";

        try
        {
            var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Hevy Workouts CSV File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("CSV Files")
                    {
                        Patterns = new[] { "*.csv" }
                    }
                }
            });

            if (files == null || files.Count == 0)
            {
                StatusText = "Ready";
                return;
            }

            var csvPath = files[0].Path.LocalPath;
            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
            {
                StatusText = "Error: Selected file does not exist.";
                return;
            }

            StatusText = "Importing Hevy CSV...";

            ImportResult result;
            using (var db = new AppDbContext())
            {
                result = await HevyImporter.ImportAsync(db, csvPath);
            }

            if (result.NewExercises != null && result.NewExercises.Count > 0)
            {
                StatusText = $"{result.NewExercises.Count} new exercises detected! Waiting for classification...";
                
                var dialog = new NewExercisePromptWindow(result.NewExercises);
                var promptResult = await dialog.ShowDialog<bool>(parentWindow);

                if (promptResult)
                {
                    StatusText = $"Success: Imported {result.Imported} workouts, resolved {result.NewExercises.Count} new exercises!";
                }
                else
                {
                    StatusText = $"Imported {result.Imported} workouts. Warning: new exercises were not fully categorized.";
                }
            }
            else
            {
                StatusText = $"Success: Imported {result.Imported} workouts! (Skipped: {result.Skipped})";
            }

            if (result.HasErrors)
            {
                var errorSummary = string.Join("; ", result.Errors.Take(3));
                StatusText += $" (Errors: {result.Errors.Count} - {errorSummary})";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }
}
