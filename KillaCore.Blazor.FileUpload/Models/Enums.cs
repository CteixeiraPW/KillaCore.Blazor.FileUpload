using System.ComponentModel;

namespace KillaCore.Blazor.FileUpload.Models;

public enum EventNotificationType
{
    // --- Batch Level Events ---
    [Description("Batch Started")]
    BatchStarted,      // Processing loop begins
    [Description("Batch Completed")]
    BatchCompleted,    // All files finished (success, failed, or skipped)
    [Description("Batch Cancelled")]
    BatchCancelled,    // User clicked "Cancel All" or component disposed
    [Description("Batch Failed")]
    BatchFailed,       // Critical system error stopping the whole queue

    // --- File Level Events ---
    [Description("Progress")]
    Progress,          // Upload percentage changed
    [Description("Stage Change")]
    StageChange,       // File moved from Uploading -> Hashing, etc.
    [Description("File Completed")]
    FileCompleted,     // Successfully saved
    [Description("File Failed")]
    FileFailed,        // Network error, hash mismatch, or server error
    [Description("File Cancelled")]
    FileCancelled,     // User cancelled specific file
    [Description("File Skipped")]
    FileSkipped        // Skipped (Duplicate, Size limit, Invalid Type)
}


public enum TransferStage
{
    [Description("Pending")]
    Pending = 0,                 // Default state. Sitting in queue.
    [Description("Validating Attributes")]
    ValidatingAttributes,        // Checking Size, MimeType (Pre-upload)
    [Description("Uploading")]
    Uploading,                   // Sending bytes (JS Interop)
    [Description("Hashing File")]
    Hashing,                     // Calculation SHA256 (Server-side CPU)
    [Description("Verifying Local Duplicates")]
    VerifyingLocalDuplicates,    // Checking against other files in the current batch
    [Description("Verifying Remote Duplicates")]
    VerifyingRemoteDuplicates,   // Checking against the Server DB/API
    [Description("Saving to Server")]
    ServerSaving                // Moving temp file to final storage
}

// This enum ONLY contains the optional features the user can toggle.
public enum FileUploadFeature
{
    [Description("Verify Local Duplicates")]
    VerifyLocalDuplicates,   // Maps to TransferStage.VerifyingLocalDuplicates
    [Description("Verify Remote Duplicates")]
    VerifyRemoteDuplicates,  // Maps to TransferStage.VerifyingRemoteDuplicates
    [Description("Save To Server")]
    SaveToServer             // Maps to TransferStage.ServerSaving
}

public enum TransferStatus
{
    [Description("Pending")]
    Pending,     // Not started yet (Added this to match the UI "gray" state)
    [Description("In Progress")]
    InProgress,  // Active (Matches any Stage: Uploading, Hashing, Saving)
    [Description("Completed")]
    Completed,   // Green state
    [Description("Failed")]
    Failed,      // Red state (Error)
    [Description("Cancelled")]
    Cancelled,   // Gray/Strikethrough state
    [Description("Skipped")]
    Skipped      // Yellow state (Duplicate or Validation error)
}