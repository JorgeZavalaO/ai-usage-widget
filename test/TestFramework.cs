using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AiUsageWidget.Tests
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class TestAttribute : Attribute
    {
    }

    public sealed class TestFailureException : Exception
    {
        public TestFailureException(string message) : base(message)
        {
        }
    }

    public static class Assert
    {
        public static void True(bool condition, string message = null)
        {
            if (!condition) throw new TestFailureException(message ?? "Expected true but was false.");
        }

        public static void False(bool condition, string message = null)
        {
            if (condition) throw new TestFailureException(message ?? "Expected false but was true.");
        }

        public static void Null(object value, string message = null)
        {
            if (value != null) throw new TestFailureException(message ?? "Expected null but was " + value + ".");
        }

        public static void NotNull(object value, string message = null)
        {
            if (value == null) throw new TestFailureException(message ?? "Expected a value but was null.");
        }

        public static void Equal<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new TestFailureException(
                    (message == null ? string.Empty : message + " ") +
                    "Expected <" + expected + "> but was <" + actual + ">.");
            }
        }

        public static void NotEqual<T>(T unexpected, T actual, string message = null)
        {
            if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            {
                throw new TestFailureException(
                    (message == null ? string.Empty : message + " ") +
                    "Did not expect <" + actual + ">.");
            }
        }

        public static void Near(double expected, double actual, double tolerance, string message = null)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new TestFailureException(
                    (message == null ? string.Empty : message + " ") +
                    "Expected <" + expected + "> +/- " + tolerance + " but was <" + actual + ">.");
            }
        }

        public static void Contains(string expectedSubstring, string actual, string message = null)
        {
            if (actual == null || actual.IndexOf(expectedSubstring, StringComparison.Ordinal) < 0)
            {
                throw new TestFailureException(
                    (message == null ? string.Empty : message + " ") +
                    "Expected to find <" + expectedSubstring + "> in <" + actual + ">.");
            }
        }

        public static void DoesNotContain(string unexpectedSubstring, string actual, string message = null)
        {
            if (actual != null && actual.IndexOf(unexpectedSubstring, StringComparison.Ordinal) >= 0)
            {
                throw new TestFailureException(
                    (message == null ? string.Empty : message + " ") +
                    "Did not expect to find <" + unexpectedSubstring + ">.");
            }
        }

        public static void SequenceEqual<T>(IList<T> expected, IList<T> actual, string message = null)
        {
            if (expected == null || actual == null || expected.Count != actual.Count)
            {
                throw new TestFailureException(
                    (message == null ? string.Empty : message + " ") +
                    "Sequences differ in length.");
            }

            for (int i = 0; i < expected.Count; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(expected[i], actual[i]))
                {
                    throw new TestFailureException(
                        (message == null ? string.Empty : message + " ") +
                        "Sequence differs at index " + i + ": expected <" + expected[i] + "> but was <" + actual[i] + ">.");
                }
            }
        }

        public static T Throws<T>(Action action, string message = null) where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new TestFailureException(
                    (message == null ? string.Empty : message + " ") +
                    "Expected " + typeof(T).Name + " but got " + ex.GetType().Name + ".");
            }

            throw new TestFailureException(
                (message == null ? string.Empty : message + " ") +
                "Expected " + typeof(T).Name + " but no exception was thrown.");
        }
    }

    public static class TestPaths
    {
        public static string RepositoryRoot
        {
            get
            {
                var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (directory != null)
                {
                    if (Directory.Exists(Path.Combine(directory.FullName, "test", "fixtures")) &&
                        Directory.Exists(Path.Combine(directory.FullName, "src")))
                    {
                        return directory.FullName;
                    }
                    directory = directory.Parent;
                }

                throw new TestFailureException("Cannot locate the repository root from " +
                                               AppDomain.CurrentDomain.BaseDirectory);
            }
        }

        public static string Fixture(string name)
        {
            var path = Path.Combine(RepositoryRoot, "test", "fixtures", name);
            if (!File.Exists(path)) throw new TestFailureException("Missing fixture: " + path);
            return path;
        }

        public static string FixtureText(string name)
        {
            return File.ReadAllText(Fixture(name), Encoding.UTF8);
        }
    }

    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix = "ai-usage-widget-test")
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; private set; }

        public string Combine(params string[] parts)
        {
            var result = Path;
            for (int i = 0; i < parts.Length; i++) result = System.IO.Path.Combine(result, parts[i]);
            return result;
        }

        public string Write(string relativePath, string content)
        {
            var path = Combine(relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public string MakeDirectory(string relativePath)
        {
            var path = Combine(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path)) Directory.Delete(Path, true);
                    return;
                }
                catch (Exception)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }

    public static class TestRunner
    {
        public static int Main(string[] args)
        {
            string filter = null;
            var selfTest = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
                {
                    filter = args[i].Substring("--filter=".Length);
                }
                else if (string.Equals(args[i], "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    selfTest = true;
                }
            }

            if (selfTest)
            {
                Console.WriteLine("Self-test: intentional failure propagation.");
                try
                {
                    Assert.True(false, "intentional failure");
                }
                catch (TestFailureException)
                {
                    Console.WriteLine("Self-test produced a failing assertion as expected.");
                    return 1;
                }
                Console.WriteLine("Self-test did not fail as expected.");
                return 0;
            }

            var tests = Discover(filter);
            Console.WriteLine("AI Usage Widget offline tests");
            Console.WriteLine("Focused filter: " + (filter ?? "(none)"));
            Console.WriteLine();

            var failed = 0;
            var passed = 0;
            var total = Stopwatch.StartNew();

            for (int i = 0; i < tests.Count; i++)
            {
                var test = tests[i];
                var watch = Stopwatch.StartNew();
                try
                {
                    test.Method.Invoke(null, null);
                    watch.Stop();
                    passed++;
                    Console.WriteLine("  PASS " + test.Name + " (" + watch.ElapsedMilliseconds + " ms)");
                }
                catch (TargetInvocationException ex)
                {
                    watch.Stop();
                    failed++;
                    var inner = ex.InnerException ?? ex;
                    Console.WriteLine("  FAIL " + test.Name + " (" + watch.ElapsedMilliseconds + " ms)");
                    Console.WriteLine("       " + inner.GetType().Name + ": " + inner.Message);
                    if (!(inner is TestFailureException) && inner.StackTrace != null)
                    {
                        var lines = inner.StackTrace.Split('\n');
                        for (int line = 0; line < Math.Min(4, lines.Length); line++)
                        {
                            Console.WriteLine("       " + lines[line].Trim());
                        }
                    }
                }
                catch (Exception ex)
                {
                    watch.Stop();
                    failed++;
                    Console.WriteLine("  FAIL " + test.Name + " (" + watch.ElapsedMilliseconds + " ms)");
                    Console.WriteLine("       " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            total.Stop();
            Console.WriteLine();
            Console.WriteLine(string.Format(
                "{0} passed, {1} failed, {2} total in {3:0.0}s",
                passed,
                failed,
                tests.Count,
                total.Elapsed.TotalSeconds));

            return failed == 0 ? 0 : 1;
        }

        private sealed class TestCase
        {
            public string Name;
            public MethodInfo Method;
        }

        private static List<TestCase> Discover(string filter)
        {
            var cases = new List<TestCase>();
            var assembly = typeof(TestRunner).Assembly;

            var types = assembly.GetTypes()
                .Where(t => t.IsClass && t.IsAbstract && t.IsSealed)
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0)
                    .OrderBy(m => m.Name, StringComparer.Ordinal);

                foreach (var method in methods)
                {
                    if (method.GetParameters().Length != 0) continue;
                    if (method.ReturnType != typeof(void)) continue;

                    var name = type.Name + "." + method.Name;
                    if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    cases.Add(new TestCase { Name = name, Method = method });
                }
            }

            return cases;
        }
    }
}
