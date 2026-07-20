using System.Collections.ObjectModel;

namespace AutomaticOnlineHostComputer.Service.OperationalEvents;

public enum OperationalEventSeverity
{
    Critical,
    Error,
    Warning,
    Information
}

public enum OperationalEventCategory
{
    Safety,
    FinalFailure,
    PhysicalUnknown,
    AutomaticRecovery,
    Communication,
    StageStall,
    FineTune,
    Motion,
    Magnet,
    Sensor,
    Handshake,
    Emergency
}

public enum EvidenceAvailability
{
    Confirmed,
    Inferred,
    Unknown,
    Unavailable,
    NotApplicable
}

public enum DeviceCommandState
{
    NotSent,
    SentUnconfirmed,
    Acknowledged,
    Failed,
    Unknown,
    Unavailable,
    NotApplicable
}

public enum PhysicalConclusionCode
{
    Unknown,
    CommandNotSent,
    CommandResultUnknown,
    WorkpieceConfirmedHeld,
    WorkpieceConfirmedPlaced,
    SafePositionConfirmed,
    ManualConfirmationRequired
}

public enum RecoveryStepState
{
    NotAttempted,
    Succeeded,
    Failed,
    Skipped,
    Unknown
}

public sealed record EvidenceValue<T>(
    EvidenceAvailability Availability,
    T Value,
    bool HasValue,
    string Reason)
{
    public static EvidenceValue<T> Confirmed(T value, string reason) =>
        new(EvidenceAvailability.Confirmed, value, true, reason);

    public static EvidenceValue<T> Inferred(T value, string reason) =>
        new(EvidenceAvailability.Inferred, value, true, reason);

    public static EvidenceValue<T> Unknown(string reason) =>
        new(EvidenceAvailability.Unknown, default!, false, reason);

    public static EvidenceValue<T> Unavailable(string reason) =>
        new(EvidenceAvailability.Unavailable, default!, false, reason);

    public static EvidenceValue<T> NotApplicable(string reason) =>
        new(EvidenceAvailability.NotApplicable, default!, false, reason);
}

public sealed record DeviceCommandEvidence(
    DeviceCommandState State,
    EvidenceAvailability Availability,
    string Reason);

public sealed record PhysicalConclusionEvidence(
    PhysicalConclusionCode Code,
    EvidenceAvailability Availability,
    string Summary,
    string Basis);

public sealed record WorkpieceEvidence(
    EvidenceValue<string> PlateNo,
    EvidenceValue<string> Sequence,
    EvidenceValue<double> Diameter,
    EvidenceValue<double> Length,
    EvidenceValue<string> Source,
    EvidenceValue<string> Target,
    EvidenceValue<string> SoftwareOwner,
    EvidenceValue<string> LastConfirmedLocation,
    string EvidenceSource);

public sealed record PositionEvidence(
    EvidenceValue<int> DisplayX,
    EvidenceValue<int> DisplayY,
    EvidenceValue<int> DisplayZ,
    EvidenceValue<int> AbsX,
    EvidenceValue<int> AbsY,
    EvidenceValue<int> TargetX,
    EvidenceValue<int> TargetY,
    EvidenceValue<int> TargetAbsX,
    EvidenceValue<int> TargetAbsY,
    EvidenceValue<int> DeltaX,
    EvidenceValue<int> DeltaY,
    EvidenceValue<int> Tolerance,
    EvidenceValue<int> MaximumCorrection,
    EvidenceValue<int> StableSampleCount,
    EvidenceValue<int> FineTuneAttemptCount,
    EvidenceValue<DateTime> CapturedAtUtc)
{
    public static PositionEvidence Unavailable(string reason) =>
        new(
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<int>.Unavailable(reason),
            EvidenceValue<DateTime>.Unavailable(reason));
}

public sealed record MotionAndMagnetEvidence(
    DeviceCommandEvidence ZDownCommand,
    EvidenceValue<bool> ZMayStillBeLow,
    DeviceCommandEvidence MagnetOnCommand,
    DeviceCommandEvidence MagnetOffCommand,
    EvidenceValue<int> X11LastValue,
    EvidenceValue<bool> X11ReadValid,
    EvidenceValue<int> X11Attempts,
    EvidenceValue<DateTime> X11ReadAtUtc);

public sealed record PhysicalCommitmentEvidence(
    string Name,
    EvidenceValue<bool> State,
    string Detail);

