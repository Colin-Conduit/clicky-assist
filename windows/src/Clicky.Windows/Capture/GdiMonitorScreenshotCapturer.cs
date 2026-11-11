using System.ComponentModel;
using System.Runtime.InteropServices;
using Clicky.Core.Pointing;
using Clicky.Windows.Display;

namespace Clicky.Windows.Capture;

/// <summary>
/// Fallback backend: copies one monitor's pixels out of the desktop with GDI BitBlt.
/// It works on every Windows version and inside RDP sessions and VMs where
/// Windows.Graphics.Capture can fail, so it must never depend on the GPU.
///
/// Our own overlay windows stay out of these screenshots because they call
/// SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE), which BitBlt and
/// Windows.Graphics.Capture both honor. That replaces the Mac's SCContentFilter
/// "excludingWindows" list (to be confirmed by an integration test).
///
/// Unlike Windows.Graphics.Capture, BitBlt does not draw the mouse pointer into the image.
///
/// Everything here is synchronous on purpose: device contexts belong to the thread that
/// created them, so there must be no await between GetDC and ReleaseDC.
/// </summary>
internal sealed class GdiMonitorScreenshotCapturer
{
    public const string BackendDisplayName = "GDI BitBlt";

    private const ushort ThirtyTwoBitsPerPixel = 32;

    public CapturedMonitorPixels CaptureMonitor(DisplayMonitorInfo monitor)
    {
        PhysicalPixelRectangle monitorBounds = monitor.BoundsInPhysicalPixels;
        int captureWidthInPixels = monitorBounds.Width;
        int captureHeightInPixels = monitorBounds.Height;
        if (captureWidthInPixels <= 0 || captureHeightInPixels <= 0)
        {
            throw new InvalidOperationException($"Monitor bounds are empty ({captureWidthInPixels}x{captureHeightInPixels}).");
        }

        int strideInBytes = captureWidthInPixels * CapturedMonitorPixels.BytesPerPixel;
        byte[] capturedPixelBytes = new byte[checked(strideInBytes * captureHeightInPixels)];

        // A null window handle gives a device context for the whole virtual screen. This
        // process is per-monitor DPI aware v2, so its coordinates are physical pixels and
        // match DisplayMonitorInfo.BoundsInPhysicalPixels without any scaling.
        IntPtr screenDeviceContextHandle = CaptureNativeMethods.GetDC(IntPtr.Zero);
        if (screenDeviceContextHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC for the screen returned no device context.");
        }

        IntPtr memoryDeviceContextHandle = IntPtr.Zero;
        IntPtr dibSectionHandle = IntPtr.Zero;
        IntPtr previouslySelectedObjectHandle = IntPtr.Zero;
        try
        {
            memoryDeviceContextHandle = CaptureNativeMethods.CreateCompatibleDC(screenDeviceContextHandle);
            if (memoryDeviceContextHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");
            }

            CaptureNativeMethods.BitmapInfo dibSectionFormat = new()
            {
                Header = new CaptureNativeMethods.BitmapInfoHeader
                {
                    StructureSizeInBytes = (uint)Marshal.SizeOf<CaptureNativeMethods.BitmapInfoHeader>(),
                    WidthInPixels = captureWidthInPixels,

                    // Negative height asks for a top-down DIB so rows come out in the same
                    // order WPF expects, with no flip needed later.
                    HeightInPixels = -captureHeightInPixels,
                    PlaneCount = 1,
                    BitsPerPixel = ThirtyTwoBitsPerPixel,
                    CompressionType = CaptureNativeMethods.BitmapCompressionUncompressedRgb,
                },
            };

            // A DIB section (rather than CreateCompatibleBitmap) gives us a direct pointer to
            // the pixel memory in a known 32 bpp BGRX layout, whatever the display format.
            dibSectionHandle = CaptureNativeMethods.CreateDIBSection(
                memoryDeviceContextHandle,
                ref dibSectionFormat,
                CaptureNativeMethods.DeviceIndependentBitmapRgbColors,
                out IntPtr dibPixelBitsPointer,
                IntPtr.Zero,
                0);
            if (dibSectionHandle == IntPtr.Zero || dibPixelBitsPointer == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateDIBSection failed for {captureWidthInPixels}x{captureHeightInPixels}.");
            }

            previouslySelectedObjectHandle = CaptureNativeMethods.SelectObject(memoryDeviceContextHandle, dibSectionHandle);

            // CAPTUREBLT includes layered windows, which plain SRCCOPY skips. Windows that set
            // WDA_EXCLUDEFROMCAPTURE (our overlays) are still left out by the system.
            bool didCopySucceed = CaptureNativeMethods.BitBlt(
                memoryDeviceContextHandle,
                0,
                0,
                captureWidthInPixels,
                captureHeightInPixels,
                screenDeviceContextHandle,
                monitorBounds.Left,
                monitorBounds.Top,
                CaptureNativeMethods.RasterOperationSourceCopy | CaptureNativeMethods.RasterOperationCaptureLayeredWindows);
            if (!didCopySucceed)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt from the screen failed.");
            }

            CaptureNativeMethods.GdiFlush();

            // 32 bpp rows are always DWORD aligned, so the DIB stride is exactly width * 4.
            Marshal.Copy(dibPixelBitsPointer, capturedPixelBytes, 0, capturedPixelBytes.Length);
        }
        finally
        {
            if (memoryDeviceContextHandle != IntPtr.Zero && previouslySelectedObjectHandle != IntPtr.Zero)
            {
                // A bitmap cannot be deleted while it is selected into a device context.
                CaptureNativeMethods.SelectObject(memoryDeviceContextHandle, previouslySelectedObjectHandle);
            }

            if (dibSectionHandle != IntPtr.Zero)
            {
                CaptureNativeMethods.DeleteObject(dibSectionHandle);
            }

            if (memoryDeviceContextHandle != IntPtr.Zero)
            {
                CaptureNativeMethods.DeleteDC(memoryDeviceContextHandle);
            }

            CaptureNativeMethods.ReleaseDC(IntPtr.Zero, screenDeviceContextHandle);
        }

        return new CapturedMonitorPixels(capturedPixelBytes, captureWidthInPixels, captureHeightInPixels, strideInBytes);
    }
}
