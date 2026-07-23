namespace RemotePointer.Contracts.Validation;

public sealed record PointerValidationOptions(
    int MaximumTimeToLiveMilliseconds = 10_000,
    int AllowedClockSkewMilliseconds = 5_000);
