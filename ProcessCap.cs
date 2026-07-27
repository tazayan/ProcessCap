using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ProcessCap
{
    public static class Program
    {
        private const ulong BytesPerMebibyte = 1024 * 1024;

        public static int Main()
        {
            try
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    throw new PlatformNotSupportedException("ProcessCap requires Windows.");
                }

                WindowsVersionInfo windowsVersion = NativeMethods.GetWindowsVersionInfo();
                if (windowsVersion.Major < 10)
                {
                    throw new PlatformNotSupportedException("ProcessCap requires Windows 10 or later.");
                }

                if (IntPtr.Size != 8)
                {
                    throw new PlatformNotSupportedException("ProcessCap requires a 64-bit process.");
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\nProcessCap");
                Console.WriteLine("----------------------------------------");
                Console.ResetColor();
                WriteColored("Checking operating system and machine resources...", ConsoleColor.Gray);

                SystemResourceInfo resources = NativeMethods.GetSystemResourceInfo(windowsVersion);
                int totalMemoryMb = checked((int)(resources.TotalPhysicalMemoryBytes / BytesPerMebibyte));
                int freeMemoryMb = checked((int)(resources.AvailablePhysicalMemoryBytes / BytesPerMebibyte));
                int logicalProcessorCount = Environment.ProcessorCount;
                int maximumAffinityProcessorCount = Math.Min(logicalProcessorCount, 64);

                Console.WriteLine("OS: {0} build {1}", resources.Caption, resources.BuildNumber);
                Console.WriteLine("Logical processors: {0}", logicalProcessorCount);
                Console.WriteLine("Usable CPU affinity range: 1-{0} logical processor(s)", maximumAffinityProcessorCount);
                Console.WriteLine("Physical memory: {0} MB total, {1} MB currently free\n", totalMemoryMb, freeMemoryMb);
                WriteColored("CPU limiting uses processor affinity; it is not an exact percentage throttle.", ConsoleColor.DarkYellow);
                WriteColored("Memory limiting is a hard total job-memory limit inherited by child processes.\n", ConsoleColor.DarkYellow);
                WriteColored("Input required. Press Enter to accept values shown in brackets.\n", ConsoleColor.Cyan);

                string applicationPath = ReadExistingExecutable("Application executable path");
                Console.Write("Application arguments (optional): ");
                string arguments = Console.ReadLine() ?? string.Empty;
                string defaultDirectory = Path.GetDirectoryName(applicationPath);
                string workingDirectory = ReadDirectory("Working directory", defaultDirectory);
                int cpuLimit = ReadInt32("Logical processors to allow", 1, maximumAffinityProcessorCount, maximumAffinityProcessorCount);
                int defaultMemoryLimitMb = Math.Max(256, Math.Min(freeMemoryMb, totalMemoryMb));
                int memoryLimitMb = ReadInt32("Total memory limit in MB", 1, totalMemoryMb, defaultMemoryLimitMb);

                if (memoryLimitMb > freeMemoryMb)
                {
                    WriteColored(string.Format("Warning: the limit exceeds currently free memory ({0} MB).", freeMemoryMb), ConsoleColor.Yellow);
                }

                ulong affinityMask = cpuLimit == 64 ? ulong.MaxValue : (1UL << cpuLimit) - 1;
                ulong memoryLimitBytes = checked((ulong)memoryLimitMb * BytesPerMebibyte);
                string jobName = "ProcessCap." + Guid.NewGuid().ToString("N");

                using (RestrictedJob job = new RestrictedJob(jobName, memoryLimitBytes))
                {
                    WriteColored("\nStarting restricted process...", ConsoleColor.Cyan);
                    Console.WriteLine("Executable: {0}", applicationPath);
                    Console.WriteLine("Arguments: {0}", arguments);
                    Console.WriteLine("Working directory: {0}", workingDirectory);
                    Console.WriteLine("CPU affinity logical processors: {0} of {1}", cpuLimit, logicalProcessorCount);
                    Console.WriteLine("Memory limit: {0} MB", memoryLimitMb);

                    using (LaunchedProcess process = job.StartProcess(applicationPath, arguments, workingDirectory, affinityMask))
                    {
                        int processId = process.Id;
                        WriteColored(string.Format("Started process ID {0}. Keep this launcher running to keep the restricted job alive.", processId), ConsoleColor.Green);

                        Thread.Sleep(250);
                        try
                        {
                            RestrictionVerification result = job.Verify(process.Handle, memoryLimitBytes, affinityMask);
                            WriteColored(
                                result.AllApplied ? "Restrictions verified successfully." : "Restrictions could not be fully verified.",
                                result.AllApplied ? ConsoleColor.Green : ConsoleColor.Yellow);
                            Console.WriteLine("  Job membership: {0}", result.IsAssignedToJob ? "applied" : "not applied");
                            Console.WriteLine(
                                "  Job memory limit: {0} (expected {1} MB, actual {2:F2} MB, flags 0x{3:X})",
                                result.MemoryLimitApplied ? "applied" : "not applied",
                                memoryLimitMb,
                                result.ActualJobMemoryLimitBytes / (double)BytesPerMebibyte,
                                result.JobLimitFlags);
                            Console.WriteLine(
                                "  CPU affinity: {0} (expected 0x{1:X}, actual 0x{2:X})",
                                result.AffinityApplied ? "applied" : "not applied",
                                result.ExpectedAffinityMask,
                                result.ActualAffinityMask);
                        }
                        catch (Exception ex)
                        {
                            WriteColored("Unable to verify restrictions after startup: " + ex.Message, ConsoleColor.Yellow);
                        }

                        process.WaitForExit();
                        WriteColored(string.Format("Process {0} exited.", processId), ConsoleColor.Green);
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                WriteColored("Error: " + ex.Message, ConsoleColor.Red);
                return 1;
            }
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().Trim('"', '\'');
        }

        private static string ReadExistingExecutable(string prompt)
        {
            while (true)
            {
                Console.Write("{0}: ", prompt);
                string value = Normalize(Console.ReadLine());
                if (string.IsNullOrWhiteSpace(value))
                {
                    WriteColored("A path is required.", ConsoleColor.Yellow);
                    continue;
                }

                string fullPath;
                if (!TryGetFullPath(value, out fullPath))
                {
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    WriteColored("The file does not exist.", ConsoleColor.Yellow);
                    continue;
                }

                if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    WriteColored("The selected file must be an .exe.", ConsoleColor.Yellow);
                    continue;
                }

                return fullPath;
            }
        }

        private static string ReadDirectory(string prompt, string defaultPath)
        {
            while (true)
            {
                Console.Write("{0} [{1}]: ", prompt, defaultPath);
                string value = Normalize(Console.ReadLine());
                if (string.IsNullOrWhiteSpace(value))
                {
                    return defaultPath;
                }

                string fullPath;
                if (!TryGetFullPath(value, out fullPath))
                {
                    continue;
                }

                if (Directory.Exists(fullPath))
                {
                    return fullPath;
                }

                WriteColored("The directory does not exist.", ConsoleColor.Yellow);
            }
        }

        private static int ReadInt32(string prompt, int minimum, int maximum, int defaultValue)
        {
            while (true)
            {
                Console.Write("{0} [{1}]: ", prompt, defaultValue);
                string value = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return defaultValue;
                }

                int parsed;
                if (!int.TryParse(value, out parsed))
                {
                    WriteColored("Enter a whole number.", ConsoleColor.Yellow);
                    continue;
                }

                if (parsed >= minimum && parsed <= maximum)
                {
                    return parsed;
                }

                WriteColored(string.Format("Enter a value from {0} to {1}.", minimum, maximum), ConsoleColor.Yellow);
            }
        }

        private static void WriteColored(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static bool TryGetFullPath(string value, out string fullPath)
        {
            try
            {
                fullPath = Path.GetFullPath(value);
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is NotSupportedException) && !(ex is PathTooLongException))
                {
                    throw;
                }

                WriteColored("The path is invalid: " + ex.Message, ConsoleColor.Yellow);
                fullPath = string.Empty;
                return false;
            }
        }
    }

    internal sealed class RestrictedJob : IDisposable
    {
        private const uint CreateSuspended = 0x00000004;
        private const uint JobObjectLimitJobMemory = 0x00000200;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;
        private readonly SafeJobHandle jobHandle;

        public RestrictedJob(string name, ulong memoryLimit)
        {
            jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, name);
            if (jobHandle.IsInvalid)
            {
                throw NativeMethods.Error("Unable to create the Windows job object.");
            }

            JobObjectExtendedLimitInformation info = new JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitJobMemory;
            info.JobMemoryLimit = new UIntPtr(memoryLimit);
            WithStructure(
                info,
                delegate(IntPtr pointer, uint size)
                {
                    return NativeMethods.SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, pointer, size);
                },
                "Unable to configure the Windows job object memory limit.");
        }

        public LaunchedProcess StartProcess(string fileName, string arguments, string workingDirectory, ulong affinityMask)
        {
            StringBuilder commandLine = new StringBuilder(
                QuoteWindowsArgument(fileName) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments));
            StartupInfo startup = new StartupInfo();
            startup.Size = Marshal.SizeOf(typeof(StartupInfo));
            ProcessInformation process;
            if (!NativeMethods.CreateProcess(
                fileName,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended,
                IntPtr.Zero,
                workingDirectory,
                ref startup,
                out process))
            {
                throw NativeMethods.Error("Unable to create the process.");
            }

            using (SafeKernelHandle threadHandle = new SafeKernelHandle(process.Thread, true))
            {
                SafeProcessHandle processHandle = new SafeProcessHandle(process.Process, true);
                try
                {
                    if (!NativeMethods.AssignProcessToJobObject(jobHandle, processHandle))
                    {
                        throw NativeMethods.Error("Unable to assign the process to the job.");
                    }

                    if (!NativeMethods.SetProcessAffinityMask(processHandle, new UIntPtr(affinityMask)))
                    {
                        throw NativeMethods.Error("Unable to apply processor affinity.");
                    }

                    if (NativeMethods.ResumeThread(threadHandle) == uint.MaxValue)
                    {
                        throw NativeMethods.Error("Unable to resume the process.");
                    }

                    return new LaunchedProcess(process.ProcessId, processHandle);
                }
                catch
                {
                    NativeMethods.TerminateProcess(processHandle, 1);
                    processHandle.Dispose();
                    throw;
                }
            }
        }

        public RestrictionVerification Verify(SafeProcessHandle process, ulong expectedMemory, ulong expectedAffinity)
        {
            bool inJob;
            if (!NativeMethods.IsProcessInJob(process, jobHandle, out inJob))
            {
                throw NativeMethods.Error("Unable to verify job membership.");
            }

            UIntPtr affinity;
            UIntPtr systemAffinity;
            if (!NativeMethods.GetProcessAffinityMask(process, out affinity, out systemAffinity))
            {
                throw NativeMethods.Error("Unable to verify affinity.");
            }

            JobObjectExtendedLimitInformation info = QueryInfo();
            ulong memory = info.JobMemoryLimit.ToUInt64();
            ulong actualAffinity = affinity.ToUInt64();
            return new RestrictionVerification(
                inJob,
                (info.BasicLimitInformation.LimitFlags & JobObjectLimitJobMemory) != 0 && memory == expectedMemory,
                actualAffinity == expectedAffinity,
                expectedMemory,
                memory,
                expectedAffinity,
                actualAffinity,
                info.BasicLimitInformation.LimitFlags);
        }

        private JobObjectExtendedLimitInformation QueryInfo()
        {
            int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(
                    jobHandle,
                    JobObjectExtendedLimitInformation,
                    pointer,
                    (uint)size,
                    IntPtr.Zero))
                {
                    throw NativeMethods.Error("Unable to query job limits.");
                }

                return (JobObjectExtendedLimitInformation)Marshal.PtrToStructure(
                    pointer,
                    typeof(JobObjectExtendedLimitInformation));
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private static void WithStructure<T>(T value, Func<IntPtr, uint, bool> operation, string error)
            where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                if (!operation(pointer, (uint)size))
                {
                    throw NativeMethods.Error(error);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private static string QuoteWindowsArgument(string value)
        {
            StringBuilder result = new StringBuilder(value.Length + 2).Append('"');
            int backslashCount = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', backslashCount * 2 + 1).Append(character);
                    backslashCount = 0;
                    continue;
                }

                result.Append('\\', backslashCount).Append(character);
                backslashCount = 0;
            }

            return result.Append('\\', backslashCount * 2).Append('"').ToString();
        }

        public void Dispose()
        {
            jobHandle.Dispose();
        }
    }

    internal sealed class LaunchedProcess : IDisposable
    {
        public LaunchedProcess(int id, SafeProcessHandle handle)
        {
            Id = id;
            Handle = handle;
        }

        public int Id { get; private set; }
        public SafeProcessHandle Handle { get; private set; }

        public void WaitForExit()
        {
            uint result = NativeMethods.WaitForSingleObject(Handle, NativeMethods.Infinite);
            if (result == NativeMethods.WaitFailed)
            {
                throw NativeMethods.Error("Unable to wait for the launched process.");
            }
        }

        public void Dispose()
        {
            Handle.Dispose();
        }
    }

    internal sealed class RestrictionVerification
    {
        public RestrictionVerification(
            bool isAssignedToJob,
            bool memoryLimitApplied,
            bool affinityApplied,
            ulong expectedJobMemoryLimitBytes,
            ulong actualJobMemoryLimitBytes,
            ulong expectedAffinityMask,
            ulong actualAffinityMask,
            uint jobLimitFlags)
        {
            IsAssignedToJob = isAssignedToJob;
            MemoryLimitApplied = memoryLimitApplied;
            AffinityApplied = affinityApplied;
            ExpectedJobMemoryLimitBytes = expectedJobMemoryLimitBytes;
            ActualJobMemoryLimitBytes = actualJobMemoryLimitBytes;
            ExpectedAffinityMask = expectedAffinityMask;
            ActualAffinityMask = actualAffinityMask;
            JobLimitFlags = jobLimitFlags;
        }

        public bool IsAssignedToJob { get; private set; }
        public bool MemoryLimitApplied { get; private set; }
        public bool AffinityApplied { get; private set; }
        public ulong ExpectedJobMemoryLimitBytes { get; private set; }
        public ulong ActualJobMemoryLimitBytes { get; private set; }
        public ulong ExpectedAffinityMask { get; private set; }
        public ulong ActualAffinityMask { get; private set; }
        public uint JobLimitFlags { get; private set; }
        public bool AllApplied { get { return IsAssignedToJob && MemoryLimitApplied && AffinityApplied; } }
    }

    internal sealed class SystemResourceInfo
    {
        public SystemResourceInfo(string caption, int buildNumber, ulong totalPhysicalMemoryBytes, ulong availablePhysicalMemoryBytes)
        {
            Caption = caption;
            BuildNumber = buildNumber;
            TotalPhysicalMemoryBytes = totalPhysicalMemoryBytes;
            AvailablePhysicalMemoryBytes = availablePhysicalMemoryBytes;
        }

        public string Caption { get; private set; }
        public int BuildNumber { get; private set; }
        public ulong TotalPhysicalMemoryBytes { get; private set; }
        public ulong AvailablePhysicalMemoryBytes { get; private set; }
    }

    internal sealed class WindowsVersionInfo
    {
        public WindowsVersionInfo(int major, int minor, int build, int revision)
        {
            Major = major;
            Minor = minor;
            Build = build;
            Revision = revision;
        }

        public int Major { get; private set; }
        public int Minor { get; private set; }
        public int Build { get; private set; }
        public int Revision { get; private set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
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
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OsVersionInfo
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ServicePack;
    }

    internal static class NativeMethods
    {
        internal const uint Infinite = 0xFFFFFFFF;
        internal const uint WaitFailed = 0xFFFFFFFF;

        internal static WindowsVersionInfo GetWindowsVersionInfo()
        {
            try
            {
                return GetWindowsVersionInfoFromWinRt();
            }
            catch (Exception ex)
            {
                if (!(ex is FileNotFoundException) && !(ex is FileLoadException) && !(ex is TypeLoadException))
                {
                    throw;
                }

                return GetWindowsVersionInfoFromNativeApi();
            }
        }

        private static WindowsVersionInfo GetWindowsVersionInfoFromWinRt()
        {
            const string AnalyticsInfoTypeName =
                "Windows.System.Profile.AnalyticsInfo, Windows.System.Profile, ContentType=WindowsRuntime";
            Type analyticsInfoType = Type.GetType(AnalyticsInfoTypeName, true);
            PropertyInfo versionInfoProperty = analyticsInfoType.GetProperty("VersionInfo", BindingFlags.Public | BindingFlags.Static);
            if (versionInfoProperty == null)
            {
                throw new InvalidOperationException("Unable to locate the WinRT AnalyticsInfo.VersionInfo property.");
            }

            object versionInfo = versionInfoProperty.GetValue(null, null);
            if (versionInfo == null)
            {
                throw new InvalidOperationException("WinRT AnalyticsInfo.VersionInfo returned no value.");
            }

            PropertyInfo deviceFamilyVersionProperty = versionInfo.GetType().GetProperty("DeviceFamilyVersion");
            if (deviceFamilyVersionProperty == null)
            {
                throw new InvalidOperationException("Unable to locate the WinRT DeviceFamilyVersion property.");
            }

            object rawValue = deviceFamilyVersionProperty.GetValue(versionInfo, null);
            ulong encodedVersion;
            if (rawValue == null || !ulong.TryParse(rawValue.ToString(), out encodedVersion))
            {
                throw new InvalidOperationException("WinRT returned an invalid DeviceFamilyVersion value.");
            }

            return new WindowsVersionInfo(
                (int)((encodedVersion >> 48) & 0xFFFF),
                (int)((encodedVersion >> 32) & 0xFFFF),
                (int)((encodedVersion >> 16) & 0xFFFF),
                (int)(encodedVersion & 0xFFFF));
        }

        private static WindowsVersionInfo GetWindowsVersionInfoFromNativeApi()
        {
            OsVersionInfo version = new OsVersionInfo();
            version.Size = (uint)Marshal.SizeOf(typeof(OsVersionInfo));
            version.ServicePack = string.Empty;
            int status = RtlGetVersion(ref version);
            if (status < 0)
            {
                throw new InvalidOperationException(
                    string.Format("Unable to query the native Windows version. NTSTATUS: 0x{0:X8}.", status));
            }

            return new WindowsVersionInfo(
                checked((int)version.Major),
                checked((int)version.Minor),
                checked((int)version.Build),
                0);
        }

        [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "ProcessCap validates Windows before calling this method.")]
        internal static SystemResourceInfo GetSystemResourceInfo(WindowsVersionInfo windowsVersion)
        {
            MemoryStatusEx memory = new MemoryStatusEx();
            memory.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref memory))
            {
                throw Error("Unable to query physical memory status.");
            }

            string caption = "Windows";
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key != null)
                {
                    object productName = key.GetValue("ProductName");
                    if (productName != null && !string.IsNullOrWhiteSpace(productName.ToString()))
                    {
                        caption = productName.ToString();
                    }
                }
            }

            if (windowsVersion.Build >= 22000 && caption.StartsWith("Windows 10", StringComparison.Ordinal))
            {
                caption = "Windows 11" + caption.Substring(10);
            }

            return new SystemResourceInfo(caption, windowsVersion.Build, memory.TotalPhys, memory.AvailPhys);
        }

        internal static Win32Exception Error(string message)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), message);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersionInfo versionInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int type, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool QueryInformationJobObject(SafeJobHandle job, int type, IntPtr info, uint length, IntPtr returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetProcessAffinityMask(SafeProcessHandle process, out UIntPtr processMask, out UIntPtr systemMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetProcessAffinityMask(SafeProcessHandle process, UIntPtr mask);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeKernelHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);
    }

    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }

    internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle(IntPtr handle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }
}
