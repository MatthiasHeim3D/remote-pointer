using System.Windows.Media;
using RemotePointer.Client.Overlays;
using RemotePointer.Contracts.Messages;

namespace RemotePointer.Client.Tests.Overlays;

public sealed class AnnotationPaletteTests
{
    [Fact]
    public void ToColor_ReadsEachChannelFromTheHexValue()
    {
        var color = AnnotationPalette.ToColor("#4FC3F7");

        Assert.Equal(Color.FromRgb(0x4F, 0xC3, 0xF7), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a colour")]
    public void ToColor_FallsBackToTheDefaultAccentForUnusableValues(string? annotationColor)
    {
        var color = AnnotationPalette.ToColor(annotationColor);

        Assert.Equal(AnnotationPalette.DefaultAccent, color);
        Assert.Equal(AnnotationPalette.ToColor(AnnotationColors.Default), color);
    }

    [Fact]
    public void ToAnnotationColor_RoundTripsThroughToColor()
    {
        var encoded = AnnotationPalette.ToAnnotationColor(0x0A, 0xB3, 0xFF);

        Assert.Equal("#0AB3FF", encoded);
        Assert.Equal(Color.FromRgb(0x0A, 0xB3, 0xFF), AnnotationPalette.ToColor(encoded));
    }

    [Fact]
    public void Darken_KeepsTheHueAndLowersEveryChannel()
    {
        var darkened = AnnotationPalette.Darken(Color.FromRgb(200, 100, 50), 0.5d);

        Assert.Equal(Color.FromRgb(100, 50, 25), darkened);
    }
}
