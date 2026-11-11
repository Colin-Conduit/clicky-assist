using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Clicky.Core.Audio;
using Clicky.Windows.Companion;
using Clicky.Windows.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clicky.Windows.AudioIO;

/// <summary>
/// Push-to-talk microphone capture, the Windows counterpart of the AVAudioEngine input tap in
/// BuddyDictationManager. Opens the default capture endpoint in WASAPI shared mode, converts each
/// buffer to 16 kHz mono PCM16 for AssemblyAI and reports the waveform level with the Mac's math.
///
/// Threading: all three events are raised on NAudio's capture thread while a per-session gate lock
/// is held. That lock is what lets StopCapture promise that no event fires after it returns, so
/// handlers must not block waiting on the UI thread (use Dispatcher.BeginInvoke, never Invoke),
/// or a StopCapture call on the UI thread would wait on the handler forever.
/// </summary>
public sealed class MicrophoneCaptureService : IDisposable
{
    private const string LogCategory = "Audio";

    /// <summary>
    /// TODO: tune by eye against the Mac waveform. NAudio's polling capture sleeps for half of this
    /// between reads, so each buffer (and each level update) carries about 50 ms of audio; with
    /// the Windows timer granularity that measured about 16 level updates per second. The Mac's tap
    /// asks for 1024 frames, but Apple documents the tap buffer size range as 100 to 400 ms, so the
    /// Mac waveform likely updates about every 100 ms (not measured). Because the meter's 0.72
    /// release factor applies per buffer, a faster cadence makes the waveform fall faster. 200 here
    /// would roughly match the Mac, at the cost of StopCapture blocking up to about 100 ms while it
    /// joins the capture thread.
    /// </summary>
    private const int SharedModeCaptureBufferDurationInMilliseconds = 100;

    /// <summary>
    /// NAudio calls IAudioClient::Start on its capture thread, not inside StartRecording, so an
    /// access-denied error from Start would otherwise only appear later as a stopped event. This
    /// bounds how long StartCapture waits to see whether Start succeeded. It normally takes a few
    /// milliseconds; on timeout the session is assumed healthy and any later failure goes through
    /// CaptureFailed instead.
    /// </summary>
    private static readonly TimeSpan CaptureThreadStartupTimeout = TimeSpan.FromSeconds(2);

