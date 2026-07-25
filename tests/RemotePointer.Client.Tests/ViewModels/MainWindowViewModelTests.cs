using RemotePointer.Client.Native;
using RemotePointer.Client.Configuration;
using RemotePointer.Client.Services;
using RemotePointer.Client.Tests.Fakes;
using RemotePointer.Client.ViewModels;
using RemotePointer.Contracts.Coordinates;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Theory]
    [InlineData("Ada", "A")]
    [InlineData("ada lovelace", "AL")]
    [InlineData("\U0001F600 Grin", "\U0001F600G")]
    [InlineData("\U0001F600", "\U0001F600")]
    public void FirstCharacter_KeepsCharactersOutsideTheBasicPlaneIntact(
        string userName,
        string expected)
    {
        var parts = userName.Split(
            ' ',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length == 1
            ? MainWindowViewModel.FirstCharacter(parts[0])
            : $"{MainWindowViewModel.FirstCharacter(parts[0])}{MainWindowViewModel.FirstCharacter(parts[^1])}";

        Assert.Equal(expected, initials);
    }

    [Fact]
    public async Task NoServerPassword_WarnsAndSaysWhyTheListIsEmpty()
    {
        using var overlay = new FakeOverlayService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(ServerPasswordRequired: true),
        };
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            receiverRelayClient: relay,
            clientSettings: new ClientSettings());

        await viewModel.InitializeAsync();

        Assert.False(viewModel.HasServerPassword);
        Assert.True(viewModel.ShowServerPasswordWarning);
        Assert.Contains(
            "requires a server password",
            viewModel.ServerPasswordWarning,
            StringComparison.Ordinal);
        Assert.Equal(
            "Set a server password in Settings to see other clients.",
            viewModel.EmptyClientListMessage);
    }

    [Fact]
    public async Task OpenRelayWithoutPassword_WarnsThatEveryoneCanSeeTheProfile()
    {
        using var overlay = new FakeOverlayService();
        var relay = new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(ServerPasswordRequired: false),
        };
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            receiverRelayClient: relay,
            clientSettings: new ClientSettings());

        await viewModel.InitializeAsync();

        Assert.False(viewModel.ServerPasswordRequired);
        Assert.True(viewModel.ShowServerPasswordWarning);
        Assert.Contains(
            "visible to everyone",
            viewModel.ServerPasswordWarning,
            StringComparison.Ordinal);
        Assert.Equal("No available clients", viewModel.EmptyClientListMessage);
    }

    [Fact]
    public async Task StoredServerPassword_SuppressesTheWarningAndAllowsRemoval()
    {
        using var overlay = new FakeOverlayService();
        var relay = new FakeRelayClient();
        var settings = new ClientSettings();
        settings.Server.PasswordKey = "stored-group-key";
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            receiverRelayClient: relay,
            clientSettings: settings);

        Assert.True(viewModel.HasServerPassword);
        Assert.False(viewModel.ShowServerPasswordWarning);
        Assert.True(viewModel.ClearServerPasswordCommand.CanExecute(null));

        await viewModel.ClearServerPasswordAsync();

        Assert.False(viewModel.HasServerPassword);
        Assert.True(viewModel.ShowServerPasswordWarning);
        Assert.Null(settings.Server.PasswordKey);
        Assert.Null(relay.ServerPasswordKey);
        Assert.Equal(1, relay.ServerPasswordKeyUpdateCount);
        Assert.False(viewModel.HasServerPasswordCheckCode);
        Assert.Empty(viewModel.ServerPasswordCheckCode);
    }

    [Fact]
    public void WithoutAStoredServerPassword_TheBoxIsOfferedWithoutAChangeStep()
    {
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: new ClientSettings());

        Assert.True(viewModel.ShowServerPasswordEditor);
        Assert.False(viewModel.ShowServerPasswordSetState);
        Assert.False(viewModel.IsChangingServerPassword);
        Assert.False(viewModel.ChangeServerPasswordCommand.CanExecute(null));
        Assert.False(viewModel.ClearServerPasswordCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingAStoredServerPassword_OffersTheBoxUntilItIsAppliedOrCancelled()
    {
        using var overlay = new FakeOverlayService();
        var relay = new FakeRelayClient();
        var settings = new ClientSettings();
        var storedKey = ServerPasswordKey.Derive("first team password");
        settings.Server.PasswordKey = storedKey;
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            receiverRelayClient: relay,
            clientSettings: settings);

        Assert.True(viewModel.ShowServerPasswordSetState);
        Assert.False(viewModel.ShowServerPasswordEditor);

        viewModel.ChangeServerPasswordCommand.Execute(null);

        Assert.True(viewModel.IsChangingServerPassword);
        Assert.True(viewModel.ShowServerPasswordEditor);
        Assert.False(viewModel.ShowServerPasswordSetState);
        Assert.False(viewModel.ApplyServerPasswordCommand.CanExecute(null));

        viewModel.ServerPasswordInput = "short";

        Assert.False(viewModel.ApplyServerPasswordCommand.CanExecute(null));

        viewModel.CancelServerPasswordChangeCommand.Execute(null);

        Assert.False(viewModel.IsChangingServerPassword);
        Assert.True(viewModel.ShowServerPasswordSetState);
        Assert.Empty(viewModel.ServerPasswordInput);
        Assert.Equal(storedKey, settings.Server.PasswordKey);

        viewModel.ChangeServerPasswordCommand.Execute(null);
        viewModel.ServerPasswordInput = "second team password";

        Assert.True(viewModel.ApplyServerPasswordCommand.CanExecute(null));
        await viewModel.ApplyServerPasswordDraftAsync();

        Assert.False(viewModel.IsChangingServerPassword);
        Assert.True(viewModel.ShowServerPasswordSetState);
        Assert.Empty(viewModel.ServerPasswordInput);
        Assert.Equal(ServerPasswordKey.Derive("second team password"), relay.ServerPasswordKey);
        Assert.Equal(relay.ServerPasswordKey, settings.Server.PasswordKey);
    }

    [Fact]
    public async Task ServerPasswordCheckCode_IdentifiesTheCurrentPasswordAndFollowsAChange()
    {
        using var overlay = new FakeOverlayService();
        var relay = new FakeRelayClient();
        var settings = new ClientSettings();
        settings.Server.PasswordKey = ServerPasswordKey.Derive("first team password");
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            receiverRelayClient: relay,
            clientSettings: settings);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var first = viewModel.ServerPasswordCheckCode;
        Assert.True(viewModel.HasServerPasswordCheckCode);
        Assert.Equal(
            ServerPasswordKey.DeriveCheckCode(ServerPasswordKey.Derive("first team password")),
            first);

        // Replacing one password with another leaves HasServerPassword true throughout, so the
        // code has to be raised on its own or the settings screen keeps showing the old one.
        viewModel.ServerPasswordInput = "second team password";
        await viewModel.CloseSettingsAsync();

        Assert.NotEqual(first, viewModel.ServerPasswordCheckCode);
        Assert.Equal(
            ServerPasswordKey.DeriveCheckCode(ServerPasswordKey.Derive("second team password")),
            viewModel.ServerPasswordCheckCode);
        Assert.Contains(nameof(MainWindowViewModel.ServerPasswordCheckCode), raised);
    }

    [Fact]
    public async Task DrawingOpacity_IsClampedAndPersistedWhenSettingsClose()
    {
        using var testSettings = new TemporaryClientSettings("https://relay.example.test");
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: testSettings.Settings);

        Assert.Equal(
            PointerSettings.DefaultDrawingOpacityPercent,
            viewModel.DrawingOpacityPercent);

        viewModel.DrawingOpacityPercent = 250;
        Assert.Equal(PointerSettings.MaximumDrawingOpacityPercent, viewModel.DrawingOpacityPercent);

        viewModel.DrawingOpacityPercent = 30;
        Assert.Equal("30%", viewModel.DrawingOpacityLabel);
        await viewModel.CloseSettingsAsync();

        Assert.Equal(30, testSettings.Settings.Pointer.DrawingOpacityPercent);
    }

    [Fact]
    public void MissingServer_ShowsMainScreenWithSetupGuidance()
    {
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: new ClientSettings());

        Assert.True(viewModel.IsServerConfigurationMissing);
        Assert.False(viewModel.IsSettingsOpen);
        Assert.Equal("Set the server address in Settings.", viewModel.ServerConnectionGuidance);
        Assert.Equal(viewModel.ServerConnectionGuidance, viewModel.EmptyClientListMessage);
    }

    [Fact]
    public void ServerAddressInput_StripsPastedHttpsPrefixAndRejectsHttp()
    {
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay);

        viewModel.ServerAddressInput = "https://relay.example.test/path/";

        Assert.Equal("relay.example.test/path/", viewModel.ServerAddressInput);
        Assert.Equal("https://relay.example.test/path/", viewModel.ServerAddress);
        Assert.Empty(viewModel.ServerAddressValidationMessage);

        viewModel.ServerAddressInput = "http://relay.example.test";

        Assert.Contains("HTTPS", viewModel.ServerAddressValidationMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidServerAddress_DoesNotPreventClosingSettings()
    {
        using var testSettings = new TemporaryClientSettings(string.Empty);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: testSettings.Settings);
        viewModel.ServerAddressInput = "http://relay.example.test";

        await viewModel.CloseSettingsAsync();

        Assert.False(viewModel.IsSettingsOpen);
        Assert.Empty(viewModel.ServerAddressInput);
    }

    [Fact]
    public async Task TestServerConnection_SuccessSavesAddressAndShowsCheckmark()
    {
        using var testSettings = new TemporaryClientSettings(string.Empty);
        using var overlay = new FakeOverlayService();
        var tester = new FakeServerConnectionTester(
            new ServerConnectionTestResult(true, "Connection successful."));
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: testSettings.Settings,
            serverConnectionTester: tester);
        viewModel.ToggleSettingsCommand.Execute(null);
        viewModel.ServerAddressInput = "relay.example.test";

        Assert.True(viewModel.TestServerConnectionCommand.CanExecute(null));
        await viewModel.TestServerConnectionAsync();

        Assert.Equal(["https://relay.example.test"], tester.TestedAddresses);
        Assert.Equal("https://relay.example.test", testSettings.Settings.Server.BaseUrl);
        Assert.True(viewModel.IsServerAddressVerified);
        Assert.True(viewModel.IsSettingsOpen);

        // The saved address stays testable so a reachability check never needs a fake edit first.
        Assert.True(viewModel.TestServerConnectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestServerConnection_AdvertisedVersionIsShownAndClearedOnEdit()
    {
        using var testSettings = new TemporaryClientSettings(string.Empty);
        using var overlay = new FakeOverlayService();
        var tester = new FakeServerConnectionTester(
            new ServerConnectionTestResult(true, "Connection successful.", "1.2.3"));
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: testSettings.Settings,
            serverConnectionTester: tester);
        viewModel.ToggleSettingsCommand.Execute(null);
        viewModel.ServerAddressInput = "relay.example.test";

        await viewModel.TestServerConnectionAsync();

        Assert.True(viewModel.HasServerVersion);
        Assert.Equal("Server version 1.2.3", viewModel.ServerVersionLabel);

        viewModel.ServerAddressInput = "other.example.test";

        Assert.False(viewModel.HasServerVersion);
        Assert.Empty(viewModel.ServerVersionLabel);
    }

    [Fact]
    public async Task TestServerConnection_UnreachableServerDropsStaleVersion()
    {
        using var testSettings = new TemporaryClientSettings(string.Empty);
        using var overlay = new FakeOverlayService();
        var tester = new FakeServerConnectionTester(
            new ServerConnectionTestResult(true, "Connection successful.", "1.2.3"));
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: testSettings.Settings,
            serverConnectionTester: tester);
        viewModel.ToggleSettingsCommand.Execute(null);
        viewModel.ServerAddressInput = "relay.example.test";
        await viewModel.TestServerConnectionAsync();

        tester.Result = new ServerConnectionTestResult(
            false,
            "The server could not be reached.");
        await viewModel.TestServerConnectionAsync();

        Assert.False(viewModel.HasServerVersion);
        Assert.Empty(viewModel.ServerVersionLabel);
    }

    [Fact]
    public async Task ClosingSettings_UntestedReachableAddressTestsAndSavesIt()
    {
        using var testSettings = new TemporaryClientSettings("https://old.example.test");
        using var overlay = new FakeOverlayService();
        var tester = new FakeServerConnectionTester(
            new ServerConnectionTestResult(true, "Connection successful."));
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: testSettings.Settings,
            serverConnectionTester: tester);
        var relayReinitializationRequested = false;
        viewModel.RelayReinitializationRequested += (_, _) =>
            relayReinitializationRequested = true;
        viewModel.ToggleSettingsCommand.Execute(null);
        viewModel.ServerAddressInput = "new.example.test";

        await viewModel.CloseSettingsAsync();

        Assert.Equal(["https://new.example.test"], tester.TestedAddresses);
        Assert.Equal("https://new.example.test", testSettings.Settings.Server.BaseUrl);
        Assert.False(viewModel.IsSettingsOpen);
        Assert.True(relayReinitializationRequested);
    }

    [Fact]
    public async Task ClosingSettings_UnreachableAddressRestoresConfiguredAddress()
    {
        using var testSettings = new TemporaryClientSettings("https://old.example.test");
        using var overlay = new FakeOverlayService();
        var tester = new FakeServerConnectionTester(
            new ServerConnectionTestResult(false, "The server could not be reached."));
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            clientSettings: testSettings.Settings,
            serverConnectionTester: tester);
        viewModel.ServerAddressInput = "unreachable.example.test";

        await viewModel.CloseSettingsAsync();

        Assert.Equal(["https://unreachable.example.test"], tester.TestedAddresses);
        Assert.Equal("https://old.example.test", testSettings.Settings.Server.BaseUrl);
        Assert.Equal("old.example.test", viewModel.ServerAddressInput);
        Assert.False(viewModel.IsSettingsOpen);
    }

    [Fact]
    public void DisconnectedServer_ShowsReachabilityGuidance()
    {
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        relay.RaiseConnectionStatus(RelayConnectionStatus.Disconnected, "Connection failed.");
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([CreateMonitor("DISPLAY1", isPrimary: true)]),
            overlay,
            receiverRelayClient: relay);

        Assert.False(viewModel.IsServerAvailable);
        Assert.Equal(
            "Server not reachable. Check the server address in Settings.",
            viewModel.ServerConnectionGuidance);
    }

    [Fact]
    public async Task Initialize_RestoresSavedAvailableState()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        var settings = new ClientSettings
        {
            Receiver = new ReceiverSettings
            {
                IsAvailable = true,
            },
        };
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay,
            clientSettings: settings);

        await viewModel.InitializeAsync();

        Assert.Equal(ReceiverAvailability.Available, viewModel.ReceiverAvailability);
        Assert.True(viewModel.HasReceiverSession);
        Assert.True(relay.IsDiscoverable);
    }

    [Fact]
    public async Task ReceiverAvailability_PublishesTheSessionAndUpdatesTheServer()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);

        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);

        Assert.True(viewModel.CanSetReceiverAvailability);
        Assert.Equal(ReceiverAvailability.Available, viewModel.ReceiverAvailability);
        Assert.True(relay.IsDiscoverable);
    }

    [Fact]
    public async Task ReceiverResolutionChange_IsSentForActiveSelectedDisplay()
    {
        var initial = CreateMonitor("DISPLAY1", isPrimary: true, width: 1_920);
        var monitors = new FakeMonitorService([initial]);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            monitors,
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);
        monitors.Monitors = [CreateMonitor("DISPLAY1", isPrimary: true, width: 2_560)];

        await viewModel.HandleDisplayConfigurationChangedAsync();

        Assert.Equal(2_560, relay.UpdatedReceiverDisplay?.WidthPixels);
    }

    [Fact]
    public async Task RestoreSessions_SkipsBothRolesForSharedProfileTestClients()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var receiverRelay = new FakeRelayClient
        {
            Credential = new SessionCredential(
                "receiver-session",
                ClientRole.Receiver,
                "shared-client",
                new string('s', 32),
                new string('r', 32),
                expiresAt),
            SessionId = "receiver-session",
            ResumeResult = true,
        };
        var presenterRelay = new FakeRelayClient
        {
            Credential = new SessionCredential(
                "presenter-session",
                ClientRole.Presenter,
                "shared-client",
                new string('t', 32),
                new string('u', 32),
                expiresAt),
            SessionId = "presenter-session",
            ResumeResult = true,
        };
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: receiverRelay,
            presenterRelayClient: presenterRelay);

        await viewModel.RestoreSessionsAsync();

        Assert.Equal(0, receiverRelay.ResumeCount);
        Assert.Equal(0, presenterRelay.ResumeCount);
        Assert.Contains(
            "skipped",
            viewModel.ReceiverConnectionMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReceiverSession_ApprovesPresenterAndDisplaysFreshPointerWithAcknowledgement()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);

        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);
        byte[] picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var presenter = new PresenterDescriptor(
            "connection-1",
            "presenter-1",
            "Presenter PC",
            "1.0.0",
            picture);
        relay.RaiseJoinRequest(presenter);
        Assert.Equal(picture, viewModel.PendingPresenterProfilePicturePng);
        viewModel.ApprovePresenterCommand.Execute(null);
        var pointer = new PointerEventMessage(
            Guid.NewGuid(),
            "session-1",
            1,
            0.25d,
            0.75d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            2_000);

        relay.RaisePointer(pointer);

        Assert.Equal("connection-1", relay.ApprovedPresenter?.ConnectionId);
        Assert.Equal(new NormalizedPoint(0.25d, 0.75d), Assert.Single(overlay.Markers));
        Assert.Equal(pointer.EventId, relay.SentAcknowledgement?.EventId);
        Assert.True(viewModel.CanSelectMonitor);
    }

    [Fact]
    public async Task ConnectedPresenter_EnablesReceiverDisconnectAllCommand()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);
        relay.RaiseApproved(
            new SessionStateMessage(
                "session-1",
                true,
                monitor.Display,
                DateTimeOffset.UtcNow.AddHours(1),
                ReceiverDiscoverable: true,
                ConnectedPresenters:
                [
                    new ConnectedPresenterDescriptor("Presenter One"),
                    new ConnectedPresenterDescriptor("Presenter Two"),
                ]));

        Assert.True(viewModel.HasConnectedPresenter);
        Assert.Equal(2, viewModel.ConnectedPresenters.Count);
        Assert.Equal("2 senders connected", viewModel.ConnectedPresenterCountLabel);
        Assert.False(viewModel.Presenter.SenderRoleEnabled);
        Assert.False(viewModel.Presenter.JoinDiscoveredReceiverCommand.CanExecute(
            new AvailableReceiverDescriptor("other-session", "Other receiver")));
        Assert.Equal("Available and connected", viewModel.AvailabilityLabel);
        Assert.Equal("#63C5DA", viewModel.AvailabilityColor);
        Assert.True(viewModel.CanSetReceiverAvailability);

        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Invisible);

        Assert.True(viewModel.HasConnectedPresenter);
        Assert.False(relay.IsDiscoverable);
        Assert.Equal("Invisible", viewModel.AvailabilityLabel);
        Assert.Equal("#8B8B8B", viewModel.AvailabilityColor);
        Assert.True(viewModel.DisconnectAllConnectionsCommand.CanExecute(null));
        viewModel.DisconnectAllConnectionsCommand.Execute(null);

        Assert.Equal(1, relay.DisconnectAllConnectionsCount);
    }

    [Fact]
    public void RestoredReceiverState_NotifiesThatDisconnectAllBecameEnabled()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        relay.Credential = new SessionCredential(
            "restored-session",
            ClientRole.Receiver,
            "receiver-client",
            new string('s', 32),
            new string('r', 32),
            DateTimeOffset.UtcNow.AddHours(1));
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        var observedEnabledState = false;
        viewModel.DisconnectAllConnectionsCommand.CanExecuteChanged +=
            (_, _) => observedEnabledState |=
                viewModel.DisconnectAllConnectionsCommand.CanExecute(null);

        relay.RaiseApproved(
            new SessionStateMessage(
                "restored-session",
                true,
                monitor.Display,
                DateTimeOffset.UtcNow.AddHours(1),
                ConnectedPresenters: [new ConnectedPresenterDescriptor("Sender")]));

        Assert.True(observedEnabledState);
        Assert.True(viewModel.DisconnectAllConnectionsCommand.CanExecute(null));
    }

    [Fact]
    public async Task PendingPresenter_CanBeExplicitlyRejected()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);
        var presenter = new PresenterDescriptor(
            "pending-connection",
            "pending-client",
            "Pending Presenter",
            "1.0.0");
        relay.RaiseJoinRequest(presenter);

        await viewModel.RejectPendingPresenterAsync();

        Assert.False(viewModel.HasPendingPresenter);
        Assert.Equal("pending-connection", relay.RejectedPresenter?.ConnectionId);
    }

    [Fact]
    public void PendingPresenterDisconnect_ClearsStaleApprovalRequest()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        relay.RaiseJoinRequest(
            new PresenterDescriptor(
                "pending-connection",
                "pending-client",
                "Pending Presenter",
                "1.0.0"));

        relay.RaiseJoinRequestCancelled("pending-connection");

        Assert.False(viewModel.HasPendingPresenter);
        Assert.Contains("withdrew", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReceiverSession_DropsExpiredPointerWithoutAcknowledging()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);
        var expired = new PointerEventMessage(
            Guid.NewGuid(),
            "session-1",
            1,
            0.5d,
            0.5d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.AddSeconds(-3).ToUnixTimeMilliseconds(),
            2_000);

        relay.RaisePointer(expired);

        Assert.Empty(overlay.Markers);
        Assert.Null(relay.SentAcknowledgement);
    }

    [Fact]
    public async Task ReceiverSession_ForwardsGesturePayloadToOverlay()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);
        var gestureId = Guid.NewGuid();
        var pointer = new PointerEventMessage(
            Guid.NewGuid(),
            "session-1",
            1,
            0.2d,
            0.8d,
            PointerKind.RectangleUpdate,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            2_000,
            gestureId);

        relay.RaisePointer(pointer);

        Assert.Same(pointer, Assert.Single(overlay.Pointers));
        Assert.Equal(pointer.EventId, relay.SentAcknowledgement?.EventId);
    }

    [Fact]
    public void ReceiverWithoutActiveSession_DropsPointerWithoutAcknowledging()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        overlay.Show(monitor);
        var pointer = new PointerEventMessage(
            Guid.NewGuid(),
            "session-1",
            1,
            0.5d,
            0.5d,
            PointerKind.Click,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            2_000);

        relay.RaisePointer(pointer);

        Assert.Empty(overlay.Markers);
        Assert.Null(relay.SentAcknowledgement);
    }

    [Fact]
    public async Task ReceiverAvailability_UpdateFailureKeepsRequestedStateAndRetries()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();
        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);
        relay.DiscoverabilityException = new InvalidOperationException("Relay unavailable.");

        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Invisible);

        Assert.True(viewModel.HasReceiverSession);
        Assert.Equal(ReceiverAvailability.Invisible, viewModel.ReceiverAvailability);
        Assert.True(viewModel.IsError);
        Assert.Contains("availability", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);

        relay.DiscoverabilityException = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (relay.IsDiscoverable && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.False(relay.IsDiscoverable);
    }

    [Fact]
    public async Task SelectingInvisibleBeforeReceiverSession_RemainsInvisibleWithoutRelayUpdate()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();

        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Invisible);

        Assert.False(viewModel.HasReceiverSession);
        Assert.Equal(ReceiverAvailability.Invisible, viewModel.ReceiverAvailability);
        Assert.False(relay.IsDiscoverable);
        Assert.Equal(0, relay.DiscoverabilityUpdateCount);
    }

    [Fact]
    public async Task SelectingAvailableWhileServerIsOffline_KeepsChoiceAndQueuesRetry()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        var relay = CreateReceiverRelay();
        relay.CreateException = new InvalidOperationException("Server unavailable.");
        relay.RaiseConnectionStatus(RelayConnectionStatus.Disconnected, "Server unavailable.");
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay,
            receiverRelayClient: relay);
        await viewModel.InitializeAsync();

        await viewModel.SetReceiverAvailabilityAsync(ReceiverAvailability.Available);

        Assert.True(viewModel.SetReceiverAvailabilityCommand.CanExecute(
            ReceiverAvailability.Invisible));
        Assert.False(viewModel.HasReceiverSession);
        Assert.Equal(ReceiverAvailability.Available, viewModel.ReceiverAvailability);
        Assert.Equal("Server unavailable", viewModel.AvailabilityLabel);
        Assert.True(viewModel.IsError);

        relay.CreateException = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (!viewModel.HasReceiverSession && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(viewModel.HasReceiverSession);
        Assert.Equal("Available", viewModel.AvailabilityLabel);
        Assert.True(relay.IsDiscoverable);
    }

    [Fact]
    public void Constructor_LoadsAndSelectsFirstMonitor()
    {
        var primary = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([primary]),
            overlay);

        Assert.Single(viewModel.Monitors);
        Assert.Same(primary, viewModel.SelectedMonitor);
        Assert.False(viewModel.IsError);
    }

    [Fact]
    public void RefreshMonitors_PreservesSelectionByDisplayId()
    {
        var first = CreateMonitor("DISPLAY1", isPrimary: true);
        var second = CreateMonitor("DISPLAY2", isPrimary: false);
        var monitorService = new FakeMonitorService([first, second]);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(monitorService, overlay)
        {
            SelectedMonitor = second,
        };

        var refreshedSecond = CreateMonitor("DISPLAY2", isPrimary: false, width: 2_560);
        monitorService.Monitors = [first, refreshedSecond];
        viewModel.RefreshMonitors();

        Assert.Same(refreshedSecond, viewModel.SelectedMonitor);
    }

    [Fact]
    public void RefreshMonitors_RemovesOverlayWhenSelectionDisconnects()
    {
        var first = CreateMonitor("DISPLAY1", isPrimary: true);
        var second = CreateMonitor("DISPLAY2", isPrimary: false);
        var monitorService = new FakeMonitorService([first, second]);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(monitorService, overlay)
        {
            SelectedMonitor = second,
        };
        overlay.Show(second);

        monitorService.Monitors = [first];
        viewModel.RefreshMonitors();

        Assert.True(overlay.HideWasCalled);
        Assert.False(viewModel.IsOverlayVisible);
        Assert.True(viewModel.IsError);
        Assert.Contains("disconnected", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Same(first, viewModel.SelectedMonitor);
    }

    [Fact]
    public void OverlayDisconnectionState_IsPresentedAsError()
    {
        var monitor = CreateMonitor("DISPLAY1", isPrimary: true);
        using var overlay = new FakeOverlayService();
        using var viewModel = new MainWindowViewModel(
            new FakeMonitorService([monitor]),
            overlay);

        overlay.RaiseDisconnected();

        Assert.False(viewModel.IsOverlayVisible);
        Assert.True(viewModel.IsError);
        Assert.Contains("disconnected", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static MonitorDescriptor CreateMonitor(
        string id,
        bool isPrimary,
        int width = 1_920) => new(
            Handle: 1,
            new DisplayDescriptor(id, id, width, 1_080, 1d, 0),
            new PhysicalRectangle(isPrimary ? 0 : -width, 0, width, 1_080),
            new PhysicalRectangle(isPrimary ? 0 : -width, 0, width, 1_040),
            isPrimary);

    private static FakeRelayClient CreateReceiverRelay()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(8);
        var credential = new SessionCredential(
            "session-1",
            ClientRole.Receiver,
            "receiver-1",
            new string('s', 32),
            new string('r', 32),
            expiresAt);
        return new FakeRelayClient
        {
            Capabilities = new RelayCapabilities(ServerPasswordRequired: false),
            CreateResponse = new CreateSessionResponse(
                "session-1",
                new string('x', 32),
                credential),
        };
    }

    private sealed class FakeMonitorService(IReadOnlyList<MonitorDescriptor> monitors)
        : IMonitorService
    {
        public IReadOnlyList<MonitorDescriptor> Monitors { get; set; } = monitors;

        public IReadOnlyList<MonitorDescriptor> GetMonitors() => Monitors;

        public MonitorDescriptor? FindByDisplayId(string displayId) =>
            Monitors.FirstOrDefault(
                monitor => string.Equals(
                    monitor.Display.DisplayId,
                    displayId,
                    StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeServerConnectionTester(ServerConnectionTestResult result)
        : IServerConnectionTester
    {
        public List<string> TestedAddresses { get; } = [];

        public ServerConnectionTestResult Result { get; set; } = result;

        public Task<ServerConnectionTestResult> TestAsync(
            string serverAddress,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestedAddresses.Add(serverAddress);
            return Task.FromResult(Result);
        }
    }

    private sealed class TemporaryClientSettings : IDisposable
    {
        private readonly string directory;

        public TemporaryClientSettings(string serverAddress)
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                $"RemotePointer.ViewModelTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var json = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    Server = new
                    {
                        BaseUrl = serverAddress,
                        ReconnectDelaysSeconds = new[] { 0, 2 },
                    },
                    Pointer = new
                    {
                        DefaultTtlMilliseconds = 2_000,
                    },
                });
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), json);
            Settings = ClientSettings.Load(directory, null);
        }

        public ClientSettings Settings { get; }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeOverlayService : IReceiverOverlayService
    {
        public event EventHandler<OverlayStateChangedEventArgs>? StateChanged;

        public bool IsVisible { get; private set; }

        public bool HideWasCalled { get; private set; }

        public MonitorDescriptor? ShownMonitor { get; private set; }

        public List<NormalizedPoint> Markers { get; } = [];

        public List<PointerEventMessage> Pointers { get; } = [];

        public void Show(MonitorDescriptor monitor)
        {
            ShownMonitor = monitor;
            IsVisible = true;
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs("Overlay active.", false, true));
        }

        public void Hide()
        {
            HideWasCalled = true;
            IsVisible = false;
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs("Overlay hidden.", false, false));
        }

        public bool ShowPointer(PointerEventMessage pointerEvent)
        {
            if (!IsVisible)
            {
                return false;
            }

            Pointers.Add(pointerEvent);
            if (pointerEvent.Kind == PointerKind.Click)
            {
                Markers.Add(new NormalizedPoint(
                    pointerEvent.NormalizedX,
                    pointerEvent.NormalizedY));
            }

            return true;
        }

        public void RaiseDisconnected()
        {
            IsVisible = false;
            StateChanged?.Invoke(
                this,
                new OverlayStateChangedEventArgs(
                    "The selected monitor was disconnected.",
                    true,
                    false));
        }

        public void Dispose()
        {
        }
    }
}
