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
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core;
using HealthDashboard.Core.CsvIngestion;
using HealthDashboard.Core.Analytics;
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

    private int _latestTdeeCalculated;
    public int LatestTdeeCalculated
    {
        get => _latestTdeeCalculated;
        set => SetProperty(ref _latestTdeeCalculated, value);
    }

    public ICommand ImportHevyCsvCommand { get; }
    public ICommand ImportCronoCsvCommand { get; }

    public MainWindowViewModel()
    {
        ImportHevyCsvCommand = new AsyncRelayCommand(ImportHevyCsvAsync);
        ImportCronoCsvCommand = new AsyncRelayCommand(ImportCronoCsvAsync);
        _ = LoadLatestTdeeAsync();
    }

    private async Task LoadLatestTdeeAsync()
    {
        try
        {
            using (var db = new AppDbContext())
            {
                var latestMetric = await db.DailyMetrics
                    .Where(m => m.TdeeCalculated > 0)
                    .OrderByDescending(m => m.Date)
                    .FirstOrDefaultAsync();

                if (latestMetric != null)
                {
                    LatestTdeeCalculated = latestMetric.TdeeCalculated;
                }
                else
                {
                    var defaultTdeeConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "DefaultTdee");
                    int defaultTdee = 2000;
                    if (defaultTdeeConfig != null && int.TryParse(defaultTdeeConfig.Value, out int parsedTdee))
                    {
                        defaultTdee = parsedTdee;
                    }
                    LatestTdeeCalculated = defaultTdee;
                }
            }
        }
        catch
        {
            LatestTdeeCalculated = 2000;
        }
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

            // Recalculate TDEE after CSV import
            StatusText += " Recalculating TDEE...";
            using (var db = new AppDbContext())
            {
                await new TdeeEngine().UpdateTdeeHistoryAsync(db);
            }
            await LoadLatestTdeeAsync();
            StatusText = "Recalculation complete! " + StatusText;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    private async Task ImportCronoCsvAsync()
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

        StatusText = "Selecting Cronometer CSV file...";

        try
        {
            var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Cronometer daily_summary.csv File",
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

            StatusText = "Importing Cronometer CSV...";

            ImportResult result;
            using (var db = new AppDbContext())
            {
                result = await CronoImporter.ImportAsync(db, csvPath);
            }

            StatusText = $"Success: Imported {result.Imported} daily summaries! (Skipped: {result.Skipped})";

            if (result.HasErrors)
            {
                var errorSummary = string.Join("; ", result.Errors.Take(3));
                StatusText += $" (Errors: {result.Errors.Count} - {errorSummary})";
            }

            // Recalculate TDEE after CSV import
            StatusText += " Recalculating TDEE...";
            using (var db = new AppDbContext())
            {
                await new TdeeEngine().UpdateTdeeHistoryAsync(db);
            }
            await LoadLatestTdeeAsync();
            StatusText = "Recalculation complete! " + StatusText;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }
}
