using System.Runtime.InteropServices;

namespace Clicky.Windows.Capture;

/// <summary>
/// Win32, GDI and Direct3D 11 declarations used by the two screenshot backends.
/// Kept in one place so every interface ID has a single, documented source.
/// </summary>
internal static class CaptureNativeMethods
{
    // ---------------------------------------------------------------------------------
    // COM interface IDs. Each one was checked against a source outside this repo.
    // QueryInterface fails with E_NOINTERFACE if one of these were wrong, so a working
    // Windows.Graphics.Capture path is also a runtime check of all four.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// IDXGIDevice (dxgi.h). Matches the uuid in the dxgi.h declaration as mirrored by the
    /// Wine IDL and the winapi crate; Microsoft's reference page does not print the value.
    /// </summary>
    public static readonly Guid DxgiDeviceInterfaceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>
    /// IGraphicsCaptureItemInterop (windows.graphics.capture.interop.h), as declared in
    /// Microsoft's Windows.UI.Composition-Win32-Samples ScreenCapture CaptureHelper.cs.
    /// </summary>
    public static readonly Guid GraphicsCaptureItemInteropInterfaceId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>
    /// Windows.Graphics.Capture.IGraphicsCaptureItem, the default interface of
    /// GraphicsCaptureItem. Same Microsoft sample as above (GraphicsCaptureItemGuid).
    /// </summary>
    public static readonly Guid GraphicsCaptureItemInterfaceId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// IMemoryBufferByteAccess (memorybuffer.h), as declared in Raymond Chen's
    /// The Old New Thing post of 2024-01-22 on reading IMemoryBuffer bytes from C#.
    /// </summary>
    public static readonly Guid MemoryBufferByteAccessInterfaceId = new("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");

    /// <summary>
    /// Vtable slot of IGraphicsCaptureItemInterop::CreateForMonitor. The interface derives from
    /// IUnknown (slots 0 to 2) and declares CreateForWindow (slot 3) before CreateForMonitor.
    /// Microsoft's reference page lists the methods alphabetically, which is not vtable order.
    /// </summary>
    public const int GraphicsCaptureItemInteropCreateForMonitorVtableSlot = 4;

    /// <summary>Vtable slot of IMemoryBufferByteAccess::GetBuffer, its only method after IUnknown.</summary>
    public const int MemoryBufferByteAccessGetBufferVtableSlot = 3;

    // ---------------------------------------------------------------------------------
    // Direct3D 11
    // ---------------------------------------------------------------------------------

    /// <summary>D3D_DRIVER_TYPE_HARDWARE: the real GPU.</summary>
    public const int Direct3DDriverTypeHardware = 1;

    /// <summary>D3D_DRIVER_TYPE_WARP: the software rasterizer, used when no usable GPU driver exists (some VMs and RDP sessions).</summary>
    public const int Direct3DDriverTypeWarp = 5;

    /// <summary>D3D11_CREATE_DEVICE_BGRA_SUPPORT: required for B8G8R8A8 surfaces, which is the format the capture frame pool uses.</summary>
    public const uint Direct3D11CreateDeviceBgraSupportFlag = 0x20;

    /// <summary>D3D11_SDK_VERSION from d3d11.h.</summary>
    public const uint Direct3D11SdkVersion = 7;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        IntPtr adapterPointer,
        int driverType,
        IntPtr softwareRasterizerModuleHandle,
        uint creationFlags,
        IntPtr featureLevelsPointer,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr direct3DDevicePointer,
        out int obtainedFeatureLevel,
        out IntPtr immediateContextPointer);

    /// <summary>
    /// Wraps an IDXGIDevice as the WinRT IDirect3DDevice that Direct3D11CaptureFramePool needs.
    /// Returns an IInspectable pointer the caller owns.
    /// </summary>
    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevicePointer, out IntPtr inspectableGraphicsDevicePointer);

    // ---------------------------------------------------------------------------------
    // GDI
    // ---------------------------------------------------------------------------------

    /// <summary>SRCCOPY raster operation: copy source pixels unchanged.</summary>
    public const uint RasterOperationSourceCopy = 0x00CC0020;

    /// <summary>
    /// CAPTUREBLT: also copy layered windows (anything with WS_EX_LAYERED, such as
    /// translucent popups and many modern app windows). Without it those windows are
    /// missing from the screenshot.
    /// </summary>
    public const uint RasterOperationCaptureLayeredWindows = 0x40000000;

    /// <summary>DIB_RGB_COLORS: the BITMAPINFO color table holds literal RGB values.</summary>
    public const uint DeviceIndependentBitmapRgbColors = 0;

    /// <summary>BI_RGB: uncompressed pixels.</summary>
    public const uint BitmapCompressionUncompressedRgb = 0;

    /// <summary>BITMAPINFOHEADER from wingdi.h (40 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint StructureSizeInBytes;
        public int WidthInPixels;

        /// <summary>Negative height means a top-down DIB (first row in memory is the top row), which matches WPF's layout.</summary>
        public int HeightInPixels;

        public ushort PlaneCount;
        public ushort BitsPerPixel;
        public uint CompressionType;
        public uint ImageSizeInBytes;
        public int HorizontalPixelsPerMeter;
        public int VerticalPixelsPerMeter;
        public uint ColorTableEntriesUsed;
        public uint ImportantColorCount;
    }

    /// <summary>BITMAPINFO from wingdi.h: the header plus a one-entry color table, unused for 32 bpp BI_RGB.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint FirstColorTableEntry;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContextHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr deviceContextHandle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr deviceContextHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr deviceContextHandle,
        ref BitmapInfo bitmapInfo,
        uint colorUsage,
        out IntPtr pixelBitsPointer,
        IntPtr fileMappingSectionHandle,
        uint fileMappingOffset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr deviceContextHandle, IntPtr graphicsObjectHandle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr graphicsObjectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        IntPtr destinationDeviceContextHandle,
        int destinationLeft,
        int destinationTop,
        int copyWidth,
        int copyHeight,
        IntPtr sourceDeviceContextHandle,
        int sourceLeft,
        int sourceTop,
        uint rasterOperation);

    /// <summary>GDI may batch drawing calls; this makes sure BitBlt has finished writing the DIB before we read it.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GdiFlush();
}
