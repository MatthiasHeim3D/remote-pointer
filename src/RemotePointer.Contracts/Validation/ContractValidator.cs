using RemotePointer.Contracts.Messages;

namespace RemotePointer.Contracts.Validation;

public static class ContractValidator
{
    public static ValidationResult Validate(
        PointerEventMessage? message,
        DateTimeOffset now,
        PointerValidationOptions? options = null)
    {
        if (message is null)
        {
            return RequiredMessage(nameof(message));
        }

        options ??= new PointerValidationOptions();
        var errors = new List<ValidationError>();

        AddRequired(errors, message.EventId != Guid.Empty, nameof(message.EventId));
        AddRequired(errors, !string.IsNullOrWhiteSpace(message.SessionId), nameof(message.SessionId));
        AddRange(errors, message.SequenceNumber >= 0, nameof(message.SequenceNumber));
        AddNormalizedCoordinate(errors, message.NormalizedX, nameof(message.NormalizedX));
        AddNormalizedCoordinate(errors, message.NormalizedY, nameof(message.NormalizedY));
        AddValue(errors, Enum.IsDefined(message.Kind), nameof(message.Kind));
        AddRange(errors, message.SentAtUnixMilliseconds >= 0, nameof(message.SentAtUnixMilliseconds));
        AddRange(
            errors,
            message.TimeToLiveMilliseconds > 0
                && message.TimeToLiveMilliseconds <= options.MaximumTimeToLiveMilliseconds,
            nameof(message.TimeToLiveMilliseconds));

        if (message.SentAtUnixMilliseconds >= 0 && message.TimeToLiveMilliseconds > 0)
        {
            var age = now.ToUnixTimeMilliseconds() - message.SentAtUnixMilliseconds;
            if (age > message.TimeToLiveMilliseconds)
            {
                errors.Add(new ValidationError(
                    ValidationErrors.Expired,
                    "The pointer event is older than its time to live."));
            }

            if (age < -options.AllowedClockSkewMilliseconds)
            {
                errors.Add(new ValidationError(
                    ValidationErrors.FutureTimestamp,
                    "The pointer event timestamp is too far in the future."));
            }
        }

        return ValidationResult.Failure(errors);
    }

    public static ValidationResult Validate(DisplayDescriptor? display)
    {
        if (display is null)
        {
            return RequiredMessage(nameof(display));
        }

        var errors = new List<ValidationError>();
        AddRequired(errors, !string.IsNullOrWhiteSpace(display.DisplayId), nameof(display.DisplayId));
        AddRequired(errors, !string.IsNullOrWhiteSpace(display.DisplayName), nameof(display.DisplayName));
        AddRange(errors, display.WidthPixels > 0, nameof(display.WidthPixels));
        AddRange(errors, display.HeightPixels > 0, nameof(display.HeightPixels));
        AddRange(
            errors,
            double.IsFinite(display.ScaleFactor) && display.ScaleFactor > 0d,
            nameof(display.ScaleFactor));
        AddValue(
            errors,
            display.RotationDegrees is 0 or 90 or 180 or 270,
            nameof(display.RotationDegrees));

        return ValidationResult.Failure(errors);
    }

    public static ValidationResult Validate(JoinRequest? request)
    {
        if (request is null)
        {
            return RequiredMessage(nameof(request));
        }

        var errors = new List<ValidationError>();
        if (!PairingCodeValidator.IsValid(request.PairingCode))
        {
            errors.Add(new ValidationError(
                ValidationErrors.InvalidValue,
                "PairingCode is not in the expected format."));
        }

        AddValue(errors, Enum.IsDefined(request.Role), nameof(request.Role));
        AddRequired(
            errors,
            !string.IsNullOrWhiteSpace(request.ClientInstanceId),
            nameof(request.ClientInstanceId));
        AddRequired(errors, !string.IsNullOrWhiteSpace(request.ClientVersion), nameof(request.ClientVersion));

        return ValidationResult.Failure(errors);
    }

    public static ValidationResult Validate(PointerAcknowledgement? acknowledgement)
    {
        if (acknowledgement is null)
        {
            return RequiredMessage(nameof(acknowledgement));
        }

        var errors = new List<ValidationError>();
        AddRequired(errors, acknowledgement.EventId != Guid.Empty, nameof(acknowledgement.EventId));
        AddRange(
            errors,
            acknowledgement.DisplayedAtUnixMilliseconds >= 0,
            nameof(acknowledgement.DisplayedAtUnixMilliseconds));

        return ValidationResult.Failure(errors);
    }

    public static ValidationResult Validate(SessionStateMessage? state)
    {
        if (state is null)
        {
            return RequiredMessage(nameof(state));
        }

        var errors = new List<ValidationError>();
        AddRequired(errors, !string.IsNullOrWhiteSpace(state.SessionId), nameof(state.SessionId));
        AddRange(errors, state.ExpiresAt != default, nameof(state.ExpiresAt));

        if (state.ReceiverDisplay is not null)
        {
            errors.AddRange(Validate(state.ReceiverDisplay).Errors);
        }

        return ValidationResult.Failure(errors);
    }

    private static ValidationResult RequiredMessage(string name) =>
        ValidationResult.Failure(
            [new ValidationError(ValidationErrors.Required, $"{name} is required.")]);

    private static void AddRequired(
        ICollection<ValidationError> errors,
        bool condition,
        string fieldName)
    {
        if (!condition)
        {
            errors.Add(new ValidationError(ValidationErrors.Required, $"{fieldName} is required."));
        }
    }

    private static void AddRange(
        ICollection<ValidationError> errors,
        bool condition,
        string fieldName)
    {
        if (!condition)
        {
            errors.Add(new ValidationError(
                ValidationErrors.OutOfRange,
                $"{fieldName} is outside its permitted range."));
        }
    }

    private static void AddValue(
        ICollection<ValidationError> errors,
        bool condition,
        string fieldName)
    {
        if (!condition)
        {
            errors.Add(new ValidationError(
                ValidationErrors.InvalidValue,
                $"{fieldName} is invalid."));
        }
    }

    private static void AddNormalizedCoordinate(
        ICollection<ValidationError> errors,
        double coordinate,
        string fieldName) =>
        AddRange(
            errors,
            double.IsFinite(coordinate) && coordinate is >= 0d and <= 1d,
            fieldName);
}
