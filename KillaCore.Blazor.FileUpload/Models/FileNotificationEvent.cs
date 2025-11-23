namespace KillaCore.Blazor.FileUpload.Models;

/// <summary>
/// Represents a single update event from the File Upload Processor.
/// Used by the UI to react to progress, errors, or status changes.
/// </summary>
/// <param name="Type">The type of event (Progress, Completed, Failed, etc.)</param>
/// <param name="Transfer">The specific file model involved (null if it is a Batch event).</param>
/// <param name="Message">Optional context message (e.g., error details).</param>
public sealed record FileNotificationEvent(
    EventNotificationType Type,
    FileTransferData? Transfer = null,
    string? Message = null
);