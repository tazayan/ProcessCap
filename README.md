# ProcessCap

ProcessCap starts a Windows executable with configurable CPU-affinity and memory restrictions.

The launcher uses a Windows Job Object to apply a hard memory limit to the launched process and its child processes. CPU access is restricted with a processor-affinity mask. Processor affinity controls which logical processors may run the application; it is not an exact CPU-percentage throttle.

The application is implemented in one C# source file. It can run as a .NET 10 file-based app, or Windows PowerShell 5.1 and later.

Unlike the **Number of processors** and **Maximum memory** options under **System Configuration (`msconfig`) > Boot > Advanced options**, which change resources available to the entire Windows system and require a computer restart, ProcessCap applies restrictions only to the selected application and its child processes. No restart is required, and limits can be changed between launches. This makes ProcessCap useful for quickly finding an application's practical lower CPU and memory boundaries before deciding whether to test corresponding system-level limitations.

## Files

- `ProcessCap.cs` — complete file-based C# application.
- `ProcessCap.ps1` — PowerShell wrapper that compiles and invokes `ProcessCap.cs` directly.

## Requirements

- Windows 10 or later.
- A 64-bit Windows installation.
- For direct C# file-app execution: .NET 10 SDK or later available through the `dotnet` command.
- For PowerShell execution: Windows PowerShell 5.1 or later. The separate .NET SDK is not required.

Confirm that .NET is installed when using the direct C# version:

```powershell
dotnet --version
```

Confirm that a supported PowerShell version is installed when using the PowerShell version:

```powershell
$PSVersionTable.PSVersion
```

## Run the C# version directly

Open PowerShell in this directory and run:

```powershell
dotnet run --file .\ProcessCap.cs
```

You can also provide the full path from another directory:

```powershell
dotnet run --file "full paht\ProcessCap.cs"
```

## Run through PowerShell

From this directory, run:

```powershell
.\ProcessCap.ps1
```

The wrapper resolves `ProcessCap.cs` relative to its own location, compiles it into an in-memory assembly with `Add-Type`, and calls `ProcessCap.Program.Main()`.
It can also be invoked from another working directory:

```powershell
& "full paht\ProcessCap.ps1"
```

If the local PowerShell execution policy prevents the script from running, use a process-scoped bypass:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\ProcessCap.ps1
```

This bypass applies only to that PowerShell process and does not change the system execution policy.

The C# source intentionally uses syntax and framework APIs compatible with the compiler included in Windows PowerShell 5.1.

## Using the launcher

The launcher displays system information and requests the following values:

1. **Application executable path** — path to an existing `.exe` file.
2. **Application arguments** — optional arguments passed to the application.
3. **Working directory** — press Enter to use the executable's directory.
4. **Logical processors to allow** — press Enter to allow the displayed default.
5. **Total memory limit in MB** — hard memory limit for the complete job.

After collecting the values, the launcher:

1. Creates the target process in a suspended state.
2. Assigns it to a Windows Job Object.
3. Applies the job memory limit and CPU-affinity mask.
4. Resumes the process and verifies the restrictions.
5. Waits for the process to exit.

Keep the launcher open while the restricted application is running. Closing the launcher closes the Job Object and terminates processes assigned to it.

## Build the C# app

The source can be compiled without a project file:

```powershell
dotnet build .\ProcessCap.cs -p:PublishAot=false -p:Nullable=disable
```

Native AOT is disabled because the Windows version lookup uses runtime WinRT reflection. Nullable analysis is disabled for this build because the source intentionally remains compatible with the C# compiler included in Windows PowerShell 5.1.

To publish a Windows x64 executable:

```powershell
dotnet publish .\ProcessCap.cs -c Release -r win-x64 --self-contained true -p:PublishAot=false
```

To request a single-file executable:

```powershell
dotnet publish .\ProcessCap.cs -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishSingleFile=true
```

The `dotnet publish` output reports the generated publish directory.

## Important behavior

- CPU affinity is limited to at most 64 logical processors by the current implementation.
- The memory value is a total Job Object memory limit shared by the launched application and its child processes.
- Selecting a limit above currently available memory may cause the application to reach the limit sooner.
- Raw application arguments are passed to the target executable exactly as entered.
- The launcher is Windows-specific because it depends on native Windows Job Object APIs.
