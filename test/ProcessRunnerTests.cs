using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AiUsageWidget.Tests
{
    public static class ProcessRunnerTests
    {
        [Test]
        public static void NormalOutput_IsCaptured()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("tools/echo.cmd",
                    "@echo off\r\necho {\"ok\":true,\"value\":42}\r\n");

                var result = new ProcessRunner().Run(script, string.Empty, 5000, CancellationToken.None);

                Assert.True(result.Started);
                Assert.False(result.TimedOut);
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("{\"ok\":true", result.StandardOutput);
            }
        }

        [Test]
        public static void NonZeroExit_IsReported()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("fail.cmd", "@echo off\r\nexit /b 3\r\n");
                var result = new ProcessRunner().Run(script, string.Empty, 5000, CancellationToken.None);

                Assert.True(result.Started);
                Assert.False(result.TimedOut);
                Assert.Equal(3, result.ExitCode);
            }
        }

        [Test]
        public static void EmptyOutput_IsNotAnError()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("empty.cmd", "@echo off\r\n");
                var result = new ProcessRunner().Run(script, string.Empty, 5000, CancellationToken.None);

                Assert.True(result.Started);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(string.Empty, result.StandardOutput.Trim());
            }
        }

        [Test]
        public static void MissingExecutable_IsReportedWithoutHanging()
        {
            using (var temp = new TempDirectory())
            {
                var missing = Path.Combine(temp.Path, "does-not-exist.exe");
                var watch = Stopwatch.StartNew();
                var result = new ProcessRunner().Run(missing, string.Empty, 5000, CancellationToken.None);
                watch.Stop();

                Assert.False(result.Started);
                Assert.Equal("not_found", result.ErrorCode);
                Assert.True(watch.ElapsedMilliseconds < 5000);
            }
        }

        [Test]
        public static void HungProcess_WithOpenStdout_TimesOutWithinDeadline()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("hang.cmd", "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n");

                var watch = Stopwatch.StartNew();
                var result = new ProcessRunner().Run(script, string.Empty, 700, CancellationToken.None);
                watch.Stop();

                Assert.True(result.TimedOut);
                Assert.Equal("timeout", result.ErrorCode);
                Assert.True(watch.ElapsedMilliseconds < 6000,
                    "Timeout cleanup took " + watch.ElapsedMilliseconds + " ms.");
            }
        }

        [Test]
        public static void ExcessiveOutput_IsRejectedAndBounded()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("flood.cmd",
                    "@echo off\r\nfor /L %%i in (1,1,300000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\r\n");

                var watch = Stopwatch.StartNew();
                var result = new ProcessRunner().Run(script, string.Empty, 20000, CancellationToken.None);
                watch.Stop();

                Assert.True(result.OutputLimitExceeded);
                Assert.Equal("output_limit", result.ErrorCode);
                Assert.True(result.StandardOutput.Length <= ProcessRunner.MaxOutputBytes);
                Assert.True(watch.ElapsedMilliseconds < 10000,
                    "Output limit cleanup took " + watch.ElapsedMilliseconds + " ms.");
            }
        }

        [Test]
        public static void ExcessiveStderr_IsAlsoBoundedAndDrained()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("stderr-flood.cmd",
                    "@echo off\r\nfor /L %%i in (1,1,300000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2\r\n");

                var watch = Stopwatch.StartNew();
                var result = new ProcessRunner().Run(script, string.Empty, 20000, CancellationToken.None);
                watch.Stop();

                Assert.True(result.OutputLimitExceeded);
                Assert.True(watch.ElapsedMilliseconds < 10000,
                    "stderr output limit cleanup took " + watch.ElapsedMilliseconds + " ms.");
            }
        }

        [Test]
        public static void Cancellation_StopsTheCommandQuickly()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("cancel.cmd", "@echo off\r\nping -n 60 127.0.0.1 >nul\r\n");
                using (var cts = new CancellationTokenSource())
                {
                    cts.CancelAfter(400);

                    var watch = Stopwatch.StartNew();
                    var result = new ProcessRunner().Run(script, string.Empty, 30000, cts.Token);
                    watch.Stop();

                    Assert.True(result.Canceled);
                    Assert.Equal("canceled", result.ErrorCode);
                    Assert.True(watch.ElapsedMilliseconds < 5000,
                        "Cancellation took " + watch.ElapsedMilliseconds + " ms.");
                }
            }
        }

        [Test]
        public static void WrapperChildProcess_IsTerminatedWithTheJob()
        {
            using (var temp = new TempDirectory())
            {
                var pidFile = temp.Combine("child.pid");
                var childScript = temp.Write("child.ps1",
                    "$p = Start-Process -FilePath 'ping.exe' -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden -PassThru\r\n" +
                    "Set-Content -Path '" + pidFile.Replace("'", "''") + "' -Value $p.Id\r\n" +
                    "Wait-Process -Id $p.Id\r\n");

                var wrapper = temp.Write("spawn.cmd",
                    "@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"" +
                    childScript + "\"\r\n");

                var result = new ProcessRunner().Run(wrapper, string.Empty, 4000, CancellationToken.None);

                Assert.True(result.TimedOut);

                int pid = 0;
                for (int attempt = 0; attempt < 40 && pid == 0; attempt++)
                {
                    try
                    {
                        if (File.Exists(pidFile))
                        {
                            var text = File.ReadAllText(pidFile).Trim();
                            int.TryParse(text, out pid);
                        }
                    }
                    catch (Exception)
                    {
                    }

                    if (pid == 0) Thread.Sleep(100);
                }

                Assert.True(pid > 0, "The synthetic child never reported its PID.");

                var alive = true;
                for (int attempt = 0; attempt < 40 && alive; attempt++)
                {
                    try
                    {
                        using (var process = Process.GetProcessById(pid))
                        {
                            alive = !process.HasExited;
                        }
                    }
                    catch (ArgumentException)
                    {
                        alive = false;
                    }
                    catch (InvalidOperationException)
                    {
                        alive = false;
                    }

                    if (alive) Thread.Sleep(100);
                }

                Assert.False(alive, "The synthetic descendant survived the job cleanup.");
            }
        }

        [Test]
        public static void PathsWithSpacesAndNonAsciiCharacters_Work()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("t\u00e9st dir with space/\u00f6utput .cmd",
                    "@echo off\r\necho non-ascii-ok\r\n");

                var result = new ProcessRunner().Run(script, string.Empty, 5000, CancellationToken.None);

                Assert.True(result.Started);
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("non-ascii-ok", result.StandardOutput);
            }
        }

        [Test]
        public static void ArgumentsArePassedThroughCmdWrapper()
        {
            using (var temp = new TempDirectory())
            {
                var script = temp.Write("args.cmd", "@echo off\r\necho one=%1 two=%2\r\n");
                var result = new ProcessRunner().Run(script, "\"hello world\" second", 5000, CancellationToken.None);

                Assert.True(result.Started);
                Assert.Contains("one=\"hello world\"", result.StandardOutput);
                Assert.Contains("two=second", result.StandardOutput);
            }
        }
    }
}
