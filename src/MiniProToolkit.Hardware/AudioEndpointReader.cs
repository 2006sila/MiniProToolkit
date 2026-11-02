using System.Runtime.InteropServices;
using MiniProToolkit.Core;

namespace MiniProToolkit.Hardware;

/// <summary>
/// Read-only default-endpoint audio status: master volume and mute of the default
/// playback device plus the mute state of the default recording device.
/// </summary>
public sealed record AudioEndpointReadResult(AudioVolumeReading Output, AudioMuteReading Microphone)
{
    public static AudioEndpointReadResult Unavailable(string? status) =>
        new(AudioVolumeReading.Unavailable(status), AudioMuteReading.Unavailable(status));

    /// <summary>True only when both endpoints returned a real read-only value.</summary>
    public bool IsAvailable => Output.IsAvailable && Microphone.IsAvailable;
}

/// <summary>
/// Minimal CoreAudio reader/controller for the two default endpoints
/// (<c>MMDeviceEnumerator.GetDefaultAudioEndpoint</c> → <c>IMMDevice.Activate</c> →
/// <c>IAudioEndpointVolume</c>). Read operations use the read-only projection; explicit
/// control operations use a separate full projection whose vtable positions are kept
/// exact. No write runs during a read or availability probe.
/// <para>
/// Threading model: all COM work runs on <b>one</b> process-wide, long-lived MTA
/// worker thread that is created lazily on the first read. A read only enqueues one
/// request into a depth-1 single-flight slot and waits at most three seconds for the
/// answer. Because the slot is taken while a request is outstanding, an overlapping
/// or repeated call returns an unavailable reading immediately instead of creating a
/// second thread.
/// </para>
/// <para>
/// A CoreAudio call cannot be cancelled, so a call that exceeds the 3 s budget is
/// abandoned rather than aborted: the caller degrades to an unavailable reading,
/// the worker stays occupied until that call finally returns (then the slot is
/// released and the next read may run), and no further thread is ever created. If
/// the worker thread itself is gone, reads are fused to unavailable for the rest of
/// the process instead of spawning replacements, so a hang can never accumulate
/// threads or COM references.
/// </para>
/// <para>
/// Every attempt creates its COM objects locally on the worker thread and releases
/// them before the next request is dequeued: nothing is cached across requests, no
/// event/notification callbacks are registered, and the endpoint objects never
/// outlive a single call. Any failure (no default device, endpoint without a volume
/// interface, COM error, timeout, busy worker) degrades to an unavailable reading
/// with a redacted status instead of an exception. Device names/IDs are never read,
/// so they cannot leak into diagnostics.
/// </para>
/// </summary>
public sealed class AudioEndpointReader
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    private const int MaximumStatusLength = 160;

    private const string TimeoutStatus = "CoreAudio 只读读取超时（未返回结果）。";
    private const string NoResultStatus = "CoreAudio 只读读取未返回结果。";
    private const string BusyStatus = "CoreAudio 只读读取仍在进行，本次未发起新调用（上一次调用可能尚未返回）。";
    private const string WorkerStartStatus = "CoreAudio 只读读取线程启动失败。";
    private const string WorkerFaultedStatus = "CoreAudio 只读读取线程已不可用，后续读取按不可用处理。";
    private const string EnumeratorStatus = "CoreAudio 端点枚举器不可用";

    private const int DataFlowRender = 0;
    private const int DataFlowCapture = 1;
    private const int RoleConsole = 0;
    private const int ClsCtxAll = 23;

    /// <summary>
    /// The one MTA worker shared by every reader instance (and therefore by every
    /// capability probe). Static on purpose: per-instance workers would reintroduce
    /// one thread per probe whenever a call hangs.
    /// </summary>
    private static readonly CoreAudioWorker SharedWorker = new();
    private static readonly SingleFlightOperationWorker SharedControlWorker = new(
        "MiniProToolkit-CoreAudio-Control", thread => thread.SetApartmentState(ApartmentState.MTA));

    /// <summary>
    /// Reads default output volume/mute and default microphone mute. Blocking (at
    /// most <see cref="ReadTimeout"/>): the COM work happens on the shared worker
    /// thread, so call this from a background thread (it never touches the UI
    /// thread itself).
    /// </summary>
    public AudioEndpointReadResult Read() => SharedWorker.Read(ReadTimeout);

    /// <summary>Checks the default endpoints without writing any value.</summary>
    public SystemControlAvailabilitySnapshot ProbeControls() => SharedControlWorker.Execute(
        ProbeControlsCore,
        SystemControlAvailabilitySnapshot.Unavailable("CoreAudio 写入控制探测不可用。"),
        ReadTimeout);

    /// <summary>Writes output volume only after the caller explicitly requests it.</summary>
    public SystemControlWriteResult SetOutputVolume(int percent)
    {
        if (percent is < 0 or > 100)
            return SystemControlVerification.VerifyOutputVolume(percent, AudioVolumeReading.Unavailable());
        var deadline = Environment.TickCount64 + (long)ReadTimeout.TotalMilliseconds;
        return SharedControlWorker.Execute(
            () => SetOutputVolumeCore(percent, deadline),
            new SystemControlWriteResult("output-volume", SystemControlWriteState.Unconfirmed,
                "CoreAudio 操作超时、忙碌或不可用；未确认成功，调用可能仍在完成，不会自动重试。", RequestedPercent: percent),
            ReadTimeout);
    }

    /// <summary>Writes microphone mute only after the caller explicitly requests it.</summary>
    public SystemControlWriteResult SetMicrophoneMute(bool muted)
    {
        var deadline = Environment.TickCount64 + (long)ReadTimeout.TotalMilliseconds;
        return SharedControlWorker.Execute(
            () => SetMicrophoneMuteCore(muted, deadline),
            new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Unconfirmed,
                "CoreAudio 操作超时、忙碌或不可用；未确认成功，调用可能仍在完成，不会自动重试。", RequestedMuted: muted),
            ReadTimeout);
    }

    private static SystemControlAvailabilitySnapshot ProbeControlsCore()
    {
        object? enumeratorObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            return new SystemControlAvailabilitySnapshot(
                ProbeControl(enumerator, DataFlowRender, "输出音量"),
                ProbeControl(enumerator, DataFlowCapture, "麦克风静音"),
                SystemControlAvailability.Unavailable("CoreAudio 不提供屏幕亮度控制。"));
        }
        catch (Exception ex)
        {
            var status = Sanitize(ex);
            return SystemControlAvailabilitySnapshot.Unavailable(status);
        }
        finally
        {
            ReleaseComObject(enumeratorObject);
        }
    }

    private static SystemControlAvailability ProbeControl(IMMDeviceEnumerator enumerator, int dataFlow, string name)
    {
        var volume = TryOpenDefaultEndpointVolumeControl(enumerator, dataFlow, name, out var error);
        if (volume is null)
            return SystemControlAvailability.Unavailable(error);
        try
        {
            if (dataFlow == DataFlowRender)
            {
                var hr = volume.GetMasterVolumeLevelScalar(out var scalar);
                if (hr < 0 || !AudioVolumeReading.FromScalar(scalar, false).IsAvailable)
                    return SystemControlAvailability.Unavailable("默认输出音量不可读，写入保持禁用。");
            }
            return volume.GetMute(out _) < 0
                ? SystemControlAvailability.Unavailable($"默认{name}状态不可读，写入保持禁用。")
                : SystemControlAvailability.Available($"默认{name}控制接口可读；尚未执行写入验收。");
        }
        catch (Exception ex)
        {
            return SystemControlAvailability.Unavailable(Sanitize(ex));
        }
        finally
        {
            ReleaseComObject(volume);
        }
    }

    private static SystemControlWriteResult SetOutputVolumeCore(int percent, long deadline)
    {
        if (percent is < 0 or > 100)
            return new SystemControlWriteResult("output-volume", SystemControlWriteState.Failed,
                "目标输出音量超出 0-100 范围，未执行写入。", RequestedPercent: percent);

        object? enumeratorObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            var volume = TryOpenDefaultEndpointVolumeControl(enumerator, DataFlowRender, "输出", out var error);
            if (volume is null)
                return new SystemControlWriteResult("output-volume", SystemControlWriteState.Unavailable,
                    error, RequestedPercent: percent);

            try
            {
                if (volume.GetMasterVolumeLevelScalar(out var before) < 0 ||
                    !AudioVolumeReading.FromScalar(before, false).IsAvailable || volume.GetMute(out _) < 0)
                    return new SystemControlWriteResult("output-volume", SystemControlWriteState.Unavailable,
                        "默认输出音量不可读，未执行写入。", RequestedPercent: percent);
                if (Environment.TickCount64 >= deadline)
                    return new SystemControlWriteResult("output-volume", SystemControlWriteState.Unconfirmed,
                        "写入前已超过操作预算，未发起音量写入。", RequestedPercent: percent);
                var hr = volume.SetMasterVolumeLevelScalar(percent / 100f, IntPtr.Zero);
                if (hr < 0)
                    return new SystemControlWriteResult("output-volume", SystemControlWriteState.Failed,
                        $"默认输出音量写入失败{FormatHResult(hr)}。", RequestedPercent: percent);

                hr = volume.GetMasterVolumeLevelScalar(out var scalar);
                if (hr < 0)
                    return new SystemControlWriteResult("output-volume", SystemControlWriteState.Unconfirmed,
                        $"默认输出音量写入后读回失败{FormatHResult(hr)}，未确认成功。", RequestedPercent: percent);
                hr = volume.GetMute(out var muted);
                if (hr < 0)
                    return new SystemControlWriteResult("output-volume", SystemControlWriteState.Unconfirmed,
                        $"默认输出音量写入后静音状态读回失败{FormatHResult(hr)}，未确认成功。", RequestedPercent: percent);

                return SystemControlVerification.VerifyOutputVolume(
                    percent, AudioVolumeReading.FromScalar(scalar, muted));
            }
            catch (Exception ex)
            {
                return new SystemControlWriteResult("output-volume", SystemControlWriteState.Failed,
                    Sanitize(ex), RequestedPercent: percent);
            }
            finally
            {
                ReleaseComObject(volume);
            }
        }
        catch (Exception ex)
        {
            return new SystemControlWriteResult("output-volume", SystemControlWriteState.Failed,
                Sanitize(ex), RequestedPercent: percent);
        }
        finally
        {
            ReleaseComObject(enumeratorObject);
        }
    }

    private static SystemControlWriteResult SetMicrophoneMuteCore(bool muted, long deadline)
    {
        object? enumeratorObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            var volume = TryOpenDefaultEndpointVolumeControl(enumerator, DataFlowCapture, "输入", out var error);
            if (volume is null)
                return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Unavailable,
                    error, RequestedMuted: muted);

            try
            {
                if (volume.GetMute(out _) < 0)
                    return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Unavailable,
                        "默认麦克风静音不可读，未执行写入。", RequestedMuted: muted);
                if (Environment.TickCount64 >= deadline)
                    return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Unconfirmed,
                        "写入前已超过操作预算，未发起静音写入。", RequestedMuted: muted);
                var hr = volume.SetMute(muted, IntPtr.Zero);
                if (hr < 0)
                    return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Failed,
                        $"默认输入静音写入失败{FormatHResult(hr)}。", RequestedMuted: muted);
                hr = volume.GetMute(out var readback);
                if (hr < 0)
                    return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Unconfirmed,
                        $"默认输入静音写入后读回失败{FormatHResult(hr)}，未确认成功。", RequestedMuted: muted);
                return SystemControlVerification.VerifyMicrophoneMute(muted, AudioMuteReading.FromMute(readback));
            }
            catch (Exception ex)
            {
                return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Failed,
                    Sanitize(ex), RequestedMuted: muted);
            }
            finally
            {
                ReleaseComObject(volume);
            }
        }
        catch (Exception ex)
        {
            return new SystemControlWriteResult("microphone-mute", SystemControlWriteState.Failed,
                Sanitize(ex), RequestedMuted: muted);
        }
        finally
        {
            ReleaseComObject(enumeratorObject);
        }
    }

    private static AudioEndpointReadResult ReadCore()
    {
        object? enumeratorObject = null;
        IMMDeviceEnumerator? enumerator;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            enumerator = (IMMDeviceEnumerator)enumeratorObject;
        }
        catch (Exception ex)
        {
            ReleaseComObject(enumeratorObject);
            return AudioEndpointReadResult.Unavailable($"{EnumeratorStatus}{FormatHResult(ex.GetBaseException().HResult)}。");
        }

        try
        {
            return new AudioEndpointReadResult(ReadOutput(enumerator), ReadMicrophone(enumerator));
        }
        finally
        {
            ReleaseComObject(enumeratorObject);
        }
    }

    private static AudioVolumeReading ReadOutput(IMMDeviceEnumerator enumerator)
    {
        var volume = TryOpenDefaultEndpointVolume(enumerator, DataFlowRender, "输出", out var error);
        if (volume is null)
            return AudioVolumeReading.Unavailable(error);

        try
        {
            var hr = volume.GetMasterVolumeLevelScalar(out var scalar);
            if (hr < 0)
                return AudioVolumeReading.Unavailable($"读取默认输出音量失败{FormatHResult(hr)}。");

            hr = volume.GetMute(out var muted);
            return hr < 0
                ? AudioVolumeReading.Unavailable($"读取默认输出静音状态失败{FormatHResult(hr)}。")
                : AudioVolumeReading.FromScalar(scalar, muted);
        }
        catch (Exception ex)
        {
            return AudioVolumeReading.Unavailable(Sanitize(ex));
        }
        finally
        {
            ReleaseComObject(volume);
        }
    }

    private static IAudioEndpointVolumeControl? TryOpenDefaultEndpointVolumeControl(
        IMMDeviceEnumerator enumerator,
        int dataFlow,
        string flowName,
        out string error)
    {
        IMMDevice? device = null;
        object? activated = null;
        IAudioEndpointVolumeControl? volume = null;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(dataFlow, RoleConsole, out device);
            if (hr < 0 || device is null)
            {
                error = $"没有可用的默认{flowName}设备{FormatHResult(hr)}。";
                return null;
            }

            var iid = typeof(IAudioEndpointVolumeControl).GUID;
            hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activated);
            if (hr < 0 || activated is null)
            {
                error = $"默认{flowName}设备未暴露音量控制接口{FormatHResult(hr)}。";
                return null;
            }

            volume = activated as IAudioEndpointVolumeControl;
            if (volume is null)
            {
                error = $"默认{flowName}设备的音量控制接口与定义不匹配。";
                return null;
            }

            error = string.Empty;
            return volume;
        }
        catch (Exception ex)
        {
            error = Sanitize(ex);
            return null;
        }
        finally
        {
            ReleaseComObject(device);
            if (volume is null)
                ReleaseComObject(activated);
        }
    }

    private static AudioMuteReading ReadMicrophone(IMMDeviceEnumerator enumerator)
    {
        var volume = TryOpenDefaultEndpointVolume(enumerator, DataFlowCapture, "输入", out var error);
        if (volume is null)
            return AudioMuteReading.Unavailable(error);

        try
        {
            var hr = volume.GetMute(out var muted);
            return hr < 0
                ? AudioMuteReading.Unavailable($"读取默认输入静音状态失败{FormatHResult(hr)}。")
                : AudioMuteReading.FromMute(muted);
        }
        catch (Exception ex)
        {
            return AudioMuteReading.Unavailable(Sanitize(ex));
        }
        finally
        {
            ReleaseComObject(volume);
        }
    }

    /// <summary>
    /// Opens the activated volume interface of the default endpoint for one data flow.
    /// The intermediate <c>IMMDevice</c> is released before returning; the returned
    /// interface belongs to the caller and must be released by it.
    /// </summary>
    private static IAudioEndpointVolumeReadOnly? TryOpenDefaultEndpointVolume(
        IMMDeviceEnumerator enumerator,
        int dataFlow,
        string flowName,
        out string error)
    {
        IMMDevice? device = null;
        object? activated = null;
        IAudioEndpointVolumeReadOnly? volume = null;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(dataFlow, RoleConsole, out device);
            if (hr < 0 || device is null)
            {
                error = $"没有可用的默认{flowName}设备{FormatHResult(hr)}。";
                return null;
            }

            var iid = typeof(IAudioEndpointVolumeReadOnly).GUID;
            hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activated);
            if (hr < 0 || activated is null)
            {
                error = $"默认{flowName}设备未暴露音量接口{FormatHResult(hr)}。";
                return null;
            }

            volume = activated as IAudioEndpointVolumeReadOnly;
            if (volume is null)
            {
                error = $"默认{flowName}设备的音量接口与只读定义不匹配。";
                return null;
            }

            error = string.Empty;
            return volume;
        }
        catch (Exception ex)
        {
            error = Sanitize(ex);
            return null;
        }
        finally
        {
            ReleaseComObject(device);
            if (volume is null)
                ReleaseComObject(activated);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
            return;
        try
        {
            if (Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch
        {
            // Best effort only: this reader is optional and must never throw out of the
            // read call, not even while dropping a COM reference.
        }
    }

    private static string FormatHResult(int hresult) =>
        hresult < 0 ? $"（HRESULT 0x{hresult:X8}）" : string.Empty;

    private static string Sanitize(Exception exception)
    {
        var inner = exception.GetBaseException();
        // Provider exception messages can contain endpoint IDs/names. Never include
        // them, even after path redaction: fixed text plus HRESULT is sufficient.
        var text = $"CoreAudio 操作失败{FormatHResult(inner.HResult)}。";
        return Sanitize(text);
    }

    private static string Sanitize(string text)
    {
        var redacted = DiagnosticRedactor.Redact(text);
        return redacted.Length <= MaximumStatusLength
            ? redacted
            : redacted[..MaximumStatusLength] + "…";
    }

    /// <summary>
    /// The single-flight dispatcher around the shared MTA worker thread.
    /// <para>
    /// Invariants, all guarded by <see cref="_gate"/>: at most one request is
    /// outstanding (<see cref="_active"/>), the queue holds at most one entry
    /// (<see cref="_pending"/>), and the worker thread is created at most once
    /// (<see cref="_thread"/>).
    /// </para>
    /// </summary>
    private sealed class CoreAudioWorker
    {
        private readonly object _gate = new();

        /// <summary>Worker thread; created on first use and never replaced.</summary>
        private Thread? _thread;

        /// <summary>Depth-1 queue slot for the request the worker has not dequeued yet.</summary>
        private ReadRequest? _pending;

        /// <summary>A request is executing (or was abandoned after its timeout) on the worker.</summary>
        private bool _active;

        /// <summary>The worker is gone or could not be started; reads are fused to unavailable.</summary>
        private bool _faulted;

        public AudioEndpointReadResult Read(TimeSpan timeout)
        {
            ReadRequest request;

            lock (_gate)
            {
                // A dead worker is never replaced: that is exactly the thread/COM leak
                // this class exists to prevent.
                if (_thread is { IsAlive: false })
                    _faulted = true;

                if (_faulted)
                    return AudioEndpointReadResult.Unavailable(WorkerFaultedStatus);

                // Single flight: while a call is running (or abandoned after timeout) no
                // second request is queued and no second thread is created.
                if (_active)
                    return AudioEndpointReadResult.Unavailable(BusyStatus);

                if (_thread is null && !TryStartWorkerLocked())
                {
                    _faulted = true;
                    return AudioEndpointReadResult.Unavailable(WorkerStartStatus);
                }

                request = new ReadRequest();
                _pending = request;
                _active = true;
                Monitor.Pulse(_gate);
            }

            if (!request.Wait(timeout))
            {
                // The COM call cannot be cancelled. The request is abandoned: the worker
                // keeps the slot until the call returns, and every read in between gets
                // the busy-unavailable reading above. No thread is created meanwhile.
                return AudioEndpointReadResult.Unavailable(TimeoutStatus);
            }

            var result = request.Result;
            request.Dispose();
            return result;
        }

        /// <summary>Starts the MTA worker. Called with <see cref="_gate"/> held.</summary>
        private bool TryStartWorkerLocked()
        {
            try
            {
                var thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "MiniProToolkit-CoreAudio-Read"
                };
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();
                _thread = thread;
                return true;
            }
            catch
            {
                _thread = null;
                return false;
            }
        }

        /// <summary>
        /// Worker loop. Executes one queued request at a time; a request abandoned by a
        /// timed-out caller still finishes here (its result is simply dropped).
        /// </summary>
        private void Run()
        {
            while (true)
            {
                try
                {
                    ReadRequest request;
                    lock (_gate)
                    {
                        while (_pending is null)
                            Monitor.Wait(_gate);

                        request = _pending;
                        _pending = null;
                    }

                    // Fresh COM objects per request; nothing survives across iterations.
                    var result = ExecuteRead();

                    // Release the single-flight slot before publishing, so the next read is
                    // admitted by an idle worker rather than racing the completion signal.
                    lock (_gate)
                    {
                        _active = false;
                    }

                    request.Complete(result);
                }
                catch
                {
                    // Defensive: a worker-side fault must never kill the loop (that would
                    // fuse the reader) and must never leave the slot taken.
                    lock (_gate)
                    {
                        _pending = null;
                        _active = false;
                    }
                }
            }
        }

        private static AudioEndpointReadResult ExecuteRead()
        {
            try
            {
                return ReadCore();
            }
            catch (Exception ex)
            {
                return AudioEndpointReadResult.Unavailable(Sanitize(ex));
            }
        }
    }

    /// <summary>One dispatched read: a completion signal plus the published result.</summary>
    private sealed class ReadRequest : IDisposable
    {
        private readonly ManualResetEventSlim _completed = new(false);
        private AudioEndpointReadResult? _result;

        public AudioEndpointReadResult Result =>
            Volatile.Read(ref _result) ?? AudioEndpointReadResult.Unavailable(NoResultStatus);

        /// <summary>Waits for completion; false means the timeout elapsed first.</summary>
        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);

        /// <summary>Publishes the result. May run after the caller already gave up.</summary>
        public void Complete(AudioEndpointReadResult result)
        {
            Volatile.Write(ref _result, result);
            try
            {
                _completed.Set();
            }
            catch (ObjectDisposedException)
            {
                // The caller completed normally and disposed the signal; nothing to wake.
            }
        }

        public void Dispose() => _completed.Dispose();
    }

    /// <summary><c>MMDeviceEnumerator</c> coclass; instantiated only inside <see cref="ReadCore"/>.</summary>
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    /// <summary>
    /// <c>IMMDeviceEnumerator</c> projection. Only <c>GetDefaultAudioEndpoint</c>
    /// (vtable slot 4) is called; the placeholder before it keeps the offset correct
    /// and is never invoked.
    /// </summary>
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // EnumAudioEndpoints: deliberately not projected (unused).
        [PreserveSig]
        int ReservedEnumAudioEndpoints();

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            int dataFlow,
            int role,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice? device);
    }

    /// <summary>
    /// <c>IMMDevice</c> projection. Only <c>Activate</c> (vtable slot 3, the first
    /// interface method) is called.
    /// </summary>
    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            int clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    /// <summary>
    /// Read-only projection of <c>IAudioEndpointVolume</c>. Only the two read slots are
    /// declared as callable methods:
    /// <list type="bullet">
    /// <item><c>GetMasterVolumeLevelScalar</c> — COM slot index 6.</item>
    /// <item><c>GetMute</c> — COM slot index 12.</item>
    /// </list>
    /// Every other slot (including all <c>Set*</c> and the notification registration
    /// entry points) is present only as an unnamed placeholder so the vtable offsets
    /// stay correct. This projection is read-only; explicit writes use the separate
    /// full control projection below.
    /// </summary>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeReadOnly
    {
        // 0: RegisterControlChangeNotify — placeholder, never called.
        [PreserveSig]
        int ReservedSlot0();

        // 1: UnregisterControlChangeNotify — placeholder, never called.
        [PreserveSig]
        int ReservedSlot1();

        // 2: GetChannelCount — placeholder, never called.
        [PreserveSig]
        int ReservedSlot2();

        // 3: SetMasterVolumeLevel — placeholder, never called.
        [PreserveSig]
        int ReservedSlot3();

        // 4: SetMasterVolumeLevelScalar — placeholder, never called.
        [PreserveSig]
        int ReservedSlot4();

        // 5: GetMasterVolumeLevel — placeholder, never called.
        [PreserveSig]
        int ReservedSlot5();

        /// <summary>6: <c>HRESULT GetMasterVolumeLevelScalar(float *pfLevel)</c>.</summary>
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        // 7: SetChannelVolumeLevel — placeholder, never called.
        [PreserveSig]
        int ReservedSlot7();

        // 8: SetChannelVolumeLevelScalar — placeholder, never called.
        [PreserveSig]
        int ReservedSlot8();

        // 9: GetChannelVolumeLevel — placeholder, never called.
        [PreserveSig]
        int ReservedSlot9();

        // 10: GetChannelVolumeLevelScalar — placeholder, never called.
        [PreserveSig]
        int ReservedSlot10();

        // 11: SetMute — placeholder, never called.
        [PreserveSig]
        int ReservedSlot11();

        /// <summary>12: <c>HRESULT GetMute(BOOL *pbMute)</c>.</summary>
        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    /// <summary>
    /// Full control projection of IAudioEndpointVolume. The declarations before each
    /// callable method are retained so SetMasterVolumeLevelScalar (slot 4),
    /// GetMasterVolumeLevelScalar (slot 6), SetMute (slot 11), and GetMute (slot 12)
    /// cannot drift to a different COM vtable position. No method is inferred or
    /// invoked outside these documented CoreAudio signatures. Verified against Microsoft
    /// win32metadata generation/WinSDK/RecompiledIdlHeaders/um/endpointvolume.h:
    /// https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/endpointvolume.h
    /// Slot numbers here exclude IUnknown's three entries (absolute slots 7/9/14/15).
    /// LPCGUID is a nullable native pointer (IntPtr.Zero); BOOL is a 32-bit Win32 BOOL.
    /// </summary>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeControl
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(IntPtr eventContext);
        [PreserveSig] int VolumeStepDown(IntPtr eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimumDb, out float maximumDb, out float incrementDb);
    }
}
