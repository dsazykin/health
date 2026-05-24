# Phase 2.2 - Step 5: Avalonia UI Dialog/Prompt Orchestration

## Objective
When the user imports a Hevy CSV file, the backend `HevyImporter` might encounter custom exercises that do not yet exist in the `Exercises` reference table. The backend automatically saves these with placeholder values (`"Other"`) and returns their names in the `ImportResult.NewExercises` list. 

The goal of this task is to build the UI orchestration in the Avalonia `HealthDashboard.App` project to gracefully collect the missing information (Target Muscle Group and Movement Pattern) from the user and save it to the database.

## Requirements

### 1. ViewModel Orchestration
- The ViewModel responsible for handling the file picker and CSV import must inspect the `ImportResult`.
- If `ImportResult.NewExercises` is not null and contains elements, the ViewModel should pause the standard success notification and instead trigger a sequential UI flow.

### 2. UI/UX Flow
- Present a modal dialog or wizard to the user.
- For each exercise name in the `NewExercises` list, ask the user:
  - **"We noticed a new exercise: [Exercise Name]. What muscle group does it target?"** (Dropdown of common muscle groups: Chest, Back, Quads, Hamstrings, Shoulders, Biceps, Triceps, Core, Other).
  - **"What is its movement pattern?"** (Dropdown of common patterns: Push, Pull, Legs, Core, Cardio, Other).
- The dialog should ideally present these sequentially or in a clean list format where the user can fill them all out and click "Save".

### 3. Database Update
- Once the user submits the form, the ViewModel should update the corresponding `Exercise` records in the database via Entity Framework Core:
  ```csharp
  // Pseudocode for the save operation
  var exercise = await dbContext.Exercises.FirstOrDefaultAsync(e => e.ExerciseName == newExerciseName);
  if (exercise != null)
  {
      exercise.TargetMuscleGroup = userSelectedMuscleGroup;
      exercise.MovementPattern = userSelectedMovementPattern;
      await dbContext.SaveChangesAsync();
  }
  ```

### 4. Clean Architecture Considerations
- Keep the Avalonia-specific Dialog and Window logic inside the `HealthDashboard.App` project.
- Do not leak Avalonia dependencies into `HealthDashboard.Core`. 

## Next Steps
When ready to resume, we will:
1. Create the Avalonia View (`NewExercisePromptView.axaml`) and its ViewModel.
2. Integrate it into the main dashboard's import pipeline.