public sealed record BusinessStateEvidence
{
    public BusinessStateEvidence(
        EvidenceValue<string> lastSuccessfulCheckpoint,
        EvidenceValue<string> stateBefore,
        EvidenceValue<string> stateAfter,
        EvidenceValue<string> cacheBefore,
        EvidenceValue<string> cacheAfter,
        EvidenceValue<string> ownerBefore,
        EvidenceValue<string> ownerAfter,
        EvidenceValue<bool> holdingWorkpiece,
        EvidenceValue<bool> placed,
        EvidenceValue<bool> cacheNotified)
        : this(
            EvidenceAvailability.Confirmed,
            "调用点已提供业务状态字段",
            lastSuccessfulCheckpoint,
            stateBefore,
            stateAfter,
            cacheBefore,
            cacheAfter,
            ownerBefore,
            ownerAfter,
            holdingWorkpiece,
            placed,
            cacheNotified,
            Array.Empty<PhysicalCommitmentEvidence>())
    {
    }

    public BusinessStateEvidence(
        EvidenceAvailability availability,
        string reason,
        EvidenceValue<string> lastSuccessfulCheckpoint,
        EvidenceValue<string> stateBefore,
        EvidenceValue<string> stateAfter,
        EvidenceValue<string> cacheBefore,
        EvidenceValue<string> cacheAfter,
        EvidenceValue<string> ownerBefore,
        EvidenceValue<string> ownerAfter,
        EvidenceValue<bool> holdingWorkpiece,
        EvidenceValue<bool> placed,
        EvidenceValue<bool> cacheNotified,
        IEnumerable<PhysicalCommitmentEvidence>? physicalCommitments)
    {
        Availability = availability;
        Reason = reason;
        LastSuccessfulCheckpoint = lastSuccessfulCheckpoint;
        StateBefore = stateBefore;
        StateAfter = stateAfter;
        CacheBefore = cacheBefore;
        CacheAfter = cacheAfter;
        OwnerBefore = ownerBefore;
        OwnerAfter = ownerAfter;
        HoldingWorkpiece = holdingWorkpiece;
        Placed = placed;
        CacheNotified = cacheNotified;
        PhysicalCommitments = EvidenceCollection.Freeze(physicalCommitments);
    }

    public EvidenceAvailability Availability { get; }

    public string Reason { get; }

    public EvidenceValue<string> LastSuccessfulCheckpoint { get; }

    public EvidenceValue<string> StateBefore { get; }

    public EvidenceValue<string> StateAfter { get; }

    public EvidenceValue<string> CacheBefore { get; }

    public EvidenceValue<string> CacheAfter { get; }

    public EvidenceValue<string> OwnerBefore { get; }

    public EvidenceValue<string> OwnerAfter { get; }

    public EvidenceValue<bool> HoldingWorkpiece { get; }

    public EvidenceValue<bool> Placed { get; }

    public EvidenceValue<bool> CacheNotified { get; }

    public IReadOnlyList<PhysicalCommitmentEvidence> PhysicalCommitments { get; }
}

public sealed record LockItemEvidence(
    string Name,
    EvidenceValue<bool> HeldAtFailure,
    EvidenceValue<bool> ReleasedAfterward,
    string ManualConfirmation);

public sealed record LockEvidence
{
    public LockEvidence(IEnumerable<LockItemEvidence>? items)
        : this(EvidenceAvailability.Confirmed, "已取得锁证据", items)
    {
    }

    public LockEvidence(
        EvidenceAvailability availability,
        string reason,
        IEnumerable<LockItemEvidence>? items)
    {
        Availability = availability;
        Reason = reason;
        Items = EvidenceCollection.Freeze(items);
    }

    public EvidenceAvailability Availability { get; }

    public string Reason { get; }

    public IReadOnlyList<LockItemEvidence> Items { get; }
}

public sealed record RecoveryStepEvidence(
    string Step,
    RecoveryStepState State,
    string Detail);

public sealed record RecoveryEvidence
{
    public RecoveryEvidence(
        EvidenceValue<bool> attempted,
        EvidenceValue<bool> completed,
        IEnumerable<RecoveryStepEvidence>? steps,
        IEnumerable<string>? postRecoveryVerification,
        string resultingBusinessBehavior)
        : this(
            EvidenceAvailability.Confirmed,
            "已取得恢复处理证据",
            attempted,
            completed,
            steps,
            postRecoveryVerification,
            resultingBusinessBehavior)
    {
    }

    public RecoveryEvidence(
        EvidenceAvailability availability,
        string reason,
        EvidenceValue<bool> attempted,
        EvidenceValue<bool> completed,
        IEnumerable<RecoveryStepEvidence>? steps,
        IEnumerable<string>? postRecoveryVerification,
        string resultingBusinessBehavior)
    {
        Availability = availability;
        Reason = reason;
        Attempted = attempted;
        Completed = completed;
        Steps = EvidenceCollection.Freeze(steps);
        PostRecoveryVerification = EvidenceCollection.Freeze(postRecoveryVerification);
        ResultingBusinessBehavior = resultingBusinessBehavior;
    }

