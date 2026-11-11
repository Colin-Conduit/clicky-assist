using System.Runtime.InteropServices;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;

namespace Clicky.Windows.Capture;

/// <summary>
/// Preferred backend: grabs one frame of a monitor with Windows.Graphics.Capture, the
/// closest Windows equivalent of ScreenCaptureKit's SCScreenshotManager on the Mac.
/// It composes the desktop the way DWM shows it (including hardware-accelerated and
/// DirectX content that BitBlt can miss) and can include the mouse pointer.
///
/// Not thread safe: <see cref="ScreenCaptureService"/> runs one capture at a time,
/// which also keeps the shared Direct3D device to a single user.
/// </summary>
internal sealed class WindowsGraphicsCaptureMonitorScreenshotCapturer
{
    public const string BackendDisplayName = "Windows.Graphics.Capture";

    private const string LogCategory = "Capture";
    private const string GraphicsCaptureItemRuntimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    /// <summary>
    /// Microsoft Learn lists GraphicsCaptureAccess and GraphicsCaptureSession.IsBorderRequired as
    /// introduced in 10.0.20348.0 (UniversalApiContract v12). Older builds always draw the border.
    /// </summary>
    private const int FirstWindowsBuildWithBorderlessCapture = 20348;

    /// <summary>
    /// The first frame normally arrives within a few milliseconds of StartCapture. Two seconds
    /// means something is wrong (for example a protected or disconnected display), and the
    /// GDI fallback is the better use of the user's time.
    /// </summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound on waiting for the one-time borderless permission check before capturing anyway.</summary>
    private static readonly TimeSpan BorderlessAccessRequestTimeout = TimeSpan.FromSeconds(1);

    private static readonly Lazy<bool> IsWindowsGraphicsCaptureSupportedOnThisSystem = new(QueryWindowsGraphicsCaptureSupport);

    /// <summary>
    /// Asked once per process. Without borderless access Windows draws a yellow frame around
    /// the captured monitor for the length of the capture, which the Mac never shows.
    /// This unpackaged app was granted access without a prompt in testing; a packaged (MSIX)
    /// build would also need the graphicsCaptureWithoutBorder capability in its manifest.
    /// </summary>
    private static readonly Lazy<Task<bool>> BorderlessCaptureAccessRequest = new(RequestBorderlessCaptureAccessAsync);

    /// <summary>
    /// Created on first use and reused, because creating a Direct3D device costs tens of
    /// milliseconds. Dropped after any failure so the next capture starts with a fresh one
    /// (the old device may have been lost to a driver update, sleep or a GPU reset).
    /// </summary>
    private IDirect3DDevice? cachedDirect3DDevice;

    public static bool IsSupportedOnThisSystem => IsWindowsGraphicsCaptureSupportedOnThisSystem.Value;

    public async Task<CapturedMonitorPixels> CaptureMonitorAsync(DisplayMonitorInfo monitor, CancellationToken cancellationToken)
    {
        if (!IsSupportedOnThisSystem)
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        cachedDirect3DDevice ??= CreateDirect3DDevice();
        IDirect3DDevice direct3DDevice = cachedDirect3DDevice;

        try
        {
            return await CaptureMonitorWithDeviceAsync(monitor, direct3DDevice, cancellationToken);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            DiscardCachedDirect3DDevice();
            throw;
        }
    }

    private static async Task<CapturedMonitorPixels> CaptureMonitorWithDeviceAsync(
        DisplayMonitorInfo monitor,
        IDirect3DDevice direct3DDevice,
        CancellationToken cancellationToken)
    {
        GraphicsCaptureItem monitorCaptureItem = CreateCaptureItemForMonitor(monitor.MonitorHandle);
        SizeInt32 monitorCaptureSize = monitorCaptureItem.Size;
        if (monitorCaptureSize.Width <= 0 || monitorCaptureSize.Height <= 0)
        {
            throw new InvalidOperationException($"The capture item reports an empty size ({monitorCaptureSize.Width}x{monitorCaptureSize.Height}).");
        }

        // Free threaded, so FrameArrived is raised on a system worker thread and this
        // thread-pool thread needs no DispatcherQueue. One buffer is enough for one still frame,
        // and while we hold that frame the pool has nowhere to write a newer one.
        using Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            monitorCaptureSize);
        using GraphicsCaptureSession captureSession = framePool.CreateCaptureSession(monitorCaptureItem);

