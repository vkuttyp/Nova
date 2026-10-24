using System.Diagnostics;
using System.Globalization;

namespace Nova.Fuzz;

/// <summary>
/// The fuzz campaign tool (docs/testing.md §5):
/// <c>Nova.Fuzz [--seed S] [--count N | --minutes M] [--jobs J] [--out DIR]</c>, or <c>--print SEED</c> to show a
/// program, or <c>--accept N</c> to check only that the first N programs type-check.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--print", var printSeed])
        {
            Console.Out.Write(ProgramGenerator.Generate(int.Parse(printSeed, CultureInfo.InvariantCulture)));
            return 0;
        }
        if (args is ["--accept", var countText])
        {
            return Accept(int.Parse(countText, CultureInfo.InvariantCulture));
        }

        var seed = 1;
        long? count = null;
        double? minutes = null;
        var jobs = Environment.ProcessorCount;
        var output = "fuzz-findings";
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--seed": seed = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--count": count = long.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--minutes": minutes = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--jobs": jobs = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                case "--out": output = args[i + 1]; break;
                default:
                    Console.Error.WriteLine($"unknown option '{args[i]}'");
                    return 2;
            }
        }
        if (args.Length % 2 != 0)
        {
            Console.Error.WriteLine("usage: Nova.Fuzz [--seed S] [--count N | --minutes M] [--jobs J] [--out DIR] | --print SEED | --accept N");
            return 2;
        }
        return Campaign(seed, count ?? (minutes is null ? 100 : long.MaxValue), minutes, jobs, output);
    }

    private static int Accept(int count)
    {
        var rejected = 0;
        for (var s = 1; s <= count; s++)
        {
            if (Oracle.Accepts(ProgramGenerator.Generate(s)) is { } error)
            {
                rejected++;
                Console.Out.WriteLine($"seed {s}: {error.Split('\n')[0]}");
            }
        }
        Console.Out.WriteLine($"{count - rejected}/{count} accepted");
        return rejected == 0 ? 0 : 1;
    }

    private static int Campaign(int firstSeed, long count, double? minutes, int jobs, string output)
    {
        Directory.CreateDirectory(output);
        var clock = Stopwatch.StartNew();
        var deadline = minutes is { } m ? TimeSpan.FromMinutes(m) : TimeSpan.MaxValue;
        long next = firstSeed - 1, programs = 0, findings = 0, panicking = 0, exiting = 0;
        var gate = new object();
        Console.Out.WriteLine($"nova-fuzz: seeds from {firstSeed}, {jobs} jobs, configurations: {string.Join(", ", Oracle.Configs.Select(c => c.Name))} + interpreter");

        void Worker()
        {
            while (clock.Elapsed < deadline)
            {
                long seed;
                lock (gate)
                {
                    if (next - firstSeed + 1 >= count)
                    {
                        return;
                    }
                    seed = ++next;
                }
                var verdict = Oracle.Check((int)seed);
                lock (gate)
                {
                    programs++;
                    if (verdict.Reference is { } outcome)
                    {
                        panicking += outcome.ExitCode == 134 ? 1 : 0;
                        exiting += outcome.ExitCode is not (0 or 134) ? 1 : 0;
                    }
                    if (verdict.Finding is { } finding)
                    {
                        findings++;
                        File.WriteAllText(Path.Combine(output, $"finding-{seed}.nova"), verdict.Program);
                        File.WriteAllText(Path.Combine(output, $"finding-{seed}.txt"), finding + "\n");
                        Console.Out.WriteLine($"FINDING seed {seed}: {finding.Split('\n')[0]}");
                    }
                    if (programs % 100 == 0)
                    {
                        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"[{(int)clock.Elapsed.TotalHours:00}:{clock.Elapsed:mm\\:ss}] {programs} programs, {findings} findings, {panicking} panicked, {exiting} other exit statuses"));
                    }
                }
            }
        }

        var threads = Enumerable.Range(0, jobs).Select(_ => new Thread(Worker)).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"nova-fuzz: {programs} programs (seeds {firstSeed}..{next}) in {(int)clock.Elapsed.TotalHours:00}:{clock.Elapsed:mm\\:ss}, {findings} findings, {panicking} panicked, {exiting} other exit statuses");
        Console.Out.WriteLine(summary);
        File.WriteAllText(Path.Combine(output, "summary.txt"), summary + "\n");
        return findings == 0 ? 0 : 1;
    }
}
