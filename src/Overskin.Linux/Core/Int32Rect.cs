// The one piece of WPF the shared picture finder (ImageFinder.cs) uses: a rectangle in whole pixels.
namespace System.Windows;

public readonly record struct Int32Rect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
