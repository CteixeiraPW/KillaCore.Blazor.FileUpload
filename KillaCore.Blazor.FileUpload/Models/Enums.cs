namespace KillaCore.Blazor.FileUpload.Models;

public enum EventNotificationType
{
    // --- Batch Level Events ---
    BatchStarted,      // Processing loop begins
    BatchCompleted,    // All files finished (success, failed, or skipped)
    BatchCancelled,    // User clicked "Cancel All" or component disposed
    BatchFailed,       // Critical system error stopping the whole queue

    // --- File Level Events ---
    Progress,          // Upload percentage changed
    StageChange,       // File moved from Uploading -> Hashing, etc.

    FileCompleted,     // Successfully saved
    FileFailed,        // Network error, hash mismatch, or server error
    FileCancelled,     // User cancelled specific file
    FileSkipped        // Skipped (Duplicate, Size limit, Invalid Type)
}


public enum TransferStage
{
    Pending = 0,                 // Default state. Sitting in queue.

    ValidatingAttributes,        // Checking Size, MimeType (Pre-upload)

    Uploading,                   // Sending bytes (JS Interop)

    Hashing,                     // Calculation SHA256 (Server-side CPU)

    VerifyingLocalDuplicates,    // Checking against other files in the current batch

    VerifyingRemoteDuplicates,   // Checking against the Server DB/API

    ServerSaving,                      // Moving temp file to final storage

    // Terminal Stages (Optional, but helps if you want Stage to track end state too)
    // Completed, Failed, Cancelled 
}

// This enum ONLY contains the optional features the user can toggle.
public enum FileUploadFeature
{
    VerifyLocalDuplicates,   // Maps to TransferStage.VerifyingLocalDuplicates
    VerifyRemoteDuplicates,  // Maps to TransferStage.VerifyingRemoteDuplicates
    SaveToServer             // Maps to TransferStage.ServerSaving
}

public enum TransferStatus
{
    Pending,     // Not started yet (Added this to match the UI "gray" state)
    InProgress,  // Active (Matches any Stage: Uploading, Hashing, Saving)
    Completed,   // Green state
    Failed,      // Red state (Error)
    Cancelled,   // Gray/Strikethrough state
    Skipped      // Yellow state (Duplicate or Validation error)
}