using System.Globalization;
using System.Windows;
using RemotePointer.Client.Views;

namespace RemotePointer.Client.Tests.Views;

public sealed class ProfilePictureFallbackVisibilityConverterTests
{
    private readonly ProfilePictureFallbackVisibilityConverter converter = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Convert_ShowsFallbackWhenLocalPictureIsNotConfigured(string? value)
    {
        Assert.Equal(Visibility.Visible, Convert(value));
    }

    [Fact]
    public void Convert_HidesFallbackForConfiguredLocalPicture()
    {
        Assert.Equal(Visibility.Collapsed, Convert(@"C:\Pictures\profile.png"));
    }

    [Fact]
    public void Convert_UsesRemotePictureByteContent()
    {
        Assert.Equal(Visibility.Visible, Convert(Array.Empty<byte>()));
        Assert.Equal(Visibility.Collapsed, Convert(new byte[] { 1 }));
    }

    private Visibility Convert(object? value) => (Visibility)converter.Convert(
        value,
        typeof(Visibility),
        parameter: null,
        CultureInfo.InvariantCulture);
}
