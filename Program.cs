using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VdoNinjaStreamer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            using var supervisor = new Supervisor();
            using var window = new SupervisorWindow(supervisor);
            Application.Run(window);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                error.Message,
                "VDO-Ninja — não foi possível iniciar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

internal sealed class Supervisor : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly IntPtr _job;
    private readonly IntPtr _panelProcessHandle;
    private readonly Process _panel;
    private bool _disposed;

    public string PanelUrl => "http://127.0.0.1:8765/";

    public Supervisor()
    {
        var root = AppContext.BaseDirectory;
        var panelPath = Path.Combine(root, "_app", "painel-transmissao.exe");
        if (!File.Exists(panelPath))
            throw new FileNotFoundException("Painel portátil não encontrado.", panelPath);

        _job = CreateKillOnCloseJob();
        Environment.SetEnvironmentVariable("VDO_NINJA_SUPERVISED", "1");
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VDO-Ninja-Streamer-Compiled");
        Directory.CreateDirectory(dataDirectory);
        Environment.SetEnvironmentVariable("VDO_NINJA_DATA_DIR", dataDirectory);

        var commandLine = new StringBuilder($"\"{panelPath}\"");
        var startup = new NativeMethods.STARTUPINFO
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFO>()
        };

        if (!NativeMethods.CreateProcess(
                panelPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                IntPtr.Zero,
                root,
                ref startup,
                out var processInfo))
        {
            CloseJob(_job);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui iniciar o painel portátil.");
        }

        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_job, processInfo.hProcess))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui proteger o painel.");

            if (NativeMethods.ResumeThread(processInfo.hThread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui iniciar o painel protegido.");

            _panelProcessHandle = processInfo.hProcess;
            NativeMethods.CloseHandle(processInfo.hThread);
            _panel = Process.GetProcessById((int)processInfo.dwProcessId);
        }
        catch
        {
            NativeMethods.CloseHandle(processInfo.hThread);
            NativeMethods.CloseHandle(processInfo.hProcess);
            CloseJob(_job);
            throw;
        }
    }

    public bool PanelExited => _panel.HasExited;

    public async Task<bool> GetStreamActiveAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
            using var response = await client.GetAsync(PanelUrl + "api/state");
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.TryGetProperty("stream_active", out var active) && active.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    public void OpenPanel() => Process.Start(new ProcessStartInfo
    {
        FileName = PanelUrl,
        UseShellExecute = true
    });

    public void StopAndClose()
    {
        if (_disposed) return;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            client.PostAsync(PanelUrl + "api/stop", content).GetAwaiter().GetResult();
        }
        catch
        {
            // Closing the job below is the hard fallback and does not depend
            // on the panel's HTTP server being responsive.
        }
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _panel.Dispose(); } catch { }
        if (_panelProcessHandle != IntPtr.Zero)
            NativeMethods.CloseHandle(_panelProcessHandle);
        CloseJob(_job);
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui criar o supervisor do OBS.");

        var limits = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf(limits));
        try
        {
            Marshal.StructureToPtr(limits, memory, false);
            if (!NativeMethods.SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    memory,
                    (uint)Marshal.SizeOf(limits)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Não consegui ativar o encerramento seguro do OBS.");
            }
            return job;
        }
        catch
        {
            CloseJob(job);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static void CloseJob(IntPtr job)
    {
        if (job != IntPtr.Zero) NativeMethods.CloseHandle(job);
    }
}

internal sealed class SupervisorWindow : Form
{
    private readonly Supervisor _supervisor;
    private readonly Label _dot = new();
    private readonly Label _state = new();
    private readonly Label _detail = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private bool _closing;

    public SupervisorWindow(Supervisor supervisor)
    {
        _supervisor = supervisor;
        Text = "VDO-Ninja — supervisor";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(360, 178);
        BackColor = Color.FromArgb(17, 21, 31);
        ForeColor = Color.White;

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(Math.Max(10, screen.Right - Width - 24), screen.Top + 24);

        _dot.Size = new Size(30, 30);
        _dot.Location = new Point(18, 20);
        _dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_dot.ForeColor);
            e.Graphics.FillEllipse(brush, 2, 2, 26, 26);
        };
        Controls.Add(_dot);

        _state.AutoSize = true;
        _state.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        _state.Location = new Point(60, 18);
        Controls.Add(_state);

        _detail.AutoSize = false;
        _detail.Size = new Size(280, 42);
        _detail.Location = new Point(60, 48);
        _detail.ForeColor = Color.FromArgb(190, 198, 212);
        _detail.Text = "O OBS será encerrado junto com este app.";
        Controls.Add(_detail);

        var open = MakeButton("Abrir painel", 18, 112, Color.FromArgb(43, 51, 66));
        open.Click += (_, _) => _supervisor.OpenPanel();
        Controls.Add(open);

        var stop = MakeButton("Parar transmissão", 173, 112, Color.FromArgb(125, 41, 59));
        stop.Click += (_, _) =>
        {
            _supervisor.StopAndClose();
            Close();
        };
        Controls.Add(stop);

        FormClosing += (_, e) =>
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            _supervisor.StopAndClose();
        };
        _timer.Tick += async (_, _) => await RefreshStateAsync();
        _timer.Start();
        Shown += async (_, _) => await RefreshStateAsync();
    }

    private Button MakeButton(string text, int x, int y, Color color) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(150, 34),
        FlatStyle = FlatStyle.Flat,
        BackColor = color,
        ForeColor = Color.White,
        FlatAppearance = { BorderColor = color }
    };

    private async Task RefreshStateAsync()
    {
        if (_closing) return;
        if (_supervisor.PanelExited)
        {
            _closing = true;
            _supervisor.StopAndClose();
            Close();
            return;
        }

        var active = await _supervisor.GetStreamActiveAsync();
        _dot.ForeColor = active ? Color.FromArgb(54, 201, 143) : Color.FromArgb(227, 75, 96);
        _dot.Invalidate();
        _state.Text = active ? "TRANSMITINDO" : "PARADO";
        _state.ForeColor = _dot.ForeColor;
        _detail.Text = active
            ? "Feche esta janela para encerrar tudo."
            : "O OBS será encerrado junto com este app.";
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(IntPtr job, uint infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
