namespace CursorCleaner.Models;

public sealed record OperationError(string Path, string Message);

public sealed record CleanupResult(
    int DeletedFiles,
    long ReclaimedBytes,
    IReadOnlyList<OperationError> Errors,
    bool Cancelled = false);

public sealed record BackupResult(
    bool Succeeded,
    string? BackupPath,
    int BackedUpFiles,
    long BackedUpBytes,
    IReadOnlyList<OperationError> Errors);

public sealed record RestoreResult(
    bool Succeeded,
    int RestoredFiles,
    IReadOnlyList<OperationError> Errors);

public sealed record DatabaseMaintenanceResult(
    string DatabasePath,
    bool Succeeded,
    long SizeBefore,
    long SizeAfter,
    string? ErrorMessage);

public sealed record DatabaseOperationSummary(
    IReadOnlyList<DatabaseMaintenanceResult> Results,
    long ReclaimedBytes);
