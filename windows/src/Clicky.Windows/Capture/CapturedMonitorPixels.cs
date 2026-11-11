namespace Clicky.Windows.Capture;

/// <summary>
/// Full-resolution pixels from one backend, before scaling and JPEG encoding.
/// Both backends produce the same layout so the encoder has one code path:
/// 32 bits per pixel in B, G, R, X byte order, top row first. The fourth byte is
/// not meaningful alpha (GDI leaves it at zero), so the encoder treats it as padding.
/// </summary>
/// <param name="Bgra32PixelBytes">Pixel rows, <paramref name="StrideInBytes"/> bytes apart.</param>
/// <param name="WidthInPixels">Width of the captured image, which can differ from the monitor bounds.</param>
/// <param name="HeightInPixels">Height of the captured image.</param>
/// <param name="StrideInBytes">Distance in bytes from the start of one row to the start of the next.</param>
internal sealed record CapturedMonitorPixels(
    byte[] Bgra32PixelBytes,
    int WidthInPixels,
    int HeightInPixels,
    int StrideInBytes)
{
    public const int BytesPerPixel = 4;
}
