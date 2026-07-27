using RemotePointer.Contracts.Messages;

namespace RemotePointer.Contracts.Validation;

public static class ContractValidator
{
    public const int MaximumPointerTextLength = 256;
    public const int MaximumPathPointsPerEvent = 128;
    public const int MaximumProfilePictureBytes = 20 * 1_024;
    public const int MaximumConnectedAnnotators = 64;

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
        AddRequired(errors, IsRequiredIdentifier(message.SessionId), nameof(message.SessionId));
        AddRange(errors, message.SequenceNumber >= 0, nameof(message.SequenceNumber));
        AddNormalizedCoordinate(errors, message.NormalizedX, nameof(message.NormalizedX));
        AddNormalizedCoordinate(errors, message.NormalizedY, nameof(message.NormalizedY));
        AddValue(errors, Enum.IsDefined(message.Kind), nameof(message.Kind));
        AddValue(
            errors,
            message.Color is null || AnnotationColors.IsValid(message.Color),
            nameof(message.Color));
        AddPointerPayloadErrors(errors, message);
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

    private static void AddPointerPayloadErrors(
        ICollection<ValidationError> errors,
        PointerEventMessage message)
    {
        var isGesture = message.Kind is
            PointerKind.PathStart or PointerKind.PathUpdate or PointerKind.PathEnd or
            PointerKind.LineStart or PointerKind.LineUpdate or PointerKind.LineEnd or
            PointerKind.RectangleStart or PointerKind.RectangleUpdate or PointerKind.RectangleEnd or
            PointerKind.CircleStart or PointerKind.CircleUpdate or PointerKind.CircleEnd;
        if (isGesture && (!message.GestureId.HasValue || message.GestureId.Value == Guid.Empty))
        {
            AddRequired(errors, condition: false, nameof(message.GestureId));
        }

        if (!isGesture && message.GestureId is not null)
        {
            AddValue(errors, condition: false, nameof(message.GestureId));
        }

        if (message.Kind == PointerKind.Text)
        {
            AddRequired(
                errors,
                !string.IsNullOrWhiteSpace(message.Text),
                nameof(message.Text));
            AddRange(
                errors,
                message.Text is null || message.Text.Length <= MaximumPointerTextLength,
                nameof(message.Text));
        }
        else if (message.Text is not null)
        {
            AddValue(errors, condition: false, nameof(message.Text));
        }

        var supportsPathPoints = message.Kind is PointerKind.PathUpdate or PointerKind.PathEnd;
        if (!supportsPathPoints && message.PathPoints is not null)
        {
            AddValue(errors, condition: false, nameof(message.PathPoints));
            return;
        }

        if (message.PathPoints is null)
        {
            return;
        }

        AddRange(
            errors,
            message.PathPoints.Length <= MaximumPathPointsPerEvent,
            nameof(message.PathPoints));
        foreach (var point in message.PathPoints.Take(MaximumPathPointsPerEvent + 1))
        {
            AddNormalizedCoordinate(errors, point.X, nameof(message.PathPoints));
            AddNormalizedCoordinate(errors, point.Y, nameof(message.PathPoints));
        }
    }

    public static ValidationResult Validate(DisplayDescriptor? display)
    {
        if (display is null)
        {
            return RequiredMessage(nameof(display));
        }

        var errors = new List<ValidationError>();
        AddRequired(errors, IsRequiredIdentifier(display.DisplayId), nameof(display.DisplayId));
        AddRequired(
            errors,
            !string.IsNullOrWhiteSpace(display.DisplayName) && display.DisplayName.Length <= 256,
            nameof(display.DisplayName));
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

    public static ValidationResult Validate(ClientProfile? profile)
    {
        if (profile is null)
        {
            return RequiredMessage(nameof(profile));
        }

        var errors = new List<ValidationError>();
        AddRange(
            errors,
            profile.PicturePng is null
                || profile.PicturePng.Length <= MaximumProfilePictureBytes,
            nameof(profile.PicturePng));
        if (profile.PicturePng is { Length: > 0 } picture)
        {
            AddValue(
                errors,
                picture.Length >= 8
                    && picture[0] == 0x89
                    && picture[1] == 0x50
                    && picture[2] == 0x4E
                    && picture[3] == 0x47
                    && picture[4] == 0x0D
                    && picture[5] == 0x0A
                    && picture[6] == 0x1A
                    && picture[7] == 0x0A,
                nameof(profile.PicturePng));
        }

        return ValidationResult.Failure(errors);
    }

    public static ValidationResult Validate(DirectJoinRequest? request)
    {
        if (request is null)
        {
            return RequiredMessage(nameof(request));
        }

        var errors = new List<ValidationError>();
        AddRequired(errors, IsRequiredIdentifier(request.SessionId), nameof(request.SessionId));
        AddRequired(
            errors,
            IsRequiredIdentifier(request.ClientInstanceId),
            nameof(request.ClientInstanceId));
        AddRequired(
            errors,
            !string.IsNullOrWhiteSpace(request.ClientVersion) && request.ClientVersion.Length <= 64,
            nameof(request.ClientVersion));
        if (request.Profile is not null)
        {
            errors.AddRange(Validate(request.Profile).Errors);
        }

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
        AddRequired(errors, IsRequiredIdentifier(state.SessionId), nameof(state.SessionId));
        AddRange(errors, state.ExpiresAt != default, nameof(state.ExpiresAt));
        if (state.HostClientInstanceId is not null)
        {
            AddRequired(
                errors,
                IsRequiredIdentifier(state.HostClientInstanceId),
                nameof(state.HostClientInstanceId));
        }

        if (state.HostDisplayName is not null)
        {
            AddRequired(
                errors,
                IsRequiredIdentifier(state.HostDisplayName),
                nameof(state.HostDisplayName));
        }

        if (state.HostProfilePicturePng is not null)
        {
            errors.AddRange(Validate(new ClientProfile(state.HostProfilePicturePng)).Errors);
        }

        if (state.HostDisplay is not null)
        {
            errors.AddRange(Validate(state.HostDisplay).Errors);
        }

        if (state.ConnectedAnnotators is not null)
        {
            AddRange(
                errors,
                state.ConnectedAnnotators.Length <= MaximumConnectedAnnotators,
                nameof(state.ConnectedAnnotators));
            foreach (var annotator in state.ConnectedAnnotators.Take(
                         MaximumConnectedAnnotators + 1))
            {
                AddRequired(
                    errors,
                    !string.IsNullOrWhiteSpace(annotator.DisplayName)
                        && annotator.DisplayName.Length <= 128,
                    nameof(annotator.DisplayName));
            }
        }

        return ValidationResult.Failure(errors);
    }

    public static ValidationResult Validate(SessionResumeRequest? request)
    {
        if (request is null)
        {
            return RequiredMessage(nameof(request));
        }

        var errors = new List<ValidationError>();
        AddRequired(errors, IsRequiredIdentifier(request.SessionId), nameof(request.SessionId));
        AddValue(errors, Enum.IsDefined(request.Role), nameof(request.Role));
        AddRequired(
            errors,
            IsRequiredIdentifier(request.ClientInstanceId),
            nameof(request.ClientInstanceId));
        AddRequired(errors, IsRequiredSecret(request.SessionToken), nameof(request.SessionToken));
        AddRequired(errors, IsRequiredSecret(request.ReconnectToken), nameof(request.ReconnectToken));
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

    private static bool IsRequiredIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static bool IsRequiredSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 32 and <= 256;
}
