namespace DocuMind.Data.Entities;

/// <summary>Tracks whether a document is safe to include in retrieval; only Ready documents are searchable.</summary>
public enum DocumentProcessingStatus { Pending, Processing, Ready, Failed }

/// <summary>A running job owns an expiring lease; failed terminal jobs require an explicit new processing request.</summary>
public enum ProcessingJobStatus { Queued, Running, Succeeded, Failed }

/// <summary>Roles are assigned by trusted application code; a browser cannot submit system or assistant authority.</summary>
public enum MessageRole { User, Assistant, System }

/// <summary>Allowlisted public failure categories; raw exception/provider text must never be stored here.</summary>
public enum ProcessingFailureCode
{
    UnsupportedFile, ExtractionFailed, EmbeddingFailed, StorageUnavailable, WorkerInterrupted, ProcessingFailed
}

/// <summary>Maps stored failure codes to safe UI text without including paths, document contents, or secrets.</summary>
public static class ProcessingFailures
{
    /// <summary>Produces a stable public explanation; diagnostic exception details belong in private logging.</summary>
    public static string? GetSafeMessage(ProcessingFailureCode? code) => code switch
    {
        null => null,
        ProcessingFailureCode.UnsupportedFile => "This file type is not supported.",
        ProcessingFailureCode.ExtractionFailed => "Text could not be extracted from this document.",
        ProcessingFailureCode.EmbeddingFailed => "Search preparation is temporarily unavailable.",
        ProcessingFailureCode.StorageUnavailable => "Document storage is temporarily unavailable.",
        ProcessingFailureCode.WorkerInterrupted => "Processing was interrupted and may be retried.",
        _ => "Document processing could not be completed."
    };
}
