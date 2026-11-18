// claude-headless.exe: status, run-queue, autostart and job, with output (UTF-8 when redirected).
using System.Text;

if (Console.IsOutputRedirected)
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
return ClaudeHeadlessTool.Cli.Run(args, new ClaudeHeadlessTool.CliHost { Out = Console.Out, Err = Console.Error, LogEcho = Console.Out });
