using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoslynKate;

/// <summary>
/// Ties the language server's lifetime to ours. Kate kills a server that doesn't exit quickly
/// enough, and on Windows killing a process leaves its children running, so without this every
/// restart would leave a Roslyn process behind. The OS closes the job handle when we die, however
/// we die, and that kills everything in the job.
/// </summary>
static partial class ChildProcessJob
{
	static nint _job;

	public static void Add( Process process )
	{
		if ( !OperatingSystem.IsWindows() )
			return;

		if ( _job == 0 )
		{
			_job = CreateJobObject( 0, null );

			var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
			info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

			var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
			var ptr = Marshal.AllocHGlobal( size );
			try
			{
				Marshal.StructureToPtr( info, ptr, false );
				SetInformationJobObject( _job, JobObjectExtendedLimitInformation, ptr, (uint)size );
			}
			finally
			{
				Marshal.FreeHGlobal( ptr );
			}
		}

		AssignProcessToJobObject( _job, process.Handle );
	}

	const int JobObjectExtendedLimitInformation = 9;
	const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

	[StructLayout( LayoutKind.Sequential )]
	struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout( LayoutKind.Sequential )]
	struct IO_COUNTERS
	{
		public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
		public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
	}

	[StructLayout( LayoutKind.Sequential )]
	struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[LibraryImport( "kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16 )]
	private static partial nint CreateJobObject( nint attributes, string? name );

	[LibraryImport( "kernel32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool SetInformationJobObject( nint job, int infoClass, nint info, uint length );

	[LibraryImport( "kernel32.dll" )]
	[return: MarshalAs( UnmanagedType.Bool )]
	private static partial bool AssignProcessToJobObject( nint job, nint process );
}
