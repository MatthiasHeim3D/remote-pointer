using Microsoft.Extensions.Options;
using RemotePointer.Contracts.Messages;
using RemotePointer.Server.RateLimiting;
using RemotePointer.Server.Sessions;

namespace RemotePointer.Server.Tests.Sessions;

/// <summary>
/// Exercises colour allocation the way the hub drives it: a reallocation after every membership
/// change, and the returned deltas applied to what each annotator is currently drawing in.
/// </summary>
public sealed class SessionManagerAnnotationColorTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreferenceIsGrantedWhenNobodyElseHoldsIt()
    {
        var session = new ColorSession();
        var annotator = session.Approve("annotator-one");

        session.Prefer(annotator, "#6CCB7F");

        Assert.Equal("#6CCB7F", session[annotator]);
    }

    [Fact]
    public void PreferenceIsNormalisedBeforeItIsAllocated()
    {
        var session = new ColorSession();
        var annotator = session.Approve("annotator-one");

        session.Prefer(annotator, "  #6ccb7f ");

        Assert.Equal("#6CCB7F", session[annotator]);
    }

    [Fact]
    public void SecondAnnotatorWantingATakenColorIsMovedAndTheFirstIsUntouched()
    {
        var session = new ColorSession();
        var first = session.Approve("annotator-one");
        var second = session.Approve("annotator-two");
        session.Prefer(first, "#B388FF");

        session.Prefer(second, "#B388FF");

        Assert.Equal("#B388FF", session[first]);
        Assert.NotEqual("#B388FF", session[second]);
        Assert.Contains(session[second], AnnotationColors.Palette);
    }

    [Fact]
    public void CustomColorIsHonouredUntilASecondAnnotatorAsksForIt()
    {
        var session = new ColorSession();
        var first = session.Approve("annotator-one");
        var second = session.Approve("annotator-two");

        session.Prefer(first, "#123456");
        Assert.Equal("#123456", session[first]);

        session.Prefer(second, "#123456");

        // Only presets are handed out, so the displaced annotator lands on something the
        // settings pane can name rather than on another arbitrary colour.
        Assert.Equal("#123456", session[first]);
        Assert.Contains(session[second], AnnotationColors.Palette);
    }

    [Fact]
    public void DisplacedAnnotatorIsGivenItsPreferenceBackWhenTheHolderLeaves()
    {
        var session = new ColorSession();
        var first = session.Approve("annotator-one");
        var second = session.Approve("annotator-two");
        session.Prefer(first, "#B388FF");
        session.Prefer(second, "#B388FF");
        Assert.NotEqual("#B388FF", session[second]);

        session.Disconnect(first);

        Assert.Equal("#B388FF", session[second]);
    }

    [Fact]
    public void ReallocationReportsNothingWhenNobodyMoved()
    {
        var session = new ColorSession();
        var first = session.Approve("annotator-one");
        var second = session.Approve("annotator-two");
        session.Prefer(first, "#B388FF");
        session.Prefer(second, "#6CCB7F");

        // Nothing is contended, so a refresh must not churn either annotator's colour.
        Assert.Empty(session.Refresh());
    }

    [Fact]
    public void EveryAnnotatorGetsADistinctPresetUpToPaletteCapacity()
    {
        var session = new ColorSession();
        var capacity = AnnotationColors.Palette.Count;
        for (var index = 0; index < capacity; index++)
        {
            // Everyone asks for the same colour, so allocation has to do all the work.
            session.Prefer(session.Approve($"annotator-{index}"), AnnotationColors.Default);
        }

        Assert.Equal(capacity, session.Colors.Count);
        Assert.Equal(capacity, session.Colors.Distinct(StringComparer.Ordinal).Count());
        Assert.All(session.Colors, color => Assert.Contains(color, AnnotationColors.Palette));
    }

    [Fact]
    public void ColorsRepeatOnceThereAreMoreAnnotatorsThanPresets()
    {
        var session = new ColorSession();
        var capacity = AnnotationColors.Palette.Count;
        for (var index = 0; index < capacity + 2; index++)
        {
            session.Prefer(session.Approve($"annotator-{index}"), AnnotationColors.Default);
        }

        Assert.Equal(capacity + 2, session.Colors.Count);
        Assert.All(session.Colors, color => Assert.Contains(color, AnnotationColors.Palette));
        // Every preset is in use and the two that had to double up are on different colours.
        Assert.Equal(capacity, session.Colors.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            2,
            session.Colors.GroupBy(color => color, StringComparer.Ordinal).Max(group => group.Count()));
    }

    [Fact]
    public void ChoosingAColorAlwaysAnswersTheCallerEvenWhenNothingMoved()
    {
        var manager = CreateManager();
        var session = CreateHost(manager);
        var first = ApproveConnection(manager, session, "annotator-one");
        var second = ApproveConnection(manager, session, "annotator-two");
        _ = manager.RefreshAnnotationColors(session.SessionId);
        _ = manager.SetAnnotationColorPreference(first, "#B388FF");

        // The second annotator is already sitting on the colour allocation would give it, so
        // nothing moves. It applied its own pick locally the moment the user clicked, so it
        // still has to be told what it is actually allowed to draw in.
        var changes = manager.SetAnnotationColorPreference(second, "#B388FF");

        var answer = Assert.Single(
            changes,
            change => string.Equals(change.ConnectionId, second, StringComparison.Ordinal));
        Assert.NotEqual("#B388FF", answer.Color);
        Assert.Contains(answer.Color, AnnotationColors.Palette);
    }

    [Fact]
    public void RefreshingAVanishedSessionIsHarmless() =>
        Assert.Empty(CreateManager().RefreshAnnotationColors("no-such-session"));

    [Fact]
    public void OnlyAnApprovedAnnotatorMayChooseAColor()
    {
        var manager = CreateManager();
        _ = CreateHost(manager);

        Assert.ThrowsAny<InvalidOperationException>(
            () => manager.SetAnnotationColorPreference("host-connection", "#6CCB7F"));
        Assert.ThrowsAny<InvalidOperationException>(
            () => manager.SetAnnotationColorPreference("stranger-connection", "#6CCB7F"));
    }

    private static SessionManager CreateManager() => new(
        Options.Create(new SessionOptions
        {
            AbandonedSessionLifetimeMinutes = 10,
            MaximumSessionHours = 8,
            SequenceWindowSize = 64,
            MaximumAnnotatorsPerHost = 16,
            RequireServerPassword = false,
        }),
        Options.Create(new PointerRateLimitOptions
        {
            EventsPerSecond = 20,
            BurstSize = 30,
        }),
        new SessionSecretGenerator(),
        new ManualTimeProvider(InitialTime));

    private static string ApproveConnection(
        SessionManager manager,
        CreateSessionResponse session,
        string clientInstanceId)
    {
        var join = manager.RequestToJoinHost(
            new DirectJoinRequest(session.SessionId, clientInstanceId, "1.0.0"),
            $"{clientInstanceId}-connection",
            clientInstanceId);
        _ = manager.ApproveAnnotator(
            session.SessionId,
            join.Annotator!.ConnectionId,
            "host-connection");
        return join.Annotator!.ConnectionId;
    }

    private static CreateSessionResponse CreateHost(SessionManager manager) =>
        manager.CreateHostSession(
            new DisplayDescriptor("display-1", "Display 1", 1_920, 1_080, 1d, 0),
            "host-connection",
            "host-client",
            "Host Machine",
            maximumAnnotatorConnections: 16);

    /// <summary>
    /// A session plus the colour each annotator is currently drawing in, kept up to date from the
    /// deltas the manager returns. That is exactly what the hub delivers to the clients, so
    /// asserting on it asserts on what an annotator would really see.
    /// </summary>
    private sealed class ColorSession
    {
        private readonly Dictionary<string, string> colors = new(StringComparer.Ordinal);
        private readonly SessionManager manager = CreateManager();
        private readonly CreateSessionResponse session;

        internal ColorSession() => session = CreateHost(manager);

        internal IReadOnlyCollection<string> Colors => colors.Values;

        internal string this[string connectionId] => colors[connectionId];

        internal string Approve(string clientInstanceId)
        {
            var join = manager.RequestToJoinHost(
                new DirectJoinRequest(session.SessionId, clientInstanceId, "1.0.0"),
                $"{clientInstanceId}-connection",
                clientInstanceId);
            _ = manager.ApproveAnnotator(
                session.SessionId,
                join.Annotator!.ConnectionId,
                "host-connection");
            // An annotator draws in the default until it is told otherwise, and the hub
            // reallocates the moment the membership changes.
            colors[join.Annotator!.ConnectionId] = AnnotationColors.Default;
            Apply(manager.RefreshAnnotationColors(session.SessionId));
            return join.Annotator!.ConnectionId;
        }

        internal void Prefer(string connectionId, string color) =>
            Apply(manager.SetAnnotationColorPreference(connectionId, color));

        internal void Disconnect(string connectionId)
        {
            _ = manager.Disconnect(connectionId);
            colors.Remove(connectionId);
            Apply(manager.RefreshAnnotationColors(session.SessionId));
        }

        internal IReadOnlyList<AnnotationColorAssignment> Refresh()
        {
            var changes = manager.RefreshAnnotationColors(session.SessionId);
            Apply(changes);
            return changes;
        }

        private void Apply(IReadOnlyList<AnnotationColorAssignment> changes)
        {
            foreach (var change in changes)
            {
                colors[change.ConnectionId] = change.Color;
            }
        }
    }
}
