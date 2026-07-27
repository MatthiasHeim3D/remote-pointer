namespace RemoteAnnotate.Client.Native;

public readonly record struct PhysicalRectangle(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);

    public int Bottom => checked(Top + Height);
}