        TaskCompletionSource firstFrameArrivedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void SignalFirstFrameArrived(Direct3D11CaptureFramePool framePoolThatRaisedEvent, object eventArguments) => firstFrameArrivedSignal.TrySetResult();

        // Subscribe before StartCapture, otherwise the first frame can arrive before anyone listens.
        framePool.FrameArrived += SignalFirstFrameArrived;
        try
        {
            // ScreenCaptureKit includes the pointer by default, so the Mac screenshots show it too.
            captureSession.IsCursorCaptureEnabled = true;
            await TryHideCaptureBorderAsync(captureSession);

            captureSession.StartCapture();
            await WaitForFirstFrameAsync(firstFrameArrivedSignal.Task, cancellationToken);

            using Direct3D11CaptureFrame? capturedFrame = framePool.TryGetNextFrame();
            if (capturedFrame is null)
            {
                throw new InvalidOperationException("FrameArrived was raised but TryGetNextFrame returned no frame.");
            }

            // Copies the GPU surface into CPU memory while the frame (and so its surface) is still valid.
            using SoftwareBitmap frameBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(capturedFrame.Surface);
            return CopySoftwareBitmapPixels(frameBitmap);
        }
        finally
        {
            // The bitmap and frame were disposed at the end of the try block. The session and
            // then the frame pool are disposed when this method returns.
            framePool.FrameArrived -= SignalFirstFrameArrived;
        }
    }

    private static async Task WaitForFirstFrameAsync(Task firstFrameArrivedTask, CancellationToken cancellationToken)
    {
        try
        {
            await firstFrameArrivedTask.WaitAsync(FirstFrameTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No frame arrived within {FirstFrameTimeout.TotalSeconds:0} seconds of starting the capture.");
        }
    }

    /// <summary>
    /// Desktop apps cannot use the GraphicsCapturePicker silently, so the item comes from the
    /// COM interop interface on GraphicsCaptureItem's activation factory, which accepts an HMONITOR.
    /// Called through the raw vtable instead of a [ComImport] interface so the reference
    /// counting is explicit and does not depend on the built-in COM marshaller.
    /// </summary>
    private static unsafe GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr monitorHandle)
    {
        IObjectReference activationFactoryReference = ActivationFactory.Get(GraphicsCaptureItemRuntimeClassName);

        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
            activationFactoryReference.ThisPtr,
            in CaptureNativeMethods.GraphicsCaptureItemInteropInterfaceId,
            out IntPtr graphicsCaptureItemInteropPointer));
        try
        {
            IntPtr* interopVirtualMethodTable = *(IntPtr**)graphicsCaptureItemInteropPointer;
            delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int> createForMonitor =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)interopVirtualMethodTable[CaptureNativeMethods.GraphicsCaptureItemInteropCreateForMonitorVtableSlot];

            Guid graphicsCaptureItemInterfaceId = CaptureNativeMethods.GraphicsCaptureItemInterfaceId;
            IntPtr graphicsCaptureItemPointer = IntPtr.Zero;
            int createForMonitorResult = createForMonitor(graphicsCaptureItemInteropPointer, monitorHandle, &graphicsCaptureItemInterfaceId, &graphicsCaptureItemPointer);
            Marshal.ThrowExceptionForHR(createForMonitorResult);

            try
            {
                // FromAbi takes its own reference, so ours is released right after.
                return GraphicsCaptureItem.FromAbi(graphicsCaptureItemPointer);
            }
            finally
            {
                Marshal.Release(graphicsCaptureItemPointer);
            }
        }
        finally
        {
            Marshal.Release(graphicsCaptureItemInteropPointer);
            GC.KeepAlive(activationFactoryReference);
        }
    }

    /// <summary>
    /// Copies the frame into a tightly packed BGRA byte array. The bitmap's own rows can be
    /// padded (stride wider than width * 4), so rows are copied one at a time using the
    /// plane's start offset and stride.
    /// </summary>
    private static unsafe CapturedMonitorPixels CopySoftwareBitmapPixels(SoftwareBitmap frameBitmap)
    {
        if (frameBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            throw new InvalidOperationException($"Expected a Bgra8 frame but received {frameBitmap.BitmapPixelFormat}.");
        }

        using BitmapBuffer lockedBitmapBuffer = frameBitmap.LockBuffer(BitmapBufferAccessMode.Read);
        BitmapPlaneDescription pixelPlaneDescription = lockedBitmapBuffer.GetPlaneDescription(0);

        int widthInPixels = pixelPlaneDescription.Width;
        int heightInPixels = pixelPlaneDescription.Height;
        int packedRowLengthInBytes = widthInPixels * CapturedMonitorPixels.BytesPerPixel;
        if (widthInPixels <= 0 || heightInPixels <= 0 || pixelPlaneDescription.Stride < packedRowLengthInBytes)
        {
            throw new InvalidOperationException(
                $"Unexpected frame layout: {widthInPixels}x{heightInPixels}, stride {pixelPlaneDescription.Stride} bytes.");
        }

        byte[] packedPixelBytes = new byte[checked(packedRowLengthInBytes * heightInPixels)];

        using IMemoryBufferReference memoryBufferReference = lockedBitmapBuffer.CreateReference();
        if (memoryBufferReference is not IWinRTObject memoryBufferReferenceWinRTObject)
        {
            throw new InvalidOperationException("The bitmap buffer reference is not a Windows Runtime object.");
        }

        // IMemoryBufferByteAccess is a classic COM interface (not projected to C#) that hands out
        // the raw pointer behind the WinRT buffer. The memory is only valid while that interface
        // is held, so the copy happens before it is released.
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
            memoryBufferReferenceWinRTObject.NativeObject.ThisPtr,
            in CaptureNativeMethods.MemoryBufferByteAccessInterfaceId,
            out IntPtr memoryBufferByteAccessPointer));
        try
        {
            IntPtr* byteAccessVirtualMethodTable = *(IntPtr**)memoryBufferByteAccessPointer;
            delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int> getBuffer =
                (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)byteAccessVirtualMethodTable[CaptureNativeMethods.MemoryBufferByteAccessGetBufferVtableSlot];

            byte* bufferStartPointer = null;
            uint bufferCapacityInBytes = 0;
            Marshal.ThrowExceptionForHR(getBuffer(memoryBufferByteAccessPointer, &bufferStartPointer, &bufferCapacityInBytes));

            long lastByteNeededExclusive = pixelPlaneDescription.StartIndex
                + ((long)(heightInPixels - 1) * pixelPlaneDescription.Stride)
                + packedRowLengthInBytes;
            if (bufferStartPointer is null || pixelPlaneDescription.StartIndex < 0 || lastByteNeededExclusive > bufferCapacityInBytes)
            {
                throw new InvalidOperationException(
                    $"The frame buffer is smaller than its plane description ({bufferCapacityInBytes} bytes, needs {lastByteNeededExclusive}).");
            }

            for (int rowIndex = 0; rowIndex < heightInPixels; rowIndex++)
            {
                byte* sourceRowPointer = bufferStartPointer + pixelPlaneDescription.StartIndex + ((long)rowIndex * pixelPlaneDescription.Stride);
                ReadOnlySpan<byte> sourceRow = new(sourceRowPointer, packedRowLengthInBytes);
                sourceRow.CopyTo(packedPixelBytes.AsSpan(rowIndex * packedRowLengthInBytes, packedRowLengthInBytes));
            }
        }
        finally
        {
            Marshal.Release(memoryBufferByteAccessPointer);
            GC.KeepAlive(memoryBufferReference);
        }

        return new CapturedMonitorPixels(packedPixelBytes, widthInPixels, heightInPixels, packedRowLengthInBytes);
    }

    private static async Task TryHideCaptureBorderAsync(GraphicsCaptureSession captureSession)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstWindowsBuildWithBorderlessCapture))
        {
            return;
        }

        try
        {
            bool isBorderlessCaptureAllowed = await BorderlessCaptureAccessRequest.Value.WaitAsync(BorderlessAccessRequestTimeout);
            if (isBorderlessCaptureAllowed)
            {
                captureSession.IsBorderRequired = false;
            }
        }
        catch (Exception exception)
        {
            // The border is cosmetic. Capture with it rather than fail.
            ClickyApplicationLog.Write(LogCategory, $"Could not hide the capture border: {DescribeException(exception)}");
        }
    }

    private static async Task<bool> RequestBorderlessCaptureAccessAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstWindowsBuildWithBorderlessCapture))
        {
            return false;
        }

        try
        {
            AppCapabilityAccessStatus borderlessAccessStatus = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            ClickyApplicationLog.Write(LogCategory, $"Borderless capture access: {borderlessAccessStatus}.");
            return borderlessAccessStatus == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception exception)
        {
            ClickyApplicationLog.Write(LogCategory, $"Borderless capture access request failed: {DescribeException(exception)}");
            return false;
        }
    }

    private static bool QueryWindowsGraphicsCaptureSupport()
    {
        try
        {
            bool isSupported = GraphicsCaptureSession.IsSupported();
            ClickyApplicationLog.Write(LogCategory, $"Windows.Graphics.Capture supported: {isSupported}.");
            return isSupported;
        }
        catch (Exception exception)
        {
            ClickyApplicationLog.Write(LogCategory, $"Windows.Graphics.Capture support check failed: {DescribeException(exception)}");
            return false;
        }
    }

    private void DiscardCachedDirect3DDevice()
    {
        IDirect3DDevice? deviceToDiscard = cachedDirect3DDevice;
        cachedDirect3DDevice = null;
        if (deviceToDiscard is null)
        {
            return;
        }

        try
        {
            deviceToDiscard.Dispose();
        }
        catch (Exception exception)
        {
            ClickyApplicationLog.Write(LogCategory, $"Closing the old Direct3D device failed: {DescribeException(exception)}");
        }
    }

    /// <summary>
    /// Builds the WinRT IDirect3DDevice the frame pool needs: a Direct3D 11 device, its
    /// IDXGIDevice, then CreateDirect3D11DeviceFromDXGIDevice to wrap it for WinRT.
    /// Every intermediate COM pointer is released here; the WinRT wrapper holds its own references.
    /// </summary>
    private static IDirect3DDevice CreateDirect3DDevice()
    {
        IntPtr direct3DDevicePointer = CreateDirect3D11DevicePointerWithWarpFallback();
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(direct3DDevicePointer, in CaptureNativeMethods.DxgiDeviceInterfaceId, out IntPtr dxgiDevicePointer));
            try
            {
                Marshal.ThrowExceptionForHR(CaptureNativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePointer, out IntPtr inspectableGraphicsDevicePointer));
                try
                {
                    // FromAbi takes its own reference, so ours is released right after.
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectableGraphicsDevicePointer);
                }
                finally
                {
                    Marshal.Release(inspectableGraphicsDevicePointer);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevicePointer);
            }
        }
        finally
        {
            Marshal.Release(direct3DDevicePointer);
        }
    }

    private static IntPtr CreateDirect3D11DevicePointerWithWarpFallback()
    {
        int hardwareDeviceResult = TryCreateDirect3D11Device(CaptureNativeMethods.Direct3DDriverTypeHardware, out IntPtr hardwareDevicePointer);
        if (hardwareDeviceResult >= 0)
        {
            ClickyApplicationLog.Write(LogCategory, "Created a hardware Direct3D 11 device for Windows.Graphics.Capture.");
            return hardwareDevicePointer;
        }

        // WARP is the software rasterizer. It covers VMs and remote sessions without a usable GPU driver.
        int warpDeviceResult = TryCreateDirect3D11Device(CaptureNativeMethods.Direct3DDriverTypeWarp, out IntPtr warpDevicePointer);
        if (warpDeviceResult >= 0)
        {
            ClickyApplicationLog.Write(
                LogCategory,
                $"Hardware Direct3D 11 device failed (0x{hardwareDeviceResult:X8}); using the WARP software device for Windows.Graphics.Capture.");
            return warpDevicePointer;
        }

        throw new COMException(
            $"D3D11CreateDevice failed for the hardware driver (0x{hardwareDeviceResult:X8}) and for WARP (0x{warpDeviceResult:X8}).",
            warpDeviceResult);
    }

    private static int TryCreateDirect3D11Device(int driverType, out IntPtr direct3DDevicePointer)
    {
        int creationResult = CaptureNativeMethods.D3D11CreateDevice(
            IntPtr.Zero,
            driverType,
            IntPtr.Zero,
            CaptureNativeMethods.Direct3D11CreateDeviceBgraSupportFlag,
            IntPtr.Zero,
            0,
            CaptureNativeMethods.Direct3D11SdkVersion,
            out direct3DDevicePointer,
            out _,
            out IntPtr immediateContextPointer);

        // Nothing here draws, so the immediate context is not needed. The device keeps its own reference to it.
        if (immediateContextPointer != IntPtr.Zero)
        {
            Marshal.Release(immediateContextPointer);
        }

        if (creationResult < 0)
        {
            direct3DDevicePointer = IntPtr.Zero;
        }

        return creationResult;
    }

    private static string DescribeException(Exception exception)
    {
        return ScreenCaptureService.DescribeExceptionOnOneLine(exception);
    }
}
