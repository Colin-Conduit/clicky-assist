using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicky.Core.Pointing;
using Clicky.Windows.Diagnostics;

namespace Clicky.Windows.Capture;

/// <summary>
/// Scales captured pixels to the screenshot size Claude sees and encodes them as JPEG.
/// One code path for both backends, so a GDI fallback image is indistinguishable from a
/// Windows.Graphics.Capture image apart from the mouse pointer.
///
/// Runs on a worker thread. WPF imaging objects are bound to the thread that creates them
/// until frozen, so this method creates, freezes and encodes everything synchronously on
/// the calling thread and never awaits.
/// </summary>
internal static class ScreenshotJpegEncoder
{
    /// <summary>The Mac encodes with NSBitmapImageRep compressionFactor 0.8; WPF's scale is 1 to 100.</summary>
    public const int JpegQualityLevel = 80;

    /// <summary>
    /// The pixel values are what matter; the DPI only becomes JPEG metadata. 96 means
    /// "one image pixel per device-independent pixel", the neutral value for WPF.
    /// </summary>
    private const double NeutralImageDpi = 96.0;

    private const string LogCategory = "Capture";

    public static byte[] EncodeScaledJpeg(CapturedMonitorPixels capturedPixels, ScreenshotDimensions screenshotDimensions)
    {
        // Bgr32 ignores the fourth byte. GDI leaves it at zero, so reading it as alpha
        // (Bgra32) would make the whole screenshot transparent.
        BitmapSource fullResolutionBitmap = BitmapSource.Create(
            capturedPixels.WidthInPixels,
            capturedPixels.HeightInPixels,
            NeutralImageDpi,
            NeutralImageDpi,
            PixelFormats.Bgr32,
            null,
            capturedPixels.Bgra32PixelBytes,
            capturedPixels.StrideInBytes);
        fullResolutionBitmap.Freeze();

        BitmapSource scaledBitmap = ScaleToExactDimensions(fullResolutionBitmap, screenshotDimensions);

        JpegBitmapEncoder jpegEncoder = new() { QualityLevel = JpegQualityLevel };
        jpegEncoder.Frames.Add(BitmapFrame.Create(scaledBitmap));

        using MemoryStream jpegOutputStream = new();
        jpegEncoder.Save(jpegOutputStream);
        return jpegOutputStream.ToArray();
    }

    /// <summary>
    /// Claude's coordinates are read in the screenshot's pixel space and mapped back with
    /// <see cref="ScreenCoordinateMapper"/>, so the image must be exactly
    /// <paramref name="screenshotDimensions"/>, not one pixel off.
    /// </summary>
    private static BitmapSource ScaleToExactDimensions(BitmapSource fullResolutionBitmap, ScreenshotDimensions screenshotDimensions)
    {
        int targetWidthInPixels = screenshotDimensions.WidthInPixels;
        int targetHeightInPixels = screenshotDimensions.HeightInPixels;

        if (fullResolutionBitmap.PixelWidth == targetWidthInPixels && fullResolutionBitmap.PixelHeight == targetHeightInPixels)
        {
            return fullResolutionBitmap;
        }

        // TransformedBitmap scales with WIC's Fant interpolation (see TransformedBitmap.cs in
        // dotnet/wpf), which averages every source pixel when shrinking, so small UI text stays
        // legible instead of aliasing.
        // Each axis gets its own factor in case the captured frame and the monitor bounds
        // disagree slightly on aspect ratio.
        double horizontalScaleFactor = (double)targetWidthInPixels / fullResolutionBitmap.PixelWidth;
        double verticalScaleFactor = (double)targetHeightInPixels / fullResolutionBitmap.PixelHeight;
        TransformedBitmap scaledBitmap = new(fullResolutionBitmap, new ScaleTransform(horizontalScaleFactor, verticalScaleFactor));
        scaledBitmap.Freeze();

        if (scaledBitmap.PixelWidth == targetWidthInPixels && scaledBitmap.PixelHeight == targetHeightInPixels)
        {
            return scaledBitmap;
        }

        // WPF computes the scaled size as (uint)(scale * sourceSize + 0.5), so with
        // scale = target / source it rounds to the target. If floating point ever lands one
        // pixel away anyway, copy into an exact-size buffer: crop the extra edge, or leave a
        // black edge pixel where the scaled image came up short.
        ClickyApplicationLog.Write(
            LogCategory,
            $"Scaled bitmap came out {scaledBitmap.PixelWidth}x{scaledBitmap.PixelHeight} instead of {targetWidthInPixels}x{targetHeightInPixels}; copying into an exact-size buffer.");
        return CopyIntoExactSizeBitmap(scaledBitmap, targetWidthInPixels, targetHeightInPixels);
    }

    private static BitmapSource CopyIntoExactSizeBitmap(BitmapSource scaledBitmap, int targetWidthInPixels, int targetHeightInPixels)
    {
        BitmapSource scaledBitmapInBgr32 = scaledBitmap.Format == PixelFormats.Bgr32
            ? scaledBitmap
            : new FormatConvertedBitmap(scaledBitmap, PixelFormats.Bgr32, null, 0);

        int exactStrideInBytes = targetWidthInPixels * CapturedMonitorPixels.BytesPerPixel;
        byte[] exactSizePixelBytes = new byte[exactStrideInBytes * targetHeightInPixels];

        int copiedWidthInPixels = Math.Min(targetWidthInPixels, scaledBitmapInBgr32.PixelWidth);
        int copiedHeightInPixels = Math.Min(targetHeightInPixels, scaledBitmapInBgr32.PixelHeight);
        scaledBitmapInBgr32.CopyPixels(
            new Int32Rect(0, 0, copiedWidthInPixels, copiedHeightInPixels),
            exactSizePixelBytes,
            exactStrideInBytes,
            0);

        BitmapSource exactSizeBitmap = BitmapSource.Create(
            targetWidthInPixels,
            targetHeightInPixels,
            NeutralImageDpi,
            NeutralImageDpi,
            PixelFormats.Bgr32,
            null,
            exactSizePixelBytes,
            exactStrideInBytes);
        exactSizeBitmap.Freeze();
        return exactSizeBitmap;
    }
}
