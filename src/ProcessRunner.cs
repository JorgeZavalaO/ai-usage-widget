using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AiUsageWidget
{
    public sealed class ProcessResult
    {
        public bool Started;
        public bool TimedOut;
        public bool Canceled;
        public bool OutputLimitExceeded;
        public int ExitCode;
        public string StandardOutput = string.Empty;
        public string ErrorCode;
    }

    public interface IProcessRunner
    {
        ProcessResult Run(string executable, string arguments, int timeoutMs, CancellationToken token);
    }

    /// <summary>
    /// Runs a short-lived CLI command with a hard deadline, bounded output, and
    /// deterministic descendant cleanup. The child is placed in a Windows job
    /// object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so killing the
    /// wrapper also removes any process it spawned (killing cmd.exe alone does not).
    /// </summary>
    public sealed class ProcessRunner : IProcessRunner
    {
        public const int MaxOutputBytes = 2 * 1024 * 1024;

        public ProcessResult Run(string executable, string arguments, int timeoutMs, CancellationToken token)
        {
            var result = new ProcessResult();

            if (string.IsNullOrWhiteSpace(executable))
            {
                result.ErrorCode = "missing_executable";
                return result;
            }

            var command = BuildCommand(executable, arguments);
            IntPtr job = CreateJob();
            Process process = null;
            ManualResetEventSlim stdoutDone = new ManualResetEventSlim(false);
            ManualResetEventSlim stderrDone = new ManualResetEventSlim(false);
            var stdout = new OutputBuffer(MaxOutputBytes);
            var stderr = new OutputBuffer(MaxOutputBytes);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = command.FileName,
                    Arguments = command.Arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                try
                {
                    process = Process.Start(startInfo);
                }
                catch (Win32Exception)
                {
                    result.ErrorCode = "not_found";
                    return result;
                }
                catch (Exception)
                {
                    result.ErrorCode = "start_failed";
                    return result;
                }

                if (process == null)
                {
                    result.ErrorCode = "start_failed";
                    return result;
                }

                result.Started = true;
                AssignToJob(job, process);

                StartReader(process.StandardOutput, stdout, stdoutDone);
                StartReader(process.StandardError, stderr, stderrDone);

                var stopwatch = Stopwatch.StartNew();
                var terminationReason = WaitReason.Exited;

                using (token.Register(() => TerminateJob(job)))
                {
                    while (true)
                    {
                        if (process.WaitForExit(50))
                        {
                            terminationReason = token.IsCancellationRequested
                                ? WaitReason.Canceled
                                : WaitReason.Exited;
                            break;
                        }

                        if (token.IsCancellationRequested)
                        {
                            terminationReason = WaitReason.Canceled;
                            break;
                        }

                        if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                        {
                            terminationReason = WaitReason.TimedOut;
                            break;
                        }

                        if (stdout.LimitExceeded || stderr.LimitExceeded)
                        {
                            terminationReason = WaitReason.OutputLimit;
                            break;
                        }
                    }
                }

                if (terminationReason == WaitReason.Exited)
                {
                    // Flush the synchronous readers; the process has already exited.
                    process.WaitForExit();
                }

                // Kill any descendant that outlived the wrapper or is holding the
                // output pipes open. This is a no-op when nothing remains.
                TerminateJob(job);

                if (terminationReason != WaitReason.Exited)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch (Exception) { }

                    try { process.WaitForExit(2000); }
                    catch (Exception) { }
                }

                stdoutDone.Wait(2000);
                stderrDone.Wait(2000);

                result.StandardOutput = stdout.ToString();
                result.ExitCode = process.HasExited ? process.ExitCode : -1;

                if (terminationReason == WaitReason.TimedOut)
                {
                    result.TimedOut = true;
                    result.ErrorCode = "timeout";
                }
                else if (terminationReason == WaitReason.Canceled)
                {
                    result.Canceled = true;
                    result.ErrorCode = "canceled";
                }
                else if (terminationReason == WaitReason.OutputLimit || stdout.LimitExceeded || stderr.LimitExceeded)
                {
                    result.OutputLimitExceeded = true;
                    result.ErrorCode = "output_limit";
                }

                return result;
            }
            finally
            {
                stdoutDone.Dispose();
                stderrDone.Dispose();
                if (process != null)
                {
                    try { process.Dispose(); }
                    catch (Exception) { }
                }
                CloseHandle(job);
            }
        }

        private enum WaitReason
        {
            Exited,
            TimedOut,
            Canceled,
            OutputLimit
        }

        private sealed class Command
        {
            public string FileName;
            public string Arguments;
        }

        private static Command BuildCommand(string executable, string arguments)
        {
            executable = executable.Trim();
            arguments = arguments == null ? string.Empty : arguments.Trim();

            if (EndsWith(executable, ".cmd") || EndsWith(executable, ".bat"))
            {
                // cmd.exe needs the whole command wrapped in one extra pair of
                // quotes when the executable path contains spaces.
                var line = "\"" + executable + "\"";
                if (arguments.Length > 0) line += " " + arguments;
                return new Command
                {
                    FileName = "cmd.exe",
                    Arguments = "/d /s /c \"" + line + "\""
                };
            }

            return new Command { FileName = executable, Arguments = arguments };
        }

        private static bool EndsWith(string value, string suffix)
        {
            return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static void StartReader(StreamReader source, OutputBuffer target, ManualResetEventSlim done)
        {
            var thread = new Thread(() =>
            {
                try { target.Read(source); }
                catch (Exception) { }
                finally
                {
                    try { done.Set(); }
                    catch (Exception) { }
                }
            })
            {
                IsBackground = true,
                Name = "process-reader"
            };
            thread.Start();
        }

        private sealed class OutputBuffer
        {
            private readonly int _limit;
            private readonly StringBuilder _text = new StringBuilder();
            private readonly object _gate = new object();
            private long _bytes;
            private bool _limitExceeded;

            public OutputBuffer(int limit)
            {
                _limit = limit;
            }

            public bool LimitExceeded
            {
                get { lock (_gate) { return _limitExceeded; } }
            }

            public void Read(StreamReader reader)
            {
                var buffer = new char[4096];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (_gate)
                    {
                        _bytes += read;
                        if (_bytes > _limit)
                        {
                            _limitExceeded = true;
                        }
                        else
                        {
                            _text.Append(buffer, 0, read);
                        }
                    }
                }
            }

            public override string ToString()
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        // ---------------------------------------------------------- job objects

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            int informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
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
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationStruct
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static IntPtr CreateJob()
        {
            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;

                var info = new JobObjectExtendedLimitInformationStruct();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

                var length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformationStruct));
                var pointer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, pointer, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, length))
                    {
                        CloseHandle(job);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }

                return job;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private static void AssignToJob(IntPtr job, Process process)
        {
            if (job == IntPtr.Zero) return;
            try
            {
                // Nested jobs are supported on Windows 8 and later. If assignment
                // fails we still kill the direct child on timeout.
                AssignProcessToJobObject(job, process.Handle);
            }
            catch (Exception)
            {
            }
        }

        private static void TerminateJob(IntPtr job)
        {
            if (job == IntPtr.Zero) return;
            try { TerminateJobObject(job, 1); }
            catch (Exception) { }
        }
    }
}
