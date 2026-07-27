namespace RemoteAnnotate.Contracts.Validation;

public sealed class ValidationResult
{
    private static readonly ValidationResult SuccessfulResult = new([]);

    private ValidationResult(IReadOnlyList<ValidationError> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<ValidationError> Errors { get; }

    public static ValidationResult Success() => SuccessfulResult;

    public static ValidationResult Failure(IEnumerable<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var errorList = errors.ToArray();
        return errorList.Length == 0 ? SuccessfulResult : new ValidationResult(errorList);
    }
}
