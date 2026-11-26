using System.Runtime.InteropServices;

namespace PcmonProbe;

/// <summary>
/// A bare Win32 service host, written against the service manager API directly rather than
/// ServiceBase, so it can accept and log every notice the probe is checking for: stop,
/// pre-shutdown, shutdown and power events (suspend, resume).
/// </summary>
static class ServiceHost
{
    const string Name = "pcmon-probe";

    const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    const int SERVICE_STOPPED = 1, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    const int ACCEPT_STOP = 0x1, ACCEPT_SHUTDOWN = 0x4, ACCEPT_POWEREVENT = 0x40, ACCEPT_PRESHUTDOWN = 0x100;
    const int CONTROL_STOP = 0x1, CONTROL_INTERROGATE = 0x4, CONTROL_SHUTDOWN = 0x5,
              CONTROL_POWEREVENT = 0xD, CONTROL_PRESHUTDOWN = 0xF;
    const int PBT_APMPOWERSTATUSCHANGE = 0xA, PBT_APMRESUMEAUTOMATIC = 0x12, PBT_APMRESUMESUSPEND = 0x7,
              PBT_APMSUSPEND = 0x4, PBT_POWERSETTINGCHANGE = 0x8013;
    const int NO_ERROR = 0, ERROR_CALL_NOT_IMPLEMENTED = 120, ERROR_SERVICE_SPECIFIC_ERROR = 1066,
              ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;

    delegate void ServiceMainProc(int argc, IntPtr argv);
    delegate int HandlerExProc(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_TABLE_ENTRY
    {
        public string? ServiceName;
        public ServiceMainProc? ServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
                   ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool StartServiceCtrlDispatcherW([In] SERVICE_TABLE_ENTRY[] table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerExProc handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

    // Held in statics so the GC never collects delegates the service manager calls back into.
    static readonly ServiceMainProc MainProc = ServiceMain;
    static readonly HandlerExProc HandlerProc = Handler;
    static readonly CancellationTokenSource Cts = new();
    static readonly object StatusGate = new();
    static IntPtr _handle;
    static SERVICE_STATUS _status;

    public static int Run()
    {
        var table = new[]
        {
            new SERVICE_TABLE_ENTRY { ServiceName = Name, ServiceProc = MainProc },
            new SERVICE_TABLE_ENTRY(),
        };
        if (StartServiceCtrlDispatcherW(table)) return 0;

        int err = Marshal.GetLastPInvokeError();
        Console.Error.WriteLine(err == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT
            ? "Not started by the service manager. Install it with install.ps1, or run 'pcmon-probe.exe --console' from an elevated terminal."
            : $"StartServiceCtrlDispatcher failed: error {err}");
        return 1;
    }

    static void ServiceMain(int argc, IntPtr argv)
    {
        _handle = RegisterServiceCtrlHandlerExW(Name, HandlerProc, IntPtr.Zero);
        if (_handle == IntPtr.Zero) return;

        Log.Open(echo: false);
        SetState(SERVICE_RUNNING, ACCEPT_STOP | ACCEPT_SHUTDOWN | ACCEPT_PRESHUTDOWN | ACCEPT_POWEREVENT, 0);

        var worker = new Thread(() =>
        {
            int exitCode = 0;
            try { Probe.Run("service", Cts.Token); }
            catch (Exception ex) { Log.Line($"FATAL {ex}"); exitCode = 1; }
            Log.Line("service: reporting STOPPED");
            Log.Hard();
            SetState(SERVICE_STOPPED, 0, 0, exitCode);
        }) { Name = "probe", IsBackground = false };
        worker.Start();
    }

    static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case CONTROL_INTERROGATE:
                return NO_ERROR;

            case CONTROL_STOP:
            case CONTROL_SHUTDOWN:
            case CONTROL_PRESHUTDOWN:
                Log.Line($"NOTICE {ControlName(control)} uptime={Probe.Uptime()} -> the Reporter would send 'stopping' here");
                Log.Hard();
                SetState(SERVICE_STOP_PENDING, 0, 5000);
                Cts.Cancel();
                return NO_ERROR;

            case CONTROL_POWEREVENT:
                Log.Line($"NOTICE POWEREVENT {PowerEventName(eventType)} uptime={Probe.Uptime()}" +
                         (eventType == PBT_APMSUSPEND ? " -> the Reporter would send 'stopping' here" : ""));
                Log.Hard();
                return NO_ERROR;

            default:
                Log.Line($"NOTICE control 0x{control:X} event 0x{eventType:X} (not handled)");
                return ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    static void SetState(int state, int accepted, int waitHint, int exitCode = 0)
    {
        lock (StatusGate)
        {
            _status.ServiceType = SERVICE_WIN32_OWN_PROCESS;
            _status.CurrentState = state;
            _status.ControlsAccepted = accepted;
            _status.Win32ExitCode = exitCode == 0 ? NO_ERROR : ERROR_SERVICE_SPECIFIC_ERROR;
            _status.ServiceSpecificExitCode = exitCode;
            _status.CheckPoint = state == SERVICE_STOP_PENDING ? _status.CheckPoint + 1 : 0;
            _status.WaitHint = waitHint;
            if (!SetServiceStatus(_handle, ref _status))
                Log.Line($"SetServiceStatus({state}) failed: error {Marshal.GetLastPInvokeError()}");
        }
    }

    static string ControlName(int control) => control switch
    {
        CONTROL_STOP => "STOP",
        CONTROL_SHUTDOWN => "SHUTDOWN",
        CONTROL_PRESHUTDOWN => "PRESHUTDOWN",
        _ => $"0x{control:X}",
    };

    static string PowerEventName(int eventType) => eventType switch
    {
        PBT_APMSUSPEND => "SUSPEND",
        PBT_APMRESUMEAUTOMATIC => "RESUME_AUTOMATIC",
        PBT_APMRESUMESUSPEND => "RESUME_SUSPEND",
        PBT_APMPOWERSTATUSCHANGE => "POWER_STATUS_CHANGE",
        PBT_POWERSETTINGCHANGE => "POWER_SETTING_CHANGE",
        _ => $"0x{eventType:X}",
    };
}