    public EvidenceAvailability Availability { get; }

    public string Reason { get; }

    public EvidenceValue<bool> Attempted { get; }

    public EvidenceValue<bool> Completed { get; }

    public IReadOnlyList<RecoveryStepEvidence> Steps { get; }

    public IReadOnlyList<string> PostRecoveryVerification { get; }

    public string ResultingBusinessBehavior { get; }
}

public sealed record OperatorGuidance
{
    public OperatorGuidance(
        IEnumerable<string>? requiredActions,
        IEnumerable<string>? forbiddenActions,
        string continueCondition)
        : this(
            EvidenceAvailability.Confirmed,
            "已提供操作指引",
            requiredActions,
            forbiddenActions,
            continueCondition)
    {
    }

    public OperatorGuidance(
        EvidenceAvailability availability,
        string reason,
        IEnumerable<string>? requiredActions,
        IEnumerable<string>? forbiddenActions,
        string continueCondition)
    {
        Availability = availability;
        Reason = reason;
        RequiredActions = EvidenceCollection.Freeze(requiredActions);
        ForbiddenActions = EvidenceCollection.Freeze(forbiddenActions);
        ContinueCondition = continueCondition;
    }

    public EvidenceAvailability Availability { get; }

    public string Reason { get; }

    public IReadOnlyList<string> RequiredActions { get; }

    public IReadOnlyList<string> ForbiddenActions { get; }

    public string ContinueCondition { get; }
}

public sealed record ExceptionEvidence(
    string Type,
    string Message,
    string InnerExceptionChain,
    string BusinessContext,
    string StackTrace)
{
    public EvidenceAvailability Availability { get; init; } = EvidenceAvailability.Confirmed;

    public string Reason { get; init; } = string.Empty;
}

public sealed record OperationalEvidence(
    WorkpieceEvidence Workpiece,
    PositionEvidence Position,
    MotionAndMagnetEvidence MotionAndMagnet,
    BusinessStateEvidence BusinessState,
    LockEvidence Locks,
    RecoveryEvidence Recovery,
    OperatorGuidance Guidance,
    ExceptionEvidence Exception)
{
    public static OperationalEvidence Unavailable(string reason) =>
        OperationalEvidenceDefaults.Unavailable(reason);
}

internal static class OperationalEvidenceDefaults
{
    public static OperationalEvidence Unavailable(string reason)
    {
        DeviceCommandEvidence unavailableCommand = new(
            DeviceCommandState.Unavailable,
            EvidenceAvailability.Unavailable,
            reason);

        return new OperationalEvidence(
            new WorkpieceEvidence(
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<double>.Unavailable(reason),
                EvidenceValue<double>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                reason),
            PositionEvidence.Unavailable(reason),
            new MotionAndMagnetEvidence(
                unavailableCommand,
                EvidenceValue<bool>.Unavailable(reason),
                unavailableCommand,
                unavailableCommand,
                EvidenceValue<int>.Unavailable(reason),
                EvidenceValue<bool>.Unavailable(reason),
                EvidenceValue<int>.Unavailable(reason),
                EvidenceValue<DateTime>.Unavailable(reason)),
            new BusinessStateEvidence(
                EvidenceAvailability.Unavailable,
                reason,
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<string>.Unavailable(reason),
                EvidenceValue<bool>.Unavailable(reason),
                EvidenceValue<bool>.Unavailable(reason),
                EvidenceValue<bool>.Unavailable(reason),
                new PhysicalCommitmentEvidence[0]),
            new LockEvidence(
                EvidenceAvailability.Unavailable,
                reason,
                new LockItemEvidence[0]),
            new RecoveryEvidence(
                EvidenceAvailability.Unavailable,
                reason,
                EvidenceValue<bool>.Unavailable(reason),
                EvidenceValue<bool>.Unavailable(reason),
                new RecoveryStepEvidence[0],
                new string[0],
                reason),
            new OperatorGuidance(
                EvidenceAvailability.Unavailable,
                reason,
                new string[0],
                new string[0],
                reason),
            new ExceptionEvidence(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty)
            {
                Availability = EvidenceAvailability.Unavailable,
                Reason = reason
            });
    }
}

internal static class EvidenceCollection
{
    public static IReadOnlyList<T> Freeze<T>(IEnumerable<T>? source)
    {
        T[] values = source?.ToArray() ?? new T[0];
        var copy = new T[values.Length];
        Array.Copy(values, copy, values.Length);
        return new ReadOnlyCollection<T>(copy);
    }
}