    // HRESULTs that mean "no usable microphone". Marshal.ThrowExceptionForHR turns
    // E_ACCESSDENIED into UnauthorizedAccessException, so these are matched on Exception.HResult
    // rather than on COMException alone.
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);
    private const int AudioClientDeviceInvalidatedHResult = unchecked((int)0x88890004);

    private const string BlockedByPrivacySettingsMessage =
        "Windows privacy settings are blocking microphone access. Turn on microphone access for desktop apps in Settings, Privacy and security, Microphone.";

    private const string NoDeviceFoundMessage =
        "No microphone was found. Connect a microphone or choose one in Windows sound settings, then try again.";

    private readonly object captureLifecycleLock = new();
    private readonly AudioPowerLevelMeter audioPowerLevelMeter = new();
    private MicrophoneCaptureSession? activeCaptureSession;
    private bool isDisposed;

    /// <summary>
    /// 16 kHz, mono, signed 16-bit little-endian PCM bytes, any length per event (the AssemblyAI
    /// session regroups them into 100 ms frames). Raised on the capture thread.
    /// </summary>
    public event Action<byte[]>? Pcm16AudioAvailable;

    /// <summary>
    /// Smoothed 0..1 level from AudioPowerLevelMeter, computed from the device-rate mono samples of
    /// each buffer. Raised on the capture thread once per buffer.
    /// </summary>
    public event Action<double>? AudioPowerLevelChanged;

    /// <summary>
    /// Raised once, on the capture thread, if capture stops on its own (device unplugged, driver
    /// error). Not raised for StopCapture, and not raised when StartCapture itself throws.
    /// </summary>
    public event Action<Exception>? CaptureFailed;

    public bool IsCapturing => Volatile.Read(ref activeCaptureSession) is not null;

    /// <summary>
    /// Opens the default capture endpoint and starts capturing. No-op if already capturing.
    /// Throws MicrophoneUnavailableException (NoDeviceFound or BlockedByPrivacySettings) when
    /// Windows cannot provide a microphone; other failures are rethrown unchanged.
    /// </summary>
    public void StartCapture()
    {
        lock (captureLifecycleLock)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (IsCapturing)
            {
                return;
            }

            audioPowerLevelMeter.Reset();

            // A fresh session also means a fresh resampler, so no filter state carries over.
            MicrophoneCaptureSession newCaptureSession = OpenDefaultCaptureSession();

            Exception? captureStartupFailure = StartRecordingAndWaitForCaptureThread(newCaptureSession);
            if (captureStartupFailure is null)
            {
                ClickyApplicationLog.Write(LogCategory, $"Microphone capture started ({newCaptureSession.FormatConverter.CaptureFormatDescription}).");
                return;
            }

            DisposeSessionResources(newCaptureSession);

            MicrophoneUnavailableException? microphoneUnavailableException = TranslateToMicrophoneUnavailableException(captureStartupFailure);
            if (microphoneUnavailableException is not null)
            {
                throw microphoneUnavailableException;
            }

            ClickyApplicationLog.Write(LogCategory, $"Microphone capture failed to start: {captureStartupFailure.GetType().Name}: {captureStartupFailure.Message}");
            ExceptionDispatchInfo.Throw(captureStartupFailure);
        }
    }

    /// <summary>
    /// Idempotent and synchronous: once this returns, no more events fire for the stopped session.
    /// Safe to call from any thread, including from inside one of this service's event handlers.
    /// </summary>
    public void StopCapture()
    {
        MicrophoneCaptureSession? sessionToStop = Interlocked.Exchange(ref activeCaptureSession, null);
        if (sessionToStop is null)
        {
            return;
        }

        // Taking the gate waits for any handler that is mid-invocation, and clearing the flag
        // makes every later buffer and stop notification for this session a no-op.
        bool ownsSessionDisposal;
        lock (sessionToStop.EventDeliveryLock)
        {
            ownsSessionDisposal = sessionToStop.IsDeliveringEvents;
            sessionToStop.IsDeliveringEvents = false;
        }

        if (!ownsSessionDisposal)
        {
            // The capture thread already reported a failure and scheduled its own cleanup.
            return;
        }

        sessionToStop.WasapiCapture.StopRecording();

        if (sessionToStop.IsCurrentThreadTheCaptureThread())
        {
            // WasapiCapture.Dispose joins the capture thread, which would deadlock if a handler on
            // that same thread called StopCapture. Let a pool thread do the join instead.
            ThreadPool.QueueUserWorkItem(DisposeSessionResources, sessionToStop, preferLocal: false);
        }
        else
        {
            DisposeSessionResources(sessionToStop);
        }

        ClickyApplicationLog.Write(LogCategory, "Microphone capture stopped.");
    }

    public void Dispose()
    {
        lock (captureLifecycleLock)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
        }

        StopCapture();
    }

    private static MicrophoneCaptureSession OpenDefaultCaptureSession()
    {
        MMDeviceEnumerator? deviceEnumerator = null;
        MMDevice? captureDevice = null;
        WasapiCapture? wasapiCapture = null;

        try
        {
            deviceEnumerator = new MMDeviceEnumerator();

            // Windows always has a default capture endpoint while at least one is active, so a
            // missing default means there is no active microphone at all.
            if (!deviceEnumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                ClickyApplicationLog.Write(LogCategory, "No active microphone endpoint was found.");
                throw new MicrophoneUnavailableException(NoDeviceFoundMessage, MicrophoneAvailability.NoDeviceFound);
            }

            // Role.Console is the "Default Device" in Sound settings, the same device the Mac's
            // AVAudioEngine input node follows.
            captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

            // Activating IAudioClient and reading its mix format happen in this constructor, and
            // either can fail with access denied when privacy settings block the microphone.
            wasapiCapture = CreateWasapiCaptureWithoutSynchronizationContext(captureDevice);

            CapturedAudioFormatConverter formatConverter = new(wasapiCapture.WaveFormat);
            return new MicrophoneCaptureSession(captureDevice, wasapiCapture, formatConverter);
        }
        catch (MicrophoneUnavailableException)
        {
            DisposeQuietly(wasapiCapture);
            DisposeQuietly(captureDevice);
            throw;
        }
        catch (Exception captureOpenException)
        {
            DisposeQuietly(wasapiCapture);
            DisposeQuietly(captureDevice);

            MicrophoneUnavailableException? microphoneUnavailableException = TranslateToMicrophoneUnavailableException(captureOpenException);
            if (microphoneUnavailableException is not null)
            {
                throw microphoneUnavailableException;
            }

            ClickyApplicationLog.Write(LogCategory, $"Could not open the microphone: {captureOpenException.GetType().Name}: {captureOpenException.Message}");
            throw;
        }
        finally
        {
            DisposeQuietly(deviceEnumerator);
        }
    }

    /// <summary>
    /// NAudio's WasapiCapture remembers SynchronizationContext.Current when it is constructed and
    /// posts RecordingStopped to it. Built on the WPF UI thread, that would route the stop event
    /// through the dispatcher, which breaks "raised on the capture thread". Worse, StartCapture
    /// blocks the UI thread while it waits to learn whether startup failed, so a posted stop could
    /// not run until the wait timed out, and an access-denied error from IAudioClient::Start would
    /// arrive late as CaptureFailed instead of as an exception from StartCapture. Clearing the
    /// context for the constructor makes NAudio raise the event directly on its capture thread.
    /// </summary>
    private static WasapiCapture CreateWasapiCaptureWithoutSynchronizationContext(MMDevice captureDevice)
    {
        SynchronizationContext? callerSynchronizationContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        try
        {
            // Polling mode (useEventSync false) is what NAudio's default constructor uses. Event
            // mode would mainly lower latency, which push-to-talk does not need at this buffer size.
            return new WasapiCapture(captureDevice, useEventSync: false, SharedModeCaptureBufferDurationInMilliseconds);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(callerSynchronizationContext);
        }
    }

    /// <summary>
    /// Starts NAudio's capture thread and waits until it has either called IAudioClient::Start
    /// successfully or stopped. Returns the startup failure, or null when capture is running and
    /// the session has been published as the active one.
    /// </summary>
    private Exception? StartRecordingAndWaitForCaptureThread(MicrophoneCaptureSession captureSession)
    {
        captureSession.WasapiCapture.DataAvailable += (captureSender, capturedAudioArguments) =>
            HandleCapturedAudioBuffer(captureSession, capturedAudioArguments);

        captureSession.WasapiCapture.RecordingStopped += (captureSender, recordingStoppedArguments) =>
            HandleRecordingStopped(captureSession, recordingStoppedArguments);

        lock (captureSession.EventDeliveryLock)
        {
            captureSession.IsDeliveringEvents = true;
        }

        try
        {
            // IAudioClient::Initialize runs synchronously in here.
            captureSession.WasapiCapture.StartRecording();
        }
        catch (Exception startRecordingException)
        {
            lock (captureSession.EventDeliveryLock)
            {
                captureSession.IsDeliveringEvents = false;
            }

            return startRecordingException;
        }

        Stopwatch captureStartupStopwatch = Stopwatch.StartNew();
        while (captureSession.WasapiCapture.CaptureState != CaptureState.Capturing
            && !captureSession.HasRecordingStopped
            && captureStartupStopwatch.Elapsed < CaptureThreadStartupTimeout)
        {
            Thread.Sleep(1);
        }

        lock (captureSession.EventDeliveryLock)
        {
            // Decided under the gate so exactly one of two things happens: StartCapture reports
            // the failure (stop seen here first), or CaptureFailed does (startup marked finished
            // first). Never both, never neither.
            if (captureSession.HasRecordingStopped)
            {
                captureSession.IsDeliveringEvents = false;
                return captureSession.RecordingStoppedException
                    ?? new InvalidOperationException("The microphone stopped while it was starting.");
            }

            if (captureSession.WasapiCapture.CaptureState != CaptureState.Capturing)
            {
                ClickyApplicationLog.Write(LogCategory, "Microphone capture thread did not confirm startup in time. Continuing; a later failure will be reported.");
            }

            captureSession.HasFinishedStartup = true;
            Volatile.Write(ref activeCaptureSession, captureSession);
        }

        return null;
    }

    private void HandleCapturedAudioBuffer(MicrophoneCaptureSession captureSession, WaveInEventArgs capturedAudioArguments)
    {
        captureSession.RememberCurrentThreadAsCaptureThread();

        // NAudio raises DataAvailable with zero bytes on every poll that found no packets.
        if (capturedAudioArguments.BytesRecorded <= 0)
        {
            return;
        }

        lock (captureSession.EventDeliveryLock)
        {
            if (!captureSession.IsDeliveringEvents)
            {
                return;
            }

            // NAudio reuses its record buffer for the next read, so everything is converted here,
            // inside the handler, into arrays this service owns.
            ReadOnlySpan<float> monoSamples = captureSession.FormatConverter.ConvertToMonoSamples(
                capturedAudioArguments.Buffer,
                capturedAudioArguments.BytesRecorded);

            if (monoSamples.Length == 0)
            {
                return;
            }

            // The Mac computes the level from the device-rate samples before resampling.
            double smoothedAudioPowerLevel = audioPowerLevelMeter.ApplySamples(monoSamples);
            byte[] pcm16AudioBytes = captureSession.FormatConverter.ConvertMonoSamplesToTargetPcm16(monoSamples);

            // Same order as the Mac tap: hand the audio to transcription, then update the level.
            if (pcm16AudioBytes.Length > 0)
            {
                RaisePcm16AudioAvailable(pcm16AudioBytes);
            }

            // An audio subscriber may have called StopCapture on this thread (the gate lock is
            // reentrant), and nothing may be raised once StopCapture has returned.
            if (!captureSession.IsDeliveringEvents)
            {
                return;
            }

            RaiseAudioPowerLevelChanged(smoothedAudioPowerLevel);
        }
    }

    private void HandleRecordingStopped(MicrophoneCaptureSession captureSession, StoppedEventArgs recordingStoppedArguments)
    {
        captureSession.RememberCurrentThreadAsCaptureThread();
        captureSession.RecordingStoppedException = recordingStoppedArguments.Exception;
        captureSession.HasRecordingStopped = true;

        bool ownsSessionDisposal = false;

        lock (captureSession.EventDeliveryLock)
        {
            // A stop requested through StopCapture cleared IsDeliveringEvents first, so only a
            // stop nobody asked for (unplugged device, driver error) reaches the failure path.
            // During startup StartCapture reports the failure itself.
            if (captureSession.IsDeliveringEvents && captureSession.HasFinishedStartup)
            {
                captureSession.IsDeliveringEvents = false;
                Interlocked.CompareExchange(ref activeCaptureSession, null, captureSession);
                ownsSessionDisposal = true;

                Exception captureFailure = recordingStoppedArguments.Exception
                    ?? new InvalidOperationException("Microphone capture stopped unexpectedly.");

                ClickyApplicationLog.Write(LogCategory, $"Microphone capture stopped unexpectedly: {captureFailure.GetType().Name}: {captureFailure.Message}");
                RaiseCaptureFailed(captureFailure);
            }
        }

        if (ownsSessionDisposal)
        {
            // This is the capture thread, so it must not join itself through Dispose.
            ThreadPool.QueueUserWorkItem(DisposeSessionResources, captureSession, preferLocal: false);
        }
    }

    private void RaisePcm16AudioAvailable(byte[] pcm16AudioBytes)
    {
        try
        {
            Pcm16AudioAvailable?.Invoke(pcm16AudioBytes);
        }
        catch (Exception subscriberException)
        {
            // A throwing subscriber would otherwise end NAudio's capture loop.
            ClickyApplicationLog.Write(LogCategory, $"A microphone audio subscriber threw {subscriberException.GetType().Name}: {subscriberException.Message}");
        }
    }

    private void RaiseAudioPowerLevelChanged(double smoothedAudioPowerLevel)
    {
        try
        {
            AudioPowerLevelChanged?.Invoke(smoothedAudioPowerLevel);
        }
        catch (Exception subscriberException)
        {
            ClickyApplicationLog.Write(LogCategory, $"A microphone level subscriber threw {subscriberException.GetType().Name}: {subscriberException.Message}");
        }
    }

    private void RaiseCaptureFailed(Exception captureFailure)
    {
        try
        {
            CaptureFailed?.Invoke(captureFailure);
        }
        catch (Exception subscriberException)
        {
            // NAudio raises RecordingStopped outside its own try block, so an exception escaping
            // here would be unhandled on the capture thread and end the process.
            ClickyApplicationLog.Write(LogCategory, $"A capture failure subscriber threw {subscriberException.GetType().Name}: {subscriberException.Message}");
        }
    }

    private static MicrophoneUnavailableException? TranslateToMicrophoneUnavailableException(Exception captureException)
    {
        switch (captureException.HResult)
        {
            case AccessDeniedHResult:
                ClickyApplicationLog.Write(LogCategory, "Windows denied microphone access (privacy settings).");
                return new MicrophoneUnavailableException(BlockedByPrivacySettingsMessage, MicrophoneAvailability.BlockedByPrivacySettings, captureException);

            case ElementNotFoundHResult:
            case AudioClientDeviceInvalidatedHResult:
                ClickyApplicationLog.Write(LogCategory, $"The microphone disappeared while opening it (HRESULT 0x{captureException.HResult:X8}).");
                return new MicrophoneUnavailableException(NoDeviceFoundMessage, MicrophoneAvailability.NoDeviceFound, captureException);

            default:
                return null;
        }
    }

    private static void DisposeSessionResources(MicrophoneCaptureSession captureSession)
    {
        // WasapiCapture.Dispose stops the stream, joins the capture thread and releases the
        // IAudioClient, which is what turns off the microphone-in-use indicator in the taskbar.
        DisposeQuietly(captureSession.WasapiCapture);
        DisposeQuietly(captureSession.CaptureDevice);
    }

    private static void DisposeQuietly(IDisposable? disposableResource)
    {
        if (disposableResource is null)
        {
            return;
        }

        try
        {
            disposableResource.Dispose();
        }
        catch (Exception disposeException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Releasing {disposableResource.GetType().Name} threw {disposeException.GetType().Name}: {disposeException.Message}");
        }
    }

    /// <summary>Everything that belongs to one StartCapture call.</summary>
    private sealed class MicrophoneCaptureSession
    {
        private const int UnknownCaptureThreadIdentifier = 0;

        private int captureThreadManagedIdentifier = UnknownCaptureThreadIdentifier;
        private volatile bool hasRecordingStopped;
        private volatile Exception? recordingStoppedException;

        public MicrophoneCaptureSession(MMDevice captureDevice, WasapiCapture wasapiCapture, CapturedAudioFormatConverter formatConverter)
        {
            CaptureDevice = captureDevice;
            WasapiCapture = wasapiCapture;
            FormatConverter = formatConverter;
        }

        public MMDevice CaptureDevice { get; }

        public WasapiCapture WasapiCapture { get; }

        /// <summary>Only touched on the capture thread, inside the gate.</summary>
        public CapturedAudioFormatConverter FormatConverter { get; }

        /// <summary>Held while events are raised, and while delivery is switched off.</summary>
        public object EventDeliveryLock { get; } = new();

        /// <summary>Guarded by EventDeliveryLock. Whoever flips it from true to false owns disposal.</summary>
        public bool IsDeliveringEvents { get; set; }

        /// <summary>Guarded by EventDeliveryLock.</summary>
        public bool HasFinishedStartup { get; set; }

        public bool HasRecordingStopped
        {
            get => hasRecordingStopped;
            set => hasRecordingStopped = value;
        }

        public Exception? RecordingStoppedException
        {
            get => recordingStoppedException;
            set => recordingStoppedException = value;
        }

        public void RememberCurrentThreadAsCaptureThread()
        {
            Volatile.Write(ref captureThreadManagedIdentifier, Environment.CurrentManagedThreadId);
        }

        /// <summary>
        /// Handlers record the capture thread before raising anything, so a StopCapture call made
        /// from inside a handler is always recognized here.
        /// </summary>
        public bool IsCurrentThreadTheCaptureThread()
        {
            return Volatile.Read(ref captureThreadManagedIdentifier) == Environment.CurrentManagedThreadId;
        }
    }
}
