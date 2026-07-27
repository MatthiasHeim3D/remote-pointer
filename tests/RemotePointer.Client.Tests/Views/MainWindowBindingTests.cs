using System.Reflection;
using System.Text.RegularExpressions;
using RemotePointer.Client.ViewModels;

namespace RemotePointer.Client.Tests.Views;

/// <summary>
/// WPF reports a binding to a property that does not exist by drawing nothing, so a renamed or
/// mistyped path leaves a blank field rather than an error anyone would notice. This walks the
/// paths the window actually binds and fails on one that resolves nowhere.
/// </summary>
public sealed partial class MainWindowBindingTests
{
    [Fact]
    public void EveryBindingPathResolvesToAPropertyThatExists()
    {
        var known = KnownPropertyNames();
        var unresolved = BindingPaths()
            .Where(path => !known.Contains(path))
            .ToArray();

        Assert.Empty(unresolved);
    }

    [Theory]
    [InlineData(nameof(MainWindowViewModel.RoomInput))]
    [InlineData(nameof(MainWindowViewModel.RoomValidationMessage))]
    [InlineData(nameof(MainWindowViewModel.ServerPasswordWarning))]
    [InlineData(nameof(MainWindowViewModel.ShowServerPasswordWarning))]
    public void SettingsBindsTheRoomAndPasswordProperties(string path)
    {
        Assert.Contains(path, BindingPaths());
    }

    [Fact]
    public void NothingStillBindsTheCheckCodeThatRoomsReplaced()
    {
        var markup = ReadMarkup();

        Assert.DoesNotContain("CheckCode", markup, StringComparison.Ordinal);
    }

    private static IReadOnlyCollection<string> BindingPaths() =>
        [.. BindingPattern()
            .Matches(ReadMarkup())
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)];

    private static HashSet<string> KnownPropertyNames()
    {
        // A window binds against its own view model and, inside item templates, against whatever
        // type the row holds, so the union of what the client and contracts expose is the set a
        // path may legitimately name.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in new[]
                 {
                     typeof(MainWindowViewModel).Assembly,
                     typeof(Contracts.Messages.AvailableHostDescriptor).Assembly,
                 })
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var property in type.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    names.Add(property.Name);
                }
            }
        }

        // Attached and framework properties the markup binds through rather than declares.
        names.UnionWith(
        [
            "DataContext",
            "IsDropDownOpen",
            "IsMouseOver",
            "IsSelected",
            "Tag",
            "Text",
        ]);
        return names;
    }

    private static string ReadMarkup() => File.ReadAllText(
        Path.Combine(RepositoryRoot(), "src", "RemotePointer.Client", "Views", "MainWindow.xaml"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "RemotePointer.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root could not be located.");
    }

    [GeneratedRegex(@"\{Binding\s+(?:Path=)?(?<path>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex BindingPattern();
}
