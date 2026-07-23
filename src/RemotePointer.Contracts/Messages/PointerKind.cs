namespace RemotePointer.Contracts.Messages;

public enum PointerKind
{
    Click,
    DoubleClick,
    Attention,
    PathStart,
    PathUpdate,
    PathEnd,
    LineStart,
    LineUpdate,
    LineEnd,
    Text,
    RectangleStart,
    RectangleUpdate,
    RectangleEnd,
}
